using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SpoolTools.Revit;
using SpoolTools.Revit.Spooling;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace SpoolTools.UI
{
    /// <summary>
    /// Standalone top-level dialog for batch ("multi") spooling. Launched
    /// from its own ribbon button (<c>SpoolerCommand</c>); shared spool
    /// settings (titleblock, schedule, view directions, scale, tag, leader,
    /// renumber prefs, Include Welds) are read from the project's
    /// persisted <see cref="Revit.Spooling.SpoolSettings"/> — the same
    /// store the single-spool Create Spool dialog writes to. Future Spool
    /// Settings tool will let the user edit that store directly.
    ///
    /// This dialog collects only the batch-specific concerns:
    ///   • Spool number / name templating (tokens: {Service}, {ServiceName},
    ///     {ID}, {N}, {N:fmt}, {Number})
    ///   • Starting sequence number + starting sheet number
    ///   • Start element (where the network walk begins)
    ///   • Break elements (each one is the last part of its spool; the
    ///     next walked part begins the next spool)
    ///
    /// Modeless + Topmost so it floats above Revit; Hides itself during
    /// element picks to free the viewport.
    /// </summary>
    public partial class SpoolerDialog : Window
    {
        private readonly UIDocument _uiDoc;
        public SpoolerDialogVm Vm { get; }

        /// <summary>Throwaway 3D preview view, created by SpoolerCommand
        /// before the dialog opens. Hosted in the PreviewControl and
        /// color-coded per-partition by SpoolerPreviewPainter whenever
        /// Start / Breaks change. Null when the user opened the dialog
        /// with no pre-selection.</summary>
        private ElementId? _previewViewId;
        private PreviewControl? _previewControl;

        public SpoolerDialog(UIDocument uiDoc, IReadOnlyList<ElementId> preselected, ElementId? previewViewId)
        {
            InitializeComponent();
            _uiDoc         = uiDoc;
            _previewViewId = previewViewId;

            // Load persisted Spooler fields so the user's last templates /
            // identifier / starting sheet survive between sessions and
            // Revit restarts. Defaults still apply if no settings have
            // been saved yet.
            var settings = SpoolSettings.Load(uiDoc.Document);
            Vm = new SpoolerDialogVm(uiDoc.Document)
            {
                Identifier           = settings.SpoolerIdentifier          ?? "001",
                SpoolNumberTemplate  = settings.SpoolerNumberTemplate      ?? "{Service}-{ID}-{N:00}",
                SpoolNameTemplate    = settings.SpoolerNameTemplate        ?? "Spool {Number}",
                StartingSequenceText = settings.SpoolerStartingSequence.ToString(),
                StartingSheetNumber  = settings.SpoolerStartingSheetNumber ?? "S1",
                RuleAtFieldWelds     = settings.SpoolerRuleAtFieldWelds,
                RuleMaxWeightEnabled = settings.SpoolerRuleMaxWeightEnabled,
                RuleMaxWeightLbText  = settings.SpoolerRuleMaxWeightLbText  ?? "1000",
                RuleMaxLengthEnabled = settings.SpoolerRuleMaxLengthEnabled,
                RuleMaxLengthText    = settings.SpoolerRuleMaxLengthText    ?? "20",
                UseAssemblies        = settings.UseAssemblies,
                ConvertSplitWeldsToFieldWelds = settings.SpoolerConvertSplitWeldsToFieldWelds,
                RenumberEnabled              = settings.RenumberEnabled,
                RenumberStartingNumberText   = settings.RenumberStartingNumber.ToString(),
                RenumberUseSameForIdentical  = settings.RenumberUseSameForIdentical,
                RenumberUseLengthAsSeparator = settings.RenumberUseLengthAsSeparator,
                IncludeWelds                 = settings.IncludeWelds,
            };
            Vm.SetSelection(preselected);
            DataContext = Vm;

            // Save on close + clean up preview view + final settings save.
            Closed += (_, _) => OnDialogClosed();
            Loaded += (_, _) => AttachPreviewIfReady();

            // Repaint the 3D preview whenever any rule changes. The
            // VM raises this from inside its rule-property setters;
            // we re-use the same hide/paint/show pattern the Refresh
            // button uses (which is the only reliable way to update
            // the embedded PreviewControl).
            Vm.RulesChanged += () =>
            {
                if (_previewViewId == null) return;
                RefreshPreview_Click(this, new RoutedEventArgs());
            };
        }

        // ── PreviewControl lifecycle ───────────────────────────────────────────

        /// <summary>Builds the PreviewControl from the temp 3D view and
        /// swaps it in for the placeholder TextBlock. No-op if there's
        /// no preview view (empty pre-selection) or the control is
        /// already attached. Errors fall back to the placeholder with
        /// the exception message — preview is non-critical.</summary>
        private void AttachPreviewIfReady()
        {
            if (_previewControl != null) return;
            if (_previewViewId == null) return;

            try
            {
                _previewControl   = new PreviewControl(_uiDoc.Document, _previewViewId);
                PreviewHost.Child = _previewControl;
            }
            catch (Exception ex)
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

        /// <summary>Detaches the PreviewControl from the visual tree +
        /// disposes it. Required before launching any Revit pick (the
        /// hosted view locks viewport input otherwise) and before the
        /// underlying preview view is deleted (so the control doesn't
        /// try to render a dead view).</summary>
        private void DetachPreview()
        {
            if (_previewControl == null) return;
            try { PreviewHost.Child = null; } catch { }
            try { _previewControl.Dispose(); } catch { }
            _previewControl = null;
        }

        /// <summary>Manual Refresh button — same pattern as Pick Start /
        /// Pick Breaks: hide the dialog (so Revit's main window comes
        /// forward and its render pipeline activates), do the paint
        /// inline on the Revit thread, then re-show the dialog with
        /// the PreviewControl reattached. This is the only reliable
        /// way to update the embedded preview view — Revit's renderer
        /// is gated on its main window being in the foreground.</summary>
        private void RefreshPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_previewViewId == null) return;

            var selection = Vm.Selection.ToList();
            var start     = Vm.StartElementId ?? ElementId.InvalidElementId;
            var breaks    = Vm.BreakElementIds.ToList();
            var rules     = Vm.BuildRules();
            var viewId    = _previewViewId;

            DetachPreview();
            Hide();

            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                try
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var allBreaks = ResolveEffectiveBreaks(doc, selection, start, breaks, rules);
                    var painter   = new SpoolerPreviewPainter(doc);
                    painter.ApplyColors(viewId, selection, start, allBreaks);
                }
                catch { /* preview is best-effort */ }

                Dispatcher.Invoke(() =>
                {
                    AttachPreviewIfReady();
                    Show();
                    Activate();
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        /// <summary>Unions manual breaks with rule-detected breaks via
        /// <see cref="SpoolerRuleEvaluator.ComputeBreaks"/>. When no rules
        /// are enabled this just returns the manual list unchanged —
        /// keeps the painter call site simple regardless of rule state.</summary>
        private static IReadOnlyCollection<ElementId> ResolveEffectiveBreaks(
            Document doc,
            IReadOnlyCollection<ElementId> selection,
            ElementId start,
            IReadOnlyCollection<ElementId> manualBreaks,
            AutoSplitRules? rules)
        {
            if (rules == null || !rules.Any) return manualBreaks;
            try
            {
                return SpoolerRuleEvaluator.ComputeBreaks(doc, selection, start, manualBreaks, rules);
            }
            catch
            {
                // If the evaluator blows up, fall back to manual breaks so
                // the preview still paints something.
                return manualBreaks;
            }
        }

        /// <summary>Persists The Spooler's user-typed fields back to
        /// <see cref="SpoolSettings"/>, then deletes the throwaway 3D
        /// preview view. Loads existing settings first and modifies
        /// only the Spooler-specific keys, so the Create Spool fields
        /// stay intact. Detaching the PreviewControl must run BEFORE
        /// the view is deleted so the control isn't pointing at a
        /// dead view when it tears down.</summary>
        private void OnDialogClosed()
        {
            DetachPreview();

            // Snapshot before queueing — closure must not depend on live UI.
            var id     = Vm.Identifier;
            var snt    = Vm.SpoolNumberTemplate;
            var nm     = Vm.SpoolNameTemplate;
            var seq    = Vm.StartingSequence;
            var shn    = Vm.StartingSheetNumber;
            var rfw    = Vm.RuleAtFieldWelds;
            var rwe    = Vm.RuleMaxWeightEnabled;
            var rwt    = Vm.RuleMaxWeightLbText;
            var rle    = Vm.RuleMaxLengthEnabled;
            var rlt    = Vm.RuleMaxLengthText;
            var uas    = Vm.UseAssemblies;
            var cwfw   = Vm.ConvertSplitWeldsToFieldWelds;
            var ren    = Vm.RenumberEnabled;
            var renStart = Vm.RenumberStartingNumber ?? 1;
            var renSame  = Vm.RenumberUseSameForIdentical;
            var renLen   = Vm.RenumberUseLengthAsSeparator;
            var incWelds = Vm.IncludeWelds;
            var viewId = _previewViewId;
            _previewViewId = null;

            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Settings save — best-effort, runs whether the user
                // created a batch or just closed the window.
                try
                {
                    var existing = SpoolSettings.Load(doc);
                    existing.SpoolerIdentifier          = id;
                    existing.SpoolerNumberTemplate      = snt;
                    existing.SpoolerNameTemplate        = nm;
                    existing.SpoolerStartingSequence    = seq;
                    existing.SpoolerStartingSheetNumber = shn;
                    existing.SpoolerRuleAtFieldWelds    = rfw;
                    existing.SpoolerRuleMaxWeightEnabled = rwe;
                    existing.SpoolerRuleMaxWeightLbText  = rwt;
                    existing.SpoolerRuleMaxLengthEnabled = rle;
                    existing.SpoolerRuleMaxLengthText    = rlt;
                    existing.UseAssemblies               = uas;
                    existing.SpoolerConvertSplitWeldsToFieldWelds = cwfw;
                    existing.RenumberEnabled              = ren;
                    existing.RenumberStartingNumber       = renStart;
                    existing.RenumberUseSameForIdentical  = renSame;
                    existing.RenumberUseLengthAsSeparator = renLen;
                    existing.IncludeWelds                 = incWelds;

                    using var tx = new Transaction(doc, "Spooler: save settings on close");
                    tx.Start();
                    SpoolSettings.Save(doc, existing);
                    tx.Commit();
                }
                catch { /* best-effort */ }

                // Delete the throwaway 3D preview view. Orphan sweep on
                // next SpoolerCommand launch catches anything left over.
                if (viewId != null)
                {
                    try
                    {
                        if (doc.GetElement(viewId) != null)
                        {
                            using var tx = new Transaction(doc, "Spooler: delete preview view");
                            tx.Start();
                            doc.Delete(viewId);
                            tx.Commit();
                        }
                    }
                    catch { }
                }
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        // ── Pool management (mirrors Create Spool's Selection area) ───────────

        private void PickMore_Click(object sender, RoutedEventArgs e)
        {
            DetachPreview();
            Hide();
            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc   = uiDoc.Document;
                var combined = new HashSet<ElementId>(Vm.Selection);

                try
                {
                    var refs = uiDoc.Selection.PickObjects(
                        ObjectType.Element,
                        new FabricationPartFilter(),
                        "Pick fabrication parts for The Spooler's walk. Click Finish on the ribbon when done.");
                    foreach (var r in refs)
                    {
                        var el = doc.GetElement(r);
                        if (el != null) combined.Add(el.Id);
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { /* Esc */ }
                catch (OperationCanceledException) { /* defensive */ }

                uiDoc.Selection.SetElementIds(new List<ElementId>());
                var ids = combined.ToList();

                _previewViewId = SyncSpoolerPreviewViewOnRevitThread(doc, ids);

                Dispatcher.Invoke(() =>
                {
                    Vm.SetSelection(ids);
                    AttachPreviewIfReady();
                    Show();
                    Activate();
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        private void ResetSelection_Click(object sender, RoutedEventArgs e)
        {
            DetachPreview();
            Hide();
            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument.Document;
                _previewViewId = SyncSpoolerPreviewViewOnRevitThread(doc, Array.Empty<ElementId>());
                Dispatcher.Invoke(() =>
                {
                    Vm.SetSelection(new List<ElementId>());
                    AttachPreviewIfReady();
                    Show();
                    Activate();
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        private ElementId? SyncSpoolerPreviewViewOnRevitThread(
            Document doc, IReadOnlyCollection<ElementId> ids)
        {
            using var tx = new Transaction(doc, "Spooler: refresh preview view");
            tx.Start();
            if (_previewViewId != null)
            {
                try { doc.Delete(_previewViewId); } catch { /* already gone */ }
            }
            ElementId? newId = null;
            if (ids.Count > 0)
            {
                try
                {
                    var builder = new SpoolViewBuilder(doc);
                    var id = builder.CreatePreviewView(
                        ids, "TMP_SPOOLER_PREVIEW_" + DateTime.Now.Ticks);
                    if (id != ElementId.InvalidElementId) newId = id;
                }
                catch { /* preview is optional */ }
            }
            tx.Commit();
            return newId;
        }

        // ── Pick handlers ──────────────────────────────────────────────────────

        private void PickStart_Click(object sender, RoutedEventArgs e)
        {
            // Snapshot VM state on the UI thread before going Revit-side.
            // The paint will run while the dialog is hidden (so Revit's
            // render pipeline is active) and needs these values.
            var selection = Vm.Selection.ToList();
            var breaks    = Vm.BreakElementIds.ToList();
            var rules     = Vm.BuildRules();
            var viewId    = _previewViewId;

            // PreviewControl locks Revit's viewport input while it's
            // hosting a view — must detach before any Pick* call. Hide()
            // also brings Revit's main window forward so its renderer is
            // active for the inline paint below.
            DetachPreview();
            Hide();
            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                var uiDoc = uiApp.ActiveUIDocument;
                ElementId? picked = null;
                try
                {
                    var r = uiDoc.Selection.PickObject(
                        ObjectType.Element,
                        new FabricationPartFilter(),
                        "Pick the START element of the spooler run (single click).");
                    var el = uiDoc.Document.GetElement(r);
                    if (el != null) picked = el.Id;
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { /* Esc */ }
                catch (OperationCanceledException) { /* defensive */ }

                uiDoc.Selection.SetElementIds(new List<ElementId>());

                // Paint inline while the dialog is still hidden and
                // Revit's main window is in the foreground — its render
                // pipeline picks up the override changes immediately,
                // so when the dialog re-shows the colors are already
                // visible. This is the only reliable way; doing the
                // paint AFTER the dialog re-shows leaves Revit's main
                // window in background and its renderer pauses, which
                // is why earlier auto-refresh / manual-button attempts
                // didn't show colors until the user clicked Revit in
                // the taskbar.
                if (picked != null && viewId != null)
                {
                    try
                    {
                        var doc = uiDoc.Document;
                        var allBreaks = ResolveEffectiveBreaks(doc, selection, picked, breaks, rules);
                        var painter   = new SpoolerPreviewPainter(doc);
                        painter.ApplyColors(viewId, selection, picked, allBreaks);
                    }
                    catch { /* preview is best-effort */ }
                }

                Dispatcher.Invoke(() =>
                {
                    if (picked != null) Vm.SetStartElement(picked);
                    AttachPreviewIfReady();
                    Show();
                    Activate();
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        private void PickBreaks_Click(object sender, RoutedEventArgs e)
        {
            var selection = Vm.Selection.ToList();
            var start     = Vm.StartElementId ?? ElementId.InvalidElementId;
            var rules     = Vm.BuildRules();
            var viewId    = _previewViewId;

            DetachPreview();
            Hide();
            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var ids = new List<ElementId>();
                try
                {
                    var refs = uiDoc.Selection.PickObjects(
                        ObjectType.Element,
                        new FabricationPartFilter(),
                        "Pick BREAK elements — each picked part is the LAST part of its spool. " +
                        "Click Finish on the ribbon when done (or pick none for a single spool).");
                    foreach (var r in refs)
                    {
                        var el = uiDoc.Document.GetElement(r);
                        if (el != null) ids.Add(el.Id);
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { /* Esc */ }
                catch (OperationCanceledException) { /* defensive */ }

                uiDoc.Selection.SetElementIds(new List<ElementId>());

                // Paint inline before reshowing the dialog (see PickStart_Click
                // for the why — Revit's render needs the main window in the
                // foreground to update the embedded preview view).
                if (viewId != null && start != ElementId.InvalidElementId)
                {
                    try
                    {
                        var doc = uiDoc.Document;
                        var allBreaks = ResolveEffectiveBreaks(doc, selection, start, ids, rules);
                        var painter   = new SpoolerPreviewPainter(doc);
                        painter.ApplyColors(viewId, selection, start, allBreaks);
                    }
                    catch { }
                }

                Dispatcher.Invoke(() =>
                {
                    Vm.SetBreakElements(ids);
                    AttachPreviewIfReady();
                    Show();
                    Activate();
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        // ── Footer ─────────────────────────────────────────────────────────────

        /// <summary>"Spool Config…" shortcut — opens the shared
        /// SpoolConfigDialog. Modeless so it floats alongside this
        /// dialog; this dialog stays open so the user can come back to
        /// in-progress per-run state (selection, breaks, start). On
        /// Save we re-read the persisted store and rebind this
        /// dialog's shared fields (batch templates + Use Assemblies)
        /// via <see cref="SpoolerDialogVm.ReloadSharedDefaultsFromSettings"/>
        /// so the freshly-saved defaults take effect without forcing
        /// the user to close + reopen The Spooler.</summary>
        private void SpoolConfig_Click(object sender, RoutedEventArgs e)
        {
            SpoolTools.SpoolConfigCommand.OpenConfig(_uiDoc, onSaved: ReloadDefaults);
        }

        /// <summary>Reads the freshly-saved SpoolSettings off the Revit
        /// thread, then dispatches onto the WPF thread to rebind the VM.</summary>
        private void ReloadDefaults()
        {
            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var freshSettings = SpoolSettings.Load(doc);
                Dispatcher.Invoke(() =>
                {
                    Vm.ReloadSharedDefaultsFromSettings(freshSettings);
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            if (!Vm.CanCreate) return;

            var req = Vm.BuildBatchRequest();
            Vm.StatusText = "Running batch…";
            // PreviewControl interferes with bulk document modification
            // (re-renders on every tx commit), so detach for the batch.
            DetachPreview();
            Hide();

            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                SpoolerBatchResult res;
                try
                {
                    var svc = new SpoolerService(uiApp.ActiveUIDocument);
                    res = svc.RunBatch(req);
                }
                catch (Exception ex)
                {
                    res = new SpoolerBatchResult
                    {
                        Success = false,
                        Message = "Unhandled error: " + ex.Message + "\n\n" + ex.StackTrace,
                    };
                }

                Dispatcher.Invoke(() =>
                {
                    ShowBatchSummary(res);
                    if (res.Success)
                    {
                        Close();
                    }
                    else
                    {
                        Vm.StatusText = "Failed — adjust and try again.";
                        Show();
                        Activate();
                    }
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        /// <summary>Renders the batch result as a TaskDialog. Critical
        /// content (created spools list, unconnected counts, skipped
        /// numbers, warnings) lands in the main body. Informational log
        /// items (e.g., per-spool Include-Welds skip counts) go behind
        /// "Show details" so the summary stays clean unless the user
        /// asks for more. When unconnected parts exist, an interactive
        /// command link lets the user highlight + zoom to them in the
        /// active view via <c>uiDoc.ShowElements</c> +
        /// <c>Selection.SetElementIds</c>.</summary>
        private void ShowBatchSummary(SpoolerBatchResult res)
        {
            var sb = new StringBuilder();

            if (res.CreatedSpools.Count > 0)
            {
                sb.AppendLine("Created:");
                foreach (var s in res.CreatedSpools)
                {
                    sb.Append($"  {s.SpoolNumber}");
                    if (!string.IsNullOrWhiteSpace(s.Service)) sb.Append($" [{s.Service}]");
                    sb.Append($"  →  Sheet {s.SheetNumber}");
                    sb.AppendLine($"  ({s.PartCount} part{(s.PartCount == 1 ? "" : "s")})");
                }
                sb.AppendLine();
            }

            if (res.Unconnected.Count > 0)
            {
                sb.AppendLine($"Unconnected parts: {res.Unconnected.Count}");
                sb.AppendLine("  (in the selection but not visited by the walk — disconnected, " +
                              "or blocked by a missing connector. They were NOT spooled.)");
                sb.AppendLine();
            }

            if (res.SkippedSheetNumbers.Count > 0)
            {
                sb.AppendLine($"Sheet numbers skipped (already in use): " +
                              $"{string.Join(", ", res.SkippedSheetNumbers)}");
                sb.AppendLine();
            }
            if (res.SkippedSpoolNumbers.Count > 0)
            {
                sb.AppendLine($"Spool numbers skipped (already in use): " +
                              $"{string.Join(", ", res.SkippedSpoolNumbers)}");
                sb.AppendLine();
            }
            if (res.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (var w in res.Warnings) sb.AppendLine("  - " + w);
            }

            string title = res.Success ? "The Spooler — Success" : "The Spooler — Failed";
            var dlg = new TaskDialog(title)
            {
                MainInstruction = res.Message,
                MainContent     = sb.ToString().TrimEnd(),
                CommonButtons   = TaskDialogCommonButtons.Close,
            };
            if (res.Log.Count > 0)
                dlg.ExpandedContent = string.Join("\n", res.Log);

            // Interactive command link for unconnected parts. TaskDialog
            // is modal — when the user clicks the link, .Show() returns
            // with CommandLink1 and we queue an ExternalEvent to do the
            // actual ShowElements + SetElementIds on the Revit thread
            // (those calls have to run there, not on the WPF dispatcher).
            if (res.Unconnected.Count > 0)
            {
                dlg.AddCommandLink(
                    TaskDialogCommandLinkId.CommandLink1,
                    $"Show the {res.Unconnected.Count} unconnected part(s) in the model",
                    "Highlights them in the active view and zooms to fit. Use to decide " +
                    "whether to fix the model and re-run, or ignore them.");
            }

            var result = dlg.Show();
            if (result == TaskDialogResult.CommandLink1 && res.Unconnected.Count > 0)
            {
                var ids = res.Unconnected.ToList();
                SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
                {
                    try
                    {
                        var uiDoc = uiApp.ActiveUIDocument;
                        uiDoc.ShowElements(ids);
                        uiDoc.Selection.SetElementIds(ids);
                    }
                    catch { /* best-effort UX */ }
                });
                SpoolToolsApp.SpoolEvent!.Raise();
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════

    public sealed class SpoolerDialogVm : INotifyPropertyChanged
    {
        private readonly Document _doc;

        public SpoolerDialogVm(Document doc)
        {
            _doc = doc;
            RefreshPreview();
        }

        // ── Templating ─────────────────────────────────────────────────────────

        private string _identifier = "001";
        public string Identifier
        {
            get => _identifier;
            set { if (SetField(ref _identifier, value ?? string.Empty)) RefreshPreview(); }
        }

        private string _spoolNumberTemplate = "{Service}-{ID}-{N:00}";
        public string SpoolNumberTemplate
        {
            get => _spoolNumberTemplate;
            set { if (SetField(ref _spoolNumberTemplate, value ?? string.Empty)) RefreshPreview(); }
        }

        private string _startingSequenceText = "1";
        public string StartingSequenceText
        {
            get => _startingSequenceText;
            set { if (SetField(ref _startingSequenceText, value ?? string.Empty)) RefreshPreview(); }
        }
        public int StartingSequence =>
            int.TryParse(_startingSequenceText, out int n) ? n : 1;

        private string _spoolNameTemplate = "Spool {Number}";
        public string SpoolNameTemplate
        {
            get => _spoolNameTemplate;
            set { if (SetField(ref _spoolNameTemplate, value ?? string.Empty)) RefreshPreview(); }
        }

        private string _startingSheetNumber = "S1";
        public string StartingSheetNumber
        {
            get => _startingSheetNumber;
            set { if (SetField(ref _startingSheetNumber, value ?? string.Empty)) RefreshPreview(); }
        }

        // ── Renumber (per-spool "Item Number" rewrite) ────────────────────────

        private bool _renumberEnabled;
        public bool RenumberEnabled
        {
            get => _renumberEnabled;
            set
            {
                if (SetField(ref _renumberEnabled, value))
                    OnPropertyChanged(nameof(RenumberVisible));
            }
        }

        public System.Windows.Visibility RenumberVisible =>
            _renumberEnabled ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        private string _renumberStartingNumberText = "1";
        public string RenumberStartingNumberText
        {
            get => _renumberStartingNumberText;
            set => SetField(ref _renumberStartingNumberText, value ?? "1");
        }

        private bool _renumberUseSameForIdentical = true;
        public bool RenumberUseSameForIdentical
        {
            get => _renumberUseSameForIdentical;
            set => SetField(ref _renumberUseSameForIdentical, value);
        }

        private bool _renumberUseLengthAsSeparator;
        public bool RenumberUseLengthAsSeparator
        {
            get => _renumberUseLengthAsSeparator;
            set => SetField(ref _renumberUseLengthAsSeparator, value);
        }

        private bool _includeWelds = true;
        public bool IncludeWelds
        {
            get => _includeWelds;
            set => SetField(ref _includeWelds, value);
        }

        public int? RenumberStartingNumber
            => int.TryParse(_renumberStartingNumberText, out var n) && n > 0 ? n : null;

        public SpoolRenumberOptions? BuildRenumberOptions()
        {
            if (!_renumberEnabled) return null;
            return new SpoolRenumberOptions
            {
                StartingNumber       = RenumberStartingNumber ?? 1,
                UseSameForIdentical  = _renumberUseSameForIdentical,
                UseLengthAsSeparator = _renumberUseLengthAsSeparator,
            };
        }

        // ── Selection (pre-loaded from Revit at command launch) ───────────────

        public List<ElementId> Selection { get; private set; } = new();
        public bool HasSelection => Selection.Count > 0;
        public void SetSelection(IReadOnlyList<ElementId> ids)
        {
            Selection = ids?.ToList() ?? new List<ElementId>();
            if (_startElementId != null && !Selection.Contains(_startElementId))
            {
                _startElementId = null;
                ServiceAbbreviation = null;
                ServiceName = null;
                OnPropertyChanged(nameof(StartElementId));
                OnPropertyChanged(nameof(StartStatus));
                OnPropertyChanged(nameof(StartStatusBrush));
                OnPropertyChanged(nameof(ServiceDisplay));
                OnPropertyChanged(nameof(ServiceDisplayBrush));
            }
            if (BreakElementIds.RemoveAll(id => !Selection.Contains(id)) > 0)
            {
                OnPropertyChanged(nameof(BreakElementIds));
                OnPropertyChanged(nameof(BreaksStatus));
                OnPropertyChanged(nameof(BreaksStatusBrush));
            }
            OnPropertyChanged(nameof(SelectionSummary));
            OnPropertyChanged(nameof(SelectionSummaryBrush));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanCreate));
        }

        public string SelectionSummary =>
            Selection.Count == 0
                ? "No fabrication parts selected. Use 'Pick More' to choose them."
                : $"{Selection.Count} fabrication part(s) selected. Walk stays inside this set.";
        public Brush SelectionSummaryBrush =>
            Selection.Count == 0
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33));

        // ── Picked elements + service detection ────────────────────────────────

        private ElementId? _startElementId;
        public ElementId? StartElementId => _startElementId;

        /// <summary>Abbreviation of the start element's Fabrication Service,
        /// resolved at Pick start time. Surfaced prominently in the dialog
        /// so the user can leave + return without losing context.</summary>
        public string? ServiceAbbreviation { get; private set; }
        public string? ServiceName         { get; private set; }

        public void SetStartElement(ElementId id)
        {
            _startElementId = id;
            (ServiceAbbreviation, ServiceName) = FabricationServiceLookup.Resolve(_doc, id);
            OnPropertyChanged(nameof(StartElementId));
            OnPropertyChanged(nameof(StartStatus));
            OnPropertyChanged(nameof(StartStatusBrush));
            OnPropertyChanged(nameof(ServiceDisplay));
            OnPropertyChanged(nameof(ServiceDisplayBrush));
            OnPropertyChanged(nameof(CanCreate));
            RefreshPreview();
        }

        public List<ElementId> BreakElementIds { get; private set; } = new();
        public void SetBreakElements(List<ElementId> ids)
        {
            BreakElementIds = ids ?? new List<ElementId>();
            OnPropertyChanged(nameof(BreaksStatus));
            OnPropertyChanged(nameof(BreaksStatusBrush));
            OnPropertyChanged(nameof(CanCreate));
            RefreshPreview();
        }

        // ── Auto-Split Rules ───────────────────────────────────────────────────

        /// <summary>Fires whenever a rule changes so the dialog can
        /// trigger a repaint of the 3D preview. The dialog subscribes
        /// to this in its constructor; VM doesn't know about the
        /// repaint mechanics itself.</summary>
        public event Action? RulesChanged;

        private bool _ruleAtFieldWelds;
        public bool RuleAtFieldWelds
        {
            get => _ruleAtFieldWelds;
            set { if (SetField(ref _ruleAtFieldWelds, value)) { RefreshPreview(); RulesChanged?.Invoke(); } }
        }

        private bool _ruleMaxWeightEnabled;
        public bool RuleMaxWeightEnabled
        {
            get => _ruleMaxWeightEnabled;
            set { if (SetField(ref _ruleMaxWeightEnabled, value)) { RefreshPreview(); RulesChanged?.Invoke(); } }
        }

        private string _ruleMaxWeightLbText = "1000";
        public string RuleMaxWeightLbText
        {
            get => _ruleMaxWeightLbText;
            set { if (SetField(ref _ruleMaxWeightLbText, value ?? string.Empty)) { RefreshPreview(); RulesChanged?.Invoke(); } }
        }

        private bool _ruleMaxLengthEnabled;
        public bool RuleMaxLengthEnabled
        {
            get => _ruleMaxLengthEnabled;
            set { if (SetField(ref _ruleMaxLengthEnabled, value)) { RefreshPreview(); RulesChanged?.Invoke(); } }
        }

        private string _ruleMaxLengthText = "20";
        private bool   _useAssemblies;

        /// <summary>When on, each spool partition becomes a Revit
        /// AssemblyInstance with assembly views + sheet, instead of
        /// ad-hoc 3D views on a normal sheet. Shared with Create Spool
        /// via the same SpoolSettings flag.</summary>
        public bool UseAssemblies
        {
            get => _useAssemblies;
            set => SetField(ref _useAssemblies, value);
        }

        private bool _convertSplitWeldsToFieldWelds;
        /// <summary>When on, isolated welds (welds that would otherwise
        /// be the sole part of a spool) get "Field Weld" written to
        /// their Comments parameter before being merged into the next
        /// spool. The merge happens unconditionally — this flag only
        /// controls the relabel.</summary>
        public bool ConvertSplitWeldsToFieldWelds
        {
            get => _convertSplitWeldsToFieldWelds;
            set => SetField(ref _convertSplitWeldsToFieldWelds, value);
        }
        public string RuleMaxLengthText
        {
            get => _ruleMaxLengthText;
            set { if (SetField(ref _ruleMaxLengthText, value ?? string.Empty)) { RefreshPreview(); RulesChanged?.Invoke(); } }
        }

        /// <summary>Snapshots the VM's rule state into the
        /// <see cref="AutoSplitRules"/> object the evaluator consumes.
        /// Returns null when no rule is enabled (so callers can skip
        /// the evaluator entirely).</summary>
        public AutoSplitRules? BuildRules()
        {
            var rules = new AutoSplitRules
            {
                AtFieldWelds = _ruleAtFieldWelds,
                MaxWeightLb  = _ruleMaxWeightEnabled  && double.TryParse(_ruleMaxWeightLbText, out var w) && w > 0
                    ? w : (double?)null,
                MaxLengthFt  = _ruleMaxLengthEnabled  && ParseFeetInches(_ruleMaxLengthText) is double ft && ft > 0
                    ? ft : (double?)null,
            };
            return rules.Any ? rules : null;
        }

        /// <summary>Parses an "ft-inches" input. Accepts decimal feet
        /// (<c>10.5</c>), feet-inches with dash (<c>10-6</c>), and the
        /// canonical Imperial form (<c>10'-6"</c>). Returns null on
        /// empty / unparseable input.</summary>
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
            {
                return feet + inches / 12.0;
            }
            return null;
        }

        public string StartStatus =>
            _startElementId == null
                ? "(not picked — required)"
                : $"✓ picked (id {_startElementId.Value})";
        public Brush StartStatusBrush =>
            _startElementId == null
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x2E));

        /// <summary>Bold display string for the Service line — abbreviation
        /// first, full name in parens. Shows "(pick start to detect)" until
        /// resolved so the user knows where the value will appear.</summary>
        public string ServiceDisplay
        {
            get
            {
                if (_startElementId == null) return "(pick start to detect)";
                if (string.IsNullOrWhiteSpace(ServiceAbbreviation) &&
                    string.IsNullOrWhiteSpace(ServiceName))
                    return "(service not found on start element)";
                if (!string.IsNullOrWhiteSpace(ServiceAbbreviation) &&
                    !string.IsNullOrWhiteSpace(ServiceName))
                    return $"{ServiceAbbreviation}  —  {ServiceName}";
                return ServiceAbbreviation ?? ServiceName ?? string.Empty;
            }
        }
        public Brush ServiceDisplayBrush =>
            _startElementId == null
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x99, 0x99, 0x99))
                : !string.IsNullOrWhiteSpace(ServiceAbbreviation) || !string.IsNullOrWhiteSpace(ServiceName)
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1B, 0x8E, 0x2F))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD2, 0x69, 0x1E));

        public string BreaksStatus =>
            BreakElementIds.Count == 0
                ? "(none — entire walk becomes one spool)"
                : $"✓ {BreakElementIds.Count} break(s) — {BreakElementIds.Count + 1} spool(s) on main flow";
        public Brush BreaksStatusBrush =>
            BreakElementIds.Count == 0
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x77, 0x77, 0x77))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x2E));

        // ── Preview ────────────────────────────────────────────────────────────

        private string _previewText = string.Empty;
        public string PreviewText
        {
            get => _previewText;
            private set => SetField(ref _previewText, value);
        }

        /// <summary>Rebuilds the live preview block by running the actual
        /// template engine against the current inputs. Compact 2-line
        /// format — spool numbers on one line, sheet numbers on the
        /// next — so the user can sanity-check the templates without
        /// the preview block dominating the dialog. Uses the detected
        /// service abbreviation if Start has been picked; falls back to
        /// the literal "{Service}" placeholder when it hasn't so the
        /// user sees exactly which token will resolve at batch time.</summary>
        private void RefreshPreview()
        {
            const int PreviewCount = 3;

            string svcForPreview     = string.IsNullOrWhiteSpace(ServiceAbbreviation) ? "{Service}"     : ServiceAbbreviation;
            string svcNameForPreview = string.IsNullOrWhiteSpace(ServiceName)         ? "{ServiceName}" : ServiceName;

            var numbers = new List<string>(PreviewCount);
            var names   = new List<string>(PreviewCount);
            int start = StartingSequence;
            for (int i = 0; i < PreviewCount; i++)
            {
                int seq = start + i;
                var numberCtx = new TemplateContext
                {
                    Service     = svcForPreview,
                    ServiceName = svcNameForPreview,
                    Identifier  = _identifier,
                    Sequence    = seq,
                };
                string number = SpoolerTemplateEngine.Resolve(_spoolNumberTemplate, numberCtx);
                numbers.Add(number);

                var nameCtx = new TemplateContext
                {
                    Service     = svcForPreview,
                    ServiceName = svcNameForPreview,
                    Identifier  = _identifier,
                    Sequence    = seq,
                    Number      = number,
                };
                names.Add(SpoolerTemplateEngine.Resolve(_spoolNameTemplate, nameCtx));
            }

            var sheets = SpoolerTemplateEngine
                .SheetNumberSequence(_startingSheetNumber ?? string.Empty, PreviewCount)
                .ToList();

            // Distinct name vs number? Only show the names line when the
            // template actually differs from "{Number}" → otherwise it's
            // redundant.
            bool nameSameAsNumber = numbers.Zip(names, (n, m) => n == m).All(b => b);

            var sb = new StringBuilder();
            sb.AppendLine($"Spool #s: {string.Join(", ", numbers)}, …");
            sb.AppendLine($"Sheet #s: {string.Join(", ", sheets)}, …");
            if (!nameSameAsNumber)
                sb.AppendLine($"Names:    {string.Join(", ", names)}, …");

            PreviewText = sb.ToString().TrimEnd();
        }

        // ── Status + validation ────────────────────────────────────────────────

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value);
        }

        public bool CanCreate =>
            Selection.Count > 0 &&
            _startElementId != null &&
            !string.IsNullOrWhiteSpace(_spoolNumberTemplate) &&
            !string.IsNullOrWhiteSpace(_startingSheetNumber) &&
            int.TryParse(_startingSequenceText, out _);

        // ── Batch request assembly ─────────────────────────────────────────────

        /// <summary>Re-applies the SHARED settings (batch templates +
        /// Use Assemblies) to the bound VM properties via their public
        /// setters — so the UI updates immediately after the user
        /// saves changes in Spool Config. Auto-split rules and the
        /// per-run picks (selection, breaks, start) are deliberately
        /// left alone so an in-progress batch setup isn't disrupted.</summary>
        public void ReloadSharedDefaultsFromSettings(SpoolSettings settings)
        {
            Identifier           = settings.SpoolerIdentifier          ?? "001";
            SpoolNumberTemplate  = settings.SpoolerNumberTemplate      ?? "{Service}-{ID}-{N:00}";
            SpoolNameTemplate    = settings.SpoolerNameTemplate        ?? "Spool {Number}";
            StartingSequenceText = settings.SpoolerStartingSequence.ToString();
            StartingSheetNumber  = settings.SpoolerStartingSheetNumber ?? "S1";
            UseAssemblies        = settings.UseAssemblies;

            // Rule values are shared with Spool Config — refresh so a
            // Spool Config save while this dialog is open isn't
            // clobbered by this dialog's own write-back on close.
            RuleMaxWeightEnabled = settings.SpoolerRuleMaxWeightEnabled;
            RuleMaxWeightLbText  = settings.SpoolerRuleMaxWeightLbText  ?? "1000";
            RuleMaxLengthEnabled = settings.SpoolerRuleMaxLengthEnabled;
            RuleMaxLengthText    = settings.SpoolerRuleMaxLengthText    ?? "20";
        }

        /// <summary>Snapshots the VM's current state into the batch request
        /// type consumed by <see cref="SpoolerService.RunBatch"/>.</summary>
        public SpoolerBatchRequest BuildBatchRequest()
        {
            return new SpoolerBatchRequest
            {
                Selection           = Selection.ToList(),
                Start               = _startElementId ?? ElementId.InvalidElementId,
                Breaks              = BreakElementIds.ToList(),
                Identifier          = _identifier ?? string.Empty,
                SpoolNumberTemplate = _spoolNumberTemplate ?? string.Empty,
                SpoolNameTemplate   = _spoolNameTemplate ?? string.Empty,
                StartingSequence    = StartingSequence,
                StartingSheetNumber = _startingSheetNumber ?? "S1",
                Rules               = BuildRules(),
                UseAssemblies       = _useAssemblies,
                ConvertSplitWeldsToFieldWelds = _convertSplitWeldsToFieldWelds,
                Renumber            = BuildRenumberOptions(),
                IncludeWelds        = _includeWelds,
            };
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
            OnPropertyChanged(nameof(CanCreate));
            return true;
        }
    }
}
