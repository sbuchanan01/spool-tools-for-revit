using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>
    /// First-cut spool dimension engine. Walks the parts on a straight
    /// pipe run and emits three stacked dim layers on each enabled
    /// ortho view (ViewPlan / ViewSection):
    ///
    ///   • Layer 1 — VALVE FF-to-FF (innermost). One dim per valve,
    ///     between its two flange faces. The "valve overall length"
    ///     callout from the standard sketch.
    ///   • Layer 2 — SEGMENT FF-to-FF (middle). One dim per pipe span
    ///     between adjacent flange faces, excluding the spans that ARE
    ///     a valve (valves get their own layer-1 dim instead).
    ///   • Layer 3 — OVERALL EOP-to-EOP (outermost). One dim across the
    ///     entire spool, from the upstream pipe's cut end to the
    ///     downstream pipe's cut end.
    ///
    /// Iso views are skipped because Revit Dimension elements can't be
    /// hosted on View3D (the spool's iso views are 3D), and the spool
    /// flow already puts iso views in View3D.
    ///
    /// Non-straight runs (elbows / tees that change the run axis) are
    /// detected and skipped with a warning so callers can decide whether
    /// to fix the selection or live without dims for this spool — a
    /// follow-up pass will handle multi-axis chains and tee branches.
    /// </summary>
    public sealed class SpoolDimensioner
    {
        // Catalog Item IDs from the standard Fabrication palette.
        // Matches what SpoolerRuleEvaluator + SpoolerWeldPostProcessor
        // use to recognise welds; here we additionally need flange and
        // valve so the dim engine can classify endpoints correctly.
        private const int JointCid    = 2522;    // welds, couplings, joint flanges (range filtered below)
        private const int FlangeCid   = 2521;    // flanges (separate from joint flanges via Product Range)
        private const int ValveCid    = 2523;    // valves
        private const int PipeCid     = 2520;    // straight pipe

        // Axis-alignment tolerance for the "all parts share one axis"
        // check. 5° max divergence — anything looser starts pulling in
        // 90° elbows as "straight".
        private const double AxisDotTol = 0.996;   // cos(~5°)

        private readonly Document _doc;

        public SpoolDimensioner(Document doc) => _doc = doc;

        // ── Public entry point ─────────────────────────────────────────────────

        /// <summary>Places dimensions on every enabled ortho view in
        /// <paramref name="viewsByDirection"/>. Caller is responsible
        /// for opening a Transaction on <paramref name="_doc"/> before
        /// calling — dim creation mutates the document.
        ///
        /// Returns the count of dims successfully placed and appends
        /// non-fatal failure reasons to <paramref name="warnings"/>.</summary>
        public int PlaceDimensions(
            IReadOnlyCollection<ElementId> partIds,
            IReadOnlyDictionary<SpoolDirection, ElementId> viewsByDirection,
            SpoolRequest req,
            List<string> warnings)
        {
            warnings ??= new List<string>();
            if (!req.IncludeDimensions || req.DimensionViewMask == 0)
                return 0;
            if (partIds == null || partIds.Count == 0 || viewsByDirection.Count == 0)
                return 0;

            // 1. Snapshot every part's classification + axis-projection.
            var infos = new List<PartInfo>();
            foreach (var id in partIds)
            {
                if (_doc.GetElement(id) is not FabricationPart fp) continue;
                var info = ClassifyPart(fp);
                if (info != null) infos.Add(info);
            }
            if (infos.Count < 2)
            {
                warnings.Add("Dimensions: selection has fewer than 2 parts to dimension between.");
                return 0;
            }

            // Diagnostic breakdown — helps debug classification when
            // dim placement bails. Stays in the warning list even on
            // success so the user can sanity-check what the engine
            // saw.
            int pipes  = infos.Count(i => i.Kind == PartKind.Pipe);
            int flgs   = infos.Count(i => i.Kind == PartKind.Flange);
            int valves = infos.Count(i => i.Kind == PartKind.Valve);
            int fits   = infos.Count(i => i.Kind == PartKind.Fitting);
            int jts    = infos.Count(i => i.Kind == PartKind.Joint);
            warnings.Add($"Dimensions [classification]: {pipes} pipe, {flgs} flange, {valves} valve, {fits} fitting, {jts} joint.");

            // 2. Establish the run axis from the longest pipe (or the
            //    most connector-spanning fitting if no pipes). Bail if
            //    parts don't share a single axis to within ~5°.
            var axis = ResolveRunAxis(infos, warnings);
            if (axis == null) return 0;

            // 3. Order parts along the axis by their centre's projected
            //    position. Re-anchor projections so the upstream-most
            //    part sits at axis-position 0.
            foreach (var info in infos)
                info.AxisPos = axis.Origin.DotProduct(info.Centre - axis.Direction) + 0; // placeholder
            // The above approximation is good enough for a linear sort;
            // we use the simpler "project onto axis direction" instead:
            foreach (var info in infos)
                info.AxisPos = (info.Centre - axis.Origin).DotProduct(axis.Direction);
            infos.Sort((a, b) => a.AxisPos.CompareTo(b.AxisPos));

            // 4. Build the three dim layers as XYZ endpoint pairs. The
            //    actual References get looked up per-view (since face
            //    normals depend on view orientation).
            var chains = BuildDimChains(infos, axis, warnings);
            if (chains.Count == 0)
            {
                warnings.Add("Dimensions: no dim chains produced from selection — likely no flange / valve / EOP endpoints recognised.");
                return 0;
            }

            // 5. For each enabled ortho view, find references and place
            //    dimensions. Iso views are silently ignored.
            int placed = 0;
            foreach (var kv in viewsByDirection)
            {
                if ((req.DimensionViewMask & (1 << (int)kv.Key)) == 0) continue;
                if (IsIsoDirection(kv.Key)) continue;

                if (_doc.GetElement(kv.Value) is not View view) continue;
                placed += PlaceDimensionsOnView(view, axis, chains, req, warnings);
            }
            return placed;
        }

        // ── Part classification ───────────────────────────────────────────────

        private enum PartKind { Pipe, Flange, Valve, Fitting, Joint, Unknown }

        private sealed class PartInfo
        {
            public FabricationPart Part = default!;
            public PartKind Kind;
            public XYZ Centre = XYZ.Zero;       // geometric centre of the part's connectors
            public XYZ NearEnd = XYZ.Zero;      // upstream connector origin (low axis-pos side)
            public XYZ FarEnd  = XYZ.Zero;      // downstream connector origin (high axis-pos side)
            public double AxisPos;              // projection of Centre onto run axis
        }

        private PartInfo? ClassifyPart(FabricationPart fp)
        {
            var info = new PartInfo { Part = fp };

            // Connector survey — we need at least two with origins to
            // anchor the part along an axis. Joints (welds) often have
            // two coincident connector origins; we still capture them so
            // the chain-builder knows they exist.
            var origins = new List<XYZ>();
            try
            {
                foreach (Connector c in fp.ConnectorManager.Connectors)
                {
                    try { origins.Add(c.Origin); }
                    catch { }
                }
            }
            catch { }
            if (origins.Count == 0) return null;

            info.NearEnd = origins[0];
            info.FarEnd  = origins.Count >= 2 ? origins[origins.Count - 1] : origins[0];
            info.Centre  = AverageXYZ(origins);

            // Classification — CID match first (matches the standard
            // catalog and is unambiguous when it works), with a
            // geometric fallback so non-standard catalogs that use
            // different CID values still get useful dim chains. The
            // "is it a pipe?" geometric test is: exactly 2 connectors,
            // straight (vector between them parallel to itself, which
            // is always true), longer than 4 inches end-to-end.
            int cid = 0;
            try { cid = fp.ItemCustomId; } catch { }

            info.Kind = cid switch
            {
                PipeCid   => PartKind.Pipe,
                FlangeCid => PartKind.Flange,
                ValveCid  => PartKind.Valve,
                JointCid  => ClassifyJoint(fp),
                _         => InferKindGeometrically(fp, origins),
            };

            return info;
        }

        /// <summary>Geometric fallback classifier — used when ItemCustomId
        /// doesn't match the standard catalog values. The standard
        /// values (2520 pipe / 2521 flange / 2522 joint / 2523 valve)
        /// vary across catalogs, so we don't want to bail on dim
        /// placement just because someone's project uses different IDs.
        ///
        /// Heuristic:
        ///   • 2 connectors, end-to-end &gt;= 4" → Pipe
        ///   • 2 connectors, &lt; 4" between them → Joint (weld / coupling)
        ///   • 3+ connectors → Fitting (tee / olet / cross)
        ///   • else → Fitting fallback
        ///
        /// Flanges and valves can't be reliably inferred from geometry
        /// alone — they fall into Fitting and will simply not anchor a
        /// dim chain. That's the correct conservative behaviour for
        /// the unknown-catalog case.</summary>
        private static PartKind InferKindGeometrically(FabricationPart fp, List<XYZ> origins)
        {
            if (origins.Count == 2)
            {
                double dist = origins[0].DistanceTo(origins[1]);
                return dist >= 4.0 / 12.0 ? PartKind.Pipe : PartKind.Joint;
            }
            return PartKind.Fitting;
        }

        /// <summary>Discriminates within the joint CID — flanges /
        /// welds / couplings — based on Product Range. Joint-flanges
        /// dimension as flanges (FF endpoints); welds / couplings
        /// don't anchor a dim chain on their own.</summary>
        private static PartKind ClassifyJoint(FabricationPart fp)
        {
            try
            {
                var p = fp.LookupParameter("Product Range");
                if (p?.AsString() is string range)
                {
                    if (range.IndexOf("flange", StringComparison.OrdinalIgnoreCase) >= 0)
                        return PartKind.Flange;
                }
            }
            catch { }
            return PartKind.Joint;
        }

        // ── Run axis resolution ───────────────────────────────────────────────

        private sealed class RunAxis
        {
            public XYZ Origin    = XYZ.Zero;    // upstream anchor
            public XYZ Direction = XYZ.BasisX;  // unit vector along the run
        }

        /// <summary>Derives the run's primary axis from the longest pipe's
        /// connector-pair vector, then validates every other pipe is
        /// parallel within <see cref="AxisDotTol"/>. Returns null with
        /// a warning when the parts don't form one axis.</summary>
        private static RunAxis? ResolveRunAxis(List<PartInfo> infos, List<string> warnings)
        {
            PartInfo? seed = null;
            double seedLen = 0;
            foreach (var info in infos)
            {
                if (info.Kind != PartKind.Pipe) continue;
                double len = info.NearEnd.DistanceTo(info.FarEnd);
                if (len > seedLen) { seedLen = len; seed = info; }
            }
            if (seed == null || seedLen < 1e-6)
            {
                warnings.Add("Dimensions: no straight pipe found in selection to anchor the run axis.");
                return null;
            }

            var dir = (seed.FarEnd - seed.NearEnd).Normalize();

            // Every other pipe's axis must be parallel within tolerance.
            foreach (var info in infos)
            {
                if (info.Kind != PartKind.Pipe || info == seed) continue;
                double l = info.NearEnd.DistanceTo(info.FarEnd);
                if (l < 1e-6) continue;
                var d2 = (info.FarEnd - info.NearEnd).Normalize();
                if (Math.Abs(d2.DotProduct(dir)) < AxisDotTol)
                {
                    warnings.Add($"Dimensions: pipe {info.Part.Id.Value} runs at >5° from the spool axis — non-straight runs aren't supported yet, dim placement skipped.");
                    return null;
                }
            }

            return new RunAxis { Origin = seed.NearEnd, Direction = dir };
        }

        // ── Dim chain assembly ────────────────────────────────────────────────

        /// <summary>One dim to place: two axis-projected XYZ endpoints
        /// plus a "stack layer" (0 = inner / valve, 1 = segment, 2 =
        /// overall). The view-side step expands these into Reference
        /// pairs and chooses the dim line offset = Layer * baseOffset.</summary>
        private sealed class DimChain
        {
            public XYZ A = XYZ.Zero;
            public XYZ B = XYZ.Zero;
            public int Layer;
        }

        private static List<DimChain> BuildDimChains(
            List<PartInfo> infos, RunAxis axis, List<string> warnings)
        {
            var chains = new List<DimChain>();
            if (infos.Count < 2) return chains;

            // Endpoints along the axis are the projected positions of
            // the part's nearest face on the relevant side. For Layer 3
            // (overall EOP-to-EOP), we take the FarEnd of the most-
            // outboard pipes. For Layer 2 (segment), we use the
            // flange / valve flange faces. For Layer 1 (valve), we use
            // the valve's own end connectors.
            var first = infos[0];
            var last  = infos[infos.Count - 1];

            // Layer 3 — overall. From the upstream-most connector point
            // to the downstream-most connector point along the axis.
            chains.Add(new DimChain
            {
                A = ProjectOntoAxis(first.NearEnd, axis),
                B = ProjectOntoAxis(last.FarEnd,   axis),
                Layer = 2,
            });

            // Layer 1 — each valve dimensions FF-to-FF across itself.
            foreach (var info in infos)
            {
                if (info.Kind != PartKind.Valve) continue;
                chains.Add(new DimChain
                {
                    A = ProjectOntoAxis(info.NearEnd, axis),
                    B = ProjectOntoAxis(info.FarEnd,  axis),
                    Layer = 0,
                });
            }

            // Layer 2 — pipe segments between consecutive flange faces,
            // excluding spans across a valve (the valve gets its own
            // Layer 1 dim and the adjacent flanges dim TO the valve face).
            // Walk axis-ordered parts collecting "anchor" indices —
            // points where a Layer 2 dim should start or end.
            var anchors = new List<(int Index, XYZ Point)>();
            for (int i = 0; i < infos.Count; i++)
            {
                var info = infos[i];
                if (info.Kind == PartKind.Flange)
                {
                    // Flange face — pick the connector facing INTO the
                    // run (toward the adjacent fitting/valve), so the
                    // dim ends at the MATING face, not the back of the
                    // flange.
                    anchors.Add((i, ProjectOntoAxis(MatingFaceOfFlange(info, axis), axis)));
                }
                else if (info.Kind == PartKind.Valve)
                {
                    // A valve contributes TWO anchors — its two flange
                    // faces — so the segment dims on either side
                    // terminate at the valve's near and far faces.
                    anchors.Add((i, ProjectOntoAxis(info.NearEnd, axis)));
                    anchors.Add((i, ProjectOntoAxis(info.FarEnd,  axis)));
                }
            }

            // Now pair adjacent anchors (excluding the two on either
            // side of a single valve, since that span IS the valve and
            // already has a Layer 1 dim).
            for (int a = 0; a + 1 < anchors.Count; a++)
            {
                int idxA = anchors[a].Index;
                int idxB = anchors[a + 1].Index;
                // Skip the intra-valve pair (same part, just two faces).
                if (idxA == idxB && infos[idxA].Kind == PartKind.Valve) continue;
                chains.Add(new DimChain
                {
                    A = anchors[a].Point,
                    B = anchors[a + 1].Point,
                    Layer = 1,
                });
            }

            return chains;
        }

        /// <summary>Picks the flange connector facing the rest of the
        /// run — the "mating face." For an EOP flange that's the
        /// inboard connector; for an interior flange it's whichever
        /// connector sits between this flange and the next non-joint
        /// part along the axis. Heuristic: the connector closer to the
        /// run's geometric centre wins.</summary>
        private static XYZ MatingFaceOfFlange(PartInfo info, RunAxis axis)
        {
            // Without full neighbour-walking we approximate: pick the
            // end whose axis-projection is closer to the part's centre.
            double dNear = Math.Abs((info.NearEnd - info.Centre).DotProduct(axis.Direction));
            double dFar  = Math.Abs((info.FarEnd  - info.Centre).DotProduct(axis.Direction));
            return dNear <= dFar ? info.NearEnd : info.FarEnd;
        }

        private static XYZ ProjectOntoAxis(XYZ point, RunAxis axis)
        {
            var v = point - axis.Origin;
            double t = v.DotProduct(axis.Direction);
            return axis.Origin + axis.Direction.Multiply(t);
        }

        // ── Per-view placement ────────────────────────────────────────────────

        private int PlaceDimensionsOnView(
            View view, RunAxis axis, List<DimChain> chains,
            SpoolRequest req, List<string> warnings)
        {
            // The dim line direction in the view = perpendicular to
            // the projected axis within the view plane. Picking the
            // "up" side is a heuristic — for now we always go +Z for
            // plan views and +UpDirection for sections.
            XYZ offsetDir;
            try
            {
                var viewUp     = view.UpDirection;
                var viewNormal = view.ViewDirection;
                // Offset direction = view-up, BUT we want it
                // perpendicular to the run axis. Cross product takes
                // care of that; sign chosen to point "above" the run.
                offsetDir = viewNormal.CrossProduct(axis.Direction);
                if (offsetDir.GetLength() < 1e-6) offsetDir = viewUp;
                else offsetDir = offsetDir.Normalize();
                if (offsetDir.DotProduct(viewUp) < 0) offsetDir = offsetDir.Negate();
            }
            catch
            {
                offsetDir = XYZ.BasisZ;
            }

            int placed = 0;
            int chainIdx = 0;
            int noRefA = 0, noRefB = 0, threwInNewDim = 0;
            double baseOffset = Math.Max(req.DimensionOffsetFt, 0.05);

            foreach (var chain in chains)
            {
                chainIdx++;
                try
                {
                    // Reference acquisition per endpoint — view-specific
                    // so the returned references are valid on THIS view.
                    var refA = ReferenceAtPoint(chain.A, axis.Direction, view);
                    var refB = ReferenceAtPoint(chain.B, axis.Direction, view);
                    if (refA == null) { noRefA++; continue; }
                    if (refB == null) { noRefB++; continue; }

                    var ra = new ReferenceArray();
                    ra.Append(refA);
                    ra.Append(refB);

                    // Dim line position — offset by (layer + 1) *
                    // baseOffset along the chosen offset direction.
                    double dist = baseOffset * (chain.Layer + 1);
                    var lineMid = ((chain.A + chain.B) * 0.5) + offsetDir.Multiply(dist);
                    var line = Line.CreateBound(
                        lineMid - axis.Direction.Multiply(1.0),
                        lineMid + axis.Direction.Multiply(1.0));

                    var dim = _doc.Create.NewDimension(view, line, ra);
                    if (dim == null) { threwInNewDim++; continue; }

                    if (req.DimensionStyleId != null && req.DimensionStyleId != ElementId.InvalidElementId)
                    {
                        try { dim.DimensionType = _doc.GetElement(req.DimensionStyleId) as DimensionType; }
                        catch { }
                    }
                    placed++;
                }
                catch (Exception ex)
                {
                    threwInNewDim++;
                    warnings.Add($"Dimensions: chain #{chainIdx} on view '{view.Name}' failed — {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Per-view diagnostic so we can see WHY 0 dims placed.
            if (placed < chains.Count)
            {
                warnings.Add($"Dimensions [view '{view.Name}']: tried {chains.Count} chain(s), placed {placed} — " +
                             $"endpoint A unresolved on {noRefA}, endpoint B unresolved on {noRefB}, NewDimension failed/threw on {threwInNewDim}.");
            }
            return placed;
        }

        /// <summary>Looks up a Reference at <paramref name="target"/>
        /// usable for dimensioning on <paramref name="view"/>. Uses
        /// view-specific geometry (`Options.View`) so references the
        /// 2D view considers dim-able are returned. Two passes:
        ///   • Strict — PlanarFace with normal parallel to the run
        ///     axis (the "FF" / "EOP" / "valve face" planes).
        ///   • Relaxed — any PlanarFace whose centroid is near the
        ///     target. Catches catalog-specific cases where the end
        ///     plane isn't exactly perpendicular to the modelled axis
        ///     (e.g. fab pieces with mitred ends or rotated valve
        ///     bodies).
        /// </summary>
        private Reference? ReferenceAtPoint(XYZ target, XYZ axisDir, View view)
        {
            // View-specific geometry — references returned this way are
            // bound to THIS view's representation, so NewDimension on
            // the same view accepts them without complaining about
            // "reference not visible in view".
            var opts = new Options
            {
                ComputeReferences        = true,
                IncludeNonVisibleObjects = false,
                View                     = view,
            };

            Reference? strictBest = null;
            Reference? relaxedBest = null;
            double strictDist = double.MaxValue;
            double relaxedDist = double.MaxValue;

            var bbMin = target - new XYZ(2, 2, 2);
            var bbMax = target + new XYZ(2, 2, 2);
            var outline = new Outline(bbMin, bbMax);
            var bbf = new BoundingBoxIntersectsFilter(outline);
            var nearby = new FilteredElementCollector(_doc)
                .OfClass(typeof(FabricationPart))
                .WherePasses(bbf)
                .Cast<FabricationPart>();

            foreach (var part in nearby)
            {
                GeometryElement? geo;
                try { geo = part.get_Geometry(opts); }
                catch { continue; }
                if (geo == null) continue;
                ScanGeometry(geo, target, axisDir,
                    ref strictBest, ref strictDist,
                    ref relaxedBest, ref relaxedDist);
            }
            return strictBest ?? relaxedBest;
        }

        private static void ScanGeometry(
            GeometryElement geo, XYZ target, XYZ axisDir,
            ref Reference? strictBest, ref double strictDist,
            ref Reference? relaxedBest, ref double relaxedDist)
        {
            foreach (var go in geo)
            {
                if (go is Solid solid && solid.Volume > 0)
                {
                    foreach (Face face in solid.Faces)
                    {
                        if (face is not PlanarFace pf) continue;
                        Reference? r = null;
                        try { r = pf.Reference; } catch { }
                        if (r == null) continue;

                        var origin = pf.Origin;
                        double d = origin.DistanceTo(target);
                        if (d > 1.5) continue;   // outside the per-part window

                        // Strict — perpendicular to run axis.
                        var n = pf.FaceNormal;
                        bool perp = Math.Abs(n.DotProduct(axisDir)) >= 0.95;
                        if (perp && d < strictDist)
                        {
                            strictDist = d;
                            strictBest = r;
                        }
                        // Relaxed — any planar face near the target.
                        if (d < relaxedDist)
                        {
                            relaxedDist = d;
                            relaxedBest = r;
                        }
                    }
                }
                else if (go is GeometryInstance gi)
                {
                    var instGeo = gi.GetInstanceGeometry();
                    if (instGeo != null)
                        ScanGeometry(instGeo, target, axisDir,
                            ref strictBest, ref strictDist,
                            ref relaxedBest, ref relaxedDist);
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool IsIsoDirection(SpoolDirection d) =>
            d == SpoolDirection.NeIso || d == SpoolDirection.NwIso ||
            d == SpoolDirection.SeIso || d == SpoolDirection.SwIso;

        private static XYZ AverageXYZ(IList<XYZ> points)
        {
            double x = 0, y = 0, z = 0;
            foreach (var p in points) { x += p.X; y += p.Y; z += p.Z; }
            int n = points.Count;
            return new XYZ(x / n, y / n, z / n);
        }
    }
}
