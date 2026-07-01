using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SpoolTools.Revit;
using SpoolTools.Revit.Spooling;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace SpoolTools.UI
{
    public partial class SpoolDialog : Window
    {
        private readonly UIDocument _uiDoc;
        public SpoolDialogViewModel ViewModel { get; }

        /// <summary>ElementId of the throwaway 3D view hosted by the embedded
        /// PreviewControl. Null when no selection (no preview view exists).</summary>
        private ElementId? _previewViewId;
        private PreviewControl? _previewControl;

        public SpoolDialog(UIDocument uiDoc,
                           IReadOnlyList<SpoolNumberRegistry.Entry> existing,
                           IReadOnlyList<TitleblockChoice> titleblocks,
                           IReadOnlyList<ScheduleChoice>   schedules,
                           IReadOnlyList<TagFamilyChoice>  tagFamilies,
                           IReadOnlyList<ViewTemplateChoice> viewTemplates,
                           IReadOnlyDictionary<long, TitleblockRegion> regions,
                           SpoolSettings settings,
                           ElementId? previewViewId)
        {
            InitializeComponent();
            _uiDoc        = uiDoc;
            _previewViewId = previewViewId;
            ViewModel      = new SpoolDialogViewModel(existing, titleblocks, schedules, tagFamilies, viewTemplates, regions, settings);
            DataContext    = ViewModel;

            Loaded += (_, _) => AttachPreviewIfReady();
            Closed += (_, _) => OnDialogClosed();

            // Cap the dialog at the screen's work area so it can never end
            // up taller than the visible desktop (which previously pushed
            // the title bar off the top of the screen). The default Height
            // is the target; if the desktop is smaller we shrink to fit and
            // the ScrollViewer absorbs the rest.
            ApplyWorkAreaHeightCap();

            // When the Renumber checkbox toggles ON, three more rows
            // appear inside the dialog. SizeToContent="Height" SHOULD
            // make the Window grow, but the inner ScrollViewer claims
            // the available space and adds a scrollbar instead. Force
            // a fresh measurement pass by toggling SizeToContent so
            // the new content height propagates up to the Window.
            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.RenumberEnabled))
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var st = SizeToContent;
                        SizeToContent = SizeToContent.Manual;
                        SizeToContent = st;
                    }), System.Windows.Threading.DispatcherPriority.Render);
                }
            };
        }

        /// <summary>Limit the dialog's height + starting position so it
        /// always fits on the current display, regardless of screen size or
        /// taskbar location. Without this, smaller displays (1080p with
        /// large Windows scaling) had the title bar clipped above the top
        /// of the screen.</summary>
        private void ApplyWorkAreaHeightCap()
        {
            try
            {
                var workArea = System.Windows.SystemParameters.WorkArea;
                double cap = workArea.Height - 20;   // small buffer
                if (cap > 0)
                {
                    MaxHeight = cap;
                    if (Height > cap) Height = cap;
                }
            }
            catch { /* defensive — SystemParameters can fail on remote sessions */ }
        }

        // ── PreviewControl lifecycle ───────────────────────────────────────────

        /// <summary>Builds the PreviewControl from the temp 3D view (if any)
        /// and parents it under the PreviewHost Border. Replaces the placeholder
        /// TextBlock.</summary>
        private void AttachPreviewIfReady()
        {
            if (_previewControl != null) return;     // already attached
            if (_previewViewId == null) return;      // no view yet (empty selection)

            try
            {
                _previewControl = new PreviewControl(_uiDoc.Document, _previewViewId);
                PreviewHost.Child = _previewControl;
            }
            catch (Exception ex)
            {
                // PreviewControl failed to construct — keep the placeholder so
                // the user at least sees a message instead of a blank box.
                if (PreviewHost.Child == null)
                {
                    PreviewHost.Child = new System.Windows.Controls.TextBlock
                    {
                        Text = "Preview unavailable: " + ex.Message,
                        Foreground = System.Windows.Media.Brushes.OrangeRed,
                        TextWrapping = TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(20),
                    };
                }
            }
        }

        /// <summary>Detaches the PreviewControl from the visual tree + disposes
        /// it. Must be called BEFORE the underlying view is deleted so the
        /// control doesn't try to render a dead view.</summary>
        private void DetachPreview()
        {
            if (_previewControl == null) return;
            try { PreviewHost.Child = null; } catch { }
            try { _previewControl.Dispose(); } catch { }
            _previewControl = null;
        }

        /// <summary>Fires on Window.Closed. Persists the dialog's current
        /// settings (even on Cancel/X) so The Spooler and the next
        /// Create Spool launch pick up the user's configured shared
        /// state without forcing them to actually create a spool first.
        /// Also detaches the PreviewControl and deletes the throwaway
        /// 3D preview view via the same ExternalEvent (orphan sweep on
        /// next SpoolCommand launch is the safety net if any of this
        /// fails).</summary>
        private void OnDialogClosed()
        {
            DetachPreview();

            // Snapshot the current request BEFORE the action queues so
            // the action closure isn't relying on still-live UI state.
            // The actual settings merge runs on the Revit thread (we need
            // the doc to Load the existing settings).
            var req      = ViewModel.BuildRequest();
            var viewId   = _previewViewId;
            _previewViewId = null;

            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Save settings — best-effort, runs whether or not the
                // user clicked Create Spool. Load-then-merge so The
                // Spooler's fields aren't wiped to defaults on every
                // Create Spool dialog close.
                try
                {
                    using var saveTx = new Transaction(doc, "Spool: save settings on close");
                    saveTx.Start();
                    var merged = ApplyRequestToSettings(req, SpoolSettings.Load(doc));
                    SpoolSettings.Save(doc, merged);
                    saveTx.Commit();
                }
                catch { }

                // Delete the throwaway 3D preview view if one was created.
                if (viewId != null)
                {
                    try
                    {
                        if (doc.GetElement(viewId) != null)
                        {
                            using var tx = new Transaction(doc, "Spool: delete preview view");
                            tx.Start();
                            doc.Delete(viewId);
                            tx.Commit();
                        }
                    }
                    catch { /* orphan sweep handles leftovers */ }
                }
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        /// <summary>Called from inside Revit-thread actions (Pick More, Reset)
        /// to keep the preview's section box + isolation aligned with the
        /// current selection. Creates the view lazily if it didn't exist yet
        /// (user opened the dialog with no pre-selection then added some).
        /// Returns the new view id (or unchanged) for the caller to update.</summary>
        private ElementId? SyncPreviewViewOnRevitThread(
            Document doc, IReadOnlyCollection<ElementId> ids)
        {
            var builder = new SpoolViewBuilder(doc);

            if (ids.Count == 0)
            {
                // Selection emptied; leave the view in place (it'll be deleted
                // on close) but the PreviewControl will simply show nothing.
                return _previewViewId;
            }

            // Delete the prior preview view and recreate fresh. Updating in
            // place via IsolateElementsTemporary + ConvertToPermanent doesn't
            // reliably re-show elements that were permanently hidden by the
            // previous round — newly picked parts stay invisible. The user
            // loses their orbit orientation each Pick More but the visible
            // content is guaranteed correct.
            using var tx = new Transaction(doc, "Spool: refresh preview view");
            tx.Start();
            if (_previewViewId != null)
            {
                try { doc.Delete(_previewViewId); } catch { /* already gone */ }
            }
            var newId = builder.CreatePreviewView(
                ids, "TMP_SPOOL_PREVIEW_" + DateTime.Now.Ticks);
            tx.Commit();
            return newId == ElementId.InvalidElementId ? null : newId;
        }

        // ── Selection management ───────────────────────────────────────────────

        public void LoadSelection(IReadOnlyList<ElementId> ids, IReadOnlyList<string> existingSpoolValues)
        {
            // Pass the single common service abbreviation so the VM can
            // auto-suggest a spool number using the same template The
            // Spooler uses for batch runs. Mixed-service selections
            // resolve to null, falling through to the user-typed path.
            string? svc = LookupCommonService(_uiDoc?.Document, ids);
            ViewModel.SetSelection(ids, existingSpoolValues, svc);
        }

        /// <summary>Walks the selected parts via
        /// <see cref="FabricationServiceLookup.Resolve"/> and returns the
        /// single common service abbreviation, or null when the selection
        /// is empty, has no FabricationParts, or mixes services. Used to
        /// drive Create Spool's template-based auto-suggest so it matches
        /// The Spooler's first-batch-spool output for the same project.</summary>
        private static string? LookupCommonService(Document? doc,
                                                    IReadOnlyList<ElementId>? ids)
        {
            if (doc == null || ids == null || ids.Count == 0) return null;
            string? common = null;
            foreach (var id in ids)
            {
                var (abbr, _) = FabricationServiceLookup.Resolve(doc, id);
                if (string.IsNullOrWhiteSpace(abbr)) continue;
                if (common == null) common = abbr;
                else if (!string.Equals(common, abbr, StringComparison.OrdinalIgnoreCase))
                    return null;     // mixed services
            }
            return common;
        }

        private void ResetSelection_Click(object sender, RoutedEventArgs e)
        {
            // Hide+detach BEFORE raising the external event. Revit only
            // processes queued ExternalEvents when its main window is in
            // the foreground; with the Topmost dialog up, the event sits
            // in the queue until the user clicks Revit themselves. That
            // made Reset look broken — selection cleared "eventually" but
            // not when the user clicked the button. Same hide/show pattern
            // PickMore uses.
            DetachPreview();
            Hide();
            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                var uiDoc = uiApp.ActiveUIDocument;
                uiDoc.Selection.SetElementIds(new List<ElementId>());

                Dispatcher.Invoke(() =>
                {
                    ViewModel.SetSelection(new List<ElementId>(), new List<string>());
                    AttachPreviewIfReady();
                    Show();
                    Activate();
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        private void PickMore_Click(object sender, RoutedEventArgs e)
        {
            // Detach the PreviewControl before going into pick mode. While
            // bound to a hosted view it interferes with Revit's main-viewport
            // input handling — PickObjects locks the viewport and Revit
            // becomes unresponsive (only fixable by killing the process).
            DetachPreview();
            Hide();
            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc   = uiDoc.Document;
                var combined = new HashSet<ElementId>(ViewModel.SelectedIds);

                try
                {
                    var refs = uiDoc.Selection.PickObjects(
                        ObjectType.Element,
                        new FabricationPartFilter(),
                        "Pick fabrication parts for this spool. Click Finish on the ribbon when done.");

                    foreach (var r in refs)
                    {
                        var el = doc.GetElement(r);
                        if (el != null) combined.Add(el.Id);
                    }
                }
                catch (OperationCanceledException) { /* user pressed Escape */ }

                var ids = combined.ToList();
                // Keep the live doc selection EMPTY so the PreviewControl renders
                // parts in their natural colors. The dialog tracks the spool
                // selection internally via ViewModel.SelectedIds.
                uiDoc.Selection.SetElementIds(new List<ElementId>());
                var values = SpoolNumberRegistry.CurrentValuesOn(doc, ids);

                // Sync the preview view to the new selection. We always get a
                // fresh view ID back (delete-and-recreate semantics) so the
                // PreviewControl below is always created against the new view.
                _previewViewId = SyncPreviewViewOnRevitThread(doc, ids);

                // Extract a common service abbreviation off the Revit thread
                // before marshalling back to the UI thread for the VM update.
                // This is the second SetSelection callsite — see LookupCommonService.
                string? svc = LookupCommonService(doc, ids);
                Dispatcher.Invoke(() =>
                {
                    ViewModel.SetSelection(ids, values, svc);
                    AttachPreviewIfReady();   // builds a fresh PreviewControl
                    Show();
                    Activate();
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        // ── Used Spool Numbers toggle ──────────────────────────────────────────

        private void ToggleUsedSpoolNumbers_Click(object sender, RoutedEventArgs e)
            => ViewModel.ToggleUsedSpoolNumbers();

        // ── Define drawable region (4-point picker) ───────────────────────────

        private void DefineRegion_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedTitleblock == null) return;
            var tbId = ViewModel.SelectedTitleblock.Id;

            // Detach the PreviewControl before going into pick mode (see PickMore
            // for the why — locks Revit's main viewport otherwise).
            DetachPreview();
            Hide();
            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                TitleblockRegion? saved = null;
                string error = string.Empty;

                var uiDoc = uiApp.ActiveUIDocument;
                var doc   = uiDoc.Document;

                // Track the temp sheet outside the try so the finally can clean
                // it up even if the picker throws partway through.
                ElementId? tempSheetId = null;
                View? prevActiveView = uiDoc.ActiveView;

                try
                {
                    // Spin up a throwaway sheet using the chosen titleblock so the
                    // picks happen on a real sheet view with real graphics.
                    using (var tx = new Transaction(doc, "Spool: temp setup sheet"))
                    {
                        tx.Start();
                        var tempSheet = ViewSheet.Create(doc, tbId);
                        tempSheet.SheetNumber = "TMP_SPOOL_RGN_" + DateTime.Now.Ticks;
                        try { tempSheet.Name = "Spool Region Setup (temporary)"; } catch { /* duplicate name — leave default */ }
                        tempSheetId = tempSheet.Id;
                        tx.Commit();
                    }

                    uiDoc.ActiveView = doc.GetElement(tempSheetId) as View;

                    var pickSheet = doc.GetElement(tempSheetId) as View;
                    try
                    {
                        // 4 individual PickPoint calls keep Revit's snap
                        // engine active (endpoint / midpoint / intersection
                        // / nearest etc.) so the user can land precisely on
                        // titleblock edges. After each click we paint a
                        // bright-orange marker on the temp sheet so the
                        // user sees their progress as they go — closest
                        // approximation of a rubber-band preview without
                        // losing snap precision (PickBox has the preview
                        // but no snap support).
                        var v1 = uiDoc.Selection.PickPoint(
                            "Pick FIRST corner of the VIEW region (snapping available)");
                        DrawRegionMarker(doc, pickSheet, v1);

                        var v2 = uiDoc.Selection.PickPoint(
                            "Pick OPPOSITE corner of the VIEW region");
                        DrawRegionRectangle(doc, pickSheet, v1, v2);

                        var s1 = uiDoc.Selection.PickPoint(
                            "Pick FIRST corner of the SCHEDULE region");
                        DrawRegionMarker(doc, pickSheet, s1);

                        var s2 = uiDoc.Selection.PickPoint(
                            "Pick OPPOSITE corner of the SCHEDULE region");

                        saved = new TitleblockRegion
                        {
                            TitleblockTypeId = tbId.Value,
                            ViewMin     = new XYZ(Math.Min(v1.X, v2.X), Math.Min(v1.Y, v2.Y), 0),
                            ViewMax     = new XYZ(Math.Max(v1.X, v2.X), Math.Max(v1.Y, v2.Y), 0),
                            ScheduleMin = new XYZ(Math.Min(s1.X, s2.X), Math.Min(s1.Y, s2.Y), 0),
                            ScheduleMax = new XYZ(Math.Max(s1.X, s2.X), Math.Max(s1.Y, s2.Y), 0),
                        };
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { /* user pressed Escape — skip save */ }
                    catch (OperationCanceledException) { /* defensive — same intent */ }

                    if (saved != null)
                    {
                        using var tx = new Transaction(doc, "Spool: save region");
                        tx.Start();
                        SpoolTitleblockRegions.Set(doc, saved);
                        tx.Commit();
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                finally
                {
                    // ALWAYS delete the temp sheet, even if something above threw.
                    if (tempSheetId != null)
                    {
                        try
                        {
                            using var tx = new Transaction(doc, "Spool: delete temp setup sheet");
                            tx.Start();
                            doc.Delete(tempSheetId);
                            tx.Commit();
                        }
                        catch { /* orphan — will be swept on next Create Spool launch */ }
                    }
                    // Try to put the user back where they were.
                    try
                    {
                        if (prevActiveView != null && doc.GetElement(prevActiveView.Id) != null)
                            uiDoc.ActiveView = prevActiveView;
                    }
                    catch { /* nothing to restore */ }
                }

                Dispatcher.Invoke(() =>
                {
                    if (saved != null) ViewModel.RegionPicked(saved);
                    // Re-attach the preview we detached before the picks.
                    AttachPreviewIfReady();
                    Show();
                    Activate();
                    if (!string.IsNullOrEmpty(error))
                        TaskDialog.Show("Define Region", "Error defining region: " + error);
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        // ── Leader Settings popup ──────────────────────────────────────────────

        private void LeaderSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new LeaderSettingsDialog(
                ViewModel.PlaceLeader,
                ViewModel.LeaderEnd,
                ViewModel.LeaderLengthFt,
                ViewModel.TagOffsetInches)
            {
                Owner = this,
            };
            if (dlg.ShowDialog() == true)
            {
                ViewModel.PlaceLeader     = dlg.Vm.PlaceLeader;
                ViewModel.LeaderEnd       = dlg.Vm.LeaderEnd;
                ViewModel.LeaderLengthFt  = dlg.Vm.LeaderLengthFt;
                ViewModel.TagOffsetInches = dlg.Vm.TagOffsetInches;
            }
        }

        // ── Cancel ─────────────────────────────────────────────────────────────

        /// <summary>"Spool Config…" shortcut — opens the shared
        /// SpoolConfigDialog modelessly. This dialog stays open so any
        /// in-progress per-run state (selection, spool number, sheet
        /// number/name, renumber prefs) is preserved. On Save we re-
        /// read the persisted store and rebind the SHARED fields
        /// (titleblock + region map, schedule, scale, directions, view
        /// template, tag family, leader defaults, Include Welds, Use
        /// Assemblies, Interactive Tagging) via
        /// <see cref="SpoolDialogViewModel.ReloadSharedDefaultsFromSettings"/>
        /// so the user sees the new defaults immediately — without
        /// having to close + reopen Create Spool.</summary>
        private void SpoolConfig_Click(object sender, RoutedEventArgs e)
        {
            SpoolTools.SpoolConfigCommand.OpenConfig(_uiDoc, onSaved: ReloadDefaults);
        }

        /// <summary>Reads the freshly-saved SpoolSettings + region map
        /// off the Revit thread, then dispatches onto the WPF thread to
        /// rebind the VM. Region map needs Revit-side enumeration via
        /// SpoolTitleblockRegions, hence the ExternalEvent hop.</summary>
        private void ReloadDefaults()
        {
            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var freshSettings = SpoolSettings.Load(doc);
                var freshRegions  = SpoolTitleblockRegions.LoadAll(doc);
                Dispatcher.Invoke(() =>
                {
                    ViewModel.ReloadSharedDefaultsFromSettings(freshSettings, freshRegions);
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        // ── Spool-limit threshold check ────────────────────────────────────────

        /// <summary>Reads the persisted Max Weight / Max Length limits from
        /// <see cref="SpoolSettings"/> (the same keys The Spooler's
        /// auto-split rules use, so editing in either place is shared)
        /// and asks <see cref="SpoolerRuleEvaluator.EvaluateSelection"/>
        /// for the selection's aggregate weight + longest bbox dim.
        /// If anything exceeds an enabled limit, surfaces a Modify
        /// Selection / Continue Anyway TaskDialog and returns the
        /// user's choice. Returns true to proceed with the create
        /// action, false to abort + leave the dialog open so the user
        /// can adjust.</summary>
        private bool EnsureThresholdsOk()
        {
            var settings = SpoolSettings.Load(_uiDoc.Document);
            if (!settings.SpoolerRuleMaxWeightEnabled && !settings.SpoolerRuleMaxLengthEnabled)
                return true;

            double? maxW = settings.SpoolerRuleMaxWeightEnabled
                ? ParsePositiveDouble(settings.SpoolerRuleMaxWeightLbText) : null;
            double? maxL = settings.SpoolerRuleMaxLengthEnabled
                ? ParseFeetInches(settings.SpoolerRuleMaxLengthText) : null;
            if (maxW == null && maxL == null) return true;

            var eval = SpoolerRuleEvaluator.EvaluateSelection(
                _uiDoc.Document, ViewModel.SelectedIds);

            var lines = new List<string>();
            if (maxW.HasValue && eval.TotalWeightLb > maxW.Value)
                lines.Add($"• Weight {eval.TotalWeightLb:F0} lbs is over the {maxW.Value:F0} lbs limit (+{eval.TotalWeightLb - maxW.Value:F0} lbs over).");
            if (maxL.HasValue && eval.LongestLengthFt > maxL.Value)
                lines.Add($"• Length {eval.LongestLengthFt:F1} ft is over the {maxL.Value:F1} ft limit (+{eval.LongestLengthFt - maxL.Value:F1} ft over).");
            if (lines.Count == 0) return true;

            var dlg = new TaskDialog("Create Spool — Selection Exceeds Limit")
            {
                MainInstruction = "This selection exceeds your configured Spool limit.",
                MainContent     = string.Join("\n", lines) +
                                  "\n\nLimits are configured in Spool Config (shared with The Spooler's auto-split rules).",
                CommonButtons   = TaskDialogCommonButtons.Cancel,
                DefaultButton   = TaskDialogResult.Cancel,
            };
            dlg.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink1,
                "Modify Selection",
                "Cancel this create action so you can adjust the selection (Pick More / Reset).");
            dlg.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink2,
                "Continue Anyway",
                "Proceed with the over-threshold selection.");

            var result = dlg.Show();
            return result == TaskDialogResult.CommandLink2;
        }

        /// <summary>Parses a positive decimal weight value. Empty / non-
        /// numeric / non-positive input returns null so the caller
        /// treats it as "rule effectively disabled".</summary>
        private static double? ParsePositiveDouble(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return double.TryParse(text, out var v) && v > 0 ? v : null;
        }

        /// <summary>Same accepted forms as The Spooler dialog uses:
        /// decimal feet (<c>10.5</c>), feet-inches with dash (<c>10-6</c>),
        /// canonical Imperial (<c>10'-6"</c>). Returns null on empty /
        /// unparseable.</summary>
        private static double? ParseFeetInches(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (double.TryParse(text, out var asDecimal)) return asDecimal;
            var m = System.Text.RegularExpressions.Regex.Match(
                text, @"^(\d+)\s*['\-]\s*(\d+(?:\.\d+)?)\s*""?$");
            if (m.Success
                && int.TryParse(m.Groups[1].Value, out var feet)
                && double.TryParse(m.Groups[2].Value, out var inches))
                return feet + inches / 12.0;
            return null;
        }

        // ── Preview ────────────────────────────────────────────────────────────

        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanCreate) return;
            if (!EnsureThresholdsOk()) return;

            // The original request reflects everything the user picked in the
            // dialog — including their interactive-tagging choice. Preview
            // itself always auto-places tags (so the user sees a clean sheet
            // composition without being marched through the pick loop), but
            // we remember the original flag so Accept can run interactive
            // tagging as a follow-up step if the user wanted it.
            var origReq = ViewModel.BuildRequest();
            bool wantInteractive = origReq.InteractiveTagging && origReq.TagFamilyId != null;
            var req = origReq with { InteractiveTagging = false };
            ViewModel.StatusText = "Building preview…";

            // Same teardown as Create Spool — detach the in-dialog 3D preview
            // and hide the dialog so the PreviewControl in the preview window
            // doesn't fight us for input or render cycles.
            DetachPreview();
            Hide();

            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                SpoolPreviewSession session;
                try
                {
                    var svc = new SpoolService(uiApp.ActiveUIDocument);
                    session = svc.ExecutePreview(req);
                }
                catch (Exception ex)
                {
                    session = SpoolPreviewSession.Failure(new SpoolResult
                    {
                        Success = false,
                        Message = "Unhandled error: " + ex.Message + "\n\n" + ex.StackTrace,
                    });
                }

                Dispatcher.Invoke(() =>
                {
                    if (!session.Result.Success)
                    {
                        TaskDialog.Show("Spool Preview — Failed", session.Result.Message);
                        ViewModel.StatusText = "Failed — adjust and try again.";
                        AttachPreviewIfReady();
                        Show();
                        Activate();
                        return;
                    }

                    // Snapshot the direction → view-id map BEFORE handing the
                    // session off to the preview window (which disposes it on
                    // accept). We need this map if the user wants interactive
                    // tagging to run after Accept.
                    var viewsForInteractive = new Dictionary<SpoolDirection, ElementId>(
                        session.ViewsByDirection);

                    // Capture the spool's result up-front — once the preview
                    // window finalizes the session, session.Result is still
                    // readable but the session itself gets disposed. We need
                    // the message + warnings for the success notification.
                    var spoolResult = session.Result;

                    var win = new SpoolPreviewWindow(_uiDoc, session);
                    win.Decided += accepted =>
                    {
                        // On Accept: persist settings, optionally run interactive
                        // tagging, show the same success notification Create
                        // Spool shows, then close the spool dialog. On Discard:
                        // reshow the spool dialog with the prior selections
                        // so the user can adjust and retry.
                        if (accepted)
                        {
                            SaveSettingsFromRequest(req with { InteractiveTagging = wantInteractive });
                            if (wantInteractive)
                            {
                                RunPostAcceptInteractiveTagging(req, viewsForInteractive, spoolResult);
                            }
                            else
                            {
                                ShowSpoolSuccessNotification(spoolResult, null);
                                Close();
                            }
                        }
                        else
                        {
                            ViewModel.StatusText = "Discarded — adjust and try again.";
                            AttachPreviewIfReady();
                            Show();
                            Activate();
                        }
                    };
                    win.Show();
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        /// <summary>Runs interactive tagging on the just-accepted preview's
        /// views. Existing auto-placed tags on those views are deleted first
        /// so the user doesn't end up with both an auto and an interactive
        /// tag for each part. Shows the same success notification Create
        /// Spool shows (combining the spool's warnings with any interactive
        /// tagging warnings), then closes the dialog.</summary>
        private void RunPostAcceptInteractiveTagging(
            SpoolRequest req,
            IReadOnlyDictionary<SpoolDirection, ElementId> views,
            SpoolResult spoolResult)
        {
            ViewModel.StatusText = "Interactive tagging…";

            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                var warnings = new List<string>();
                try
                {
                    var svc = new SpoolService(uiApp.ActiveUIDocument);
                    svc.PlaceInteractiveTagsPostAccept(
                        req.Elements, views, req.TagFamilyId!,
                        req.PlaceLeader, req.LeaderEnd, req.LeaderLengthFt,
                        req.IncludeWelds, warnings);
                }
                catch (Exception ex)
                {
                    warnings.Add("Interactive tagging failed: " + ex.Message);
                }

                Dispatcher.Invoke(() =>
                {
                    ShowSpoolSuccessNotification(spoolResult, warnings);
                    Close();
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        /// <summary>Shows the same Create Spool — Success TaskDialog that
        /// the direct Create Spool path uses, combining the spool's own
        /// warnings with any additional warnings produced by post-spool
        /// steps (e.g., interactive tagging on the preview-accept path).
        /// Critical Warnings render in the main body; informational Log
        /// items go behind a "Show details" toggle so the dialog stays
        /// clean for the common case.</summary>
        private static void ShowSpoolSuccessNotification(
            SpoolResult res, IReadOnlyCollection<string>? extraWarnings)
        {
            var allWarnings = res.Warnings.AsEnumerable();
            if (extraWarnings != null) allWarnings = allWarnings.Concat(extraWarnings);
            var warnings = allWarnings.ToList();

            string warnBlock = warnings.Count > 0
                ? "\n\nWarnings:\n - " + string.Join("\n - ", warnings)
                : string.Empty;

            var dlg = new TaskDialog("Create Spool — Success")
            {
                MainInstruction = "Create Spool — Success",
                MainContent     = res.Message + warnBlock,
                CommonButtons   = TaskDialogCommonButtons.Close,
            };
            if (res.Log.Count > 0)
                dlg.ExpandedContent = string.Join("\n", res.Log);
            dlg.Show();
        }

        /// <summary>Draws a small bright-orange crosshair on the temp setup
        /// sheet at the picked point. Visible while the user picks the
        /// next corner, so they can see exactly where the previous click
        /// landed. Marker lives on the temp sheet and gets cleaned up when
        /// the sheet is deleted in the outer finally.</summary>
        private static void DrawRegionMarker(Document doc, View? sheet, XYZ p)
        {
            if (sheet == null) return;
            try
            {
                using var tx = new Transaction(doc, "Spool: region pick marker");
                tx.Start();
                const double SizeFt = 1.0 / 48.0;     // 1/4" crosshair (scales with view zoom)
                var l1 = Line.CreateBound(
                    new XYZ(p.X - SizeFt, p.Y - SizeFt, 0),
                    new XYZ(p.X + SizeFt, p.Y + SizeFt, 0));
                var l2 = Line.CreateBound(
                    new XYZ(p.X - SizeFt, p.Y + SizeFt, 0),
                    new XYZ(p.X + SizeFt, p.Y - SizeFt, 0));
                var dc1 = doc.Create.NewDetailCurve(sheet, l1);
                var dc2 = doc.Create.NewDetailCurve(sheet, l2);
                ApplyOrangePreviewOverride(sheet, new[] { dc1?.Id, dc2?.Id });
                tx.Commit();
            }
            catch { /* cosmetic — never block the picker */ }
        }

        /// <summary>Draws the completed region rectangle on the temp sheet
        /// after the second corner is picked. Stays visible while the user
        /// goes on to define the next region, giving them context for
        /// where the previously-defined area sits on the titleblock.</summary>
        private static void DrawRegionRectangle(Document doc, View? sheet, XYZ a, XYZ b)
        {
            if (sheet == null) return;
            double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X);
            double minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            if (maxX - minX < 1e-6 || maxY - minY < 1e-6) return;
            try
            {
                using var tx = new Transaction(doc, "Spool: region outline");
                tx.Start();
                var tl = new XYZ(minX, maxY, 0);
                var tr = new XYZ(maxX, maxY, 0);
                var br = new XYZ(maxX, minY, 0);
                var bl = new XYZ(minX, minY, 0);
                var top    = doc.Create.NewDetailCurve(sheet, Line.CreateBound(tl, tr));
                var right  = doc.Create.NewDetailCurve(sheet, Line.CreateBound(tr, br));
                var bottom = doc.Create.NewDetailCurve(sheet, Line.CreateBound(br, bl));
                var left   = doc.Create.NewDetailCurve(sheet, Line.CreateBound(bl, tl));
                ApplyOrangePreviewOverride(sheet,
                    new[] { top?.Id, right?.Id, bottom?.Id, left?.Id });
                tx.Commit();
            }
            catch { /* cosmetic */ }
        }

        /// <summary>Applies a bright-orange line override to the just-drawn
        /// preview detail curves so they read as picker feedback rather
        /// than real geometry.</summary>
        private static void ApplyOrangePreviewOverride(View sheet, IEnumerable<ElementId?> ids)
        {
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(new Autodesk.Revit.DB.Color(255, 100, 0));
            ogs.SetProjectionLineWeight(6);
            foreach (var id in ids)
            {
                if (id == null || id == ElementId.InvalidElementId) continue;
                try { sheet.SetElementOverrides(id, ogs); } catch { }
            }
        }

        /// <summary>Writes the just-used spool settings back to the project so
        /// the next dialog open restores them. Shared by the Preview accept
        /// path and the Create Spool success path.</summary>
        private void SaveSettingsFromRequest(SpoolRequest req)
        {
            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    using var tx = new Transaction(doc, "Spool: save settings");
                    tx.Start();
                    var merged = ApplyRequestToSettings(req, SpoolSettings.Load(doc));
                    SpoolSettings.Save(doc, merged);
                    tx.Commit();
                }
                catch { /* best-effort persistence */ }
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        /// <summary>Translates a <see cref="SpoolRequest"/> into the
        /// persisted <see cref="SpoolSettings"/> shape. Shared by every
        /// settings-save site (Create Spool success, Preview accept,
        /// dialog close) so they all write the same fields the same way.
        /// <para/>
        /// Mutates an EXISTING settings object so other tools' fields
        /// (e.g., The Spooler's template + sequence + sheet#) are not
        /// overwritten with defaults. Call sites should pass
        /// <c>SpoolSettings.Load(doc)</c> as the input.
        /// </summary>
        private static SpoolSettings ApplyRequestToSettings(SpoolRequest req, SpoolSettings settings)
        {
            settings.TitleblockTypeId             = req.TitleblockTypeId?.Value;
            settings.ScheduleId                   = req.ScheduleId?.Value;
            settings.DirectionMask                = SpoolSettings.Encode(req.Directions);
            settings.ScaleDenominator             = req.ScaleDenominator;
            settings.TagFamilyId                  = req.TagFamilyId?.Value;
            settings.ViewTemplateId               = req.ViewTemplateId?.Value;
            settings.InteractiveTagging           = req.InteractiveTagging;
            settings.PlaceLeader                  = req.PlaceLeader;
            settings.LeaderEnd                    = req.LeaderEnd == LeaderEndCondition.Free ? 1 : 0;
            settings.LeaderLengthFt               = req.LeaderLengthFt;
            settings.TagOffsetInches              = req.TagOffsetInches;
            settings.IncludeWelds                 = req.IncludeWelds;
            settings.UseAssemblies                = req.UseAssemblies;
            settings.RenumberEnabled              = req.Renumber != null;
            settings.RenumberStartingNumber       = req.Renumber?.StartingNumber       ?? 1;
            settings.RenumberUseSameForIdentical  = req.Renumber?.UseSameForIdentical  ?? true;
            settings.RenumberUseLengthAsSeparator = req.Renumber?.UseLengthAsSeparator ?? false;
            return settings;
        }

        // ── Create Spool ───────────────────────────────────────────────────────

        private void CreateSpool_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanCreate) return;
            if (!EnsureThresholdsOk()) return;

            var req = ViewModel.BuildRequest();
            ViewModel.StatusText = "Creating spool…";

            // Detach the live PreviewControl BEFORE Revit starts mutating the
            // document. The control re-renders on every transaction (and we
            // hit ~5 transactions in SpoolService) — leaving it attached
            // ballooned Create Spool from 1-3s to 30-40s. Reattach on failure
            // so the user can keep iterating without losing the preview.
            bool previewWasAttached = _previewControl != null;
            DetachPreview();

            // Hide the dialog so interactive tagging (and the general spool
            // flow) doesn't have a Topmost WPF window covering the Revit
            // viewport the user is clicking in. On failure we re-Show below.
            Hide();

            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                SpoolResult res;
                try
                {
                    var uiDoc = uiApp.ActiveUIDocument;
                    var doc   = uiDoc.Document;
                    var svc   = new SpoolService(uiDoc);
                    res = svc.Execute(req);

                    if (res.Success)
                    {
                        using var tx = new Transaction(doc, "Spool: save settings");
                        tx.Start();
                        // Merge into existing settings so The Spooler's
                        // template/sequence/sheet# fields survive every
                        // Create Spool success save.
                        var merged = ApplyRequestToSettings(req, SpoolSettings.Load(doc));
                        SpoolSettings.Save(doc, merged);
                        tx.Commit();
                    }
                }
                catch (Exception ex)
                {
                    res = new SpoolResult
                    {
                        Success = false,
                        Message = "Unhandled error: " + ex.Message + "\n\n" + ex.StackTrace,
                    };
                }

                string warn = res.Warnings.Count > 0
                    ? "\n\nWarnings:\n - " + string.Join("\n - ", res.Warnings)
                    : string.Empty;
                string body  = res.Message + warn;
                string title = res.Success ? "Create Spool — Success" : "Create Spool — Failed";

                // Close/hide the dialog BEFORE showing the result, otherwise
                // Topmost=True keeps us in front of the TaskDialog. Success
                // closes for good; failure hides so the user keeps their
                // selection + settings on retry. Log items (informational —
                // e.g., Include Welds skip notices) go behind "Show details"
                // so they don't clutter the main body.
                var resultDlg = new TaskDialog(title)
                {
                    MainInstruction = title,
                    MainContent     = body,
                    CommonButtons   = TaskDialogCommonButtons.Close,
                };
                if (res.Log.Count > 0)
                    resultDlg.ExpandedContent = string.Join("\n", res.Log);

                if (res.Success)
                {
                    Dispatcher.Invoke(() => Close());
                    resultDlg.Show();
                }
                else
                {
                    Dispatcher.Invoke(() => Hide());
                    resultDlg.Show();
                    Dispatcher.Invoke(() =>
                    {
                        ViewModel.StatusText = "Failed — adjust and try again.";
                        if (previewWasAttached) AttachPreviewIfReady();
                        Show();
                        Activate();
                    });
                }
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════

    public sealed record TitleblockChoice(ElementId Id, string DisplayName);
    public sealed record ScheduleChoice  (ElementId Id, string DisplayName);
    /// <summary>Scale dropdown entry. <see cref="Denominator"/> is the Revit
    /// view-scale value (12 = 1"=1', 48 = 1/4"=1', etc.); null means "Auto Fit".</summary>
    public sealed record ScaleChoice(string DisplayName, int? Denominator);
    /// <summary>Tag-family dropdown entry. <see cref="Id"/> = null means the
    /// "Do not place Tags" sentinel.</summary>
    public sealed record TagFamilyChoice(ElementId? Id, string DisplayName);
    /// <summary>View template dropdown entry. <see cref="Id"/> = null means
    /// the "(No template)" sentinel.</summary>
    public sealed record ViewTemplateChoice(ElementId? Id, string DisplayName);
    /// <summary>Dimension-style dropdown entry. Id is the project's
    /// DimensionType element id; null means "use Revit default".</summary>
    public sealed record DimensionStyleChoice(ElementId? Id, string DisplayName);

    /// <summary>Per-view dimension toggle bound to the inline checkbox
    /// row in Create Spool. Mutable IsChecked + INPC so the user's
    /// per-view selections survive auto-rebuilds when the "Views to
    /// Create" toggles change.</summary>
    public sealed class DimensionViewOption : INotifyPropertyChanged
    {
        public SpoolDirection Direction { get; init; }
        public string         DisplayName { get; init; } = string.Empty;

        private bool _isChecked = true;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class SpoolDialogViewModel : INotifyPropertyChanged
    {
        private readonly IReadOnlyList<SpoolNumberRegistry.Entry> _existing;
        private readonly Dictionary<long, TitleblockRegion> _regions;

        // ── Shared spool-naming template (captured from settings at VM
        // construction so Create Spool's auto-suggest produces the same
        // pattern as The Spooler's batch run for the same project) ──
        private readonly string _spoolerNumberTemplate;
        private readonly string _spoolerIdentifier;
        private readonly int    _spoolerStartingSequence;

        public SpoolDialogViewModel(
            IReadOnlyList<SpoolNumberRegistry.Entry> existing,
            IReadOnlyList<TitleblockChoice> titleblocks,
            IReadOnlyList<ScheduleChoice>   schedules,
            IReadOnlyList<TagFamilyChoice>  tagFamilies,
            IReadOnlyList<ViewTemplateChoice> viewTemplates,
            IReadOnlyDictionary<long, TitleblockRegion> regions,
            SpoolSettings settings)
        {
            _existing = existing;
            _regions  = new Dictionary<long, TitleblockRegion>(regions);
            _spoolerNumberTemplate   = string.IsNullOrWhiteSpace(settings.SpoolerNumberTemplate)
                                         ? "{Service}-{ID}-{N:00}"
                                         : settings.SpoolerNumberTemplate;
            _spoolerIdentifier       = settings.SpoolerIdentifier ?? "001";
            _spoolerStartingSequence = settings.SpoolerStartingSequence > 0
                                         ? settings.SpoolerStartingSequence : 1;

            Titleblocks = new ObservableCollection<TitleblockChoice>(titleblocks);
            Schedules   = new ObservableCollection<ScheduleChoice>(schedules);
            Scales      = new ObservableCollection<ScaleChoice>(BuildScaleChoices());

            // Prepend the "Do not place Tags" sentinel so it's the first entry.
            var tagList = new List<TagFamilyChoice>
            {
                new TagFamilyChoice(null, "Do not place Tags"),
            };
            tagList.AddRange(tagFamilies);
            TagFamilies = new ObservableCollection<TagFamilyChoice>(tagList);

            // Prepend the "(No template)" sentinel for view templates.
            var tplList = new List<ViewTemplateChoice>
            {
                new ViewTemplateChoice(null, "(No template)"),
            };
            tplList.AddRange(viewTemplates);
            ViewTemplates = new ObservableCollection<ViewTemplateChoice>(tplList);

            UsedSpoolNumbers = new ObservableCollection<SpoolNumberRegistry.Entry>(existing);

            // Restore last-used selections by id.
            if (settings.TitleblockTypeId is long tbId)
                SelectedTitleblock = Titleblocks.FirstOrDefault(t => t.Id.Value == tbId);
            SelectedTitleblock ??= Titleblocks.FirstOrDefault();

            if (settings.ScheduleId is long schId)
                SelectedSchedule = Schedules.FirstOrDefault(s => s.Id.Value == schId);

            // Restore last-used scale, defaulting to 1/4" = 1'-0" if nothing saved.
            SelectedScale = Scales.FirstOrDefault(s => s.Denominator == settings.ScaleDenominator)
                          ?? Scales.FirstOrDefault(s => s.Denominator == 48)
                          ?? Scales.First();

            // Restore last-used tag family, defaulting to "Do not place Tags".
            if (settings.TagFamilyId is long tagId)
                SelectedTagFamily = TagFamilies.FirstOrDefault(t => t.Id?.Value == tagId);
            SelectedTagFamily ??= TagFamilies.First();   // "Do not place Tags"

            // Restore last-used view template, defaulting to "(No template)".
            if (settings.ViewTemplateId is long vtId)
                SelectedViewTemplate = ViewTemplates.FirstOrDefault(v => v.Id?.Value == vtId);
            SelectedViewTemplate ??= ViewTemplates.First();   // "(No template)"

            // Restore the interactive-tagging flag.
            _interactiveTagging = settings.InteractiveTagging;

            // Restore leader-settings state (defaults: no leader, attached, no elbow offset).
            _placeLeader    = settings.PlaceLeader;
            _leaderEnd      = settings.LeaderEnd == 1 ? LeaderEndCondition.Free
                                                      : LeaderEndCondition.Attached;
            _leaderLengthFt = settings.LeaderLengthFt;
            _tagOffsetInches = settings.TagOffsetInches;

            // Restore direction toggles from mask.
            SetMaskInternal(settings.DirectionMask);

            // Restore renumber preferences (remembered between tool invocations).
            _renumberEnabled              = settings.RenumberEnabled;
            _renumberStartingNumberText   = settings.RenumberStartingNumber.ToString();
            _renumberUseSameForIdentical  = settings.RenumberUseSameForIdentical;
            _renumberUseLengthAsSeparator = settings.RenumberUseLengthAsSeparator;
            _includeWelds                 = settings.IncludeWelds;
            _useAssemblies                = settings.UseAssemblies;
            _statusParamName              = settings.SpoolStatusParamName;
            _statusParamValue             = settings.SpoolStatusParamValue;
            _includeDimensions            = settings.SpoolIncludeDimensionsDefault;
            _dimensionStyleId             = settings.SpoolDimensionStyleId is long dsId ? new ElementId(dsId) : null;
            _dimensionOffsetFt            = Math.Max(0.0, settings.SpoolDimensionOffsetInches / 12.0);
            _enhancedTagPlacement         = settings.EnhancedTagPlacement;

            // Build the initial per-view dim checklist from whatever
            // ortho directions are toggled in Views to Create.
            RebuildDimensionViewOptions();
        }

        /// <summary>Re-applies the SHARED settings (titleblock, region map,
        /// schedule, scale, directions, view template, tag family,
        /// leader defaults, Include Welds, Use Assemblies, Interactive
        /// Tagging) to the bound VM properties via their public setters
        /// — so the UI updates immediately. Called by the SpoolConfig…
        /// shortcut button after the user saves changes in Spool
        /// Config. PER-RUN fields the user has already typed (spool
        /// number, sheet number/name, renumber prefs, selection) are
        /// deliberately left alone so an open Create Spool session
        /// doesn't lose in-progress state.</summary>
        public void ReloadSharedDefaultsFromSettings(
            SpoolSettings settings,
            IReadOnlyDictionary<long, TitleblockRegion> regions)
        {
            // Refresh the in-VM region cache so the ✓ indicator reflects
            // any region the user just picked over in Spool Config.
            _regions.Clear();
            foreach (var kv in regions) _regions[kv.Key] = kv.Value;

            if (settings.TitleblockTypeId is long tbId)
                SelectedTitleblock = Titleblocks.FirstOrDefault(t => t.Id.Value == tbId)
                                  ?? Titleblocks.FirstOrDefault();
            else
                SelectedTitleblock = Titleblocks.FirstOrDefault();

            SelectedSchedule = settings.ScheduleId is long schId
                ? Schedules.FirstOrDefault(s => s.Id.Value == schId)
                : null;

            SelectedScale = Scales.FirstOrDefault(s => s.Denominator == settings.ScaleDenominator)
                          ?? Scales.FirstOrDefault(s => s.Denominator == 48)
                          ?? Scales.First();

            SelectedTagFamily = settings.TagFamilyId is long tagId
                ? TagFamilies.FirstOrDefault(t => t.Id?.Value == tagId) ?? TagFamilies.First()
                : TagFamilies.First();

            SelectedViewTemplate = settings.ViewTemplateId is long vtId
                ? ViewTemplates.FirstOrDefault(v => v.Id?.Value == vtId) ?? ViewTemplates.First()
                : ViewTemplates.First();

            InteractiveTagging = settings.InteractiveTagging;
            PlaceLeader        = settings.PlaceLeader;
            LeaderEnd          = settings.LeaderEnd == 1
                ? LeaderEndCondition.Free
                : LeaderEndCondition.Attached;
            LeaderLengthFt     = settings.LeaderLengthFt;
            TagOffsetInches    = settings.TagOffsetInches;
            IncludeWelds       = settings.IncludeWelds;
            UseAssemblies      = settings.UseAssemblies;

            _statusParamName  = settings.SpoolStatusParamName;
            _statusParamValue = settings.SpoolStatusParamValue;
            _dimensionStyleId  = settings.SpoolDimensionStyleId is long ds2 ? new ElementId(ds2) : null;
            _dimensionOffsetFt = Math.Max(0.0, settings.SpoolDimensionOffsetInches / 12.0);
            IncludeDimensions  = settings.SpoolIncludeDimensionsDefault;
            _enhancedTagPlacement = settings.EnhancedTagPlacement;

            // Direction toggles must go through the public setters so
            // PropertyChanged fires for each (the private mask helper
            // writes backing fields silently — fine for the constructor,
            // not for a live reload).
            int mask = settings.DirectionMask;
            TopChecked   = (mask & (1 << (int)SpoolDirection.Top))   != 0;
            FrontChecked = (mask & (1 << (int)SpoolDirection.Front)) != 0;
            LeftChecked  = (mask & (1 << (int)SpoolDirection.Left))  != 0;
            RightChecked = (mask & (1 << (int)SpoolDirection.Right)) != 0;
            SwIsoChecked = (mask & (1 << (int)SpoolDirection.SwIso)) != 0;
            SeIsoChecked = (mask & (1 << (int)SpoolDirection.SeIso)) != 0;
            NwIsoChecked = (mask & (1 << (int)SpoolDirection.NwIso)) != 0;
            NeIsoChecked = (mask & (1 << (int)SpoolDirection.NeIso)) != 0;

            OnPropertyChanged(nameof(RegionDefined));
        }

        private static IReadOnlyList<ScaleChoice> BuildScaleChoices() => new[]
        {
            new ScaleChoice("Auto Fit",            null),
            new ScaleChoice("1\" = 1'-0\"",        12),
            new ScaleChoice("3/4\" = 1'-0\"",      16),
            new ScaleChoice("1/2\" = 1'-0\"",      24),
            new ScaleChoice("3/8\" = 1'-0\"",      32),
            new ScaleChoice("1/4\" = 1'-0\"",      48),
            new ScaleChoice("3/16\" = 1'-0\"",     64),
            new ScaleChoice("1/8\" = 1'-0\"",      96),
            new ScaleChoice("3/32\" = 1'-0\"",    128),
            new ScaleChoice("1/16\" = 1'-0\"",    192),
            new ScaleChoice("1/32\" = 1'-0\"",    384),
        };

        // ── Selection ──────────────────────────────────────────────────────────

        public List<ElementId> SelectedIds { get; private set; } = new();
        private List<string>   _existingValuesOnSelection = new();

        public string SelectionSummary =>
            SelectedIds.Count == 0
                ? "No fabrication parts selected. Use 'Pick More' to choose them."
                : $"{SelectedIds.Count} fabrication part(s) selected.";

        public bool HasSelection => SelectedIds.Count > 0;

        public bool HasExistingSpool => _existingValuesOnSelection.Count > 0;
        public string ExistingSpoolSummary =>
            _existingValuesOnSelection.Count == 1
                ? $"Current Spool Number on selection: \"{_existingValuesOnSelection[0]}\""
                : $"Selection has mixed Spool Numbers: {string.Join(", ", _existingValuesOnSelection)}";

        /// <param name="serviceAbbreviation">Optional Fabrication Service
        /// abbreviation derived from the selection (e.g. "CHW"). When set,
        /// AND the selection has no existing spool number, AND the user
        /// hasn't typed one yet, the Spool Number field is auto-filled
        /// using the same template + identifier The Spooler uses for
        /// batch runs — so single-spool and batch naming stay consistent
        /// across both tools. Pass null for mixed/unknown services.</param>
        public void SetSelection(IReadOnlyList<ElementId> ids,
                                  IReadOnlyList<string> existingValues,
                                  string? serviceAbbreviation = null)
        {
            SelectedIds = ids.ToList();
            _existingValuesOnSelection = existingValues.ToList();

            // Pre-fill priority when the user hasn't typed anything yet:
            //   1. Single existing spool number on the selection → use it
            //      verbatim so the user can keep or tweak it.
            //   2. Template-driven suggestion using the shared Spooler
            //      template — first sequence value that doesn't collide
            //      with any existing spool number in the project.
            //   3. Leave blank (user types from scratch).
            if (string.IsNullOrWhiteSpace(SpoolNumber))
            {
                if (_existingValuesOnSelection.Count == 1)
                    SpoolNumber = _existingValuesOnSelection[0];
                else if (!string.IsNullOrWhiteSpace(serviceAbbreviation))
                {
                    string suggested = SuggestSpoolNumber(serviceAbbreviation!);
                    if (!string.IsNullOrWhiteSpace(suggested))
                        SpoolNumber = suggested;
                }
            }

            OnPropertyChanged(nameof(SelectionSummary));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasExistingSpool));
            OnPropertyChanged(nameof(ExistingSpoolSummary));
            RaiseCanCreate();
        }

        /// <summary>
        /// Resolves the shared Spooler number template against the given
        /// service abbreviation + the project's stored Identifier, then
        /// scans the existing spool registry for the first sequence value
        /// that produces a non-colliding number. Returns the suggested
        /// spool number or "" if the template can't be resolved usefully.
        ///
        /// Match for The Spooler's first-batch-spool output for the same
        /// project: identical template, identifier, and starting sequence.
        /// Caps the scan at 9999 attempts so a misconfigured template
        /// never spins forever.
        /// </summary>
        private string SuggestSpoolNumber(string serviceAbbreviation)
        {
            if (string.IsNullOrWhiteSpace(_spoolerNumberTemplate)) return string.Empty;

            var existingSet = new HashSet<string>(
                _existing.Select(e => e.SpoolNumber),
                StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < 9999; i++)
            {
                var ctx = new TemplateContext
                {
                    Service     = serviceAbbreviation,
                    ServiceName = serviceAbbreviation,  // best-effort; full name not extracted here
                    Identifier  = _spoolerIdentifier,
                    Sequence    = _spoolerStartingSequence + i,
                };
                string resolved = SpoolerTemplateEngine.Resolve(
                    _spoolerNumberTemplate, ctx);
                if (string.IsNullOrWhiteSpace(resolved)) return string.Empty;
                if (!existingSet.Contains(resolved)) return resolved;
            }
            return string.Empty;
        }

        // ── Spool Number ───────────────────────────────────────────────────────

        private string _spoolNumber = string.Empty;
        public string SpoolNumber
        {
            get => _spoolNumber;
            set
            {
                if (SetField(ref _spoolNumber, value ?? string.Empty))
                {
                    // Sheet Name auto-tracks Spool Number until the user edits
                    // Sheet Name directly. Sheet Number is left alone — the
                    // user types it explicitly (it's typically a different
                    // value, like a project sheet code).
                    if (string.IsNullOrWhiteSpace(SheetName) || SheetName == _previousAutoSheetName)
                        SheetName = value ?? string.Empty;
                    _previousAutoSheetName = value ?? string.Empty;

                    OnPropertyChanged(nameof(SpoolNumberHasError));
                    OnPropertyChanged(nameof(SpoolNumberError));
                    RaiseCanCreate();
                }
            }
        }

        public bool SpoolNumberIsDuplicate
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_spoolNumber)) return false;
                var trimmed = _spoolNumber.Trim();
                // Allow the user to keep the existing value already on their selection.
                if (_existingValuesOnSelection.Count == 1 &&
                    string.Equals(_existingValuesOnSelection[0], trimmed, StringComparison.OrdinalIgnoreCase))
                    return false;
                return _existing.Any(e => string.Equals(e.SpoolNumber, trimmed, StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool   SpoolNumberHasError => SpoolNumberIsDuplicate;
        public string SpoolNumberError    =>
            SpoolNumberIsDuplicate
                ? $"\"{_spoolNumber.Trim()}\" is already in use. Each spool must have a unique number."
                : string.Empty;

        // ── Sheet fields (auto-track spool number until user manually edits) ───

        private string _sheetNumber = string.Empty;
        private string _sheetName   = string.Empty;
        private string _previousAutoSheetName = string.Empty;

        public string SheetNumber
        {
            get => _sheetNumber;
            set { if (SetField(ref _sheetNumber, value ?? string.Empty)) RaiseCanCreate(); }
        }
        public string SheetName
        {
            get => _sheetName;
            set { if (SetField(ref _sheetName, value ?? string.Empty)) RaiseCanCreate(); }
        }

        // ── Renumber (optional pre-spool step) ─────────────────────────────────

        private bool   _renumberEnabled              = false;
        private string _renumberStartingNumberText   = "1";
        private bool   _renumberUseSameForIdentical  = true;
        private bool   _renumberUseLengthAsSeparator = false;
        private bool   _includeWelds                 = true;
        private bool   _useAssemblies;
        // Status param + value are cached for the request build only —
        // there's no Create Spool UI for them (Spool Config owns the
        // editable copy). They're refreshed by ReloadSharedDefaultsFromSettings
        // so a Spool Config save while this dialog is open isn't
        // overwritten by the close-write at the end.
        private string? _statusParamName  = SpoolNumberRegistry.FabricationStatusParam;
        private string? _statusParamValue = SpoolNumberRegistry.FabricationStatusValue;

        // Dimension style + offset come from Spool Config only — Create
        // Spool just exposes the on/off + per-view checkboxes. Cached
        // here so BuildRequest can include them.
        private ElementId? _dimensionStyleId;
        private double     _dimensionOffsetFt = 0.5;   // 6" default if settings load fails

        public bool RenumberEnabled
        {
            get => _renumberEnabled;
            set { if (SetField(ref _renumberEnabled, value)) RaiseCanCreate(); }
        }
        public string RenumberStartingNumberText
        {
            get => _renumberStartingNumberText;
            set { if (SetField(ref _renumberStartingNumberText, value ?? string.Empty)) RaiseCanCreate(); }
        }
        public bool RenumberUseSameForIdentical
        {
            get => _renumberUseSameForIdentical;
            set => SetField(ref _renumberUseSameForIdentical, value);
        }
        public bool RenumberUseLengthAsSeparator
        {
            get => _renumberUseLengthAsSeparator;
            set => SetField(ref _renumberUseLengthAsSeparator, value);
        }

        /// <summary>When on (default), every selected part is renumbered and
        /// tagged. When off, parts whose "Product Range" parameter is
        /// "Joints" (welds, joint fittings) are skipped for those two
        /// steps — they remain pinned and shown in the views.</summary>
        public bool IncludeWelds
        {
            get => _includeWelds;
            set => SetField(ref _includeWelds, value);
        }

        /// <summary>When on, the spool becomes a Revit AssemblyInstance
        /// instead of an ad-hoc 3D view tree on a normal sheet. Persisted
        /// via <see cref="Revit.Spooling.SpoolSettings.UseAssemblies"/>
        /// and shared with The Spooler.</summary>
        public bool UseAssemblies
        {
            get => _useAssemblies;
            set => SetField(ref _useAssemblies, value);
        }

        public int? RenumberStartingNumber =>
            int.TryParse(_renumberStartingNumberText, out int n) ? n : null;

        public bool RenumberValidIfEnabled =>
            !_renumberEnabled || RenumberStartingNumber is int;

        // ── Direction toggles ──────────────────────────────────────────────────

        private bool _top, _front, _left, _right, _sw, _se, _nw, _ne;

        public bool TopChecked   { get => _top;   set { if (SetField(ref _top,   value)) { RaiseCanCreate(); RebuildDimensionViewOptions(); } } }
        public bool FrontChecked { get => _front; set { if (SetField(ref _front, value)) { RaiseCanCreate(); RebuildDimensionViewOptions(); } } }
        public bool LeftChecked  { get => _left;  set { if (SetField(ref _left,  value)) { RaiseCanCreate(); RebuildDimensionViewOptions(); } } }
        public bool RightChecked { get => _right; set { if (SetField(ref _right, value)) { RaiseCanCreate(); RebuildDimensionViewOptions(); } } }
        public bool SwIsoChecked { get => _sw;    set { if (SetField(ref _sw,    value)) RaiseCanCreate(); } }
        public bool SeIsoChecked { get => _se;    set { if (SetField(ref _se,    value)) RaiseCanCreate(); } }
        public bool NwIsoChecked { get => _nw;    set { if (SetField(ref _nw,    value)) RaiseCanCreate(); } }
        public bool NeIsoChecked { get => _ne;    set { if (SetField(ref _ne,    value)) RaiseCanCreate(); } }

        // ── Dimensions ────────────────────────────────────────────────────────

        private bool _includeDimensions;
        public bool IncludeDimensions
        {
            get => _includeDimensions;
            set => SetField(ref _includeDimensions, value);
        }

        /// <summary>Mirror of the Spool Config setting; per-run dialog
        /// reads it at construction. No per-run UI — Spool Config is
        /// the only knob.</summary>
        private bool _enhancedTagPlacement;

        /// <summary>Live list of ORTHO directions currently toggled in
        /// "Views to Create" — auto-rebuilds whenever Top/Front/Left/Right
        /// changes. Iso directions are filtered out: Revit's iso views
        /// are 3D and Dimension elements are 2D annotations that don't
        /// project usefully onto a 3D view.</summary>
        public ObservableCollection<DimensionViewOption> DimensionViewOptions { get; }
            = new ObservableCollection<DimensionViewOption>();

        private void RebuildDimensionViewOptions()
        {
            // Snapshot prior checked-state so the user's per-view picks
            // survive a transient toggle of a direction (e.g. they
            // unchecked Top in the dim list, then accidentally toggled
            // Top off and back on in Views to Create — we keep their
            // unchecked state for Top).
            var prior = DimensionViewOptions.ToDictionary(o => o.Direction, o => o.IsChecked);

            var wanted = new List<(SpoolDirection Dir, string Label, bool Active)>
            {
                (SpoolDirection.Top,   "Top",   _top),
                (SpoolDirection.Front, "Front", _front),
                (SpoolDirection.Left,  "Left",  _left),
                (SpoolDirection.Right, "Right", _right),
            };

            DimensionViewOptions.Clear();
            foreach (var (dir, label, active) in wanted)
            {
                if (!active) continue;
                bool isChecked = prior.TryGetValue(dir, out var prev) ? prev : true;
                DimensionViewOptions.Add(new DimensionViewOption
                {
                    Direction   = dir,
                    DisplayName = label,
                    IsChecked   = isChecked,
                });
            }
        }

        private void SetMaskInternal(int mask)
        {
            _top   = (mask & (1 << (int)SpoolDirection.Top))   != 0;
            _front = (mask & (1 << (int)SpoolDirection.Front)) != 0;
            _left  = (mask & (1 << (int)SpoolDirection.Left))  != 0;
            _right = (mask & (1 << (int)SpoolDirection.Right)) != 0;
            _sw    = (mask & (1 << (int)SpoolDirection.SwIso)) != 0;
            _se    = (mask & (1 << (int)SpoolDirection.SeIso)) != 0;
            _nw    = (mask & (1 << (int)SpoolDirection.NwIso)) != 0;
            _ne    = (mask & (1 << (int)SpoolDirection.NeIso)) != 0;
        }

        private List<SpoolDirection> CurrentDirections()
        {
            var list = new List<SpoolDirection>();
            if (_top)   list.Add(SpoolDirection.Top);
            if (_front) list.Add(SpoolDirection.Front);
            if (_left)  list.Add(SpoolDirection.Left);
            if (_right) list.Add(SpoolDirection.Right);
            if (_sw)    list.Add(SpoolDirection.SwIso);
            if (_se)    list.Add(SpoolDirection.SeIso);
            if (_nw)    list.Add(SpoolDirection.NwIso);
            if (_ne)    list.Add(SpoolDirection.NeIso);
            return list;
        }

        // ── Titleblock / Schedule ──────────────────────────────────────────────

        public ObservableCollection<TitleblockChoice> Titleblocks { get; }
        public ObservableCollection<ScheduleChoice>   Schedules   { get; }

        private TitleblockChoice? _selectedTitleblock;
        public TitleblockChoice? SelectedTitleblock
        {
            get => _selectedTitleblock;
            set
            {
                if (SetField(ref _selectedTitleblock, value))
                {
                    OnPropertyChanged(nameof(HasSelectedTitleblock));
                    OnPropertyChanged(nameof(RegionMissing));
                    OnPropertyChanged(nameof(RegionDefined));
                    OnPropertyChanged(nameof(RegionStatusText));
                    OnPropertyChanged(nameof(RegionButtonText));
                    RaiseCanCreate();
                }
            }
        }

        // ── Drawable region (per-titleblock) ───────────────────────────────────

        public bool HasSelectedTitleblock => _selectedTitleblock != null;

        public bool RegionMissing =>
            _selectedTitleblock != null && !_regions.ContainsKey(_selectedTitleblock.Id.Value);

        /// <summary>True when the chosen titleblock has a saved drawable region.
        /// Drives the green checkmark next to the Edit Drawable Region button.</summary>
        public bool RegionDefined =>
            _selectedTitleblock != null && _regions.ContainsKey(_selectedTitleblock.Id.Value);

        public string RegionStatusText
        {
            get
            {
                if (_selectedTitleblock == null) return "(select a titleblock first)";
                return _regions.ContainsKey(_selectedTitleblock.Id.Value)
                    ? "Defined ✓"
                    : "Not yet defined — required before Create Spool";
            }
        }

        public string RegionButtonText =>
            _selectedTitleblock != null && _regions.ContainsKey(_selectedTitleblock.Id.Value)
                ? "Edit Region…"
                : "Define Region…";

        /// <summary>Called by the dialog code-behind after a successful pick.</summary>
        public void RegionPicked(TitleblockRegion r)
        {
            _regions[r.TitleblockTypeId] = r;
            OnPropertyChanged(nameof(RegionMissing));
            OnPropertyChanged(nameof(RegionDefined));
            OnPropertyChanged(nameof(RegionStatusText));
            OnPropertyChanged(nameof(RegionButtonText));
            RaiseCanCreate();
        }

        private ScheduleChoice? _selectedSchedule;
        public ScheduleChoice? SelectedSchedule
        {
            get => _selectedSchedule;
            set => SetField(ref _selectedSchedule, value);
        }

        public ObservableCollection<ScaleChoice> Scales { get; }

        private ScaleChoice? _selectedScale;
        public ScaleChoice? SelectedScale
        {
            get => _selectedScale;
            set => SetField(ref _selectedScale, value);
        }

        public ObservableCollection<TagFamilyChoice> TagFamilies { get; }

        private TagFamilyChoice? _selectedTagFamily;
        public TagFamilyChoice? SelectedTagFamily
        {
            get => _selectedTagFamily;
            set
            {
                if (SetField(ref _selectedTagFamily, value))
                {
                    OnPropertyChanged(nameof(TaggingActive));
                    OnPropertyChanged(nameof(TaggingInactiveVisibility));
                }
            }
        }

        /// <summary>True when a real tag family is selected (not the
        /// "Do not place Tags" sentinel). Used to gray out the interactive
        /// tagging checkbox.</summary>
        public bool TaggingActive => _selectedTagFamily?.Id != null;

        /// <summary>Inverse of <see cref="TaggingActive"/> for the
        /// inline "(set a Tag Family in Spool Config to enable)" hint
        /// next to the disabled Interactive Tagging checkbox. Exposes
        /// Visibility directly so we don't need a converter.</summary>
        public System.Windows.Visibility TaggingInactiveVisibility =>
            TaggingActive ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        private bool _interactiveTagging;
        public bool InteractiveTagging
        {
            get => _interactiveTagging;
            set => SetField(ref _interactiveTagging, value);
        }

        // ── Leader settings (populated by the LeaderSettingsDialog popup) ──────
        private bool _placeLeader;
        public bool PlaceLeader
        {
            get => _placeLeader;
            set => SetField(ref _placeLeader, value);
        }

        private LeaderEndCondition _leaderEnd = LeaderEndCondition.Attached;
        public LeaderEndCondition LeaderEnd
        {
            get => _leaderEnd;
            set => SetField(ref _leaderEnd, value);
        }

        private double _leaderLengthFt;
        public double LeaderLengthFt
        {
            get => _leaderLengthFt;
            set => SetField(ref _leaderLengthFt, value);
        }

        private double _tagOffsetInches = 1.0;
        public double TagOffsetInches
        {
            get => _tagOffsetInches;
            set => SetField(ref _tagOffsetInches, value);
        }

        public ObservableCollection<ViewTemplateChoice> ViewTemplates { get; }

        private ViewTemplateChoice? _selectedViewTemplate;
        public ViewTemplateChoice? SelectedViewTemplate
        {
            get => _selectedViewTemplate;
            set => SetField(ref _selectedViewTemplate, value);
        }

        // ── Used spool numbers (collapsible) ───────────────────────────────────

        public ObservableCollection<SpoolNumberRegistry.Entry> UsedSpoolNumbers { get; }

        private bool _isUsedSpoolExpanded;
        public bool IsUsedSpoolExpanded
        {
            get => _isUsedSpoolExpanded;
            private set
            {
                _isUsedSpoolExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UsedSpoolChevron));
            }
        }
        public string UsedSpoolChevron => _isUsedSpoolExpanded ? "▲" : "▼";
        public string UsedSpoolHeaderText => $"Used Spool Numbers ({UsedSpoolNumbers.Count})";
        public void ToggleUsedSpoolNumbers() => IsUsedSpoolExpanded = !_isUsedSpoolExpanded;

        // ── Validation / commit ────────────────────────────────────────────────

        public bool CanCreate =>
            HasSelection &&
            !string.IsNullOrWhiteSpace(_spoolNumber) &&
            !SpoolNumberIsDuplicate &&
            !string.IsNullOrWhiteSpace(_sheetNumber) &&
            !string.IsNullOrWhiteSpace(_sheetName) &&
            _selectedTitleblock != null &&
            !RegionMissing &&
            RenumberValidIfEnabled &&
            CurrentDirections().Count > 0;

        /// <summary>One-line explanation of the FIRST failing CanCreate
        /// check, surfaced in the footer so the user can see why Preview
        /// / Create Spool are greyed out. Empty when CanCreate is true.</summary>
        public string CanCreateBlocker
        {
            get
            {
                if (!HasSelection)
                    return "Select fabrication parts (Pick More…) to begin.";
                if (string.IsNullOrWhiteSpace(_spoolNumber))
                    return "Enter a Spool Number.";
                if (SpoolNumberIsDuplicate)
                    return "Spool Number is already used on another spool.";
                if (string.IsNullOrWhiteSpace(_sheetNumber))
                    return "Enter a Sheet #.";
                if (string.IsNullOrWhiteSpace(_sheetName))
                    return "Enter a Sheet Name.";
                if (_selectedTitleblock == null)
                    return "Choose a Titleblock in Spool Config… (no project default set).";
                if (RegionMissing)
                    return "Define the drawable region for the selected Titleblock — Spool Config… → Edit Drawable Region.";
                if (!RenumberValidIfEnabled)
                    return "Renumber Starting Number must be a positive integer.";
                if (CurrentDirections().Count == 0)
                    return "Pick at least one view direction in Views to Create.";
                return string.Empty;
            }
        }

        public SpoolRequest BuildRequest() => new SpoolRequest
        {
            Elements         = SelectedIds.ToList(),
            SpoolNumber      = _spoolNumber.Trim(),
            SheetNumber      = _sheetNumber.Trim(),
            SheetName        = _sheetName.Trim(),
            Directions       = CurrentDirections(),
            TitleblockTypeId = _selectedTitleblock?.Id,
            ScheduleId       = _selectedSchedule?.Id,
            ScaleDenominator = _selectedScale?.Denominator,
            Renumber = _renumberEnabled
                ? new SpoolRenumberOptions
                {
                    StartingNumber       = RenumberStartingNumber ?? 1,
                    UseSameForIdentical  = _renumberUseSameForIdentical,
                    UseLengthAsSeparator = _renumberUseLengthAsSeparator,
                }
                : null,
            TagFamilyId        = _selectedTagFamily?.Id,
            ViewTemplateId     = _selectedViewTemplate?.Id,
            InteractiveTagging = _interactiveTagging && _selectedTagFamily?.Id != null,
            PlaceLeader        = _placeLeader,
            LeaderEnd          = _leaderEnd,
            LeaderLengthFt     = _leaderLengthFt,
            TagOffsetInches    = _tagOffsetInches,
            IncludeWelds       = _includeWelds,
            UseAssemblies      = _useAssemblies,
            StatusParamName    = _statusParamName,
            StatusParamValue   = _statusParamValue,
            IncludeDimensions  = _includeDimensions,
            DimensionStyleId   = _dimensionStyleId,
            DimensionOffsetFt  = _dimensionOffsetFt,
            DimensionViewMask  = BuildDimensionViewMask(),
            EnhancedTagPlacement = _enhancedTagPlacement,
        };

        /// <summary>Bitmask of the per-view dim checkboxes that are
        /// currently ON. Engine intersects this with the actual
        /// created views so an ortho direction the user disabled in
        /// Views to Create can't accidentally get dim'd.</summary>
        private int BuildDimensionViewMask()
        {
            int mask = 0;
            foreach (var opt in DimensionViewOptions)
                if (opt.IsChecked)
                    mask |= 1 << (int)opt.Direction;
            return mask;
        }

        // ── Status bar ─────────────────────────────────────────────────────────

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value);
        }

        // ── INotifyPropertyChanged ─────────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        /// <summary>Raises PropertyChanged for both CanCreate and the
        /// CanCreateBlocker hint so the footer status stays in sync
        /// with the disabled state of the action buttons.</summary>
        private void RaiseCanCreate()
        {
            OnPropertyChanged(nameof(CanCreate));
            OnPropertyChanged(nameof(CanCreateBlocker));
        }
    }
}
