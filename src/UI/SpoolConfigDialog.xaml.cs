using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolTools.Revit;
using SpoolTools.Revit.Spooling;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace SpoolTools.UI
{
    /// <summary>
    /// Modeless top-level dialog for editing project-level spool defaults
    /// — the persisted <see cref="SpoolSettings"/> store shared between
    /// Create Spool and The Spooler. Launches from its own ribbon button
    /// (<c>SpoolConfigCommand</c>) and also from a "Spool Config…" link
    /// inside both per-run dialogs.
    ///
    /// Scope per user spec:
    ///   • Shared output defaults — titleblock + drawable region, schedule,
    ///     default view scale, default view directions, view template,
    ///     tag family, Include Welds, Use Assemblies, Interactive Tagging.
    ///   • Leader defaults (place leader / end style / length) — these
    ///     STILL show up as a per-run popup in Create Spool, because
    ///     leader needs vary by spool complexity, but the values seeded
    ///     there come from this dialog.
    ///   • The Spooler batch templates — identifier, spool # / name
    ///     templates, starting sequence + sheet number.
    ///
    /// Renumber preferences and auto-split rules are intentionally NOT
    /// exposed here — those are per-run concerns and stay in the per-run
    /// dialogs.
    ///
    /// Modeless + Topmost so it floats above Revit; Hides itself during
    /// the region picker so the picks can land on the live viewport.
    /// </summary>
    public partial class SpoolConfigDialog : Window
    {
        private readonly UIDocument _uiDoc;
        public SpoolConfigVm Vm { get; }

        /// <summary>Optional callback invoked AFTER a Save commits to
        /// ExtensibleStorage. Per-run dialogs use this to reload their
        /// bound values so the freshly-saved defaults take effect
        /// immediately without forcing the per-run dialog to be reopened.</summary>
        public Action? OnSaved { get; set; }

        public SpoolConfigDialog(
            UIDocument uiDoc,
            IReadOnlyList<TitleblockChoice>     titleblocks,
            IReadOnlyList<ScheduleChoice>       schedules,
            IReadOnlyList<TagFamilyChoice>      tagFamilies,
            IReadOnlyList<ViewTemplateChoice>   viewTemplates,
            IReadOnlyList<DimensionStyleChoice> dimensionStyles,
            IReadOnlyDictionary<long, TitleblockRegion> regions,
            IReadOnlyList<string>               statusParamCandidates,
            SpoolSettings settings)
        {
            InitializeComponent();
            _uiDoc = uiDoc;

            Vm = new SpoolConfigVm(
                titleblocks, schedules, tagFamilies, viewTemplates, dimensionStyles, regions,
                statusParamCandidates, settings);
            DataContext = Vm;

            // Cap height to the visible work area so the dialog never
            // ends up taller than the screen — matches the Create Spool
            // pattern that fixes a clipped title bar on small displays.
            ApplyWorkAreaHeightCap();
        }

        private void ApplyWorkAreaHeightCap()
        {
            try
            {
                var workArea = System.Windows.SystemParameters.WorkArea;
                double cap = workArea.Height - 20;
                if (cap > 0)
                {
                    MaxHeight = cap;
                    if (Height > cap) Height = cap;
                }
            }
            catch { /* defensive — SystemParameters can fail on remote sessions */ }
        }

        /// <summary>Iterates the WPF Application's open windows, hides
        /// any that are Topmost and currently Visible AND aren't THIS
        /// dialog, and returns the list so the caller can re-Show them
        /// when the picker finishes. Catches the "Spool Config opened
        /// from inside Create Spool / The Spooler" case where the
        /// per-run dialog also sits Topmost and would block the
        /// titleblock view's pick interactions.</summary>
        private List<Window> HideTopmostPeers()
        {
            var hidden = new List<Window>();
            try
            {
                foreach (Window w in System.Windows.Application.Current.Windows)
                {
                    if (w == this) continue;
                    if (!w.IsVisible) continue;
                    if (!w.Topmost) continue;
                    w.Hide();
                    hidden.Add(w);
                }
            }
            catch { /* best-effort — picker still proceeds with this dialog hidden */ }
            return hidden;
        }

        // ── Drawable region picker ─────────────────────────────────────────────

        /// <summary>Same 4-point picker flow Create Spool uses. Hides the
        /// dialog (so Revit's viewport is foreground), spins up a temp
        /// setup sheet with the chosen titleblock, walks the user
        /// through 4 PickPoint calls, persists the result via
        /// <see cref="SpoolTitleblockRegions"/>, deletes the temp sheet,
        /// and re-shows the dialog. Orphan sweep on the next
        /// SpoolCommand / SpoolConfigCommand launch is the safety net
        /// if any of this crashes partway.</summary>
        private void DefineRegion_Click(object sender, RoutedEventArgs e)
        {
            if (Vm.SelectedTitleblock == null) return;
            var tbId = Vm.SelectedTitleblock.Id;

            // Hide BOTH this dialog AND any per-run dialog that opened it
            // (Create Spool / The Spooler). Without that, the parent
            // dialog stays Topmost over the temp titleblock view and
            // the user can't actually sketch the four points.
            var hiddenParents = HideTopmostPeers();
            Hide();
            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                TitleblockRegion? saved = null;
                string error = string.Empty;

                var uiDoc = uiApp.ActiveUIDocument;
                var doc   = uiDoc.Document;

                ElementId? tempSheetId = null;
                View? prevActiveView = uiDoc.ActiveView;

                try
                {
                    using (var tx = new Transaction(doc, "Spool Config: temp setup sheet"))
                    {
                        tx.Start();
                        var tempSheet = ViewSheet.Create(doc, tbId);
                        tempSheet.SheetNumber = "TMP_SPOOL_RGN_" + DateTime.Now.Ticks;
                        try { tempSheet.Name = "Spool Region Setup (temporary)"; } catch { }
                        tempSheetId = tempSheet.Id;
                        tx.Commit();
                    }

                    uiDoc.ActiveView = doc.GetElement(tempSheetId) as View;
                    var pickSheet = doc.GetElement(tempSheetId) as View;

                    try
                    {
                        var v1 = uiDoc.Selection.PickPoint(
                            "Pick FIRST corner of the VIEW region (snapping available)");
                        SpoolRegionPickerHelper.DrawMarker(doc, pickSheet, v1);

                        var v2 = uiDoc.Selection.PickPoint(
                            "Pick OPPOSITE corner of the VIEW region");
                        SpoolRegionPickerHelper.DrawRectangle(doc, pickSheet, v1, v2);

                        var s1 = uiDoc.Selection.PickPoint(
                            "Pick FIRST corner of the SCHEDULE region");
                        SpoolRegionPickerHelper.DrawMarker(doc, pickSheet, s1);

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
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { /* Esc — skip save */ }
                    catch (OperationCanceledException) { /* defensive */ }

                    if (saved != null)
                    {
                        using var tx = new Transaction(doc, "Spool Config: save region");
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
                    if (tempSheetId != null)
                    {
                        try
                        {
                            using var tx = new Transaction(doc, "Spool Config: delete temp setup sheet");
                            tx.Start();
                            doc.Delete(tempSheetId);
                            tx.Commit();
                        }
                        catch { }
                    }
                    try
                    {
                        if (prevActiveView != null && doc.GetElement(prevActiveView.Id) != null)
                            uiDoc.ActiveView = prevActiveView;
                    }
                    catch { }
                }

                Dispatcher.Invoke(() =>
                {
                    if (saved != null) Vm.RegionPicked(saved);
                    Show();
                    Activate();
                    // Restore any parent dialogs we hid above so the
                    // user lands back where they were before the picker.
                    foreach (var w in hiddenParents) w.Show();
                    if (!string.IsNullOrEmpty(error))
                        TaskDialog.Show("Spool Config — Define Region", "Error defining region: " + error);
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        // ── Leader Settings popup ──────────────────────────────────────────────

        private void LeaderSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new LeaderSettingsDialog(
                Vm.PlaceLeader, Vm.LeaderEnd, Vm.LeaderLengthFt, Vm.TagOffsetInches)
            {
                Owner = this,
            };
            if (dlg.ShowDialog() == true)
            {
                Vm.PlaceLeader     = dlg.Vm.PlaceLeader;
                Vm.LeaderEnd       = dlg.Vm.LeaderEnd;
                Vm.LeaderLengthFt  = dlg.Vm.LeaderLengthFt;
                Vm.TagOffsetInches = dlg.Vm.TagOffsetInches;
            }
        }

        // ── Enhanced Tag Placement — Learn more popup ─────────────────────────

        private Window? _enhancedTagLearnMoreWindow;

        private void EnhancedTagLearnMore_Click(object sender, RoutedEventArgs e)
        {
            if (_enhancedTagLearnMoreWindow != null && _enhancedTagLearnMoreWindow.IsVisible)
            {
                _enhancedTagLearnMoreWindow.Activate();
                return;
            }

            const string body =
                "When ON, the placement engine tries hard to keep each tag " +
                "clear of the pipe and clear of every other tag, using the " +
                "shape of the part to pick a good starting direction.\n" +
                "\n" +
                "1) Candidate search — 24 directions × 3 tiers.\n" +
                "Before the first tag is placed on a view, every part's " +
                "projected footprint is cached. Each tag then tries a grid " +
                "of candidate positions:\n" +
                "\n" +
                "  • 24 compass directions at 15° increments around the " +
                "part.\n" +
                "  • 3 offset tiers — 1×, 1.5×, and 2× the Tag Offset value " +
                "from Leader Settings.\n" +
                "  • Ordering is tier-major: every direction is tried at " +
                "1× before any direction is tried at 1.5×, so tags stay as " +
                "close to the part as possible.\n" +
                "  • Shape-aware direction (see #2) is tried FIRST at each " +
                "tier, before the 24 compass directions.\n" +
                "\n" +
                "The first candidate that clears wins. \"Clears\" means:\n" +
                "\n" +
                "  • Tag bbox overlaps neighbouring part bboxes by ≤10%.\n" +
                "  • Tag bbox overlaps other already-placed tags by ≤10%.\n" +
                "  • The leader line from part to tag doesn't cross another " +
                "part or another tag (Liang–Barsky segment/bbox test).\n" +
                "\n" +
                "Tags are placed in priority order by Item Number so the " +
                "\"1\"s and \"2\"s of a view get the best real estate. If a " +
                "tag never finds a fully clear slot, the least-bad candidate " +
                "(lowest weighted overlap + leader-crossing penalty) wins " +
                "instead of the engine giving up.\n" +
                "\n" +
                "2) Shape-aware preferred direction.\n" +
                "Before the 24 compass directions, the engine looks at the " +
                "part's shape:\n" +
                "\n" +
                "  • Pipes — perpendicular to the pipe's axis (either side), " +
                "picking the side with less clutter.\n" +
                "  • Elbows — the INSIDE (concave) side of the bend, so the " +
                "outside stays free for the long dim line. The direction is " +
                "the sum of the two connector directions.\n" +
                "  • Tees — the bisector between the branch and the run, so " +
                "the tag lands in the open wedge instead of on top of any leg.\n" +
                "  • Other parts — no preference; the 24 compass directions " +
                "run as-is.\n" +
                "\n" +
                "3) Free-End leader anchor override.\n" +
                "When Leader Settings is set to Free End, pipes and elbows " +
                "anchor the leader endpoint at the part's centre instead of " +
                "letting Revit auto-pick the nearest surface — otherwise the " +
                "leader can attach to a weld or a connector face and the " +
                "tag reads as pointing at the fitting next door.\n" +
                "\n" +
                "When OFF, every tag goes one Tag Offset above its part on " +
                "the sheet regardless of crowding — the historical behaviour.";

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding                       = new Thickness(16, 14, 16, 12),
                Content = new TextBlock
                {
                    Text         = body,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize     = 12,
                },
            };
            var closeBtn = new Button
            {
                Content              = "Close",
                Padding              = new Thickness(18, 6, 18, 6),
                MinWidth             = 80,
                HorizontalAlignment  = System.Windows.HorizontalAlignment.Right,
                Margin               = new Thickness(0, 0, 14, 12),
            };
            var root = new DockPanel();
            DockPanel.SetDock(closeBtn, Dock.Bottom);
            root.Children.Add(closeBtn);
            root.Children.Add(scroll);

            var w = new Window
            {
                Owner                  = this,
                Title                  = "Enhanced Tag Placement",
                Width                  = 560,
                Height                 = 520,
                MinWidth               = 380,
                MinHeight              = 280,
                WindowStartupLocation  = WindowStartupLocation.CenterOwner,
                ResizeMode             = ResizeMode.CanResize,
                ShowInTaskbar          = false,
                Background             = System.Windows.Media.Brushes.White,
                Content                = root,
            };
            closeBtn.Click += (_, _) => w.Close();
            w.Closed       += (_, _) =>
            {
                if (ReferenceEquals(_enhancedTagLearnMoreWindow, w))
                    _enhancedTagLearnMoreWindow = null;
            };

            _enhancedTagLearnMoreWindow = w;
            w.Show();
        }

        // ── Footer ─────────────────────────────────────────────────────────────

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // Snapshot the VM's current values onto a fresh SpoolSettings
            // instance built from the existing on-disk state, so unknown
            // / unrelated keys (renumber prefs, auto-split rules etc.)
            // pass through unchanged.
            SpoolToolsApp.SpoolHandler!.SetAction(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument.Document;
                try
                {
                    var existing = SpoolSettings.Load(doc);
                    Vm.WriteTo(existing);

                    using var tx = new Transaction(doc, "Spool Config: save defaults");
                    tx.Start();
                    SpoolSettings.Save(doc, existing);
                    tx.Commit();

                    Dispatcher.Invoke(() =>
                    {
                        OnSaved?.Invoke();
                        Close();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        TaskDialog.Show("Spool Config", "Save failed: " + ex.Message);
                    });
                }
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // ViewModel
    // ═════════════════════════════════════════════════════════════════════════

    public sealed class SpoolConfigVm : INotifyPropertyChanged
    {
        private readonly Dictionary<long, TitleblockRegion> _regions;

        public SpoolConfigVm(
            IReadOnlyList<TitleblockChoice>     titleblocks,
            IReadOnlyList<ScheduleChoice>       schedules,
            IReadOnlyList<TagFamilyChoice>      tagFamilies,
            IReadOnlyList<ViewTemplateChoice>   viewTemplates,
            IReadOnlyList<DimensionStyleChoice> dimensionStyles,
            IReadOnlyDictionary<long, TitleblockRegion> regions,
            IReadOnlyList<string>               statusParamCandidates,
            SpoolSettings settings)
        {
            // Dimension style dropdown — prepend "(Revit default)" so
            // the user can clear a saved selection. Matches the
            // "(No template)" / "Do not place Tags" pattern used by
            // the other doc-derived dropdowns above.
            var dimList = new List<DimensionStyleChoice>
            {
                new DimensionStyleChoice(null, "(Revit default)"),
            };
            dimList.AddRange(dimensionStyles);
            DimensionStyles = dimList;
            _regions = new Dictionary<long, TitleblockRegion>(regions);

            // Status param dropdown — always-present "(none)" first so the
            // user can opt out of the status write entirely. Saved name
            // wins on selection; if it isn't in the project's text-param
            // list (e.g. param was deleted), we still show it so the
            // user sees what's persisted and can re-pick.
            var statusOptions = new List<string> { StatusParamNoneSentinel };
            statusOptions.AddRange(statusParamCandidates ?? Array.Empty<string>());
            if (!string.IsNullOrWhiteSpace(settings.SpoolStatusParamName)
                && !statusOptions.Contains(settings.SpoolStatusParamName, StringComparer.OrdinalIgnoreCase))
            {
                statusOptions.Add(settings.SpoolStatusParamName);
            }
            StatusParamOptions = statusOptions;

            // Static dropdowns shared with Create Spool.
            Scales = new List<ScaleChoice>
            {
                new ("Auto Fit",            null),
                new ("1\" = 1'-0\"",        12),
                new ("3/4\" = 1'-0\"",      16),
                new ("1/2\" = 1'-0\"",      24),
                new ("3/8\" = 1'-0\"",      32),
                new ("1/4\" = 1'-0\"",      48),
                new ("3/16\" = 1'-0\"",     64),
                new ("1/8\" = 1'-0\"",      96),
                new ("3/32\" = 1'-0\"",    128),
                new ("1/16\" = 1'-0\"",    192),
                new ("1/32\" = 1'-0\"",    384),
            };

            // Doc-derived dropdowns prepend a "(No template)" / "Do not place"
            // sentinel where applicable so the user can clear a value.
            Titleblocks   = titleblocks;
            Schedules     = schedules;
            TagFamilies   = new[] { new TagFamilyChoice(null, "Do not place Tags") }
                              .Concat(tagFamilies).ToList();
            ViewTemplates = new[] { new ViewTemplateChoice(null, "(No template)") }
                              .Concat(viewTemplates).ToList();

            // Seed selections + scalar settings from the saved store.
            // Each "find by id" falls back to the first item if the saved
            // id isn't loaded any more (e.g. titleblock family removed).
            SelectedTitleblock   = FindById(titleblocks,   settings.TitleblockTypeId);
            SelectedSchedule     = FindById(schedules,     settings.ScheduleId);
            SelectedTagFamily    = FindByIdNullable(TagFamilies,    settings.TagFamilyId);
            SelectedViewTemplate = FindByIdNullable(ViewTemplates,  settings.ViewTemplateId);
            SelectedScale        = Scales.FirstOrDefault(s => s.Denominator == settings.ScaleDenominator) ?? Scales[0];

            int mask = settings.DirectionMask;
            NwIsoChecked = (mask & (1 << (int)SpoolDirection.NwIso)) != 0;
            NeIsoChecked = (mask & (1 << (int)SpoolDirection.NeIso)) != 0;
            SwIsoChecked = (mask & (1 << (int)SpoolDirection.SwIso)) != 0;
            SeIsoChecked = (mask & (1 << (int)SpoolDirection.SeIso)) != 0;
            TopChecked   = (mask & (1 << (int)SpoolDirection.Top))   != 0;
            LeftChecked  = (mask & (1 << (int)SpoolDirection.Left))  != 0;
            RightChecked = (mask & (1 << (int)SpoolDirection.Right)) != 0;
            FrontChecked = (mask & (1 << (int)SpoolDirection.Front)) != 0;

            IncludeWelds       = settings.IncludeWelds;
            UseAssemblies      = settings.UseAssemblies;
            InteractiveTagging = settings.InteractiveTagging;

            PlaceLeader    = settings.PlaceLeader;
            LeaderEnd      = settings.LeaderEnd == 1 ? LeaderEndCondition.Free : LeaderEndCondition.Attached;
            LeaderLengthFt = settings.LeaderLengthFt;
            TagOffsetInches = settings.TagOffsetInches > 0 ? settings.TagOffsetInches : 1.0;

            Identifier           = settings.SpoolerIdentifier          ?? "001";
            SpoolNumberTemplate  = settings.SpoolerNumberTemplate      ?? "{Service}-{ID}-{N:00}";
            SpoolNameTemplate    = settings.SpoolerNameTemplate        ?? "Spool {Number}";
            StartingSequenceText = settings.SpoolerStartingSequence.ToString();
            StartingSheetNumber  = settings.SpoolerStartingSheetNumber ?? "S1";

            // Spool limits — shared with The Spooler's auto-split rules
            // (same SpoolSettings keys). Editing here changes the value
            // for both tools. Create Spool reads them as alert
            // thresholds; The Spooler reads them as batch-split
            // thresholds.
            MaxWeightEnabled = settings.SpoolerRuleMaxWeightEnabled;
            MaxWeightLbText  = settings.SpoolerRuleMaxWeightLbText  ?? "1000";
            MaxLengthEnabled = settings.SpoolerRuleMaxLengthEnabled;
            MaxLengthText    = settings.SpoolerRuleMaxLengthText    ?? "20";

            // Status param — initial selection picks the persisted name
            // when it's in the candidate list, otherwise the "(none)"
            // sentinel. Empty / null persisted → "(none)".
            SelectedStatusParam = string.IsNullOrWhiteSpace(settings.SpoolStatusParamName)
                ? StatusParamNoneSentinel
                : statusOptions.FirstOrDefault(
                    o => string.Equals(o, settings.SpoolStatusParamName, StringComparison.OrdinalIgnoreCase))
                  ?? StatusParamNoneSentinel;
            StatusParamValue = settings.SpoolStatusParamValue ?? string.Empty;

            // Dimensions — DimensionStyles already prepended "(Revit default)";
            // find saved id or fall through to the sentinel.
            SelectedDimensionStyle = settings.SpoolDimensionStyleId is long dsId
                ? DimensionStyles.FirstOrDefault(d => d.Id?.Value == dsId) ?? DimensionStyles.First()
                : DimensionStyles.First();
            IncludeDimensionsDefault = settings.SpoolIncludeDimensionsDefault;
            DimensionOffsetInchesText = settings.SpoolDimensionOffsetInches.ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture);
            EnhancedTagPlacement = settings.EnhancedTagPlacement;
        }

        // ── Static dropdowns ───────────────────────────────────────────────────

        public IReadOnlyList<TitleblockChoice>   Titleblocks   { get; }
        public IReadOnlyList<ScheduleChoice>     Schedules     { get; }
        public IReadOnlyList<TagFamilyChoice>    TagFamilies   { get; }
        public IReadOnlyList<ViewTemplateChoice> ViewTemplates { get; }
        public IReadOnlyList<ScaleChoice>        Scales        { get; }

        // ── Bound selections ───────────────────────────────────────────────────

        private TitleblockChoice? _selectedTitleblock;
        public TitleblockChoice? SelectedTitleblock
        {
            get => _selectedTitleblock;
            set
            {
                if (SetField(ref _selectedTitleblock, value))
                {
                    OnPropertyChanged(nameof(HasSelectedTitleblock));
                    OnPropertyChanged(nameof(RegionDefined));
                }
            }
        }
        public bool HasSelectedTitleblock => _selectedTitleblock != null;
        public bool RegionDefined =>
            _selectedTitleblock != null &&
            _regions.ContainsKey(_selectedTitleblock.Id.Value);

        private ScheduleChoice? _selectedSchedule;
        public ScheduleChoice? SelectedSchedule
        {
            get => _selectedSchedule;
            set => SetField(ref _selectedSchedule, value);
        }

        private ScaleChoice? _selectedScale;
        public ScaleChoice? SelectedScale
        {
            get => _selectedScale;
            set => SetField(ref _selectedScale, value);
        }

        private TagFamilyChoice? _selectedTagFamily;
        public TagFamilyChoice? SelectedTagFamily
        {
            get => _selectedTagFamily;
            set => SetField(ref _selectedTagFamily, value);
        }

        private ViewTemplateChoice? _selectedViewTemplate;
        public ViewTemplateChoice? SelectedViewTemplate
        {
            get => _selectedViewTemplate;
            set => SetField(ref _selectedViewTemplate, value);
        }

        // ── Direction toggles ──────────────────────────────────────────────────

        private bool _nwIso, _neIso, _swIso, _seIso, _top, _left, _right, _front;
        public bool NwIsoChecked { get => _nwIso; set => SetField(ref _nwIso, value); }
        public bool NeIsoChecked { get => _neIso; set => SetField(ref _neIso, value); }
        public bool SwIsoChecked { get => _swIso; set => SetField(ref _swIso, value); }
        public bool SeIsoChecked { get => _seIso; set => SetField(ref _seIso, value); }
        public bool TopChecked   { get => _top;   set => SetField(ref _top,   value); }
        public bool LeftChecked  { get => _left;  set => SetField(ref _left,  value); }
        public bool RightChecked { get => _right; set => SetField(ref _right, value); }
        public bool FrontChecked { get => _front; set => SetField(ref _front, value); }

        // ── Flags ──────────────────────────────────────────────────────────────

        private bool _includeWelds = true, _useAssemblies, _interactiveTagging;
        public bool IncludeWelds       { get => _includeWelds;       set => SetField(ref _includeWelds, value); }
        public bool UseAssemblies      { get => _useAssemblies;      set => SetField(ref _useAssemblies, value); }
        public bool InteractiveTagging { get => _interactiveTagging; set => SetField(ref _interactiveTagging, value); }

        // ── Leader defaults ────────────────────────────────────────────────────

        private bool _placeLeader;
        private LeaderEndCondition _leaderEnd = LeaderEndCondition.Attached;
        private double _leaderLengthFt;
        private double _tagOffsetInches = 1.0;

        public bool PlaceLeader
        {
            get => _placeLeader;
            set { if (SetField(ref _placeLeader, value)) OnPropertyChanged(nameof(LeaderSummary)); }
        }
        public LeaderEndCondition LeaderEnd
        {
            get => _leaderEnd;
            set { if (SetField(ref _leaderEnd, value)) OnPropertyChanged(nameof(LeaderSummary)); }
        }
        public double LeaderLengthFt
        {
            get => _leaderLengthFt;
            set { if (SetField(ref _leaderLengthFt, value)) OnPropertyChanged(nameof(LeaderSummary)); }
        }
        public double TagOffsetInches
        {
            get => _tagOffsetInches;
            set { if (SetField(ref _tagOffsetInches, value)) OnPropertyChanged(nameof(LeaderSummary)); }
        }

        /// <summary>Human-readable summary that mirrors what the Leader
        /// Settings popup is going to write back — gives the user
        /// at-a-glance feedback of the current default without
        /// re-opening the popup.</summary>
        public string LeaderSummary
        {
            get
            {
                if (!_placeLeader) return "Place leader: OFF (tag-only placement).";
                string end = _leaderEnd == LeaderEndCondition.Free ? "Free" : "Attached";
                if (_leaderLengthFt > 0)
                    return $"Place leader: ON · {end} end · {_leaderLengthFt * 12:0.##}\" length";
                return $"Place leader: ON · {end} end · no leader length set";
            }
        }

        // ── Batch templates ────────────────────────────────────────────────────

        private string _identifier            = "001";
        private string _spoolNumberTemplate   = "{Service}-{ID}-{N:00}";
        private string _spoolNameTemplate     = "Spool {Number}";
        private string _startingSequenceText  = "1";
        private string _startingSheetNumber   = "S1";

        public string Identifier           { get => _identifier;           set => SetField(ref _identifier,           value ?? string.Empty); }
        public string SpoolNumberTemplate  { get => _spoolNumberTemplate;  set => SetField(ref _spoolNumberTemplate,  value ?? string.Empty); }
        public string SpoolNameTemplate    { get => _spoolNameTemplate;    set => SetField(ref _spoolNameTemplate,    value ?? string.Empty); }
        public string StartingSequenceText { get => _startingSequenceText; set => SetField(ref _startingSequenceText, value ?? string.Empty); }
        public string StartingSheetNumber  { get => _startingSheetNumber;  set => SetField(ref _startingSheetNumber,  value ?? string.Empty); }

        // ── Spool limits (shared with The Spooler auto-split rules) ───────────

        private bool   _maxWeightEnabled;
        private string _maxWeightLbText  = "1000";
        private bool   _maxLengthEnabled;
        private string _maxLengthText    = "20";

        public bool   MaxWeightEnabled { get => _maxWeightEnabled; set => SetField(ref _maxWeightEnabled, value); }
        public string MaxWeightLbText  { get => _maxWeightLbText;  set => SetField(ref _maxWeightLbText,  value ?? string.Empty); }
        public bool   MaxLengthEnabled { get => _maxLengthEnabled; set => SetField(ref _maxLengthEnabled, value); }
        public string MaxLengthText    { get => _maxLengthText;    set => SetField(ref _maxLengthText,    value ?? string.Empty); }

        // ── Spool status (project text param + value) ─────────────────────────

        /// <summary>Sentinel for "do not write any status" — first entry
        /// in <see cref="StatusParamOptions"/>. When the user picks
        /// this, <see cref="WriteTo"/> persists an empty string and
        /// SpoolService skips the status write entirely.</summary>
        public const string StatusParamNoneSentinel = "(none — skip status write)";

        public IReadOnlyList<string> StatusParamOptions { get; }

        private string _selectedStatusParam = StatusParamNoneSentinel;
        public string SelectedStatusParam
        {
            get => _selectedStatusParam;
            set
            {
                if (SetField(ref _selectedStatusParam, value ?? StatusParamNoneSentinel))
                    OnPropertyChanged(nameof(StatusValueEnabled));
            }
        }

        private string _statusParamValue = string.Empty;
        public string StatusParamValue
        {
            get => _statusParamValue;
            set => SetField(ref _statusParamValue, value ?? string.Empty);
        }

        /// <summary>True when a real param is picked — drives the
        /// value TextBox's IsEnabled binding so the "value" field
        /// greys out when "(none)" is selected.</summary>
        public bool StatusValueEnabled =>
            !string.Equals(_selectedStatusParam, StatusParamNoneSentinel, StringComparison.Ordinal);

        // ── Dimensions ────────────────────────────────────────────────────────

        public IReadOnlyList<DimensionStyleChoice> DimensionStyles { get; }

        private DimensionStyleChoice? _selectedDimensionStyle;
        public DimensionStyleChoice? SelectedDimensionStyle
        {
            get => _selectedDimensionStyle;
            set => SetField(ref _selectedDimensionStyle, value);
        }

        private bool _includeDimensionsDefault;
        public bool IncludeDimensionsDefault
        {
            get => _includeDimensionsDefault;
            set => SetField(ref _includeDimensionsDefault, value);
        }

        private bool _enhancedTagPlacement;
        public bool EnhancedTagPlacement
        {
            get => _enhancedTagPlacement;
            set => SetField(ref _enhancedTagPlacement, value);
        }

        private string _dimensionOffsetInchesText = "6";
        public string DimensionOffsetInchesText
        {
            get => _dimensionOffsetInchesText;
            set => SetField(ref _dimensionOffsetInchesText, value ?? string.Empty);
        }

        // ── Status ─────────────────────────────────────────────────────────────

        private string _statusText = string.Empty;
        public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }

        // ── Region picker callback ─────────────────────────────────────────────

        public void RegionPicked(TitleblockRegion r)
        {
            _regions[r.TitleblockTypeId] = r;
            OnPropertyChanged(nameof(RegionDefined));
        }

        // ── Persistence ────────────────────────────────────────────────────────

        /// <summary>Snapshots the VM's current values onto an existing
        /// <see cref="SpoolSettings"/> so any keys this dialog doesn't
        /// expose (renumber prefs, auto-split rules, etc.) survive
        /// untouched.</summary>
        public void WriteTo(SpoolSettings s)
        {
            s.TitleblockTypeId = _selectedTitleblock?.Id.Value;
            s.ScheduleId       = _selectedSchedule?.Id.Value;
            s.TagFamilyId      = _selectedTagFamily?.Id?.Value;
            s.ViewTemplateId   = _selectedViewTemplate?.Id?.Value;
            s.ScaleDenominator = _selectedScale?.Denominator;

            int mask = 0;
            if (_nwIso) mask |= 1 << (int)SpoolDirection.NwIso;
            if (_neIso) mask |= 1 << (int)SpoolDirection.NeIso;
            if (_swIso) mask |= 1 << (int)SpoolDirection.SwIso;
            if (_seIso) mask |= 1 << (int)SpoolDirection.SeIso;
            if (_top)   mask |= 1 << (int)SpoolDirection.Top;
            if (_left)  mask |= 1 << (int)SpoolDirection.Left;
            if (_right) mask |= 1 << (int)SpoolDirection.Right;
            if (_front) mask |= 1 << (int)SpoolDirection.Front;
            s.DirectionMask = mask;

            s.IncludeWelds       = _includeWelds;
            s.UseAssemblies      = _useAssemblies;
            s.InteractiveTagging = _interactiveTagging;

            s.PlaceLeader    = _placeLeader;
            s.LeaderEnd      = _leaderEnd == LeaderEndCondition.Free ? 1 : 0;
            s.LeaderLengthFt = _leaderLengthFt;
            s.TagOffsetInches = _tagOffsetInches > 0 ? _tagOffsetInches : 1.0;

            s.SpoolerIdentifier          = _identifier ?? string.Empty;
            s.SpoolerNumberTemplate      = _spoolNumberTemplate ?? string.Empty;
            s.SpoolerNameTemplate        = _spoolNameTemplate ?? string.Empty;
            s.SpoolerStartingSequence    =
                int.TryParse(_startingSequenceText, out var n) ? n : 1;
            s.SpoolerStartingSheetNumber = _startingSheetNumber ?? "S1";

            // Spool limits — same SpoolSettings keys The Spooler reads
            // for its auto-split rules, so editing in either place
            // changes the shared default for both tools.
            s.SpoolerRuleMaxWeightEnabled = _maxWeightEnabled;
            s.SpoolerRuleMaxWeightLbText  = _maxWeightLbText ?? string.Empty;
            s.SpoolerRuleMaxLengthEnabled = _maxLengthEnabled;
            s.SpoolerRuleMaxLengthText    = _maxLengthText ?? string.Empty;

            // Status — sentinel maps to empty (= skip the write entirely).
            // SpoolService treats empty name as "user opted out".
            s.SpoolStatusParamName  = string.Equals(_selectedStatusParam, StatusParamNoneSentinel, StringComparison.Ordinal)
                ? string.Empty
                : (_selectedStatusParam ?? string.Empty);
            s.SpoolStatusParamValue = _statusParamValue ?? string.Empty;

            s.EnhancedTagPlacement = _enhancedTagPlacement;

            // Dimensions
            s.SpoolDimensionStyleId          = _selectedDimensionStyle?.Id?.Value;
            s.SpoolIncludeDimensionsDefault  = _includeDimensionsDefault;
            if (double.TryParse(_dimensionOffsetInchesText,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var dimOff) && dimOff > 0)
                s.SpoolDimensionOffsetInches = dimOff;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static TitleblockChoice? FindById(IReadOnlyList<TitleblockChoice> list, long? id)
        {
            if (id == null) return list.Count > 0 ? list[0] : null;
            return list.FirstOrDefault(c => c.Id.Value == id.Value) ?? (list.Count > 0 ? list[0] : null);
        }
        private static ScheduleChoice? FindById(IReadOnlyList<ScheduleChoice> list, long? id)
        {
            if (id == null) return list.Count > 0 ? list[0] : null;
            return list.FirstOrDefault(c => c.Id.Value == id.Value) ?? (list.Count > 0 ? list[0] : null);
        }
        private static TagFamilyChoice? FindByIdNullable(IReadOnlyList<TagFamilyChoice> list, long? id)
        {
            if (id == null) return list.FirstOrDefault(c => c.Id == null) ?? list.FirstOrDefault();
            return list.FirstOrDefault(c => c.Id != null && c.Id.Value == id.Value)
                ?? list.FirstOrDefault(c => c.Id == null);
        }
        private static ViewTemplateChoice? FindByIdNullable(IReadOnlyList<ViewTemplateChoice> list, long? id)
        {
            if (id == null) return list.FirstOrDefault(c => c.Id == null) ?? list.FirstOrDefault();
            return list.FirstOrDefault(c => c.Id != null && c.Id.Value == id.Value)
                ?? list.FirstOrDefault(c => c.Id == null);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Shared marker-drawing helpers used by both the Create Spool
    /// and Spool Config region pickers. The originals live as private
    /// methods inside <c>SpoolDialog</c>; pulling them out here lets
    /// both dialogs paint the same orange picker feedback on their temp
    /// setup sheets without duplicating ~30 lines of detail-line code.</summary>
    internal static class SpoolRegionPickerHelper
    {
        public static void DrawMarker(Document doc, View? view, XYZ point)
        {
            if (view == null) return;
            try
            {
                using var tx = new Transaction(doc, "Spool: paint pick marker");
                tx.Start();
                // Small X — two diagonals.
                const double size = 0.05;
                var p1 = new XYZ(point.X - size, point.Y - size, 0);
                var p2 = new XYZ(point.X + size, point.Y + size, 0);
                var p3 = new XYZ(point.X - size, point.Y + size, 0);
                var p4 = new XYZ(point.X + size, point.Y - size, 0);
                doc.Create.NewDetailCurve(view, Line.CreateBound(p1, p2));
                doc.Create.NewDetailCurve(view, Line.CreateBound(p3, p4));
                tx.Commit();
            }
            catch { /* picker feedback is best-effort */ }
        }

        public static void DrawRectangle(Document doc, View? view, XYZ a, XYZ b)
        {
            if (view == null) return;
            try
            {
                using var tx = new Transaction(doc, "Spool: paint pick rectangle");
                tx.Start();
                var c1 = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), 0);
                var c2 = new XYZ(Math.Max(a.X, b.X), Math.Min(a.Y, b.Y), 0);
                var c3 = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), 0);
                var c4 = new XYZ(Math.Min(a.X, b.X), Math.Max(a.Y, b.Y), 0);
                doc.Create.NewDetailCurve(view, Line.CreateBound(c1, c2));
                doc.Create.NewDetailCurve(view, Line.CreateBound(c2, c3));
                doc.Create.NewDetailCurve(view, Line.CreateBound(c3, c4));
                doc.Create.NewDetailCurve(view, Line.CreateBound(c4, c1));
                tx.Commit();
            }
            catch { /* picker feedback is best-effort */ }
        }
    }
}
