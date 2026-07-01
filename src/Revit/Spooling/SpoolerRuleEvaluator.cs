using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>
    /// Combinable auto-split rules that turn into <see cref="SpoolerNetworkWalker"/>
    /// break points. Any subset can be active; rule-detected breaks are
    /// unioned with the user's manual break list before the walker partitions
    /// the network. All thresholds are user-typed in imperial units (pounds,
    /// feet); the evaluator handles per-partition accumulation so each
    /// resulting spool stays under the cap.
    /// </summary>
    public sealed class AutoSplitRules
    {
        /// <summary>When true, every part that matches CID 2522 +
        /// "Joints" product range + name containing "Field Weld" is
        /// added to the break set so each field weld ships as its own
        /// spool boundary.</summary>
        public bool AtFieldWelds { get; set; }

        /// <summary>Maximum spool weight in pounds (null = no cap). When
        /// adding a part would push the current partition's cumulative
        /// weight over this value, the previous part becomes a break and
        /// the heavy part starts a new partition.</summary>
        public double? MaxWeightLb { get; set; }

        /// <summary>Maximum spool length in feet, computed by summing each
        /// part's longest bounding-box dimension (null = no cap). Same
        /// walk-back-to-last-fitting logic as the weight rule.</summary>
        public double? MaxLengthFt { get; set; }

        public bool Any => AtFieldWelds || MaxWeightLb.HasValue || MaxLengthFt.HasValue;
    }

    /// <summary>Result of <see cref="SpoolerRuleEvaluator.EvaluateSelection"/>
    /// — the aggregate weight + longest bbox dimension across a
    /// selection. Caller compares to its own configured thresholds.</summary>
    public sealed class SelectionEvaluation
    {
        public double TotalWeightLb   { get; init; }
        public double LongestLengthFt { get; init; }
    }

    /// <summary>
    /// Evaluates <see cref="AutoSplitRules"/> against a selection rooted
    /// at a START element, producing the final break set that the
    /// <see cref="SpoolerNetworkWalker"/> uses to partition the run.
    /// Manual breaks pass through unchanged; rule-detected breaks are
    /// unioned in. Field-weld detection is a one-pass scan; weight /
    /// length thresholds run a preliminary walk so accumulators reset
    /// correctly at each manual-or-rule boundary.
    /// </summary>
    public static class SpoolerRuleEvaluator
    {
        /// <summary>Catalog Item ID for the standard Fabrication joint
        /// pattern. Welds, flanges, and couplings all share it — the
        /// "Field Weld" rule further filters by Product Range + Name.</summary>
        private const int JointCid = 2522;

        public static List<ElementId> ComputeBreaks(
            Document doc,
            IReadOnlyCollection<ElementId> selection,
            ElementId start,
            IReadOnlyCollection<ElementId> manualBreaks,
            AutoSplitRules? rules)
        {
            var breaks = new HashSet<ElementId>(manualBreaks ?? Array.Empty<ElementId>());

            if (rules == null || !rules.Any) return new List<ElementId>(breaks);

            // (1) Field welds — simple scan, order doesn't matter.
            if (rules.AtFieldWelds)
            {
                foreach (var id in selection)
                {
                    if (IsFieldWeld(doc, id)) breaks.Add(id);
                }
            }

            // (2)+(3) Weight / length — need ordered walks. Run an initial
            // walk with the breaks accumulated so far (manual + field
            // welds), then accumulate per partition.
            //
            // Weight is additive: sum each part's Weight parameter; when
            // the next part would push cumulative over the cap, the
            // PREVIOUS part becomes a break and the heavy part seeds a
            // new partition.
            //
            // Length is computed as the longest dimension of the union
            // bounding box of all parts already in the partition. This
            // matches "the longest dimension of the finished spool"
            // semantics (fits on a truck / through a door) — adding
            // perpendicular branches doesn't blow up the length, and
            // small fittings don't accumulate noise the way a per-part
            // sum does. When adding the next part would push the union
            // bbox's longest dim over the cap, the PREVIOUS part becomes
            // a break and the new part seeds a new partition with its
            // own bbox.
            if (rules.MaxWeightLb.HasValue || rules.MaxLengthFt.HasValue)
            {
                var walk = SpoolerNetworkWalker.Walk(doc, selection, start, new List<ElementId>(breaks));

                foreach (var partition in walk.Spools)
                {
                    double cumW = 0;
                    BoundingBoxXYZ? cumBbox = null;
                    ElementId? prev = null;

                    foreach (var partId in partition.Parts)
                    {
                        var part = doc.GetElement(partId);
                        if (part == null) continue;

                        double w = GetWeightLb(part);
                        var partBbox = SafeBoundingBox(part);

                        bool weightExceeds = rules.MaxWeightLb.HasValue &&
                                             (cumW + w) > rules.MaxWeightLb.Value;

                        bool lengthExceeds = false;
                        BoundingBoxXYZ? candidateBbox = null;
                        if (rules.MaxLengthFt.HasValue && partBbox != null)
                        {
                            candidateBbox = UnionBboxes(cumBbox, partBbox);
                            lengthExceeds  = LongestDimFt(candidateBbox) > rules.MaxLengthFt.Value;
                        }

                        if ((weightExceeds || lengthExceeds) && prev != null)
                        {
                            breaks.Add(prev);
                            cumW    = w;
                            cumBbox = partBbox;
                        }
                        else
                        {
                            cumW   += w;
                            cumBbox = candidateBbox ?? UnionBboxes(cumBbox, partBbox);
                        }

                        prev = partId;
                    }
                }
            }

            return new List<ElementId>(breaks);
        }

        // ── Shared selection evaluation ────────────────────────────────────────

        /// <summary>Total weight (lbs) + longest bounding-box dimension
        /// (ft) for an arbitrary part selection. Reuses the same per-
        /// part weight + bbox readers the auto-split rules use, so
        /// Create Spool's "selection exceeds limit" alert reports the
        /// same numbers The Spooler would use when deciding to split a
        /// partition.
        ///
        /// Length is the longest dimension of the union AABB across all
        /// parts (same semantics as the MaxLength rule — "the longest
        /// dimension of the finished spool", what fits on a truck or
        /// through a door). Weight is a straight sum of FabricationPart.Weight
        /// values, with a Weight-parameter fallback for non-fab elements.</summary>
        public static SelectionEvaluation EvaluateSelection(
            Document doc, IReadOnlyCollection<ElementId> ids)
        {
            double totalWeightLb = 0;
            BoundingBoxXYZ? cumBbox = null;

            if (doc != null && ids != null)
            {
                foreach (var id in ids)
                {
                    var el = doc.GetElement(id);
                    if (el == null) continue;
                    totalWeightLb += GetWeightLb(el);
                    var bb = SafeBoundingBox(el);
                    if (bb != null) cumBbox = UnionBboxes(cumBbox, bb);
                }
            }

            return new SelectionEvaluation
            {
                TotalWeightLb   = totalWeightLb,
                LongestLengthFt = LongestDimFt(cumBbox),
            };
        }

        // ── Per-rule predicates ────────────────────────────────────────────────

        /// <summary>True when the element is a CID-2522 joint whose
        /// Product Range contains "Joint" AND whose name (or Item
        /// Description) contains "Field Weld" — the standard catalog
        /// signature for a field-installed weld joint.</summary>
        private static bool IsFieldWeld(Document doc, ElementId id)
        {
            if (doc.GetElement(id) is not FabricationPart fp) return false;

            try { if (fp.ItemCustomId != JointCid) return false; }
            catch { return false; }

            // Product Range must be a Joint range (rules out flanges /
            // couplings that share CID 2522).
            try
            {
                var rangeParam = fp.LookupParameter("Product Range");
                if (rangeParam == null || rangeParam.StorageType != StorageType.String)
                    return false;
                var range = rangeParam.AsString() ?? string.Empty;
                if (range.IndexOf("joint", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
            catch { return false; }

            // Name OR Item Description contains "Field Weld" (case-
            // insensitive). Catalogs vary on which holds the human-
            // readable label. Comments is also read as a back-compat
            // path: earlier builds of the batch tool stamped "Field
            // Weld" into Comments instead of swapping the catalog
            // part, and we still want those welds recognised by the
            // auto-split rule on re-runs.
            try
            {
                if (Contains(fp.Name, "field weld")) return true;
                var descParam = fp.LookupParameter("Item Description");
                if (descParam?.AsString() is string desc && Contains(desc, "field weld"))
                    return true;
                var commentsParam = SpoolerWeldPostProcessor.ResolveCommentsParam(fp);
                if (commentsParam?.AsString() is string cmt && Contains(cmt, "field weld"))
                    return true;
            }
            catch { }

            return false;
        }

        private static bool Contains(string? haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        // ── Weight + length readers ────────────────────────────────────────────

        /// <summary>Reads the part's weight as pounds. Uses
        /// <see cref="FabricationPart.Weight"/> for FabricationParts (which
        /// is already in display units for the document) and the
        /// <c>Weight</c> shared parameter as a fallback. Returns 0 when
        /// unreadable so the accumulator isn't poisoned.</summary>
        private static double GetWeightLb(Element e)
        {
            try
            {
                if (e is FabricationPart fp) return fp.Weight;
            }
            catch { }
            try
            {
                var p = e.LookupParameter("Weight");
                if (p?.StorageType == StorageType.Double) return p.AsDouble();
            }
            catch { }
            return 0;
        }

        /// <summary>Read the part's model-space bounding box. Returns
        /// null on failure so the accumulator can skip the part instead
        /// of growing wildly.</summary>
        private static BoundingBoxXYZ? SafeBoundingBox(Element e)
        {
            try { return e.get_BoundingBox(null); }
            catch { return null; }
        }

        /// <summary>Union of two AABBs in model coords. The result
        /// contains both inputs. A null first argument seeds the
        /// accumulation from <paramref name="b"/>.</summary>
        private static BoundingBoxXYZ UnionBboxes(BoundingBoxXYZ? a, BoundingBoxXYZ? b)
        {
            if (b == null) return a ?? new BoundingBoxXYZ();
            if (a == null)
            {
                return new BoundingBoxXYZ
                {
                    Min = new XYZ(b.Min.X, b.Min.Y, b.Min.Z),
                    Max = new XYZ(b.Max.X, b.Max.Y, b.Max.Z),
                };
            }
            return new BoundingBoxXYZ
            {
                Min = new XYZ(Math.Min(a.Min.X, b.Min.X),
                              Math.Min(a.Min.Y, b.Min.Y),
                              Math.Min(a.Min.Z, b.Min.Z)),
                Max = new XYZ(Math.Max(a.Max.X, b.Max.X),
                              Math.Max(a.Max.Y, b.Max.Y),
                              Math.Max(a.Max.Z, b.Max.Z)),
            };
        }

        /// <summary>Longest axis of the bbox, in feet (Revit's internal
        /// length unit — bounding boxes already report there).</summary>
        private static double LongestDimFt(BoundingBoxXYZ? bb)
        {
            if (bb == null) return 0;
            var d = bb.Max - bb.Min;
            return Math.Max(Math.Max(d.X, d.Y), d.Z);
        }
    }
}
