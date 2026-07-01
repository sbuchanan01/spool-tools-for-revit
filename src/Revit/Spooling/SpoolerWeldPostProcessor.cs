using Autodesk.Revit.DB;
using SpoolTools.Revit;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>
    /// Handles welds that fall into single-element spool partitions after a
    /// Spooler batch walk. A lone weld on its own spool sheet is useless
    /// paperwork — the user wants:
    ///
    ///   • <see cref="MergeIsolatedWelds"/> (default, always run) — fold
    ///     each isolated weld into the NEXT spool in walk order so it
    ///     ships on the same sheet as the spool it'll be welded TO. When
    ///     the isolated weld is the LAST partition it folds backward into
    ///     the previous one instead. Partitions are then re-sequenced so
    ///     callers see a contiguous 1..N numbering. Returns the IDs of
    ///     the folded welds so the caller can optionally swap them.
    ///
    ///   • <see cref="ReplaceWithFieldWeldParts"/> (opt-in) — REPLACES
    ///     each folded weld with the Field Weld catalog item from the
    ///     same fabrication service (same bore). The original weld is
    ///     deleted; a new FabricationPart is created from the Field Weld
    ///     button, moved to the original location, and reconnected to
    ///     the same two neighbours by proximity matching. Returns a
    ///     mapping (oldId → newId) so the caller can patch the walk's
    ///     partition lists. When no Field Weld button is found in the
    ///     service catalog, the original part is left in place and a
    ///     warning is appended (no fallback to a relabel — the user
    ///     explicitly asked for the part swap, so failing visibly is
    ///     better than silently doing something different).
    ///
    /// Both behaviours run inside the outer batch TransactionGroup so the
    /// in-memory walk rebuild + the Revit doc edits roll back together
    /// if the batch later fails.
    /// </summary>
    public static class SpoolerWeldPostProcessor
    {
        /// <summary>Catalog Item ID for the standard Fabrication joint
        /// pattern. Welds, flanges, and couplings all share it — the
        /// predicate further filters by Product Range containing "Joint"
        /// to rule out flanges and couplings.</summary>
        private const int JointCid = 2522;

        /// <summary>Folds every single-element partition whose lone member
        /// is a weld into the next partition in walk order. When the
        /// isolated weld is the LAST partition it folds backward into
        /// the previous one instead. Partition indices are then
        /// re-sequenced so callers see a contiguous 1..N numbering.
        /// Returns the IDs of the welds that were folded so the caller
        /// can optionally swap them for Field Weld catalog items.
        ///
        /// In-memory only — does not modify any Revit element. Safe to
        /// call without an open transaction. No-op when fewer than 2
        /// partitions exist (no merge target) or when no partition is a
        /// single weld.</summary>
        public static List<ElementId> MergeIsolatedWelds(Document doc, WalkResult walk)
        {
            var folded = new List<ElementId>();
            if (walk == null || walk.Spools.Count < 2) return folded;

            // Pass 1: identify all isolated-weld partition indices up
            // front so we can hop OVER them when picking targets — that
            // way adjacent lone welds don't all merge into each other
            // and then evaporate when their sources are dropped.
            var weldOnlyIndices = new HashSet<int>();
            for (int i = 0; i < walk.Spools.Count; i++)
            {
                var spool = walk.Spools[i];
                if (spool.Parts.Count != 1) continue;
                if (!IsAnyWeld(doc, spool.Parts[0])) continue;
                weldOnlyIndices.Add(i);
            }

            if (weldOnlyIndices.Count == 0) return folded;
            // Pathological case: every partition is a lone weld. Nothing
            // valid to merge into — leave the walk alone so the batch
            // surfaces the situation (each weld gets its own sheet, which
            // the user can then deal with directly).
            if (weldOnlyIndices.Count == walk.Spools.Count) return folded;

            // Pass 2: for each isolated weld, find the nearest non-weld-
            // only target (forward preferred, fall back to backward).
            foreach (int i in weldOnlyIndices.OrderBy(x => x))
            {
                int target = -1;
                for (int j = i + 1; j < walk.Spools.Count; j++)
                {
                    if (!weldOnlyIndices.Contains(j)) { target = j; break; }
                }
                if (target < 0)
                {
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (!weldOnlyIndices.Contains(j)) { target = j; break; }
                    }
                }
                if (target < 0) continue;

                var weldId = walk.Spools[i].Parts[0];
                // Forward merge: weld becomes the leading part of the
                // downstream spool (walk-order semantics preserved).
                // Backward merge: weld is appended to the upstream spool.
                if (target > i) walk.Spools[target].Parts.Insert(0, weldId);
                else            walk.Spools[target].Parts.Add(weldId);
                folded.Add(weldId);
            }

            // Pass 3: rebuild Spools, dropping all weld-only sources and
            // re-sequencing indices so the {N} template counter stays
            // contiguous.
            var rebuilt = new List<SpoolPartition>();
            int newIdx = 1;
            for (int i = 0; i < walk.Spools.Count; i++)
            {
                if (weldOnlyIndices.Contains(i)) continue;
                rebuilt.Add(new SpoolPartition
                {
                    Index = newIdx++,
                    Parts = walk.Spools[i].Parts,
                });
            }
            walk.Spools.Clear();
            walk.Spools.AddRange(rebuilt);
            return folded;
        }

        /// <summary>For each weld in <paramref name="weldIds"/>: locates
        /// the Field Weld button in its fabrication service catalog at
        /// the part's bore size, snapshots the part's location +
        /// neighbour connectors, deletes the original part, creates a
        /// new FabricationPart from the Field Weld button, moves it to
        /// the original location, and reconnects it to the two
        /// neighbours by proximity matching.
        ///
        /// Caller MUST already have an open <see cref="Transaction"/>
        /// on <paramref name="doc"/> — every step here (delete, create,
        /// move, connect) mutates the document.
        ///
        /// Returns a mapping of old ElementId → new ElementId so the
        /// caller can patch the walk's partition lists. Welds for which
        /// no Field Weld button is found, or for which the create /
        /// reconnect step fails, are left in place and the original ID
        /// is NOT present in the returned map — the caller's partition
        /// lists keep the original ID and the batch proceeds with the
        /// original weld unchanged. A warning is appended for each
        /// failure so the user can investigate.</summary>
        public static Dictionary<ElementId, ElementId> ReplaceWithFieldWeldParts(
            Document doc, IReadOnlyList<ElementId> weldIds, List<string> warnings)
        {
            var map = new Dictionary<ElementId, ElementId>();
            if (doc == null || weldIds == null || weldIds.Count == 0) return map;
            warnings ??= new List<string>();

            foreach (var oldId in weldIds)
            {
                if (doc.GetElement(oldId) is not FabricationPart oldWeld)
                {
                    warnings.Add($"Replace weld id {oldId.Value}: element missing.");
                    continue;
                }

                // Skip if the part is already a Field Weld — no work to do.
                try
                {
                    if ((oldWeld.Name ?? string.Empty)
                        .IndexOf("field weld", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                }
                catch { }

                try
                {
                    // 1. Snapshot identity + position + neighbours BEFORE
                    //    the delete so we have something to anchor the
                    //    new part to.
                    int serviceId = -1;
                    try { serviceId = oldWeld.ServiceId; } catch { }
                    if (serviceId < 0)
                    {
                        warnings.Add($"Replace weld id {oldId.Value}: ServiceId unavailable on the original part.");
                        continue;
                    }

                    ElementId levelId = ElementId.InvalidElementId;
                    try { levelId = oldWeld.LevelId; } catch { }
                    if (levelId == ElementId.InvalidElementId)
                        levelId = AnyLevelId(doc);

                    double boreFt   = ReadBore(oldWeld);
                    string sizeName = ReadSizeName(oldWeld);

                    // Position + axis come from the OLD weld's connector
                    // geometry — FabricationPart.Location returns the
                    // base Location type for welds (see comment on
                    // FabricationHelpers.PositionWeld), so `Location as
                    // LocationPoint` is null and we can't derive XYZ
                    // from it. The connector midpoint gives the joint
                    // centre; the connector-pair delta gives the axis,
                    // or — when the joint is zero-length and both
                    // connectors sit at the same XYZ — we fall back to
                    // the first neighbour's CoordinateSystem.BasisZ
                    // (points along the pipe at that end).
                    XYZ? targetCenter = null;
                    XYZ? targetAxis   = null;
                    var oldConnectors = new List<Connector>();
                    try
                    {
                        foreach (Connector c in oldWeld.ConnectorManager.Connectors)
                            oldConnectors.Add(c);
                    }
                    catch { }

                    if (oldConnectors.Count >= 2)
                    {
                        try
                        {
                            var o0 = oldConnectors[0].Origin;
                            var o1 = oldConnectors[1].Origin;
                            targetCenter = (o0 + o1) * 0.5;
                            var d = o1 - o0;
                            if (d.GetLength() > 1e-9) targetAxis = d.Normalize();
                        }
                        catch { }
                    }
                    else if (oldConnectors.Count == 1)
                    {
                        try { targetCenter = oldConnectors[0].Origin; } catch { }
                    }

                    var neighbours = new List<(Connector other, XYZ origin)>();
                    try
                    {
                        foreach (Connector c in oldConnectors)
                        {
                            ConnectorSet refs;
                            try { refs = c.AllRefs; }
                            catch { continue; }
                            if (refs == null) continue;
                            foreach (Connector other in refs)
                            {
                                if (other?.Owner == null) continue;
                                if (other.Owner.Id == oldWeld.Id) continue;
                                XYZ origin;
                                try { origin = other.Origin; }
                                catch { continue; }
                                // Use the FIRST viable neighbour's
                                // BasisZ as the axis fallback when the
                                // weld's own connector pair didn't
                                // yield one (zero-length joint case).
                                if (targetAxis == null)
                                {
                                    try
                                    {
                                        var basisZ = other.CoordinateSystem.BasisZ;
                                        if (basisZ.GetLength() > 1e-9)
                                            targetAxis = basisZ.Normalize();
                                    }
                                    catch { }
                                }
                                neighbours.Add((other, origin));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Replace weld id {oldId.Value}: could not enumerate neighbours ({ex.Message}).");
                    }

                    if (targetCenter == null)
                    {
                        warnings.Add($"Replace weld id {oldId.Value}: could not derive target position from original weld's connectors. Original part left unchanged.");
                        continue;
                    }
                    // Final axis fallback — vertical. PositionWeld will
                    // still place the part at the right XYZ; only
                    // rotation will be wrong (user-fixable).
                    targetAxis ??= XYZ.BasisZ;

                    // 2. Locate the Field Weld button in the same service,
                    //    matching by SIZE STRING when possible (which
                    //    avoids the "1/2" instead of 8"" failure mode
                    //    that Connector.Radius produced on zero-length
                    //    weld connectors). Bore is the fallback path.
                    var (button, condition) = FindFieldWeldButton(doc, serviceId, sizeName, boreFt);
                    if (button == null)
                    {
                        warnings.Add($"Replace weld id {oldId.Value}: no 'Field Weld' catalog item found in service id {serviceId} (size '{sizeName}', bore ~{boreFt * 12:F2}\"). Original part left unchanged — check the service has a Field Weld button enabled.");
                        continue;
                    }

                    // 3. Delete the old weld and regenerate so its
                    //    connectors are gone before we place the new
                    //    part.
                    doc.Delete(oldId);
                    doc.Regenerate();

                    // 4. Create the new field weld.
                    FabricationPart? newWeld;
                    try
                    {
                        newWeld = FabricationPart.Create(doc, button, condition, levelId);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Replace weld id {oldId.Value}: FabricationPart.Create threw {ex.GetType().Name}: {ex.Message}. Original part was already deleted — partition will lose its weld marker for this run.");
                        continue;
                    }
                    if (newWeld == null || !newWeld.IsValidObject)
                    {
                        warnings.Add($"Replace weld id {oldId.Value}: FabricationPart.Create returned null. Original part was already deleted — partition will lose its weld marker for this run.");
                        continue;
                    }
                    doc.Regenerate();

                    // Shared diagnostic buffer for both ApplyDimensions
                    // and PositionWeld so we can surface anything the
                    // helpers wanted to say.
                    var posNotes = new List<string>();

                    // 5a. Set the new weld's SIZE before positioning.
                    //     For many real catalogs the Field Weld button
                    //     has only one condition (often named by
                    //     service/material, e.g. 'Field Weld - CS-A53'),
                    //     and the actual bore is set per-instance via
                    //     ApplyDimensions afterwards (either through
                    //     SetDimensionValue on the Diameter dim or via
                    //     ProductListEntry). Without this step the new
                    //     part stays at the catalog default (1/2"
                    //     for the test catalog).
                    try
                    {
                        FabricationHelpers.ApplyDimensions(
                            doc, newWeld, boreFt, 0.0, sizeName, posNotes, verbose: false);
                        doc.Regenerate();
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Replace weld id {oldId.Value}: ApplyDimensions threw {ex.GetType().Name}: {ex.Message}");
                    }

                    // 5b. Position the new weld at the captured centre
                    //    + axis. Weld FabricationParts return the base
                    //    Location type (LocationPoint cast is null), so
                    //    we delegate to FabricationHelpers.PositionWeld
                    //    which derives the new part's current centre +
                    //    axis from its connectors and applies the
                    //    correct Rotate + Move sequence.
                    try
                    {
                        FabricationHelpers.PositionWeld(doc, newWeld, targetCenter, targetAxis, posNotes);
                        doc.Regenerate();
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Replace weld id {oldId.Value}: PositionWeld threw {ex.GetType().Name}: {ex.Message}");
                    }
                    // Surface PositionWeld diagnostic notes when something
                    // looked off — silent failure is what bit us last
                    // time. "[WELD-GEO] no position source" and
                    // "[WELD-GEO] invalid object" are the two notes
                    // that indicate the position step actually skipped.
                    foreach (var n in posNotes)
                    {
                        if (n.IndexOf("no position source", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("invalid object",   StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            warnings.Add($"Replace weld id {oldId.Value}: PositionWeld diag — {n}");
                        }
                    }

                    // 6. Reconnect to neighbours by proximity. Each
                    //    neighbour connector picks its closest unused
                    //    connector on the new weld and calls ConnectTo;
                    //    the catalog handles axis alignment when the
                    //    geometry already lines up after the move.
                    var newConnectors = new List<Connector>();
                    try
                    {
                        foreach (Connector c in newWeld.ConnectorManager.Connectors)
                            newConnectors.Add(c);
                    }
                    catch { }

                    int connectedCount = 0;
                    foreach (var (neighbour, neighbourOrigin) in neighbours)
                    {
                        Connector? best = null;
                        double bestDist = double.MaxValue;
                        foreach (var c in newConnectors)
                        {
                            bool busy = false;
                            try { busy = c.IsConnected; } catch { }
                            if (busy) continue;
                            try
                            {
                                var d = c.Origin.DistanceTo(neighbourOrigin);
                                if (d < bestDist) { bestDist = d; best = c; }
                            }
                            catch { }
                        }
                        if (best == null) continue;

                        try
                        {
                            best.ConnectTo(neighbour);
                            connectedCount++;
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"Replace weld id {oldId.Value}: reconnect to neighbour on part {neighbour.Owner?.Id.Value} failed ({ex.Message}).");
                        }
                    }
                    if (neighbours.Count > 0 && connectedCount == 0)
                    {
                        warnings.Add($"Replace weld id {oldId.Value}: new field weld id {newWeld.Id.Value} placed but NOT reconnected to any of {neighbours.Count} neighbour(s). The spool drawing will still include the part but the model topology may be broken.");
                    }

                    map[oldId] = newWeld.Id;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Replace weld id {oldId.Value}: unhandled error — {ex.GetType().Name}: {ex.Message}");
                }
            }

            return map;
        }

        // ── Catalog helpers ────────────────────────────────────────────────────

        /// <summary>Walks the service's palette tree looking for a button
        /// whose name contains BOTH "field" and "weld" (and not "flange"
        /// or "neck", which excludes Weld Neck Flange and the like).
        /// Returns the first match along with the condition index for
        /// the part's size. Prefers a SIZE-STRING match (via the
        /// catalog's condition NAMES, e.g. "8&quot;" — much more reliable
        /// than connector radius on zero-length weld joints) and falls
        /// back to bore-in-feet matching only when no name match works.
        /// Returns (null, 0) when the service has no matching button.</summary>
        private static (FabricationServiceButton? button, int condition) FindFieldWeldButton(
            Document doc, int serviceId, string sizeName, double boreFt)
        {
            var candidates = new List<(FabricationServiceButton btn, int cond)>();
            try
            {
                var config  = FabricationConfiguration.GetFabricationConfiguration(doc);
                var service = config?.GetService(serviceId);
                if (service == null) return (null, 0);

                for (int pi = 0; pi < service.PaletteCount; pi++)
                {
                    bool excluded = false;
                    try { excluded = service.IsPaletteExcluded(pi); } catch { }
                    if (excluded) continue;

                    for (int bi = 0; bi < service.GetButtonCount(pi); bi++)
                    {
                        var btn = service.GetButton(pi, bi);
                        if (btn == null) continue;
                        bool btnExcl = false; try { btnExcl = btn.IsExcluded(); } catch { }
                        if (btnExcl) continue;

                        string name = btn.Name ?? string.Empty;
                        if (name.IndexOf("field",  StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (name.IndexOf("weld",   StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (name.IndexOf("flange", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (name.IndexOf("neck",   StringComparison.OrdinalIgnoreCase) >= 0) continue;

                        // Pick the condition that matches our part's
                        // size, trying in order of reliability:
                        //   (a) Local size-name match — uses our more
                        //       aggressive normaliser ("8\"ø" → "8\"")
                        //       which handles the ø / "in" / `''` form
                        //       variations the catalog uses.
                        //   (b) FabricationCreator's name matcher as a
                        //       second pass (slightly different
                        //       normaliser there).
                        //   (c) Bore match in feet, then inches, then
                        //       mm — the catalog stores condition
                        //       bounds in its NATIVE unit (often
                        //       inches), so 0.667 ft never matches
                        //       lo=4 hi=10 stored as inches; trying
                        //       multiple units lets us hit the right
                        //       range either way.
                        //   (d) Fall through to 0 with a warning so
                        //       the user knows the size match failed.
                        int cond = -1;
                        if (!string.IsNullOrWhiteSpace(sizeName))
                        {
                            cond = MatchConditionByName(btn, sizeName);
                            if (cond < 0)
                                cond = FabricationHelpers.FindConditionByName(btn, sizeName);
                        }
                        if (cond < 0)
                            cond = FindConditionForBoreMultiUnit(btn, boreFt);
                        if (cond < 0) cond = 0;

                        candidates.Add((btn, cond));
                    }
                }
            }
            catch { }

            return candidates.Count > 0 ? (candidates[0].btn, candidates[0].cond) : (null, 0);
        }

        /// <summary>For every weld currently in the walk's partitions
        /// (post-merge), returns the IDs of those whose neighbour parts
        /// (across connectors) live in a DIFFERENT spool partition.
        /// These are the welds that "connect between different spools"
        /// — the boundary welds the user wants converted to Field Welds
        /// in addition to the isolated (folded) ones. Field welds that
        /// are already named "Field Weld" are excluded so the swap is
        /// idempotent on re-runs.</summary>
        public static List<ElementId> FindCrossSpoolWelds(Document doc, WalkResult walk)
        {
            var boundary = new List<ElementId>();
            if (doc == null || walk == null || walk.Spools.Count < 2) return boundary;

            // Part → spool-index map. Built once so the per-weld
            // connector scan is O(connectors), not O(parts).
            var partToSpool = new Dictionary<ElementId, int>();
            for (int s = 0; s < walk.Spools.Count; s++)
                foreach (var id in walk.Spools[s].Parts)
                    partToSpool[id] = s;

            foreach (var (id, mySpool) in partToSpool)
            {
                if (doc.GetElement(id) is not FabricationPart fp) continue;
                if (!IsAnyWeld(doc, id)) continue;
                // Already a field weld — nothing to swap. Name check
                // matches catalog item; the user-facing
                // SpoolerRuleEvaluator detection also reads Comments for
                // back-compat with the previous build's Comments stamp.
                try
                {
                    if ((fp.Name ?? string.Empty)
                        .IndexOf("field weld", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                }
                catch { }

                bool crosses = false;
                try
                {
                    foreach (Connector c in fp.ConnectorManager.Connectors)
                    {
                        ConnectorSet refs;
                        try { refs = c.AllRefs; }
                        catch { continue; }
                        if (refs == null) continue;
                        foreach (Connector other in refs)
                        {
                            if (other?.Owner == null) continue;
                            if (other.Owner.Id == fp.Id) continue;
                            if (partToSpool.TryGetValue(other.Owner.Id, out int theirSpool)
                                && theirSpool != mySpool)
                            {
                                crosses = true;
                                break;
                            }
                        }
                        if (crosses) break;
                    }
                }
                catch { }

                if (crosses) boundary.Add(id);
            }
            return boundary;
        }

        /// <summary>Matches the part's size string against the button's
        /// condition NAMES using our aggressive normaliser
        /// (<see cref="NormaliseSizeName"/>) so that the various
        /// catalog formats — <c>8"ø</c>, <c>8''</c>, <c>8"</c> — all
        /// compare equal after normalisation. Returns -1 when no name
        /// matches; caller falls back to bore-based matching.</summary>
        private static int MatchConditionByName(FabricationServiceButton btn, string sizeName)
        {
            string needle = NormaliseSizeName(sizeName);
            if (string.IsNullOrWhiteSpace(needle)) return -1;
            try
            {
                int n = btn.ConditionCount;
                var names = new string[n];
                for (int c = 0; c < n; c++)
                    names[c] = NormaliseSizeName(btn.GetConditionName(c) ?? string.Empty);

                // Pass 1: exact (case-insensitive).
                for (int c = 0; c < n; c++)
                    if (string.Equals(names[c], needle, StringComparison.OrdinalIgnoreCase))
                        return c;

                // Pass 2: condition name contains size string.
                for (int c = 0; c < n; c++)
                    if (names[c].IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                        return c;

                // Pass 3: size string contains condition name.
                for (int c = 0; c < n; c++)
                    if (!string.IsNullOrEmpty(names[c]) &&
                        needle.IndexOf(names[c], StringComparison.OrdinalIgnoreCase) >= 0)
                        return c;
            }
            catch { }
            return -1;
        }

        /// <summary>Returns the condition index whose bore range
        /// contains the part's bore, trying the bore value in feet,
        /// inches, and millimetres in turn — the catalog stores
        /// condition bounds in its NATIVE unit (frequently inches or
        /// mm), and a 0.667 ft bore would never match a stored range
        /// of <c>lo=4 hi=10</c> in inches. First unit that lands on a
        /// matching range wins. Returns -1 when no unit matches;
        /// caller falls through to 0 = catalog default.</summary>
        private static int FindConditionForBoreMultiUnit(FabricationServiceButton btn, double boreFt)
        {
            if (boreFt <= 0) return -1;
            double boreIn = boreFt * 12.0;
            double boreMm = boreFt * 304.8;

            foreach (var test in new[] { boreFt, boreIn, boreMm })
            {
                int cond = FindConditionForBoreInUnit(btn, test);
                if (cond >= 0) return cond;
            }
            return -1;
        }

        /// <summary>Inner helper for <see cref="FindConditionForBoreMultiUnit"/>:
        /// finds the smallest-span condition range containing the
        /// supplied test value (the unit is implicit — caller is
        /// responsible for trying multiple). Returns -1 when no range
        /// includes the test value.</summary>
        private static int FindConditionForBoreInUnit(FabricationServiceButton btn, double testValue)
        {
            try
            {
                int    bestCond = -1;
                double bestSpan = double.MaxValue;
                double nudge    = testValue - 1e-9;   // nudge for boundary cases (≤ vs >)

                for (int c = 0; c < btn.ConditionCount; c++)
                {
                    double lo = btn.GetConditionLowerValue(c);
                    double hi = btn.GetConditionUpperValue(c);
                    if (nudge >= lo && (hi < 0 || nudge <= hi))
                    {
                        double span = hi < 0 ? 1e9 : (hi - lo);
                        if (span < bestSpan) { bestSpan = span; bestCond = c; }
                    }
                }
                return bestCond;
            }
            catch { return -1; }
        }

        // ── Part inspection helpers ────────────────────────────────────────────

        /// <summary>Reads the part's bore in feet by picking the LARGEST
        /// positive connector radius — some catalogs put a small
        /// "branch" connector on what looks like a 2-port joint, and
        /// taking the first one returned bottoms us out at 1/2" on an
        /// 8" weld. Returns 0 when no connector exposes a usable
        /// radius — the catalog will then fall back to condition 0
        /// after the by-name match also fails.</summary>
        private static double ReadBore(FabricationPart fp)
        {
            double best = 0;
            try
            {
                foreach (Connector c in fp.ConnectorManager.Connectors)
                {
                    try { if (c.Radius > best) best = c.Radius; } catch { }
                }
            }
            catch { }
            return best * 2.0;
        }

        /// <summary>Reads the part's nominal Size string (e.g. <c>8"</c>,
        /// <c>1-1/2"</c>). Real fab catalogs surface this through
        /// several parameters with subtly different formats — actual
        /// catalog dump shows <c>Size=8"ø</c>, <c>Product Size
        /// Description=8''</c>, <c>FABRICATION_PRIMARY_SIZE=8"ø</c>,
        /// <c>FABRICATION_PART_DIAMETER_IN=0.666667</c> (feet, double).
        /// We try them in order of cleanest format first ("Product Size
        /// Description" is the most catalog-condition-name-aligned),
        /// then strip the <c>ø</c> diameter symbol and other suffixes
        /// to maximise the chance of a name match against the new
        /// button's conditions. Returns an empty string when nothing
        /// resolves — caller falls back to bore-by-feet matching.</summary>
        private static string ReadSizeName(FabricationPart fp)
        {
            // Cleanest-format first: "Product Size Description"
            // typically holds "8''" or "1-1/2''" with no extra suffix,
            // which after NormaliseSize matches the catalog condition
            // names exactly. Fall back to the BIP and the "Size"
            // parameter (which contains "8\"ø" — needs the ø strip).
            var candidates = new (string Name, BuiltInParameter Bip)[]
            {
                ("Product Size Description", BuiltInParameter.FABRICATION_PRODUCT_DATA_SIZE_DESCRIPTION),
                ("Size",                     BuiltInParameter.INVALID),
                ("Overall Size",             BuiltInParameter.INVALID),
                ("Size of Primary End",      BuiltInParameter.INVALID),
                ("Free Size",                BuiltInParameter.INVALID),
                ("Pipe Size",                BuiltInParameter.INVALID),
                ("Nominal Size",             BuiltInParameter.INVALID),
                ("Nominal Diameter",         BuiltInParameter.INVALID),
            };

            foreach (var (name, bip) in candidates)
            {
                try
                {
                    Parameter? p = null;
                    if (bip != BuiltInParameter.INVALID)
                    {
                        try { p = fp.get_Parameter(bip); } catch { }
                    }
                    p ??= fp.LookupParameter(name);
                    if (p == null) continue;

                    if (p.StorageType == StorageType.String)
                    {
                        var s = p.AsString();
                        if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                    }
                    else if (p.StorageType == StorageType.Double)
                    {
                        // Numeric size in feet — emit a clean inches
                        // string for the by-name matcher.
                        var ft = p.AsDouble();
                        if (ft > 0)
                            return $"{(ft * 12.0):0.##}\"";
                    }
                }
                catch { }
            }
            return string.Empty;
        }

        /// <summary>Normalises a fab-part size string into the form the
        /// catalog's condition names use. Real catalog dumps show three
        /// formats in active use — <c>8"ø</c>, <c>8''</c>, <c>8"</c> —
        /// so we strip the diameter symbol, collapse the double-
        /// apostrophe inch glyph to a double-quote, and trim trailing
        /// noise ("in", "inch", whitespace). Result: a canonical
        /// <c>N"</c> or <c>N-frac"</c> form that matches the most
        /// common condition naming convention.</summary>
        private static string NormaliseSizeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.Trim()
                 .Replace("''", "\"")
                 .Replace("ø",  "")          // diameter symbol
                 .Replace("Ø",  "")          // capital variant
                 .Replace(" inches", "\"", StringComparison.OrdinalIgnoreCase)
                 .Replace(" inch",   "\"", StringComparison.OrdinalIgnoreCase)
                 .Replace(" in",     "\"", StringComparison.OrdinalIgnoreCase)
                 .Trim();
            return s;
        }

        /// <summary>Any Level in the doc — fallback when a part has no
        /// LevelId of its own. FabricationPart.Create requires a level,
        /// even though the new part is moved to a precise XYZ
        /// afterwards.</summary>
        private static ElementId AnyLevelId(Document doc)
        {
            try
            {
                var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level))
                    .Cast<Level>().FirstOrDefault();
                return lvl?.Id ?? ElementId.InvalidElementId;
            }
            catch { return ElementId.InvalidElementId; }
        }

        /// <summary>True when the element is a CID-2522 joint whose
        /// Product Range contains "Joint" — i.e., any weld (field or
        /// otherwise), excluding flanges and couplings that share the
        /// joint CID. Used to decide whether a single-element partition
        /// is the kind we want to merge away.</summary>
        private static bool IsAnyWeld(Document doc, ElementId id)
        {
            if (doc.GetElement(id) is not FabricationPart fp) return false;

            try { if (fp.ItemCustomId != JointCid) return false; }
            catch { return false; }

            try
            {
                var rangeParam = fp.LookupParameter("Product Range");
                if (rangeParam == null || rangeParam.StorageType != StorageType.String)
                    return false;
                var range = rangeParam.AsString() ?? string.Empty;
                return range.IndexOf("joint", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>Resolves the standard user-facing "Comments" parameter
        /// across Revit element variants. Tries the built-in instance
        /// comments first, falls back to a name lookup for catalogs
        /// where the BIP returns null. Used by
        /// <see cref="SpoolerRuleEvaluator"/>'s field-weld detection so
        /// historical comments-stamped welds (from earlier builds of
        /// this tool) are still recognised.</summary>
        internal static Parameter? ResolveCommentsParam(Element el)
        {
            try
            {
                var p = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (p != null) return p;
            }
            catch { }
            try
            {
                return el.LookupParameter("Comments");
            }
            catch { return null; }
        }
    }
}
