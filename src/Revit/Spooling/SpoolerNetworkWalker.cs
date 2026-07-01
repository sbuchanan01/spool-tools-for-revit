using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>
    /// Walks a connected FabricationPart network from a START element,
    /// partitioning it into ordered spool partitions based on:
    ///   • User-picked BREAK elements (each ends its spool — the part stays
    ///     in that spool, and every outgoing connection past it seeds a
    ///     new spool).
    ///   • Tee / wye / lateral branching: when the walk traverses a part
    ///     with ≥3 connectors via its through-flow axis, any perpendicular
    ///     ("branch") connector seeds a new spool while the through-flow
    ///     continuation stays in the current spool.
    ///
    /// Walks are restricted to the user's pre-selection — connectors that
    /// lead outside the selection are ignored, so the walker naturally
    /// stops at the user-defined boundary even on a long shared run.
    ///
    /// Branch classification uses the incoming and candidate-outgoing
    /// connectors' <see cref="Connector.CoordinateSystem"/> BasisZ vectors:
    ///   • |Dot| &gt; 0.7  →  anti-parallel (through-flow) → main continuation
    ///   • |Dot| &lt; 0.7  →  perpendicular                → branch seed
    /// For 2-connector parts (pipe / elbow / valve / flange) and the START
    /// element itself (no incoming), every outgoing is treated as main —
    /// the user can place a BREAK manually if they want to split there.
    ///
    /// Spool ordering: seeds are processed in FIFO order, so the first
    /// spool is the START's walk, then breaks encountered along the way
    /// (in walk order), then branches in the order their tees were
    /// crossed. Predictable for a piper to read.
    /// </summary>
    public static class SpoolerNetworkWalker
    {
        /// <summary>Dot-product threshold for "through-flow" classification.
        /// Above this absolute value two connectors are considered to lie
        /// on the same pipe axis; below it they're considered perpendicular
        /// (i.e., a branch). 0.7 ≈ 45° tolerance, comfortably wider than
        /// the catalog modelling noise on tee angles.</summary>
        private const double ThroughFlowThreshold = 0.7;

        public static WalkResult Walk(
            Document doc,
            IReadOnlyCollection<ElementId> selection,
            ElementId start,
            IReadOnlyCollection<ElementId> breaks)
        {
            if (doc == null || selection == null || start == ElementId.InvalidElementId)
                return new WalkResult();

            var selSet = new HashSet<ElementId>(selection);
            var brkSet = new HashSet<ElementId>(breaks ?? Array.Empty<ElementId>());
            if (!selSet.Contains(start)) selSet.Add(start);   // be forgiving

            var visited   = new HashSet<ElementId>();
            var spools    = new List<SpoolPartition>();
            var seedQueue = new Queue<ElementId>();
            seedQueue.Enqueue(start);

            while (seedQueue.Count > 0)
            {
                var seed = seedQueue.Dequeue();
                if (visited.Contains(seed)) continue;
                if (!selSet.Contains(seed)) continue;

                var spool = new SpoolPartition
                {
                    Index = spools.Count + 1,
                    Parts = new List<ElementId>(),
                };
                spools.Add(spool);

                // Within-spool BFS: each entry carries the connector we
                // arrived through on the part being processed, so we can
                // skip it when iterating outgoing connectors and so we can
                // classify the OTHER outgoings as main vs branch by
                // comparing to it.
                var walkQueue = new Queue<(ElementId Id, Connector? Incoming)>();
                walkQueue.Enqueue((seed, null));

                while (walkQueue.Count > 0)
                {
                    var (id, incoming) = walkQueue.Dequeue();
                    if (visited.Contains(id)) continue;
                    if (!selSet.Contains(id)) continue;
                    visited.Add(id);

                    if (doc.GetElement(id) is not FabricationPart part) continue;
                    spool.Parts.Add(id);

                    bool isBreak       = brkSet.Contains(id);
                    var  connectors    = ReadConnectors(part);
                    bool isBranchablePart = connectors.Count >= 3;

                    foreach (var outgoing in connectors)
                    {
                        // Skip the connector we just came in through.
                        if (incoming != null && SameConnector(outgoing, incoming))
                            continue;

                        bool outgoingIsBranch =
                            !isBreak &&
                            isBranchablePart &&
                            incoming != null &&
                            IsPerpendicular(outgoing, incoming);

                        foreach (Connector other in EnumerateRefs(outgoing))
                        {
                            if (other?.Owner == null) continue;
                            var otherId = other.Owner.Id;
                            if (otherId == id) continue;          // self-ref
                            if (!selSet.Contains(otherId)) continue;
                            if (visited.Contains(otherId)) continue;

                            // Three cases:
                            //   • Break: the part being processed is the
                            //     LAST in its spool — everything past it
                            //     becomes a new spool seed.
                            //   • Branch off a tee/wye/lateral: new spool.
                            //   • Otherwise: same spool, continue walking
                            //     and remember the connector we used to
                            //     enter so the next part can classify
                            //     correctly.
                            if (isBreak || outgoingIsBranch)
                                seedQueue.Enqueue(otherId);
                            else
                                walkQueue.Enqueue((otherId, other));
                        }
                    }
                }
            }

            var unconnected = selSet
                .Where(id => !visited.Contains(id))
                .ToList();

            return new WalkResult
            {
                Spools      = spools,
                Unconnected = unconnected,
            };
        }

        // ── Connector helpers ──────────────────────────────────────────────────

        private static List<Connector> ReadConnectors(FabricationPart part)
        {
            var list = new List<Connector>();
            try
            {
                foreach (Connector c in part.ConnectorManager.Connectors)
                    list.Add(c);
            }
            catch { /* defensive — some catalogs misbehave */ }
            return list;
        }

        /// <summary>Connector identity via spatial origin. Revit returns
        /// fresh Connector instances on each property/AllRefs read so
        /// reference equality can't be used. Origin coordinates are
        /// stable per-part and within-part connectors don't overlap.</summary>
        private static bool SameConnector(Connector a, Connector b)
        {
            try
            {
                if (a == null || b == null) return false;
                return a.Origin.DistanceTo(b.Origin) < 1e-4;
            }
            catch { return false; }
        }

        /// <summary>True when two connectors on the same part are NOT on
        /// the same pipe axis — i.e., perpendicular within the
        /// <see cref="ThroughFlowThreshold"/> tolerance. The two
        /// through-flow connectors on a tee point anti-parallel
        /// (Dot ≈ −1), while the branch is perpendicular (Dot ≈ 0); this
        /// returns true for the latter.</summary>
        private static bool IsPerpendicular(Connector a, Connector b)
        {
            try
            {
                double dot = a.CoordinateSystem.BasisZ.DotProduct(
                             b.CoordinateSystem.BasisZ);
                return Math.Abs(dot) < ThroughFlowThreshold;
            }
            catch { return false; }
        }

        /// <summary>Enumerates other-end connectors of a connection without
        /// throwing if AllRefs is empty / unusable. Wrapper around
        /// <see cref="Connector.AllRefs"/> for defensive iteration.</summary>
        private static IEnumerable<Connector> EnumerateRefs(Connector c)
        {
            ConnectorSet refs;
            try { refs = c.AllRefs; }
            catch { yield break; }
            if (refs == null) yield break;
            foreach (Connector other in refs)
                yield return other;
        }
    }

    /// <summary>Result of a Spooler network walk. <see cref="Spools"/> is
    /// in creation order — index 1 starts at the user's START element,
    /// subsequent indices come from BREAK seeds (main flow) and tee
    /// branches in the order they were encountered. <see cref="Unconnected"/>
    /// are parts in the original selection that the walk never visited
    /// (disconnected, isolated, or blocked by missing connectors); the
    /// UI surfaces these so the user can decide whether to fix the model
    /// or ignore them.</summary>
    public sealed class WalkResult
    {
        public List<SpoolPartition> Spools    { get; init; } = new();
        public List<ElementId>      Unconnected { get; init; } = new();
    }

    public sealed class SpoolPartition
    {
        /// <summary>1-based creation order. Matches the {N} token's
        /// natural counter so spool 1 is the first sheet, spool 2 the
        /// second, etc.</summary>
        public int Index { get; init; }

        /// <summary>Element IDs in walk order. The first entry is the
        /// spool's seed — used to resolve the Fabrication Service for
        /// the {Service} / {ServiceName} tokens.</summary>
        public List<ElementId> Parts { get; init; } = new();
    }
}
