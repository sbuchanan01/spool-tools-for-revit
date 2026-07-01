using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>Inputs collected by the dialog and consumed by SpoolService.Execute.</summary>
    public sealed record SpoolRequest
    {
        public IReadOnlyCollection<ElementId> Elements { get; init; } = Array.Empty<ElementId>();
        public string SpoolNumber { get; init; } = string.Empty;
        public string SheetNumber { get; init; } = string.Empty;
        public string SheetName   { get; init; } = string.Empty;
        public IReadOnlyCollection<SpoolDirection> Directions { get; init; } = Array.Empty<SpoolDirection>();
        public ElementId? TitleblockTypeId { get; init; }
        public ElementId? ScheduleId       { get; init; }
        /// <summary>Revit view-scale denominator (12 = 1"=1', 48 = 1/4"=1', etc.).
        /// Null means auto-fit each view to its grid cell.</summary>
        public int? ScaleDenominator { get; init; }

        /// <summary>If non-null, renumber the selected parts' "Item Number"
        /// parameter as the first step of the spool transaction group. Null
        /// means leave Item Number untouched.</summary>
        public SpoolRenumberOptions? Renumber { get; init; }

        /// <summary>FamilySymbol id of the Fabrication Pipework tag to drop
        /// on each part. Null = no tagging (the dialog's "Do not place Tags"
        /// option maps to null).</summary>
        public ElementId? TagFamilyId { get; init; }

        /// <summary>View template to apply to every created spool view.
        /// Null = leave views unstyled. Templates that don't apply to a
        /// given view kind (e.g., a Floor Plan template on a 3D view) are
        /// silently skipped per-view.</summary>
        public ElementId? ViewTemplateId { get; init; }

        /// <summary>When true (and <see cref="TagFamilyId"/> is set), walks
        /// each view part-by-part in Item Number order and prompts the user
        /// to click each tag's location instead of auto-placing at the part
        /// center. Esc on any prompt skips that one tag.</summary>
        public bool InteractiveTagging { get; init; }

        /// <summary>Place each tag with a leader. Defaults to false to match
        /// the dialog's Leader Settings out-of-box state — the user opts in
        /// explicitly via the Leader Settings popup.</summary>
        public bool PlaceLeader { get; init; }

        /// <summary>Leader endpoint style when <see cref="PlaceLeader"/> is on.
        /// Attached = leader anchors at the part edge; Free = endpoint is a
        /// free point in space the user (or this code) explicitly positions
        /// via <see cref="IndependentTag.SetLeaderEnd"/>.</summary>
        public LeaderEndCondition LeaderEnd { get; init; } = LeaderEndCondition.Attached;

        /// <summary>Length (in feet) of the leader's shoulder segment, used
        /// when <see cref="PlaceLeader"/> is on. For Attached End this sets
        /// the elbow offset from the part along the tag direction; for Free
        /// End it sets the elbow distance from the picked free endpoint.
        /// 0 = no elbow offset (the elbow coincides with the tag head).</summary>
        public double LeaderLengthFt { get; init; } = 0.0;

        /// <summary>Master switch for Enhanced Tag Placement — bbox
        /// avoidance + elbow/tee shape preferences. False = historical
        /// "1 inch up" behaviour.</summary>
        public bool EnhancedTagPlacement { get; init; }

        /// <summary>Distance (PAPER inches) between part and tag
        /// head. Default 1.0 = historical hardcoded offset.</summary>
        public double TagOffsetInches { get; init; } = 1.0;

        /// <summary>When true (default), every selected part is renumbered
        /// and tagged. When false, parts whose "Product Range" parameter is
        /// "Joints" (welds, joint fittings) are skipped for those two
        /// steps — they're still pinned and included in the spool views.</summary>
        public bool IncludeWelds { get; init; } = true;

        /// <summary>When true, the parts become a Revit AssemblyInstance
        /// (members + assembly views + sheet) instead of getting an ad-hoc
        /// 3D view tree on a normal sheet. All other settings (titleblock,
        /// drawable region, schedule, scale, tags, leader, renumber,
        /// Include Welds, view template) still apply.</summary>
        public bool UseAssemblies { get; init; }

        /// <summary>Name of the project text-type parameter to write the
        /// "spool status" value to (e.g. <c>"Fabrication Status"</c>).
        /// Null or empty = skip the status write entirely. Lets the
        /// Spool Config dialog redirect status to any custom text param
        /// the project carries (or disable status writes wholesale).</summary>
        public string? StatusParamName  { get; init; }

        /// <summary>Value written to <see cref="StatusParamName"/>
        /// (e.g. <c>"Issued for Fabrication"</c>). Null = use the
        /// historical default value when StatusParamName is also null.</summary>
        public string? StatusParamValue { get; init; }

        // ── Dimensions ────────────────────────────────────────────────────────

        /// <summary>When true, SpoolDimensioner emits dim chains on the
        /// enabled ortho views per the rules in Spool Config. False =
        /// skip dim placement entirely.</summary>
        public bool IncludeDimensions { get; init; }

        /// <summary>Project DimensionType id applied to every dim the
        /// engine creates. Null = use Revit's default linear style.</summary>
        public ElementId? DimensionStyleId { get; init; }

        /// <summary>Distance in feet between the dimensioned element
        /// and the dim line. Inner / segment / overall layers stack at
        /// multiples of this so they don't collide.</summary>
        public double DimensionOffsetFt { get; init; }

        /// <summary>Per-view bitmask of <see cref="SpoolDirection"/>
        /// values where dims are to be placed. Engine intersects this
        /// with the actual created views so iso directions or
        /// not-created ortho views are silently skipped.</summary>
        public int DimensionViewMask { get; init; }
    }

    /// <summary>Renumber configuration carried by a SpoolRequest.</summary>
    public sealed class SpoolRenumberOptions
    {
        public int  StartingNumber       { get; init; } = 1;
        public bool UseSameForIdentical  { get; init; } = true;
        /// <summary>When true (and UseSameForIdentical is also true), PIPE
        /// parts of different centerline lengths get different numbers even
        /// if everything else matches. Non-pipe parts ignore this flag.</summary>
        public bool UseLengthAsSeparator { get; init; } = false;
    }

    public sealed class SpoolResult
    {
        public bool      Success     { get; init; }
        public string    Message     { get; init; } = string.Empty;
        public ElementId? SheetId    { get; init; }
        public List<ElementId> ViewIds { get; } = new();
        /// <summary>Critical messages — surfaced prominently in the
        /// success/failure TaskDialog body.</summary>
        public List<string> Warnings { get; } = new();
        /// <summary>Informational log items — surfaced behind a
        /// "Show details" toggle on the TaskDialog. Used for things
        /// the user already knows about (e.g., parts skipped because
        /// they explicitly turned Include Welds off) so they don't
        /// clutter the main result message.</summary>
        public List<string> Log { get; } = new();
    }

    /// <summary>Pre-spool snapshot of element state used to undo a Discard.
    /// PreviewControl can't render against a view inside an open
    /// TransactionGroup, so the service assimilates immediately and we
    /// roll back manually from this snapshot if the user discards.</summary>
    public sealed class SpoolUndoSnapshot
    {
        public List<PartSnapshot>     Parts            { get; init; } = new();
        public List<ScheduleSnapshot> Schedules        { get; init; } = new();
        public List<ElementId>        CreatedSheetIds  { get; init; } = new();
        public List<ElementId>        CreatedViewIds   { get; init; } = new();
        /// <summary>AssemblyInstance ids created on the Use-Assemblies
        /// path. Deleting an AssemblyInstance cascades-deletes its
        /// member views and sheet, so Discard prefers this over the
        /// per-element CreatedSheet/CreatedView lists when present.</summary>
        public List<ElementId>        CreatedAssemblyIds { get; init; } = new();
    }

    public sealed class PartSnapshot
    {
        public ElementId Id          { get; init; } = ElementId.InvalidElementId;
        public string?   ItemNumber  { get; init; }
        public string?   SpoolNumber { get; init; }
        public string?   FabStatus   { get; init; }
        public bool      Pinned      { get; init; }
    }

    public sealed class ScheduleSnapshot
    {
        public ElementId Id          { get; init; } = ElementId.InvalidElementId;
        public double[]  FieldWidths { get; init; } = Array.Empty<double>();
    }

    /// <summary>Held by the preview window. The spool is already committed
    /// (the TransactionGroup was assimilated in ExecutePreview) — Accept is
    /// therefore a no-op, and Discard runs a manual restore from the
    /// pre-spool snapshot: deletes the created sheet + views (tags +
    /// ScheduleSheetInstance go with them) and rewrites part parameters,
    /// pin state, and schedule column widths back to their pre-spool values.
    /// Dispose discards as a safety net.</summary>
    public sealed class SpoolPreviewSession : IDisposable
    {
        private readonly Document?         _doc;
        private readonly SpoolUndoSnapshot? _snapshot;
        private bool _finalized;

        public SpoolResult Result  { get; }
        public ElementId?  SheetId { get; }
        /// <summary>Direction → view-id map for the views the spool created.
        /// The dialog reads this before disposing the session so it can hand
        /// the map to <see cref="SpoolService.PlaceInteractiveTagsPostAccept"/>
        /// if the user wanted interactive tagging but the preview path
        /// suppressed it.</summary>
        public IReadOnlyDictionary<SpoolDirection, ElementId> ViewsByDirection { get; }

        internal SpoolPreviewSession(
            Document doc,
            SpoolResult result,
            ElementId sheetId,
            SpoolUndoSnapshot snapshot,
            IReadOnlyDictionary<SpoolDirection, ElementId> viewsByDirection)
        {
            _doc      = doc;
            _snapshot = snapshot;
            Result    = result;
            SheetId   = sheetId;
            ViewsByDirection = viewsByDirection;
        }

        private SpoolPreviewSession(SpoolResult failureResult)
        {
            Result     = failureResult;
            _finalized = true;
            ViewsByDirection = new Dictionary<SpoolDirection, ElementId>();
        }

        public static SpoolPreviewSession Failure(SpoolResult failureResult) =>
            new SpoolPreviewSession(failureResult);

        public void Accept()
        {
            // Changes already committed in ExecutePreview — nothing to do.
            _finalized = true;
        }

        public void Discard()
        {
            if (_finalized || _doc == null || _snapshot == null) return;
            _finalized = true;

            using var tx = new Transaction(_doc, "Spool: discard preview");
            tx.Start();

            // Delete created assemblies first — deleting an AssemblyInstance
            // cascades to its assembly views + assembly sheet, so the
            // sheet/view lists in this snapshot become already-gone
            // entries for the Use-Assemblies path (the foreach's catch
            // swallows the resulting failures, which is exactly what we
            // want).
            foreach (var id in _snapshot.CreatedAssemblyIds)
            {
                try { _doc.Delete(id); } catch { /* already gone */ }
            }

            // Delete created sheet (auto-deletes viewports + schedule instance)
            // and the created views (auto-deletes tags placed on them).
            foreach (var id in _snapshot.CreatedSheetIds)
            {
                try { _doc.Delete(id); } catch { /* already gone */ }
            }
            foreach (var id in _snapshot.CreatedViewIds)
            {
                try { _doc.Delete(id); } catch { /* already gone */ }
            }

            // Restore per-part parameter values + pin state.
            foreach (var snap in _snapshot.Parts)
            {
                var e = _doc.GetElement(snap.Id);
                if (e == null) continue;
                WriteString(e, "Item Number", snap.ItemNumber);
                WriteString(e, SpoolNumberRegistry.SpoolNumberParam, snap.SpoolNumber);
                WriteString(e, SpoolNumberRegistry.FabricationStatusParam, snap.FabStatus);
                if (e.Pinned != snap.Pinned)
                {
                    try { e.Pinned = snap.Pinned; } catch { }
                }
            }

            // Restore schedule column widths we mutated via ConstrainScheduleWidth.
            foreach (var snap in _snapshot.Schedules)
            {
                var sched = _doc.GetElement(snap.Id) as ViewSchedule;
                if (sched?.Definition == null) continue;
                int n = Math.Min(snap.FieldWidths.Length, sched.Definition.GetFieldCount());
                for (int i = 0; i < n; i++)
                {
                    try { sched.Definition.GetField(i).GridColumnWidth = snap.FieldWidths[i]; }
                    catch { }
                }
            }

            tx.Commit();
        }

        public void Dispose()
        {
            if (!_finalized)
            {
                try { Discard(); } catch { }
            }
        }

        private static void WriteString(Element e, string paramName, string? value)
        {
            var p = ParameterHelper.FindParameter(e, paramName);
            if (p == null || p.IsReadOnly) return;
            switch (p.StorageType)
            {
                case StorageType.String:  p.Set(value ?? ""); break;
                case StorageType.Integer: p.Set(int.TryParse(value, out int n) ? n : 0); break;
            }
        }
    }

    /// <summary>
    /// Runs inside a single Revit transaction:
    ///   1. Set Spool Number + Fabrication Status, pin all elements
    ///   2. Build the requested views (Top / Front / Left / Right + 4 iso)
    ///   3. Create sheet, place viewports per ortho grid, place schedule top-right
    /// </summary>
    public sealed class SpoolService
    {
        private readonly UIDocument _uiDoc;
        private readonly Document _doc;
        public SpoolService(UIDocument uiDoc)
        {
            _uiDoc = uiDoc;
            _doc   = uiDoc.Document;
        }

        // ── Weld / joint identification ─────────────────────────────────────────

        /// <summary>True when the element is a FabricationPart that the
        /// catalog classifies as a joint — i.e., its "Product Range"
        /// parameter contains "Joint" (case-insensitive). This is more
        /// precise than CID alone: the standard joint pattern (CID 2522)
        /// is also used by flanges and couplings, but those live in
        /// different Product Ranges ("Flanges", "Couplings", etc.).
        /// Product Range is a catalog-defined value, not a per-instance
        /// user input, so it stays reliable across projects. Tolerant to
        /// variations like "Joints" / "Pipe Joints".</summary>
        private static bool IsJoint(Element? e)
        {
            if (e is not FabricationPart fp) return false;
            try
            {
                var p = fp.LookupParameter("Product Range");
                if (p == null || p.StorageType != StorageType.String) return false;
                string range = p.AsString() ?? string.Empty;
                return range.IndexOf("joint", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>Commit-immediately path: build the spool and assimilate the
        /// TransactionGroup so it lands as one undo step. For the preview-then-
        /// accept-or-discard path, use <see cref="ExecutePreview"/>.</summary>
        public SpoolResult Execute(SpoolRequest req)
        {
            using var session = ExecutePreview(req);
            if (session.Result.Success) session.Accept();
            return session.Result;
        }

        /// <summary>Builds the spool, commits everything (one undo step), and
        /// returns a session that holds a pre-spool snapshot so Discard can
        /// roll back manually. The PreviewControl can render against a fully
        /// committed sheet — it goes blank while a TransactionGroup is still
        /// open, hence the assimilate-immediately design.</summary>
        public SpoolPreviewSession ExecutePreview(SpoolRequest req)
        {
            var result = new SpoolResult { };
            if (req.Elements.Count == 0)
                return SpoolPreviewSession.Failure(Fail("No elements selected."));
            if (string.IsNullOrWhiteSpace(req.SpoolNumber))
                return SpoolPreviewSession.Failure(Fail("Spool Number is required."));
            if (req.Directions.Count == 0)
                return SpoolPreviewSession.Failure(Fail("Select at least one view direction."));
            if (req.TitleblockTypeId == null)
                return SpoolPreviewSession.Failure(Fail("A titleblock must be selected."));

            // Capture per-part pre-state + per-schedule column widths BEFORE
            // any mutations — Discard restores from here.
            var snapshot = new SpoolUndoSnapshot
            {
                Parts     = CapturePartsSnapshot(req.Elements),
                Schedules = req.ScheduleId != null
                    ? CaptureScheduleSnapshot(req.ScheduleId)
                    : new List<ScheduleSnapshot>(),
            };

            // Welds toggle: when off, weld/joint parts are skipped for Item
            // Number renumbering AND tag placement — they're still pinned
            // and included in the views, they just don't get an Item Number
            // rewrite or a callout tag. With the toggle on (default) every
            // selected part participates.
            //
            // Manual loop (rather than LINQ Where) on a defensive copy: the
            // earlier version hit "Collection was modified" on List<T>'s
            // enumerator. Per-part try/catch keeps one classification fault
            // from killing the whole spool.
            var sourceList = req.Elements.ToList();
            var taggable   = new List<ElementId>(sourceList.Count);
            if (req.IncludeWelds)
            {
                taggable.AddRange(sourceList);
            }
            else
            {
                foreach (var id in sourceList)
                {
                    bool isJoint;
                    try { isJoint = IsJoint(_doc.GetElement(id)); }
                    catch { isJoint = false; }   // fail-safe: include on error
                    if (!isJoint) taggable.Add(id);
                }
            }
            int excludedWelds = sourceList.Count - taggable.Count;
            if (excludedWelds > 0)
            {
                // The user explicitly turned Include Welds off — this is
                // informational, not a warning. Goes to the log behind
                // "Show details" rather than the main result body.
                result.Log.Add(
                    $"{excludedWelds} weld/joint part(s) skipped for renumbering and tagging (Include Welds is off). They're still pinned and shown in the views.");
            }

            var group = new TransactionGroup(_doc, $"Create Spool {req.SpoolNumber}");
            group.Start();

            ElementId sheetId = ElementId.InvalidElementId;
            List<ElementId> viewIds = new();
            string renumberSummary = string.Empty;
            try
            {
                // Optional first step: write sequential "Item Number" values
                // before we touch anything else. Runs in its own transaction
                // inside the group so the user can undo the spool + renumber
                // together with one click.
                if (req.Renumber != null)
                {
                    var renumber = new RenumberService(_doc);
                    var rr = renumber.Renumber(
                        taggable,
                        req.Renumber.StartingNumber,
                        req.Renumber.UseSameForIdentical,
                        req.Renumber.UseLengthAsSeparator);
                    renumberSummary = rr.Message;
                    if (!rr.Success && !string.IsNullOrWhiteSpace(rr.Message))
                        result.Warnings.Add(rr.Message);
                }

                // Pre-flight for Use Assemblies: a part can only belong to
                // ONE AssemblyInstance, so abort up front if any selected
                // part is already in another assembly. Listing the IDs
                // gives the user something to act on.
                if (req.UseAssemblies)
                {
                    var asmBuilder = new SpoolAssemblyBuilder(_doc);
                    var conflicts  = asmBuilder.FindAssemblyConflicts(req.Elements);
                    if (conflicts.Count > 0)
                    {
                        var preview = string.Join(", ", conflicts.Take(10).Select(id => id.Value.ToString()));
                        var more    = conflicts.Count > 10 ? $" … (+{conflicts.Count - 10} more)" : "";
                        try { group.RollBack(); } catch { }
                        group.Dispose();
                        return SpoolPreviewSession.Failure(Fail(
                            $"Cannot build assembly — {conflicts.Count} selected part(s) already belong to another assembly. " +
                            $"Element IDs: {preview}{more}"));
                    }
                }

                using (var tx = new Transaction(_doc, "Spool: parameters + pin"))
                {
                    tx.Start();
                    // ApplyParamsAndPin mutates result.Warnings in-place and
                    // returns the SAME reference — do NOT iterate the return
                    // value and re-add to result.Warnings, that's iterating a
                    // list while modifying it and throws "Collection was
                    // modified" the moment the list isn't empty when the
                    // foreach starts.
                    // Pinning is skipped when the spool becomes an
                    // AssemblyInstance — assembly membership already locks
                    // the parts.
                    ApplyParamsAndPin(req.Elements, req.SpoolNumber, result.Warnings,
                        statusParamName: req.StatusParamName,
                        statusParamValue: req.StatusParamValue,
                        pin: !req.UseAssemblies);
                    tx.Commit();
                }

                // Pre-built sheet id, threaded through to BuildSheet later.
                // Non-null only on the Use-Assemblies path (where
                // AssemblyViewUtils.CreateSheet has already produced one).
                ElementId? prebuiltSheetId       = null;
                ElementId? createdAssemblyId     = null;

                if (req.UseAssemblies)
                {
                    // The assembly builder manages its own transactions
                    // internally — AssemblyInstance.Create must be
                    // committed before AssemblyTypeName can be set, so
                    // wrapping the whole build in one transaction would
                    // leave the assembly nameless. Each sub-tx still
                    // collapses into the outer TransactionGroup, so the
                    // whole spool is still one undo step.
                    var asmBuilder = new SpoolAssemblyBuilder(_doc);
                    var asmResult  = asmBuilder.Build(
                        req.Elements, req.Directions,
                        spoolName:        req.SheetName,
                        titleblockTypeId: req.TitleblockTypeId!,
                        viewTemplateId:   req.ViewTemplateId,
                        warnings:         result.Warnings);

                    _viewsByDirection   = asmResult.ViewsByDirection;
                    prebuiltSheetId     = asmResult.SheetId;
                    createdAssemblyId   = asmResult.AssemblyInstanceId;
                }
                else
                {
                    using (var tx = new Transaction(_doc, "Spool: create views"))
                    {
                        tx.Start();
                        var builder = new SpoolViewBuilder(_doc)
                        {
                            ViewTemplateId = req.ViewTemplateId,
                        };
                        _viewsByDirection = builder.BuildViews(
                            req.Directions, req.Elements, req.SpoolNumber, result.Warnings);
                        tx.Commit();
                    }
                }

                viewIds = _viewsByDirection.Values.ToList();
                result.ViewIds.AddRange(viewIds);
                if (createdAssemblyId != null && createdAssemblyId != ElementId.InvalidElementId)
                    snapshot.CreatedAssemblyIds.Add(createdAssemblyId);

                // Apply view scale BEFORE tag placement so tags (especially
                // interactive ones) render at the final size from the moment
                // they're placed. Without this, tags use the view's default
                // scale and look huge until BuildSheet retroactively sets the
                // real scale.
                using (var scaleTx = new Transaction(_doc, "Spool: apply view scale"))
                {
                    scaleTx.Start();
                    // Regenerate BEFORE choosing the scale so that newly
                    // created views (especially assembly views, which
                    // populate their CropBox lazily) have valid extents
                    // to feed into ChooseScale. Without this, the
                    // assembly path's auto-fit reads empty / huge bboxes
                    // and falls through to the coarsest scale in the
                    // dropdown (1/32"=1').
                    _doc.Regenerate();
                    int scaleEarly = ChooseScaleForRequest(req, _viewsByDirection);
                    foreach (var kv in _viewsByDirection)
                    {
                        var v = _doc.GetElement(kv.Value) as View;
                        if (v == null) continue;
                        try { v.Scale = scaleEarly; } catch { /* some VFTs lock scale */ }
                    }
                    _doc.Regenerate();
                    scaleTx.Commit();
                }

                if (req.TagFamilyId != null && taggable.Count > 0)
                {
                    if (req.InteractiveTagging)
                    {
                        // Each picked tag commits in its own sub-transaction so
                        // the user sees the tag drop in immediately. The outer
                        // TransactionGroup still collapses everything to one
                        // undo step.
                        PlaceTagsInteractive(
                            taggable, _viewsByDirection, req.TagFamilyId,
                            req.PlaceLeader, req.LeaderEnd, req.LeaderLengthFt, result.Warnings);
                    }
                    else
                    {
                        using var tagTx = new Transaction(_doc, "Spool: place tags");
                        tagTx.Start();
                        PlaceTagsOnViews(taggable, _viewsByDirection, req.TagFamilyId,
                            req.PlaceLeader, req.LeaderEnd, req.LeaderLengthFt,
                            req.EnhancedTagPlacement, req.TagOffsetInches,
                            result.Warnings);
                        tagTx.Commit();
                    }
                }

                // Dimensions — runs in its own transaction after tags
                // so a placement failure doesn't poison the tag step.
                // FEATURE GATE: the placement engine is parked while
                // the spool tools get extracted into their own
                // package; the dialog UI is hidden / disabled to match.
                // Re-enabling is a single-line change here (drop the
                // first &&), plus removing IsEnabled=False / Visibility=
                // Collapsed from the dialog XAML. The engine code stays
                // in place so the work isn't lost.
                const bool DimensionsFeatureEnabled = false;
                if (DimensionsFeatureEnabled && req.IncludeDimensions && req.DimensionViewMask != 0)
                {
                    using var dimTx = new Transaction(_doc, "Spool: place dimensions");
                    dimTx.Start();
                    try
                    {
                        var dimensioner = new SpoolDimensioner(_doc);
                        int n = dimensioner.PlaceDimensions(
                            req.Elements, _viewsByDirection, req, result.Warnings);
                        result.Log.Add($"Dimensions: placed {n} dim(s) across enabled ortho views.");
                    }
                    catch (Exception dex)
                    {
                        result.Warnings.Add("Dimensions: engine threw " + dex.GetType().Name + " — " + dex.Message);
                    }
                    dimTx.Commit();
                }

                using (var tx = new Transaction(_doc, "Spool: create sheet"))
                {
                    tx.Start();
                    // On the assembly path, AssemblyViewUtils.CreateSheet
                    // already produced a sheet — BuildSheet just places
                    // viewports + schedule on it. On the non-assembly path
                    // it creates a fresh ViewSheet first.
                    sheetId = BuildSheet(req, _viewsByDirection, result.Warnings,
                        prebuiltSheetId: prebuiltSheetId);
                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                try { group.RollBack(); } catch { }
                group.Dispose();
                // Include the full stack trace so we can pinpoint which
                // step inside the transaction group threw — the generic
                // message alone is not enough to debug "Collection was
                // modified" and similar mid-iteration faults.
                var detail = ex.StackTrace ?? "(no stack trace)";
                return SpoolPreviewSession.Failure(Fail(
                    $"Spool creation failed: {ex.Message}\n\n{detail}"));
            }

            // Commit the whole spool now — one undo step in Revit's stack.
            // PreviewControl needs a fully-committed view to render.
            group.Assimilate();
            group.Dispose();

            // Snapshot the IDs we created so Discard can delete them.
            snapshot.CreatedViewIds.AddRange(result.ViewIds);
            snapshot.CreatedSheetIds.Add(sheetId);

            var msg = $"Spool {req.SpoolNumber} created with {result.ViewIds.Count} view(s).";
            if (!string.IsNullOrWhiteSpace(renumberSummary))
                msg += "\n" + renumberSummary;

            var built = new SpoolResult
            {
                Success = true,
                Message = msg,
                SheetId = sheetId,
            }.CopyWarningsFrom(result);

            return new SpoolPreviewSession(_doc, built, sheetId, snapshot, _viewsByDirection);
        }

        /// <summary>Re-runs interactive tagging on the views created by a
        /// preview-then-accept spool. Preview always suppresses interactive
        /// tagging (the Preview button auto-places tags so the user can see
        /// composition), so when the user clicks Accept with InteractiveTagging
        /// originally checked, the dialog wires this method in to walk the
        /// parts and prompt for each tag location. Existing auto-placed tags
        /// on these views are deleted first so we don't end up with both an
        /// auto and an interactive tag for each part. Runs in its own
        /// TransactionGroup so the user can undo the interactive tagging as
        /// one step (separate from the spool itself, which already
        /// assimilated).</summary>
        public void PlaceInteractiveTagsPostAccept(
            IReadOnlyCollection<ElementId> partIds,
            IReadOnlyDictionary<SpoolDirection, ElementId> views,
            ElementId tagTypeId,
            bool placeLeader,
            LeaderEndCondition leaderEnd,
            double leaderLengthFt,
            bool includeWelds = true,
            List<string>? warningsOut = null)
        {
            warningsOut ??= new List<string>();
            var viewsCopy = new Dictionary<SpoolDirection, ElementId>(views);

            // Match ExecutePreview's Include Welds toggle: skip joints when off.
            // Manual loop on a defensive copy with per-part try/catch — see
            // the matching block in ExecutePreview for the same rationale.
            var sourceList = partIds.ToList();
            var taggable   = new List<ElementId>(sourceList.Count);
            if (includeWelds)
            {
                taggable.AddRange(sourceList);
            }
            else
            {
                foreach (var id in sourceList)
                {
                    bool isJoint;
                    try { isJoint = IsJoint(_doc.GetElement(id)); }
                    catch { isJoint = false; }
                    if (!isJoint) taggable.Add(id);
                }
            }
            if (taggable.Count == 0) return;

            using var group = new TransactionGroup(_doc, "Spool: interactive tagging");
            group.Start();

            // Sweep auto-placed tags on the spool views so we don't double-tag.
            using (var tx = new Transaction(_doc, "Spool: clear auto tags"))
            {
                tx.Start();
                var tagsToDelete = new List<ElementId>();
                foreach (var viewId in viewsCopy.Values)
                {
                    try
                    {
                        var existing = new FilteredElementCollector(_doc, viewId)
                            .OfClass(typeof(IndependentTag))
                            .Select(t => t.Id)
                            .ToList();
                        tagsToDelete.AddRange(existing);
                    }
                    catch { /* view may have been deleted between Accept and here */ }
                }
                if (tagsToDelete.Count > 0)
                {
                    try { _doc.Delete(tagsToDelete); } catch { }
                }
                tx.Commit();
            }

            PlaceTagsInteractive(taggable, viewsCopy, tagTypeId,
                placeLeader, leaderEnd, leaderLengthFt, warningsOut);

            group.Assimilate();
        }

        private List<PartSnapshot> CapturePartsSnapshot(IReadOnlyCollection<ElementId> ids)
        {
            var snaps = new List<PartSnapshot>(ids.Count);
            foreach (var id in ids)
            {
                var e = _doc.GetElement(id);
                if (e == null) continue;
                snaps.Add(new PartSnapshot
                {
                    Id          = id,
                    ItemNumber  = ReadString(e, "Item Number"),
                    SpoolNumber = ReadString(e, SpoolNumberRegistry.SpoolNumberParam),
                    FabStatus   = ReadString(e, SpoolNumberRegistry.FabricationStatusParam),
                    Pinned      = e.Pinned,
                });
            }
            return snaps;
        }

        private List<ScheduleSnapshot> CaptureScheduleSnapshot(ElementId scheduleId)
        {
            var sched = _doc.GetElement(scheduleId) as ViewSchedule;
            if (sched?.Definition == null) return new();

            int n = sched.Definition.GetFieldCount();
            var widths = new double[n];
            for (int i = 0; i < n; i++)
                widths[i] = sched.Definition.GetField(i).GridColumnWidth;
            return new List<ScheduleSnapshot>
            {
                new ScheduleSnapshot { Id = scheduleId, FieldWidths = widths },
            };
        }

        private static string? ReadString(Element e, string paramName)
        {
            var p = ParameterHelper.FindParameter(e, paramName);
            if (p == null) return null;
            return p.StorageType switch
            {
                StorageType.String  => p.AsString(),
                StorageType.Integer => p.AsInteger().ToString(),
                _ => null,
            };
        }

        private Dictionary<SpoolDirection, ElementId> _viewsByDirection = new();

        // ── Step 1: parameters + pin ────────────────────────────────────────────

        private List<string> ApplyParamsAndPin(
            IEnumerable<ElementId> ids, string spoolNumber, List<string> warnings,
            string? statusParamName = null, string? statusParamValue = null,
            bool pin = true)
        {
            int spoolSet = 0, statusSet = 0, pinned = 0, missingSpool = 0, missingStatus = 0;

            // Status step resolution — done ONCE before the loop:
            //   • null  → use historical defaults ("Fabrication Status"
            //             = "Issued for Fabrication") to preserve
            //             pre-Spool-Config behaviour.
            //   • empty → user explicitly opted out in Spool Config; we
            //             skip the status write and don't count
            //             "missing" warnings.
            //   • set   → write the configured value to the named param.
            string statusParam = string.IsNullOrEmpty(statusParamName)
                ? SpoolNumberRegistry.FabricationStatusParam
                : statusParamName!;
            string statusValue = statusParamValue ?? SpoolNumberRegistry.FabricationStatusValue;
            bool statusActive  = statusParamName == null
                              || !string.IsNullOrWhiteSpace(statusParamName);

            foreach (var id in ids)
            {
                var e = _doc.GetElement(id);
                if (e == null) continue;

                var pSpool = ParameterHelper.FindParameter(e, SpoolNumberRegistry.SpoolNumberParam);
                if (pSpool != null && !pSpool.IsReadOnly && pSpool.StorageType == StorageType.String)
                {
                    pSpool.Set(spoolNumber);
                    spoolSet++;
                }
                else
                {
                    missingSpool++;
                }

                if (statusActive)
                {
                    var pStatus = ParameterHelper.FindParameter(e, statusParam);
                    if (pStatus != null && !pStatus.IsReadOnly && pStatus.StorageType == StorageType.String)
                    {
                        pStatus.Set(statusValue);
                        statusSet++;
                    }
                    else
                    {
                        missingStatus++;
                    }
                }

                // Pinning is skipped when the spool is materialised as an
                // AssemblyInstance — assembly membership already locks the
                // parts in place, and a part can't be both pinned and an
                // assembly member in some Revit versions.
                if (pin && !e.Pinned) { e.Pinned = true; pinned++; }
            }

            if (missingSpool > 0)
                warnings.Add($"\"Spool Number\" parameter missing or read-only on {missingSpool} element(s).");
            if (missingStatus > 0)
                warnings.Add($"\"{statusParam}\" parameter missing or read-only on {missingStatus} element(s).");

            return warnings;
        }

        // ── Step 2.5: place tags on plan + section views ────────────────────────

        /// <summary>Drops the chosen tag-family symbol on each spool part at the
        /// part's world-space bounding-box center. Revit projects the XYZ onto
        /// the view plane for plan / section views and onto the locked screen
        /// orientation for 3D iso views (which is fine because our isos are
        /// locked via SaveOrientationAndLock). Failures (incompatible tag
        /// family, etc.) are counted and surfaced as a single warning.</summary>
        private void PlaceTagsOnViews(
            IReadOnlyCollection<ElementId> partIds,
            Dictionary<SpoolDirection, ElementId> views,
            ElementId tagTypeId,
            bool placeLeader,
            LeaderEndCondition leaderEnd,
            double leaderLengthFt,
            bool enhanced,
            double tagOffsetInches,
            List<string> warnings)
        {
            int failed = 0;
            double paperOffsetInches = tagOffsetInches > 0 ? tagOffsetInches : 1.0;
            foreach (var kv in views)
            {
                var view = _doc.GetElement(kv.Value) as View;
                if (view == null) continue;

                double scaleD       = view.Scale > 0 ? view.Scale : 48.0;
                double modelOffsetFt = paperOffsetInches * scaleD / 12.0;
                var up    = view.UpDirection?.Normalize()    ?? XYZ.BasisZ;
                var right = view.RightDirection?.Normalize() ?? XYZ.BasisX;

                if (!enhanced)
                {
                    foreach (var partId in partIds)
                    {
                        var part = _doc.GetElement(partId);
                        if (part == null) continue;
                        var bb = part.get_BoundingBox(null);
                        if (bb == null) continue;
                        var centre = (bb.Min + bb.Max) * 0.5;
                        try
                        {
                            var partRef = new Reference(part);
                            XYZ tagHead = placeLeader ? centre + up * modelOffsetFt : centre;
                            var tag = IndependentTag.Create(
                                _doc, tagTypeId, view.Id, partRef,
                                addLeader: placeLeader,
                                TagOrientation.Horizontal,
                                tagHead);
                            if (placeLeader && tag != null)
                                ApplyLeaderSettings(tag, partRef, leaderEnd,
                                                    leaderLengthFt, centre, tagHead);
                        }
                        catch { failed++; }
                    }
                    continue;
                }

                // Skip-self + tiny inflation so the loop only diverts
                // when the tag would actually overlap a NEIGHBOURING
                // part, not its own pipe extending past 1" up.
                double inflateFt = 0.25 * scaleD / 12.0;

                var partAabbs = new Dictionary<ElementId, (double minX, double maxX, double minY, double maxY)>();
                foreach (var pid in partIds)
                {
                    var p = _doc.GetElement(pid);
                    if (p == null) continue;
                    var pb = p.get_BoundingBox(view);
                    if (pb == null) continue;
                    var aabb = ProjectToViewAabb(pb, right, up);
                    partAabbs[pid] = (aabb.minX - inflateFt, aabb.maxX + inflateFt,
                                     aabb.minY - inflateFt, aabb.maxY + inflateFt);
                }

                // Tag family bbox — 0.5" × 0.4" paper. Sized close
                // to a real circle-around-digit render so overlap
                // checks reflect actual visual clash, not a false
                // positive from an inflated estimate.
                double tagW = 0.5 * scaleD / 12.0;
                double tagH = 0.4 * scaleD / 12.0;

                // Compass at 15° increments — 24 directions ordered
                // by angular distance from UP, sides alternating.
                double[] angleOrderDeg =
                {
                    0,
                    15, -15,
                    30, -30,
                    45, -45,
                    60, -60,
                    75, -75,
                    90, -90,
                    105, -105,
                    120, -120,
                    135, -135,
                    150, -150,
                    165, -165,
                    180
                };
                var compassPriority = new XYZ[angleOrderDeg.Length];
                for (int i = 0; i < angleOrderDeg.Length; i++)
                {
                    double rad = angleOrderDeg[i] * Math.PI / 180.0;
                    compassPriority[i] = (up * Math.Cos(rad) + right * Math.Sin(rad)).Normalize();
                }
                double[] tierMults = { 1.0, 1.5, 2.0 };

                // Small-overlap tolerance — 10% of tag area counts as
                // clear enough. A preferred direction with a sliver
                // of overlap wins over drifting to a less-preferred
                // direction.
                double clearThreshold = tagW * tagH * 0.10;

                var placedTagAabbs = new List<(double minX, double maxX, double minY, double maxY)>();

                // Priority ordering by Item Number — tag #1 places at
                // ideal offset first, later tags avoid it.
                var orderedIds = partIds
                    .OrderBy(id => TryReadTagNumber(id) ?? int.MaxValue)
                    .ThenBy(id => id.Value)
                    .ToList();

                foreach (var partId in orderedIds)
                {
                    var part = _doc.GetElement(partId);
                    if (part == null) continue;

                    var bb = part.get_BoundingBox(null);
                    if (bb == null) continue;
                    var centre = (bb.Min + bb.Max) * 0.5;

                    try
                    {
                        var partRef = new Reference(part);

                        XYZ defaultHead = placeLeader
                            ? centre + up * modelOffsetFt
                            : centre;
                        XYZ tagHead = defaultHead;
                        (double minX, double maxX, double minY, double maxY)? chosenTagAabb = null;

                        if (placeLeader)
                        {
                            // Shape-aware direction ladder up front,
                            // then 24-direction compass tier-major so
                            // UP (index 0) tried first at each tier,
                            // then ±15°, ±30°, …, 180°.
                            var perPart = new List<(XYZ Dir, double Mult)>(3 + 3 * 24);
                            var shape = TryGetShapeAwareTagDirection(part, centre, right, up);
                            if (shape != null)
                            {
                                foreach (var m in tierMults)
                                    perPart.Add((shape.Value.Dir, m));
                            }
                            foreach (var m in tierMults)
                                foreach (var d in compassPriority)
                                    perPart.Add((d, m));

                            double centreX = centre.DotProduct(right);
                            double centreY = centre.DotProduct(up);

                            double bestBadScore = double.MaxValue;
                            XYZ? bestBadTrial = null;
                            (double minX, double maxX, double minY, double maxY)? bestBadAabb = null;

                            foreach (var c in perPart)
                            {
                                XYZ trial = centre + c.Dir * (modelOffsetFt * c.Mult);
                                double cx = trial.DotProduct(right);
                                double cy = trial.DotProduct(up);
                                var trialAabb = (
                                    minX: cx - tagW * 0.5, maxX: cx + tagW * 0.5,
                                    minY: cy - tagH * 0.5, maxY: cy + tagH * 0.5);

                                // TAG-vs-TAG overlap is scored
                                // separately and gated STRICTLY —
                                // any overlap disqualifies.
                                double tagOverlap = 0;
                                foreach (var ta in placedTagAabbs)
                                    tagOverlap += AabbOverlapArea(trialAabb, ta);

                                // PART overlap + leader crossing use
                                // the threshold.
                                double partScore = 0;
                                foreach (var kvp in partAabbs)
                                {
                                    if (kvp.Key == partId) continue;
                                    partScore += AabbOverlapArea(trialAabb, kvp.Value);
                                    if (SegmentIntersectsAabb(
                                            centreX, centreY, cx, cy, kvp.Value))
                                    {
                                        partScore += tagW * tagH * 5;
                                    }
                                }

                                double totalScore = tagOverlap + partScore;

                                if (tagOverlap <= clearThreshold &&
                                    partScore <= clearThreshold)
                                {
                                    tagHead = trial;
                                    chosenTagAabb = trialAabb;
                                    break;
                                }
                                if (totalScore < bestBadScore)
                                {
                                    bestBadScore = totalScore;
                                    bestBadTrial = trial;
                                    bestBadAabb  = trialAabb;
                                }
                            }
                            if (chosenTagAabb == null)
                            {
                                if (bestBadTrial != null && bestBadAabb != null)
                                {
                                    tagHead = bestBadTrial;
                                    chosenTagAabb = bestBadAabb;
                                }
                                else
                                {
                                    double cx = defaultHead.DotProduct(right);
                                    double cy = defaultHead.DotProduct(up);
                                    chosenTagAabb = (cx - tagW * 0.5, cx + tagW * 0.5,
                                                     cy - tagH * 0.5, cy + tagH * 0.5);
                                }
                            }
                            placedTagAabbs.Add(chosenTagAabb.Value);
                        }

                        var tag = IndependentTag.Create(
                            _doc, tagTypeId, view.Id, partRef,
                            addLeader: placeLeader,
                            TagOrientation.Horizontal,
                            tagHead);

                        if (placeLeader && tag != null)
                        {
                            // Pipes AND elbows get Free-End anchored at
                            // the part's centre. For elbows this
                            // pulls the leader off the middle of the
                            // bend body instead of Revit's default
                            // closest-point auto-attach (which lands
                            // near a connector/weld).
                            bool anchorLeaderAtCentre =
                                IsPipeType(part) || IsElbowType(part);
                            ApplyLeaderSettings(tag, partRef, leaderEnd,
                                                leaderLengthFt, centre, tagHead,
                                                forceFreeEndAtCentre: anchorLeaderAtCentre);
                        }
                    }
                    catch
                    {
                        failed++;
                    }
                }
            }
            if (failed > 0)
                warnings.Add($"{failed} tag placement(s) failed (tag family may not support the view kind).");
        }

        /// <summary>Elbow inside-radial / pipe perp / tee branch-run
        /// bisector, projected onto the view plane. Returns (Dir,
        /// Origin) — for elbows, Origin is the ARC MIDPOINT so the
        /// trial position lies dead-on the radial line from the arc
        /// centre. Null when not applicable.</summary>
        private (XYZ Dir, XYZ Origin)? TryGetShapeAwareTagDirection(
            Element part, XYZ partCentre, XYZ right, XYZ up)
        {
            if (part is not FabricationPart fp) return null;

            string type;
            try { type = PartTypeClassifier.GetPcfType(fp) ?? ""; }
            catch { return null; }

            var conns = GetConnectorOriginsAndDirections(fp);
            XYZ? world = null;

            if (string.Equals(type, "ELBOW", StringComparison.OrdinalIgnoreCase) && conns.Count == 2)
            {
                var sum = conns[0].Dir + conns[1].Dir;
                if (sum.GetLength() >= 0.01)
                    world = sum.Normalize();
            }
            else if (string.Equals(type, "PIPE", StringComparison.OrdinalIgnoreCase) && conns.Count >= 2)
            {
                double ax = conns[0].Dir.DotProduct(right);
                double ay = conns[0].Dir.DotProduct(up);
                double m  = Math.Sqrt(ax * ax + ay * ay);
                if (m >= 0.3)
                {
                    ax /= m; ay /= m;
                    double px1 = -ay, py1 = ax;
                    double px2 =  ay, py2 = -ax;
                    bool pickFirst = py1 > py2
                                   || (Math.Abs(py1 - py2) < 1e-6 && px1 >= px2);
                    double px = pickFirst ? px1 : px2;
                    double py = pickFirst ? py1 : py2;
                    return ((right * px + up * py).Normalize(), partCentre);
                }
                return null;
            }
            else if (string.Equals(type, "TEE", StringComparison.OrdinalIgnoreCase) && conns.Count == 3)
            {
                int branchIdx = -1;
                for (int i = 0; i < 3; i++)
                {
                    int a = (i + 1) % 3, b = (i + 2) % 3;
                    if (conns[a].Dir.DotProduct(conns[b].Dir) < -0.9)
                    {
                        branchIdx = i;
                        break;
                    }
                }
                if (branchIdx >= 0)
                {
                    var branch = conns[branchIdx].Dir;
                    XYZ runA = conns[(branchIdx + 1) % 3].Dir;
                    XYZ runB = conns[(branchIdx + 2) % 3].Dir;
                    XYZ run = runA.DotProduct(up) >= runB.DotProduct(up) ? runA : runB;
                    var bis = branch + run;
                    if (bis.GetLength() >= 0.01)
                        world = bis.Normalize();
                }
            }

            if (world == null) return null;

            double sx = world.DotProduct(right);
            double sy = world.DotProduct(up);
            double mag = Math.Sqrt(sx * sx + sy * sy);
            if (mag < 0.3) return null;
            return ((right * sx + up * sy).Normalize(), partCentre);
        }

        /// <summary>Item Number → int, or null when missing/unset.</summary>
        private int? TryReadTagNumber(ElementId id)
        {
            var el = _doc.GetElement(id);
            if (el == null) return null;
            try
            {
                var p = el.LookupParameter("Item Number");
                if (p == null) return null;
                if (p.StorageType == StorageType.Integer)
                {
                    int v = p.AsInteger();
                    return v == 0 ? null : v;
                }
                if (p.StorageType == StorageType.String)
                {
                    var s = p.AsString();
                    if (int.TryParse(s, out var n)) return n;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Liang-Barsky segment-vs-AABB clipping. True when
        /// the segment (x1,y1)→(x2,y2) intersects the axis-aligned
        /// box. Used to reject leaders that would cut through
        /// neighbouring parts.</summary>
        private static bool SegmentIntersectsAabb(
            double x1, double y1, double x2, double y2,
            (double minX, double maxX, double minY, double maxY) box)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            double tmin = 0.0, tmax = 1.0;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { x1 - box.minX, box.maxX - x1, y1 - box.minY, box.maxY - y1 };
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(p[i]) < 1e-12)
                {
                    if (q[i] < 0) return false;
                }
                else
                {
                    double t = q[i] / p[i];
                    if (p[i] < 0) { if (t > tmin) tmin = t; }
                    else          { if (t < tmax) tmax = t; }
                    if (tmin > tmax) return false;
                }
            }
            return true;
        }

        private static List<(XYZ Origin, XYZ Dir)> GetConnectorOriginsAndDirections(FabricationPart part)
        {
            var list = new List<(XYZ, XYZ)>();
            try
            {
                var mgr = part.ConnectorManager;
                if (mgr == null) return list;
                foreach (Connector c in mgr.Connectors)
                {
                    if (c == null) continue;
                    try
                    {
                        var cs = c.CoordinateSystem;
                        if (cs == null) continue;
                        var origin = cs.Origin;
                        var dir    = cs.BasisZ?.Normalize();
                        if (origin == null || dir == null) continue;
                        list.Add((origin, dir));
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        private static XYZ? ComputeLineLineClosestPoint(XYZ p1, XYZ d1, XYZ p2, XYZ d2)
        {
            var w0 = p1 - p2;
            double a = d1.DotProduct(d1);
            double b = d1.DotProduct(d2);
            double c = d2.DotProduct(d2);
            double d = d1.DotProduct(w0);
            double e = d2.DotProduct(w0);
            double denom = a * c - b * b;
            if (Math.Abs(denom) < 1e-9) return null;
            double s = (b * e - c * d) / denom;
            double t = (a * e - b * d) / denom;
            return (p1 + d1 * s + p2 + d2 * t) * 0.5;
        }

        private static List<XYZ> GetConnectorOutDirections(FabricationPart part)
        {
            var list = new List<XYZ>();
            try
            {
                var mgr = part.ConnectorManager;
                if (mgr == null) return list;
                foreach (Connector c in mgr.Connectors)
                {
                    if (c == null) continue;
                    try
                    {
                        var d = c.CoordinateSystem?.BasisZ;
                        if (d == null) continue;
                        list.Add(d.Normalize());
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        /// <summary>Projects the 8 corners of a 3D BoundingBoxXYZ onto
        /// the (<paramref name="right"/>, <paramref name="up"/>) view
        /// plane and returns the AABB of the projection.</summary>
        private static (double minX, double maxX, double minY, double maxY) ProjectToViewAabb(
            BoundingBoxXYZ bb, XYZ right, XYZ up)
        {
            var t = bb.Transform ?? Transform.Identity;
            var c = new XYZ[]
            {
                t.OfPoint(new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z)),
                t.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z)),
                t.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z)),
                t.OfPoint(new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z)),
                t.OfPoint(new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z)),
                t.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z)),
                t.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z)),
                t.OfPoint(new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)),
            };
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < c.Length; i++)
            {
                double x = c[i].DotProduct(right);
                double y = c[i].DotProduct(up);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            return (minX, maxX, minY, maxY);
        }

        private static bool AabbsOverlap(
            (double minX, double maxX, double minY, double maxY) a,
            (double minX, double maxX, double minY, double maxY) b)
        {
            return a.maxX > b.minX && a.minX < b.maxX
                && a.maxY > b.minY && a.minY < b.maxY;
        }

        /// <summary>Area of the intersection of two AABBs. 0 when
        /// they don't overlap. Used to score tag candidates so the
        /// least-bad option wins when nothing is fully clear.</summary>
        private static double AabbOverlapArea(
            (double minX, double maxX, double minY, double maxY) a,
            (double minX, double maxX, double minY, double maxY) b)
        {
            double dx = Math.Min(a.maxX, b.maxX) - Math.Max(a.minX, b.minX);
            if (dx <= 0) return 0;
            double dy = Math.Min(a.maxY, b.maxY) - Math.Max(a.minY, b.minY);
            if (dy <= 0) return 0;
            return dx * dy;
        }

        /// <summary>True when the part is a PIPE per the PCF type
        /// registry — used to trigger the Free-End override so the
        /// leader anchors at the pipe centerline midpoint.</summary>
        private static bool IsPipeType(Element part)
        {
            if (part is not FabricationPart fp) return false;
            try
            {
                return string.Equals(
                    PartTypeClassifier.GetPcfType(fp) ?? "",
                    "PIPE",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>True when the part is an ELBOW — used to trigger
        /// the Free-End override so the leader anchors at the elbow's
        /// centre instead of Revit's closest-point auto-attach.</summary>
        private static bool IsElbowType(Element part)
        {
            if (part is not FabricationPart fp) return false;
            try
            {
                return string.Equals(
                    PartTypeClassifier.GetPcfType(fp) ?? "",
                    "ELBOW",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>Applies the user's Leader Settings to a just-created tag.
        /// Matches Revit's native Place Tag behavior:
        /// <list type="bullet">
        ///   <item>Attached End: leader endpoint stays glued to the part. If
        ///         <paramref name="leaderLengthFt"/> &gt; 0 the elbow is
        ///         positioned at that distance from the part centre along
        ///         the (head - centre) direction, so the leader has a
        ///         shoulder segment of that length.</item>
        ///   <item>Free End: leader endpoint is anchored at the part centre.
        ///         When <paramref name="elbowPoint"/> is provided (interactive
        ///         flow's 2-pick = elbow + head), the leader bends there to
        ///         match Revit's native "leader line + landing" geometry.
        ///         Without an elbow pick (auto-place), the leader is drawn
        ///         straight from part centre to tag head and the user can
        ///         drag the elbow after placement. Leader Length is ignored
        ///         for Free leaders (matches Revit's own options bar).</item>
        /// </list>
        /// <see cref="IndependentTag.LeaderEndCondition"/> must be flipped
        /// BEFORE <c>SetLeaderEnd</c> / <c>SetLeaderElbow</c> are called or
        /// the API rejects them.</summary>
        private static void ApplyLeaderSettings(
            IndependentTag tag,
            Reference partRef,
            LeaderEndCondition leaderEnd,
            double leaderLengthFt,
            XYZ partCentre,
            XYZ tagHead,
            XYZ? elbowPoint = null,
            bool forceFreeEndAtCentre = false)
        {
            // Pipe override: force Free End with the endpoint pinned
            // at the pipe centerline midpoint so the leader visually
            // comes off dead-centre of the pipe run.
            var effectiveEnd = forceFreeEndAtCentre ? LeaderEndCondition.Free : leaderEnd;
            try { tag.LeaderEndCondition = effectiveEnd; } catch { /* some families lock end style */ }

            if (effectiveEnd == LeaderEndCondition.Free)
            {
                // Anchor the free endpoint at part centre — without this call
                // the leader stays auto-attached to the part edge even after
                // the enum flips to Free.
                try { tag.SetLeaderEnd(partRef, partCentre); } catch { }

                // User-picked elbow gives the leader its bend; without one
                // Revit draws a straight line from endpoint to tag head.
                if (elbowPoint != null)
                {
                    try { tag.SetLeaderElbow(partRef, elbowPoint); } catch { }
                }

                // SetLeaderEnd / SetLeaderElbow make Revit recompute leader
                // geometry and silently shift the head off the click toward
                // the part. Pin the head back to the user's pick.
                try { tag.TagHeadPosition = tagHead; } catch { }
            }
            else // Attached
            {
                // Attached End: leader is a STRAIGHT line from the
                // part edge to the tag head. The tag type often
                // defaults LEADER_OFFSET_SHEET to a non-zero shoulder
                // segment, producing an unwanted bend/jag. Force it
                // to 0 on this instance so the leader stays straight.
                // Leader Length is a Free-End-only concept in this
                // tool (per the Leader Settings dialog).
                try
                {
                    var p = tag.get_Parameter(BuiltInParameter.LEADER_OFFSET_SHEET);
                    if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double)
                        p.Set(0.0);
                }
                catch { /* parameter not applicable to this tag family */ }

                try { tag.TagHeadPosition = tagHead; } catch { }
            }
        }

        /// <summary>Interactive variant of PlaceTagsOnViews. Walks the parts
        /// in Item Number order on each spool view, highlighting the part
        /// being tagged via Revit's selection (so the user sees a blue
        /// glow on the target) then capturing a free click via PickPoint.
        /// PickPoint is used (NOT PickObject) so the click can land in
        /// empty space and so SetElementIds' highlight survives the pick.
        /// PickPoint requires an active work plane — we install one
        /// per-view via <see cref="EnsureViewSketchPlane"/> before the
        /// part loop (plans already have a level work plane). Leaders
        /// are enabled so the tag can sit away from the part itself.
        /// Each successful pick commits in its own sub-transaction so the
        /// tag drops in immediately; the outer spool TransactionGroup
        /// still collapses to one undo step. Esc on any prompt skips
        /// that one tag.</summary>
        private void PlaceTagsInteractive(
            IReadOnlyCollection<ElementId> partIds,
            Dictionary<SpoolDirection, ElementId> views,
            ElementId tagTypeId,
            bool placeLeader,
            LeaderEndCondition leaderEnd,
            double leaderLengthFt,
            List<string> warnings)
        {
            var partsSorted = partIds
                .Select(id => _doc.GetElement(id))
                .Where(e => e != null)
                .OrderBy(e => ReadItemNumberSortKey(e!))
                .ThenBy(e => e!.Id.Value)
                .ToList();

            View? prevActive = _uiDoc.ActiveView;
            int skipped = 0, failed = 0;
            var solidFillId = GetSolidFillPatternId();

            foreach (var kv in views)
            {
                var view = _doc.GetElement(kv.Value) as View;
                if (view == null) continue;

                try { _uiDoc.ActiveView = view; } catch { continue; }

                // Sections + 3D iso views have no implicit work plane, so
                // PickPoint throws "No work plane set in current view".
                // Install one perpendicular to the view direction before
                // we start picking on this view.
                EnsureViewSketchPlane(view);

                foreach (var part in partsSorted)
                {
                    string item = ReadItemNumberDisplay(part!);
                    string desc = ParameterHelper.FindParameter(part!, "Item Description")?.AsString() ?? part!.Name;

                    // Selection highlight alone isn't reliably visible across
                    // view kinds (especially locked iso views), so we apply a
                    // bright orange graphic override to the part for the
                    // duration of its pick. SetElementIds in addition so the
                    // properties palette also reflects the active part.
                    ApplyHighlightOverride(view, part!.Id, solidFillId);
                    try { _uiDoc.Selection.SetElementIds(new List<ElementId> { part.Id }); } catch { }

                    // Pre-compute the part's world centre once — we'll project
                    // it onto the same plane as the picks before use.
                    var partBb = part.get_BoundingBox(view);
                    var partCentreWorld = partBb != null
                        ? (partBb.Min + partBb.Max) * 0.5
                        : (part.Location is LocationPoint lp ? lp.Point : XYZ.Zero);

                    XYZ headPick   = null!;
                    XYZ? elbowPick = null;
                    bool picked    = false;
                    bool isFreeLeader = placeLeader && leaderEnd == LeaderEndCondition.Free;
                    ElementId previewLineId = ElementId.InvalidElementId;
                    try
                    {
                        if (isFreeLeader)
                        {
                            // Free End matches Revit's native 3-click flow:
                            // element (already pre-selected) → leader elbow
                            // → tag head (end of the landing segment). The
                            // leader's free endpoint stays anchored near the
                            // part and is set programmatically below.
                            elbowPick = _uiDoc.Selection.PickPoint(
                                $"Item {item} ({desc}) on {kv.Key.Label()} — click LEADER ELBOW (end of leader, start of landing). Esc to skip.");

                            // Between the elbow pick and the tag-head pick,
                            // draw a real (visible) detail line from the
                            // part centre to the elbow so the user sees the
                            // leader segment they just defined while they
                            // choose where the landing ends. Best static
                            // approximation of Revit's native rubber-band
                            // preview — its true mouse-tracking preview is
                            // not exposed to add-ins by the API.
                            previewLineId = DrawPreviewLeaderSegment(
                                view, partCentreWorld, elbowPick);

                            headPick = _uiDoc.Selection.PickPoint(
                                $"Item {item} ({desc}) on {kv.Key.Label()} — click TAG POSITION (end of landing). Esc to skip.");
                        }
                        else
                        {
                            // Attached End / no leader: single click for the
                            // tag head. Leader Length (Attached only) sets
                            // the elbow programmatically.
                            headPick = _uiDoc.Selection.PickPoint(
                                $"Item {item} ({desc}) on {kv.Key.Label()} — click TAG POSITION. Esc to skip.");
                        }
                        picked = true;
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        // Revit throws its own OperationCanceledException
                        // type (NOT System.OperationCanceledException) when
                        // the user hits Escape. Skip ONLY this one part and
                        // move on to the next — keep walking the list.
                        skipped++;
                    }
                    finally
                    {
                        ClearHighlightOverride(view, part.Id);
                        if (previewLineId != ElementId.InvalidElementId)
                            DeletePreviewLeaderSegment(previewLineId);
                    }

                    if (!picked || headPick == null) { continue; }

                    // Free End: snap the tag-head pick to whichever screen
                    // axis (horizontal or vertical from the elbow) the user
                    // moved further along, so the landing segment is always
                    // ortho. Revit's own snap engine doesn't catch this
                    // reliably during PickPoint, so we do it post-pick.
                    if (isFreeLeader && elbowPick != null)
                    {
                        headPick = SnapToOrthogonalFromElbow(view, elbowPick, headPick);
                    }

                    // Project the part centre onto the plane containing the
                    // user's picks. PickPoint on a ViewPlan returns points
                    // on the level's work plane, but the part centre may be
                    // mid-pipe Z — that mismatch made the leader render
                    // through 3D space and look erratic across parts. Using
                    // the head pick as the plane anchor guarantees the
                    // leader is coplanar with the elbow + tag head.
                    var partCentre = ProjectOntoPickPlane(view, partCentreWorld, headPick);

                    using var tx = new Transaction(_doc, $"Spool: place tag (Item {item})");
                    tx.Start();
                    try
                    {
                        var partRef = new Reference(part!);
                        var tag = IndependentTag.Create(
                            _doc, tagTypeId, view.Id, partRef,
                            addLeader: placeLeader, TagOrientation.Horizontal, headPick);
                        if (placeLeader && tag != null)
                        {
                            ApplyLeaderSettings(tag, partRef, leaderEnd,
                                                leaderLengthFt, partCentre, headPick, elbowPick);
                        }
                        tx.Commit();
                    }
                    catch
                    {
                        tx.RollBack();
                        failed++;
                    }
                }
            }

            // Clear the highlight selection we set during the loop.
            try { _uiDoc.Selection.SetElementIds(new List<ElementId>()); } catch { }

            // Restore the user's prior active view if it still exists.
            try
            {
                if (prevActive != null && _doc.GetElement(prevActive.Id) != null)
                    _uiDoc.ActiveView = prevActive;
            }
            catch { }

            if (skipped > 0)
                warnings.Add($"{skipped} tag placement(s) skipped (Esc) during interactive tagging.");
            if (failed > 0)
                warnings.Add($"{failed} tag placement(s) failed during interactive tagging.");
        }

        /// <summary>Solid-fill drafting pattern id used to paint the highlighted
        /// part. Cached lazily — falls back to InvalidElementId if the doc has
        /// no solid fill (rare; Revit ships one by default).</summary>
        private ElementId _solidFillIdCache = ElementId.InvalidElementId;
        private bool _solidFillResolved;
        private ElementId GetSolidFillPatternId()
        {
            if (_solidFillResolved) return _solidFillIdCache;
            try
            {
                var pat = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(f =>
                    {
                        try { var fp = f.GetFillPattern(); return fp != null && fp.IsSolidFill; }
                        catch { return false; }
                    });
                _solidFillIdCache = pat?.Id ?? ElementId.InvalidElementId;
            }
            catch { _solidFillIdCache = ElementId.InvalidElementId; }
            _solidFillResolved = true;
            return _solidFillIdCache;
        }

        /// <summary>Paints the part bright orange in the given view so the user
        /// sees what they're being asked to tag. Visible across plan, section,
        /// and 3D iso (selection highlight alone isn't reliably visible in
        /// locked iso views). Runs in its own sub-transaction; the outer
        /// TransactionGroup collapses everything to one undo step.</summary>
        private void ApplyHighlightOverride(View view, ElementId partId, ElementId solidFillId)
        {
            try
            {
                using var tx = new Transaction(_doc, "Spool: highlight part for tagging");
                tx.Start();

                var orange = new Autodesk.Revit.DB.Color(255, 100, 0);
                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(orange);
                ogs.SetProjectionLineWeight(8);
                ogs.SetCutLineColor(orange);
                ogs.SetCutLineWeight(8);
                if (solidFillId != ElementId.InvalidElementId)
                {
                    ogs.SetSurfaceForegroundPatternColor(orange);
                    ogs.SetSurfaceForegroundPatternId(solidFillId);
                    ogs.SetCutForegroundPatternColor(orange);
                    ogs.SetCutForegroundPatternId(solidFillId);
                }
                ogs.SetHalftone(false);

                view.SetElementOverrides(partId, ogs);
                tx.Commit();
            }
            catch { /* override is cosmetic — don't fail the pick */ }
        }

        /// <summary>Removes the orange override applied by
        /// <see cref="ApplyHighlightOverride"/>. Always runs (even on Esc) so
        /// we don't leave a highlighted part behind.</summary>
        private void ClearHighlightOverride(View view, ElementId partId)
        {
            try
            {
                using var tx = new Transaction(_doc, "Spool: clear part highlight");
                tx.Start();
                view.SetElementOverrides(partId, new OverrideGraphicSettings());
                tx.Commit();
            }
            catch { /* worst case the override outlives the spool — user can clear via VG */ }
        }

        /// <summary>Snaps the picked tag-head point to the closer screen
        /// axis (horizontal or vertical) extending from the elbow, so the
        /// landing segment is exactly orthogonal in the view. The dominant
        /// axis (whichever the cursor was further from the elbow along) is
        /// kept; the perpendicular component is zeroed. Falls through and
        /// returns the original pick if the view's screen axes aren't
        /// readable (rare; defensive only).</summary>
        private static XYZ SnapToOrthogonalFromElbow(View view, XYZ elbow, XYZ pick)
        {
            try
            {
                var right = view.RightDirection;
                var up    = view.UpDirection;
                if (right == null || up == null) return pick;
                if (right.IsZeroLength() || up.IsZeroLength()) return pick;
                var r = right.Normalize();
                var u = up.Normalize();

                var d = pick - elbow;
                var alongRight = d.DotProduct(r);
                var alongUp    = d.DotProduct(u);

                return Math.Abs(alongRight) >= Math.Abs(alongUp)
                    ? elbow + r * alongRight   // horizontal landing
                    : elbow + u * alongUp;     // vertical landing
            }
            catch { return pick; }
        }

        /// <summary>Draws a temporary detail line in <paramref name="view"/>
        /// from the part centre (projected onto the pick plane) to the
        /// elbow click, then applies a bright-orange graphic override so it
        /// reads as a preview, not a final line. Best static stand-in for
        /// Revit's native rubber-band preview (which isn't exposed to
        /// add-ins). Detail curves only work on 2D views — for View3D
        /// (iso) the method no-ops and returns
        /// <see cref="ElementId.InvalidElementId"/>. The line is removed
        /// by <see cref="DeletePreviewLeaderSegment"/> in the finally
        /// block of the per-part pick loop, so it never leaks.</summary>
        private ElementId DrawPreviewLeaderSegment(View view, XYZ partCentreWorld, XYZ elbowPick)
        {
            if (view is View3D) return ElementId.InvalidElementId;   // NewDetailCurve unsupported on 3D
            try
            {
                var anchor = ProjectOntoPickPlane(view, partCentreWorld, elbowPick);
                if (anchor.IsAlmostEqualTo(elbowPick)) return ElementId.InvalidElementId;

                using var tx = new Transaction(_doc, "Spool: preview leader segment");
                tx.Start();
                var line = Line.CreateBound(anchor, elbowPick);
                var dc = _doc.Create.NewDetailCurve(view, line);
                if (dc != null)
                {
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(new Autodesk.Revit.DB.Color(255, 100, 0));
                    ogs.SetProjectionLineWeight(6);
                    view.SetElementOverrides(dc.Id, ogs);
                }
                tx.Commit();
                return dc?.Id ?? ElementId.InvalidElementId;
            }
            catch { return ElementId.InvalidElementId; }
        }

        /// <summary>Deletes the preview detail line created by
        /// <see cref="DrawPreviewLeaderSegment"/>. Runs in the per-part
        /// finally block regardless of whether the user picked or Esc'd
        /// the second pick.</summary>
        private void DeletePreviewLeaderSegment(ElementId lineId)
        {
            try
            {
                using var tx = new Transaction(_doc, "Spool: remove preview leader");
                tx.Start();
                if (_doc.GetElement(lineId) != null) _doc.Delete(lineId);
                tx.Commit();
            }
            catch { /* sub-transaction failures are cosmetic */ }
        }

        /// <summary>Projects a world-space point onto the plane that contains
        /// <paramref name="planeAnchor"/> and is perpendicular to the view
        /// direction. Used to keep tag-leader endpoints coplanar with the
        /// picks the user actually made — without this, the part's
        /// world-space centre sits off the picked plane (e.g., mid-pipe Z
        /// vs level Z on a Top view) and Revit renders the leader through
        /// 3D space, producing inconsistent geometry across parts.</summary>
        private static XYZ ProjectOntoPickPlane(View view, XYZ point, XYZ planeAnchor)
        {
            try
            {
                var normal = view.ViewDirection;
                if (normal == null || normal.IsZeroLength()) return point;
                var n = normal.Normalize();
                var distance = (point - planeAnchor).DotProduct(n);
                return point - n * distance;
            }
            catch { return point; }
        }

        /// <summary>PickPoint requires the active view to have a work plane.
        /// ViewPlan inherits one from its level; ViewSection and View3D do
        /// not, so we install a SketchPlane perpendicular to the view's
        /// own ViewDirection. For section-boxed 3D views we anchor at the
        /// section-box centre (so the picked work plane intersects the
        /// spool parts, not the world origin) — otherwise picks made far
        /// off-axis project the cursor onto a plane through (0,0,0) and
        /// land in unrelated screen space.</summary>
        private void EnsureViewSketchPlane(View view)
        {
            if (view is ViewPlan) return;   // level provides the work plane

            try
            {
                XYZ origin;
                if (view is View3D v3 && v3.IsSectionBoxActive)
                {
                    var sb = v3.GetSectionBox();
                    var midLocal = (sb.Min + sb.Max) * 0.5;
                    origin = sb.Transform.OfPoint(midLocal);
                }
                else
                {
                    origin = view.Origin;
                }

                using var tx = new Transaction(_doc, "Spool: ensure view sketch plane");
                tx.Start();
                var plane = Plane.CreateByNormalAndOrigin(view.ViewDirection, origin);
                var sp    = SketchPlane.Create(_doc, plane);
                view.SketchPlane = sp;
                tx.Commit();
            }
            catch { /* PickPoint will surface its own error if this didn't take */ }
        }

        private static int ReadItemNumberSortKey(Element e)
        {
            var p = ParameterHelper.FindParameter(e, "Item Number");
            if (p == null) return int.MaxValue;
            return p.StorageType switch
            {
                StorageType.Integer => p.AsInteger(),
                StorageType.String  => int.TryParse(p.AsString(), out int n) ? n : int.MaxValue,
                _ => int.MaxValue,
            };
        }

        private static string ReadItemNumberDisplay(Element e)
        {
            var p = ParameterHelper.FindParameter(e, "Item Number");
            if (p == null) return "?";
            return p.StorageType switch
            {
                StorageType.Integer => p.AsInteger().ToString(),
                StorageType.String  => p.AsString() ?? "?",
                _ => "?",
            };
        }

        // ── Step 3: sheet + viewports + schedule ────────────────────────────────

        private ElementId BuildSheet(
            SpoolRequest req,
            Dictionary<SpoolDirection, ElementId> views,
            List<string> warnings,
            ElementId? prebuiltSheetId = null)
        {
            // Load the user-picked drawable region for the chosen titleblock. The
            // dialog should not allow Create Spool to be clicked until this exists,
            // so a null here is a programming error — surface it loudly.
            var region = SpoolTitleblockRegions.Get(_doc, req.TitleblockTypeId!.Value);
            if (region == null)
                throw new InvalidOperationException(
                    "No drawable region defined for the chosen titleblock. Pick the view + schedule regions via the dialog before creating a spool.");

            // Use the prebuilt sheet when supplied (assembly path — the
            // AssemblyViewUtils.CreateSheet call already produced one).
            // Otherwise create a new ViewSheet. Either way, set the
            // SheetNumber and Name from the request.
            ViewSheet sheet;
            if (prebuiltSheetId != null && _doc.GetElement(prebuiltSheetId) is ViewSheet existing)
            {
                sheet = existing;
            }
            else
            {
                sheet = ViewSheet.Create(_doc, req.TitleblockTypeId);
            }
            try { sheet.SheetNumber = req.SheetNumber; } catch { /* duplicate # — leave whatever it had */ }
            try { sheet.Name = req.SheetName; } catch { /* duplicate name — try suffix */ sheet.Name = req.SheetName + " "; }

            // Compute layout cells inside the picked view region.
            var layout = new SpoolSheetLayout();
            var cells  = layout.Compute(region, views.Keys);

            // Scale was already applied to the views BEFORE tagging (so tags
            // rendered at the final size). Reapply here defensively in case
            // anything between then and now changed the scale.
            int scale = ChooseScaleForRequest(req, views);
            foreach (var kv in views)
            {
                var view = _doc.GetElement(kv.Value) as View;
                if (view == null) continue;
                try { view.Scale = scale; } catch { /* some VFTs lock scale */ }
            }
            _doc.Regenerate();

            foreach (var kv in views)
            {
                if (!cells.TryGetValue(kv.Key, out var cell)) continue;
                try
                {
                    Viewport.Create(_doc, sheet.Id, kv.Value, cell.Centre);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not place {kv.Key.Label()} viewport: {ex.Message}");
                }
            }

            // Schedule placement: anchored at top-left of the picked schedule
            // region. Before placing, we shrink the longest-text column on the
            // schedule definition so the whole thing fits the picked width —
            // this is a GLOBAL change to the schedule (affects every sheet
            // using it) per user's explicit choice.
            if (req.ScheduleId != null)
            {
                try
                {
                    if (_doc.GetElement(req.ScheduleId) is ViewSchedule sched)
                        ConstrainScheduleWidth(sched, region, warnings);

                    ScheduleSheetInstance.Create(_doc, sheet.Id, req.ScheduleId, SpoolSheetLayout.ScheduleAnchor(region));
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not place schedule: {ex.Message}");
                }
            }

            return sheet.Id;
        }

        /// <summary>Fits the schedule into the picked schedule region. Logic:
        /// <list type="number">
        ///   <item>If the schedule's natural total width (sum of visible
        ///         columns) already fits the picked region with the 1/4"
        ///         buffer, change nothing.</item>
        ///   <item>Otherwise the column with the most characters of cell
        ///         text (header OR body) becomes the WRAP column.</item>
        ///   <item>Every other VISIBLE column keeps its schedule-view width.
        ///         The wrap column shrinks to absorb the difference, floored
        ///         at 2".</item>
        ///   <item>If the wrap column at 2" still can't make the total fit,
        ///         the schedule overflows the picked region; we warn with
        ///         the exact overflow distance instead of mangling the
        ///         other columns.</item>
        /// </list>
        /// Mutates the schedule definition GLOBALLY per user's explicit choice.
        /// Hidden fields are ignored — they don't render, so they don't add
        /// to the total width.</summary>
        private static void ConstrainScheduleWidth(
            ViewSchedule schedule, TitleblockRegion region, List<string> warnings)
        {
            var def = schedule.Definition;
            if (def == null) return;

            int n = def.GetFieldCount();
            if (n == 0) return;

            var fields  = new ScheduleField[n];
            var widths  = new double[n];
            var visible = new List<int>();
            double total = 0;
            for (int i = 0; i < n; i++)
            {
                fields[i] = def.GetField(i);
                if (fields[i].IsHidden) continue;       // hidden columns don't render
                widths[i] = fields[i].GridColumnWidth;
                total += widths[i];
                visible.Add(i);
            }
            if (visible.Count == 0) return;

            double target =
                (region.ScheduleMax.X - region.ScheduleMin.X) - 2 * SpoolSheetLayout.InnerBufferFt;

            if (total <= target) return;     // natural widths already fit — leave alone

            // Find the visible column whose longest cell text (header OR body)
            // has the most characters. That's the wrap column.
            var maxChars = MaxCharsPerColumn(schedule, n);
            int wrapCol  = visible[0];
            foreach (int idx in visible)
                if (maxChars[idx] > maxChars[wrapCol]) wrapCol = idx;

            // Keep every OTHER visible column at its schedule-view width.
            // The wrap column absorbs the entire shrink.
            const double MinWrapColFt = 2.0 / 12.0;
            double othersSum = total - widths[wrapCol];
            double wrapWidth = target - othersSum;

            if (wrapWidth < MinWrapColFt)
            {
                wrapWidth = MinWrapColFt;
                double overflowFt = (othersSum + wrapWidth) - target;
                warnings.Add(
                    $"Schedule \"{schedule.Name}\" extends ~{overflowFt * 12:F2}\" past the " +
                    $"picked schedule region. Wrap column '{fields[wrapCol].ColumnHeading}' " +
                    $"is at its 2\" minimum and the other columns are kept at their schedule-" +
                    $"view widths. Widen the schedule region for a clean fit.");
            }

            // Only the wrap column gets written. Non-wrap columns are left
            // untouched at their natural widths.
            fields[wrapCol].GridColumnWidth = wrapWidth;
        }

        /// <summary>Per-column max-character count across header + body cells.</summary>
        private static int[] MaxCharsPerColumn(ViewSchedule schedule, int columnCount)
        {
            var max = new int[columnCount];
            try
            {
                var data = schedule.GetTableData();
                foreach (var section in new[] { SectionType.Header, SectionType.Body })
                {
                    try
                    {
                        var s = data.GetSectionData(section);
                        int rows = s.NumberOfRows;
                        int cols = Math.Min(s.NumberOfColumns, columnCount);
                        for (int r = 0; r < rows; r++)
                        for (int c = 0; c < cols; c++)
                        {
                            string text = schedule.GetCellText(section, r, c) ?? string.Empty;
                            if (text.Length > max[c]) max[c] = text.Length;
                        }
                    }
                    catch { /* some sections aren't readable on every schedule */ }
                }
            }
            catch { /* sane default of zeros */ }
            return max;
        }

        /// <summary>Picks the view scale denominator for this spool — either
        /// the dialog's explicit choice, or the auto-fit computation against
        /// the titleblock's drawable region cells. Shared between the early
        /// pre-tag scale-application pass and BuildSheet's defensive reapply.</summary>
        private int ChooseScaleForRequest(SpoolRequest req, Dictionary<SpoolDirection, ElementId> views)
        {
            if (req.ScaleDenominator is int requested && requested > 0)
                return requested;

            var region = req.TitleblockTypeId != null
                ? SpoolTitleblockRegions.Get(_doc, req.TitleblockTypeId.Value)
                : null;
            if (region == null) return 48; // sensible fallback

            var cells = new SpoolSheetLayout().Compute(region, views.Keys);
            var extents = new Dictionary<SpoolDirection, BoundingBoxXYZ?>();

            if (req.UseAssemblies)
            {
                // Assembly views don't populate CropBox / SectionBox with
                // anything meaningful (Revit treats their crop as
                // "members of the assembly" not a geometric bbox), so
                // ViewWorldExtents reads model-spanning defaults and
                // ChooseScale falls through to the coarsest scale.
                // Derive the extents directly from the selection's world
                // bbox projected per direction — this matches what the
                // non-assembly views would have reported.
                var selBbox = new SpoolViewBuilder(_doc).ComputeSelectionBoundingBox(req.Elements);
                if (selBbox != null)
                {
                    foreach (var d in views.Keys)
                        extents[d] = ProjectSelectionBboxToDirection(selBbox, d);
                }
            }
            else
            {
                foreach (var kv in views)
                    extents[kv.Key] = ViewWorldExtents(_doc.GetElement(kv.Value) as View);
            }

            bool onlyIsos = views.Keys.All(d => d.Kind() == SpoolViewKind.ThreeD);
            return SpoolSheetLayout.ChooseScale(extents, cells, bumpOneStepCoarser: onlyIsos);
        }

        /// <summary>Projects the selection's world AABB onto the screen
        /// plane for the given direction, returning a 2D-in-3D bbox the
        /// layout's ChooseScale can read. Plan / section directions
        /// trivially drop one axis; iso directions reuse the 8-corner
        /// projection logic <see cref="ViewWorldExtents"/> applies to
        /// 3D section boxes. The bbox is pre-expanded by the same 1 ft
        /// margin SpoolViewBuilder bakes into its CropBox/SectionBox
        /// for the non-assembly path, so auto-fit on the assembly path
        /// produces the same scale as the non-assembly path for an
        /// identical selection — without the margin, the projected
        /// extent is tighter and ChooseScale picks a coarser scale
        /// that crowds the sheet edge.</summary>
        private static BoundingBoxXYZ? ProjectSelectionBboxToDirection(BoundingBoxXYZ world, SpoolDirection d)
        {
            const double MarginFt = 1.0;
            var bb = new BoundingBoxXYZ
            {
                Min = new XYZ(world.Min.X - MarginFt, world.Min.Y - MarginFt, world.Min.Z - MarginFt),
                Max = new XYZ(world.Max.X + MarginFt, world.Max.Y + MarginFt, world.Max.Z + MarginFt),
            };

            switch (d)
            {
                case SpoolDirection.Top:
                    return new BoundingBoxXYZ
                    {
                        Min = new XYZ(bb.Min.X, bb.Min.Y, 0),
                        Max = new XYZ(bb.Max.X, bb.Max.Y, 0),
                    };
                case SpoolDirection.Front:
                    return new BoundingBoxXYZ
                    {
                        Min = new XYZ(bb.Min.X, bb.Min.Z, 0),
                        Max = new XYZ(bb.Max.X, bb.Max.Z, 0),
                    };
                case SpoolDirection.Left:
                case SpoolDirection.Right:
                    return new BoundingBoxXYZ
                    {
                        Min = new XYZ(bb.Min.Y, bb.Min.Z, 0),
                        Max = new XYZ(bb.Max.Y, bb.Max.Z, 0),
                    };
                case SpoolDirection.NeIso:
                case SpoolDirection.NwIso:
                case SpoolDirection.SeIso:
                case SpoolDirection.SwIso:
                    // Same 8-corner projection ViewWorldExtents does for
                    // 3D views, with the iso's forward/up derived per
                    // direction. Reuses the iso-fill shrink factor so
                    // the resulting bbox represents the visible spool
                    // rather than the empty corners of the iso AABB.
                    return ProjectBboxForIso(bb, d);
                default:
                    return null;
            }
        }

        /// <summary>Iso direction → forward / up vector mapping. Mirrors
        /// the table <see cref="SpoolViewBuilder"/> uses when orienting
        /// the iso views. The 8 corners of the selection bbox project
        /// onto the screen axes (right = forward × up); the resulting
        /// AABB is shrunk by the same iso-fill factor used elsewhere so
        /// the empty diamond corners don't make ChooseScale go too
        /// coarse.</summary>
        private static BoundingBoxXYZ ProjectBboxForIso(BoundingBoxXYZ world, SpoolDirection d)
        {
            (XYZ forward, XYZ up) = d switch
            {
                SpoolDirection.SwIso => (new XYZ( 1,  1, -1).Normalize(), new XYZ( 1,  1, 2).Normalize()),
                SpoolDirection.SeIso => (new XYZ(-1,  1, -1).Normalize(), new XYZ(-1,  1, 2).Normalize()),
                SpoolDirection.NwIso => (new XYZ( 1, -1, -1).Normalize(), new XYZ( 1, -1, 2).Normalize()),
                SpoolDirection.NeIso => (new XYZ(-1, -1, -1).Normalize(), new XYZ(-1, -1, 2).Normalize()),
                _                    => (XYZ.BasisZ.Negate(), XYZ.BasisY),
            };

            var right = forward.CrossProduct(up).Normalize();

            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = new XYZ(
                    (i & 1) == 0 ? world.Min.X : world.Max.X,
                    (i & 2) == 0 ? world.Min.Y : world.Max.Y,
                    (i & 4) == 0 ? world.Min.Z : world.Max.Z);
                double u = corner.DotProduct(right);
                double v = corner.DotProduct(up);
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }
            const double IsoEffectiveFill = 0.75;
            double cu = (minU + maxU) * 0.5;
            double cv = (minV + maxV) * 0.5;
            double halfW = (maxU - minU) * 0.5 * IsoEffectiveFill;
            double halfH = (maxV - minV) * 0.5 * IsoEffectiveFill;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(cu - halfW, cv - halfH, 0),
                Max = new XYZ(cu + halfW, cv + halfH, 0),
            };
        }

        /// <summary>On-sheet projected extent of a view at scale 1:1, in feet. For
        /// Plan/Section the crop box already represents the on-sheet rectangle.
        /// For 3D iso we have to project the section box's 8 corners onto the
        /// view's screen axes (right + up derived from <see cref="ViewOrientation3D"/>),
        /// otherwise CropBox returns the model bounding box and auto-fit picks
        /// an absurdly coarse scale (1/32"=1' was the symptom).</summary>
        private static BoundingBoxXYZ? ViewWorldExtents(View? view)
        {
            if (view == null) return null;
            try
            {
                if (view is View3D v3d && v3d.IsSectionBoxActive)
                {
                    var sb     = v3d.GetSectionBox();
                    var orient = v3d.GetOrientation();
                    // Right-hand rule: forward × up = right (camera's right on screen).
                    var right  = orient.ForwardDirection.CrossProduct(orient.UpDirection).Normalize();
                    var up     = orient.UpDirection.Normalize();
                    var t      = sb.Transform;

                    double minU = double.MaxValue, maxU = double.MinValue;
                    double minV = double.MaxValue, maxV = double.MinValue;
                    for (int i = 0; i < 8; i++)
                    {
                        var local = new XYZ(
                            (i & 1) == 0 ? sb.Min.X : sb.Max.X,
                            (i & 2) == 0 ? sb.Min.Y : sb.Max.Y,
                            (i & 4) == 0 ? sb.Min.Z : sb.Max.Z);
                        var world = t.OfPoint(local);
                        double u = world.DotProduct(right);
                        double v = world.DotProduct(up);
                        if (u < minU) minU = u;
                        if (u > maxU) maxU = u;
                        if (v < minV) minV = v;
                        if (v > maxV) maxV = v;
                    }
                    // The projected rectangle is a tight AABB of all 8 corners,
                    // but an iso pipe spool fills roughly a diamond inside that
                    // AABB (the corners of the projected box are empty space).
                    // Shrink the reported extent by 0.75 so auto-fit doesn't
                    // pick a needlessly coarse scale because of empty corners.
                    const double IsoEffectiveFill = 0.75;
                    double cu = (minU + maxU) * 0.5;
                    double cv = (minV + maxV) * 0.5;
                    double halfW = (maxU - minU) * 0.5 * IsoEffectiveFill;
                    double halfH = (maxV - minV) * 0.5 * IsoEffectiveFill;
                    return new BoundingBoxXYZ
                    {
                        Min = new XYZ(cu - halfW, cv - halfH, 0),
                        Max = new XYZ(cu + halfW, cv + halfH, 0),
                    };
                }

                // ViewPlan / ViewSection: CropBox is already in on-sheet local coords.
                var crop = view.CropBox;
                return new BoundingBoxXYZ
                {
                    Min = crop.Min,
                    Max = crop.Max,
                };
            }
            catch { return null; }
        }

        // ── Result helpers ──────────────────────────────────────────────────────

        private static SpoolResult Fail(string msg) =>
            new SpoolResult { Success = false, Message = msg };
    }

    internal static class SpoolResultExtensions
    {
        public static SpoolResult CopyWarningsFrom(this SpoolResult target, SpoolResult source)
        {
            foreach (var w in source.Warnings) target.Warnings.Add(w);
            foreach (var l in source.Log)      target.Log.Add(l);
            return target;
        }
    }
}
