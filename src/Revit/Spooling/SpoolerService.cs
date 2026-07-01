using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>
    /// Batch orchestrator for The Spooler. Takes a configured request,
    /// walks the network via <see cref="SpoolerNetworkWalker"/>, resolves
    /// per-spool numbers + names through the template engine, then loops
    /// the existing per-spool <see cref="SpoolService"/> once per
    /// partition. Everything runs inside a single outer
    /// <see cref="TransactionGroup"/> so a mid-batch failure rolls the
    /// entire batch back as one undo step.
    ///
    /// Inherits ALL shared configuration (titleblock, schedule, scale,
    /// view directions, view template, tag family, leader settings,
    /// Include Welds, renumber prefs) from the project's persisted
    /// <see cref="SpoolSettings"/> store — the same store the
    /// single-spool dialog writes to. The Spooler dialog supplies only
    /// the batch-specific concerns (templates, start, breaks, identifier,
    /// starting sheet/sequence).
    ///
    /// Interactive tagging is forced OFF inside the batch — placing
    /// tags is auto only (no per-tag prompts) so the batch can run
    /// unattended.
    /// </summary>
    public sealed class SpoolerService
    {
        private readonly UIDocument _uiDoc;
        private readonly Document   _doc;

        public SpoolerService(UIDocument uiDoc)
        {
            _uiDoc = uiDoc;
            _doc   = uiDoc.Document;
        }

        public SpoolerBatchResult RunBatch(SpoolerBatchRequest req)
        {
            var result = new SpoolerBatchResult();

            if (req.Selection == null || req.Selection.Count == 0)
            {
                result.Success = false;
                result.Message = "No parts selected. Select the pipe run in Revit before launching The Spooler.";
                return result;
            }
            if (req.Start == ElementId.InvalidElementId)
            {
                result.Success = false;
                result.Message = "Start element is required.";
                return result;
            }

            // 1. Resolve the effective break set. Auto-split rules
            //    (field welds / max weight / max length) get unioned in
            //    with the user's manual break picks before walking.
            var effectiveBreaks = req.Rules != null && req.Rules.Any
                ? SpoolerRuleEvaluator.ComputeBreaks(_doc, req.Selection, req.Start, req.Breaks, req.Rules)
                : new List<ElementId>(req.Breaks);

            // 2. Walk the network.
            var walk = SpoolerNetworkWalker.Walk(_doc, req.Selection, req.Start, effectiveBreaks);
            result.Unconnected.AddRange(walk.Unconnected);
            if (walk.Spools.Count == 0)
            {
                result.Success = false;
                result.Message = "Network walk produced no spools — the START element is not in the selection, or its connectors lead nowhere reachable.";
                return result;
            }

            // 2a. Fold any single-weld partitions forward into the next
            //     spool (or backward when it's the last). A lone weld
            //     sheet is useless paperwork — the user wants every weld
            //     to ship on the same drawing as the spool it'll be
            //     welded to. In-memory rebuild; the relabel-as-Field-Weld
            //     parameter writes happen inside the group below.
            var foldedWeldIds = SpoolerWeldPostProcessor.MergeIsolatedWelds(_doc, walk);

            // 2. Load shared settings + validate the bits SpoolService requires.
            var settings = SpoolSettings.Load(_doc);
            if (settings.TitleblockTypeId == null)
            {
                result.Success = false;
                result.Message = "No titleblock has been configured. Open Create Spool, pick a titleblock + drawable region (so it's saved), then re-run The Spooler.";
                return result;
            }
            var directions = SpoolSettings.Decode(settings.DirectionMask);
            if (directions.Count == 0)
            {
                result.Success = false;
                result.Message = "No view directions have been configured. Open Create Spool, select at least one view direction, then re-run.";
                return result;
            }

            // 3. Pre-scan existing sheet + spool numbers (collision pre-scan).
            //    These sets grow as we create spools in this batch so later
            //    spools don't collide with earlier ones either.
            var takenSheets = ExistingSheetNumbers(_doc);
            var takenSpools = ExistingSpoolNumbers(_doc);
            var sheetGen    = new SheetNumberSequencer(req.StartingSheetNumber ?? "S1", takenSheets);

            // 4. Iterate partitions inside one TransactionGroup.
            using var group = new TransactionGroup(_doc, $"The Spooler: {walk.Spools.Count} spool(s)");
            group.Start();

            int seq = req.StartingSequence;
            try
            {
                // Swap every cross-spool weld for the Field Weld catalog
                // item when the user opted in. The target set is the
                // union of:
                //   • folded welds (originally isolated, merged into a
                //     downstream spool by MergeIsolatedWelds — these
                //     always span a boundary)
                //   • cross-spool welds (welds whose neighbour across
                //     a connector lives in a different partition —
                //     covers the LAST weld of one spool when it's
                //     connected to the FIRST part of the next, even
                //     without merging)
                // Both are deduped via HashSet; FindCrossSpoolWelds
                // already excludes welds whose Name contains "Field
                // Weld" so the operation is idempotent on re-runs.
                //
                // Runs inside the group so every delete + create +
                // position + reconnect rolls back with the batch on
                // failure. Partition Parts lists are patched with the
                // new IDs (old weld IDs are now dead) before
                // SpoolService.Execute iterates them.
                if (req.ConvertSplitWeldsToFieldWelds)
                {
                    var crossSpoolWelds = SpoolerWeldPostProcessor.FindCrossSpoolWelds(_doc, walk);
                    var allToSwap       = new HashSet<ElementId>(foldedWeldIds);
                    foreach (var id in crossSpoolWelds) allToSwap.Add(id);

                    if (allToSwap.Count == 0)
                    {
                        result.Log.Add("Convert Split Welds to Field Welds: no isolated or cross-spool welds found.");
                    }
                    else
                    {
                        using var tx = new Transaction(_doc, "Spooler: replace cross-spool welds with Field Weld parts");
                        tx.Start();
                        var swap = SpoolerWeldPostProcessor.ReplaceWithFieldWeldParts(
                            _doc, allToSwap.ToList(), result.Warnings);
                        tx.Commit();

                        // Patch every partition's Parts list so the
                        // downstream loop sees the new field weld IDs.
                        // Failed swaps leave the original ID in place
                        // (not in the map) so the partition still
                        // references something real.
                        if (swap.Count > 0)
                        {
                            foreach (var spool in walk.Spools)
                            {
                                for (int i = 0; i < spool.Parts.Count; i++)
                                {
                                    if (swap.TryGetValue(spool.Parts[i], out var newId))
                                        spool.Parts[i] = newId;
                                }
                            }
                        }
                        result.Log.Add($"Convert Split Welds to Field Welds: replaced {swap.Count}/{allToSwap.Count} weld(s) with Field Weld catalog item ({foldedWeldIds.Count} folded + {crossSpoolWelds.Count} cross-spool, deduped).");
                    }
                }

                foreach (var partition in walk.Spools)
                {
                    var (svcAbbr, svcName) = FabricationServiceLookup.Resolve(
                        _doc, partition.Parts.FirstOrDefault());

                    // Resolve the spool number with the current sequence.
                    // If the resolved value collides with an existing one
                    // (or one we already created in this batch), advance
                    // the sequence and retry — bookkeeping the skips so we
                    // can report them at the end.
                    string spoolNumber;
                    while (true)
                    {
                        var ctx = new TemplateContext
                        {
                            Service     = svcAbbr,
                            ServiceName = svcName,
                            Identifier  = req.Identifier,
                            Sequence    = seq,
                        };
                        spoolNumber = SpoolerTemplateEngine.Resolve(req.SpoolNumberTemplate, ctx);
                        if (string.IsNullOrWhiteSpace(spoolNumber))
                        {
                            throw new InvalidOperationException(
                                $"Spool {partition.Index} produced an empty number from template '{req.SpoolNumberTemplate}'. Check the template and identifier.");
                        }
                        if (!takenSpools.Contains(spoolNumber, StringComparer.OrdinalIgnoreCase)) break;
                        result.SkippedSpoolNumbers.Add(spoolNumber);
                        seq++;
                    }
                    takenSpools.Add(spoolNumber);

                    string spoolName = SpoolerTemplateEngine.Resolve(req.SpoolNameTemplate, new TemplateContext
                    {
                        Service     = svcAbbr,
                        ServiceName = svcName,
                        Identifier  = req.Identifier,
                        Sequence    = seq,
                        Number      = spoolNumber,
                    });
                    if (string.IsNullOrWhiteSpace(spoolName))
                        spoolName = spoolNumber;   // sensible fallback

                    string sheetNumber = sheetGen.Next(out var sheetSkips);
                    result.SkippedSheetNumbers.AddRange(sheetSkips);

                    var spoolReq = BuildSingleSpoolRequest(
                        partition, settings, directions,
                        spoolNumber, sheetNumber, spoolName,
                        req.UseAssemblies, req.Renumber, req.IncludeWelds);

                    var svc = new SpoolService(_uiDoc);
                    var sr  = svc.Execute(spoolReq);

                    if (!sr.Success)
                    {
                        throw new InvalidOperationException(
                            $"Spool {partition.Index} ({spoolNumber}) failed: {sr.Message}");
                    }

                    foreach (var w in sr.Warnings)
                        result.Warnings.Add($"[{spoolNumber}] {w}");
                    foreach (var l in sr.Log)
                        result.Log.Add($"[{spoolNumber}] {l}");

                    result.CreatedSpools.Add(new CreatedSpoolInfo
                    {
                        Index       = partition.Index,
                        SpoolNumber = spoolNumber,
                        SpoolName   = spoolName,
                        SheetNumber = sheetNumber,
                        SheetId     = sr.SheetId,
                        PartCount   = partition.Parts.Count,
                        Service     = svcAbbr,
                    });

                    seq++;
                }

                group.Assimilate();
                result.Success = true;
                result.Message = $"Created {result.CreatedSpools.Count} spool(s).";
            }
            catch (Exception ex)
            {
                try { group.RollBack(); } catch { }
                result.Success = false;
                result.Message = "The Spooler batch failed: " + ex.Message;
            }

            return result;
        }

        // ── Per-spool request construction ─────────────────────────────────────

        /// <summary>Builds a single <see cref="SpoolRequest"/> from a walked
        /// partition + the inherited shared settings + the resolved
        /// number/name/sheet#. Interactive tagging is hard-disabled here:
        /// a batch can't reasonably stop for per-tag prompts.</summary>
        private static SpoolRequest BuildSingleSpoolRequest(
            SpoolPartition partition,
            SpoolSettings   settings,
            IReadOnlyList<SpoolDirection> directions,
            string spoolNumber, string sheetNumber, string spoolName,
            bool useAssemblies,
            SpoolRenumberOptions? perRunRenumber,
            bool includeWelds)
        {
            return new SpoolRequest
            {
                Elements         = partition.Parts,
                SpoolNumber      = spoolNumber,
                SheetNumber      = sheetNumber,
                SheetName        = spoolName,
                Directions       = directions,
                TitleblockTypeId = settings.TitleblockTypeId is long tbId ? new ElementId(tbId) : null,
                ScheduleId       = settings.ScheduleId       is long schId ? new ElementId(schId) : null,
                ScaleDenominator = settings.ScaleDenominator,
                TagFamilyId      = settings.TagFamilyId      is long tagId ? new ElementId(tagId) : null,
                ViewTemplateId   = settings.ViewTemplateId   is long vtId  ? new ElementId(vtId)  : null,
                Renumber         = perRunRenumber,
                InteractiveTagging = false,
                PlaceLeader      = settings.PlaceLeader,
                LeaderEnd        = settings.LeaderEnd == 1
                    ? LeaderEndCondition.Free
                    : LeaderEndCondition.Attached,
                LeaderLengthFt   = settings.LeaderLengthFt,
                IncludeWelds     = includeWelds,
                UseAssemblies    = useAssemblies,
                StatusParamName  = settings.SpoolStatusParamName,
                StatusParamValue = settings.SpoolStatusParamValue,
                EnhancedTagPlacement = settings.EnhancedTagPlacement,
                TagOffsetInches  = settings.TagOffsetInches > 0 ? settings.TagOffsetInches : 1.0,
            };
        }

        // ── Collision pre-scan ─────────────────────────────────────────────────

        private static HashSet<string> ExistingSheetNumbers(Document doc)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                                          .Cast<ViewSheet>())
                {
                    var n = sheet.SheetNumber;
                    if (!string.IsNullOrWhiteSpace(n)) set.Add(n);
                }
            }
            catch { }
            return set;
        }

        private static HashSet<string> ExistingSpoolNumbers(Document doc)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var entry in SpoolNumberRegistry.Collect(doc))
                {
                    if (!string.IsNullOrWhiteSpace(entry.SpoolNumber))
                        set.Add(entry.SpoolNumber);
                }
            }
            catch { }
            return set;
        }

        /// <summary>Sequential sheet-number generator. Parses the starting
        /// value into (prefix, number, padding) — e.g., <c>S1</c> →
        /// (S, 1, 1), <c>S001</c> → (S, 1, 3), <c>SHEET-100</c> →
        /// (SHEET-, 100, 3). Each <see cref="Next"/> call returns the
        /// next un-taken value and reports any numbers it had to skip
        /// (so the user knows their batch jumped over existing sheets).</summary>
        private sealed class SheetNumberSequencer
        {
            private readonly string _prefix;
            private readonly int    _padding;
            private          int    _value;
            private readonly HashSet<string> _taken;

            public SheetNumberSequencer(string start, HashSet<string> taken)
            {
                _taken = taken;
                var m = Regex.Match(start ?? string.Empty, @"^(.*?)(\d+)$");
                if (m.Success)
                {
                    _prefix  = m.Groups[1].Value;
                    var numStr = m.Groups[2].Value;
                    _padding = numStr.Length;
                    _value   = int.TryParse(numStr, out var n) ? n : 1;
                }
                else
                {
                    _prefix  = start ?? string.Empty;
                    _padding = 1;
                    _value   = 1;
                }
            }

            public string Next(out List<string> skipped)
            {
                skipped = new List<string>();
                while (true)
                {
                    string candidate = _prefix + _value.ToString().PadLeft(_padding, '0');
                    _value++;
                    if (!_taken.Contains(candidate))
                    {
                        _taken.Add(candidate);
                        return candidate;
                    }
                    skipped.Add(candidate);
                }
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Request + result types
    // ═════════════════════════════════════════════════════════════════════════

    public sealed class SpoolerBatchRequest
    {
        public IReadOnlyCollection<ElementId> Selection { get; init; } = Array.Empty<ElementId>();
        public ElementId Start { get; init; } = ElementId.InvalidElementId;
        public IReadOnlyCollection<ElementId> Breaks { get; init; } = Array.Empty<ElementId>();

        public string Identifier          { get; init; } = string.Empty;
        public string SpoolNumberTemplate { get; init; } = "{Service}-{ID}-{N:00}";
        public string SpoolNameTemplate   { get; init; } = "Spool {Number}";
        public int    StartingSequence    { get; init; } = 1;
        public string StartingSheetNumber { get; init; } = "S1";

        /// <summary>Optional auto-split rules. When any are active, the
        /// orchestrator runs <see cref="SpoolerRuleEvaluator.ComputeBreaks"/>
        /// to expand the break set before partitioning. Null = no rules
        /// (just manual breaks).</summary>
        public AutoSplitRules? Rules { get; init; }

        /// <summary>When true, each partition becomes a Revit
        /// AssemblyInstance with assembly views + sheet (instead of
        /// ad-hoc 3D views on a normal sheet). Threaded through to each
        /// per-spool <see cref="SpoolRequest"/>.</summary>
        public bool UseAssemblies { get; init; }

        /// <summary>Default off. When on, any weld that would have been
        /// the sole part of its spool gets "Field Weld" stamped on its
        /// Comments parameter before being folded into the next spool
        /// in walk order. The fold happens either way (a lone weld
        /// never gets its own spool drawing) — this flag only controls
        /// the relabel. Combined with
        /// <see cref="SpoolerRuleEvaluator"/>'s Comments-aware
        /// field-weld detection, the relabel survives re-runs of the
        /// "At Field Welds" auto-split rule.</summary>
        public bool ConvertSplitWeldsToFieldWelds { get; init; }

        /// <summary>Per-run Renumber overrides. When non-null, uses
        /// THIS instead of SpoolSettings values. When null, renumber
        /// is skipped for the batch. Applied per spool.</summary>
        public SpoolRenumberOptions? Renumber { get; init; }

        /// <summary>Per-run override for whether joint parts (welds)
        /// participate in renumber + tag placement. Defaults to true.</summary>
        public bool IncludeWelds { get; init; } = true;
    }

    public sealed class SpoolerBatchResult
    {
        public bool   Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<CreatedSpoolInfo> CreatedSpools       { get; init; } = new();
        public List<ElementId>        Unconnected           { get; init; } = new();
        public List<string>           SkippedSheetNumbers { get; init; } = new();
        public List<string>           SkippedSpoolNumbers { get; init; } = new();
        /// <summary>Critical messages — surfaced in the main TaskDialog body.</summary>
        public List<string>           Warnings            { get; init; } = new();
        /// <summary>Informational log items — surfaced behind "Show
        /// details" on the summary TaskDialog. Welds skipped because
        /// Include Welds is off goes here, etc.</summary>
        public List<string>           Log                 { get; init; } = new();
    }

    public sealed class CreatedSpoolInfo
    {
        public int        Index       { get; init; }
        public string     SpoolNumber { get; init; } = string.Empty;
        public string     SpoolName   { get; init; } = string.Empty;
        public string     SheetNumber { get; init; } = string.Empty;
        public ElementId? SheetId     { get; init; }
        public int        PartCount   { get; init; }
        public string?    Service     { get; init; }
    }
}
