using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>
    /// Builds a Revit AssemblyInstance + assembly views + assembly sheet
    /// for a single spool when "Use Assemblies" is enabled. The
    /// AssemblyInstance is named after the spool (its <c>AssemblyTypeName</c>);
    /// requested orthogonal directions map to <see cref="AssemblyViewUtils.CreateDetailSection"/>
    /// calls, and each requested iso direction creates a separate
    /// orthographic 3D assembly view oriented + locked via
    /// <see cref="View3D.SaveOrientationAndLock"/> (the AssemblyDetailViewOrientation
    /// enum has no NE/NW/SE/SW members, so we keep our 4 iso boxes and
    /// drive them through View3D orientation manually — same pattern as
    /// the non-assembly path).
    ///
    /// All other concerns are unchanged from the non-assembly path: the
    /// sheet placement, schedule, scale, tag, leader, renumber, Include
    /// Welds, and view template all apply identically since the views
    /// the assembly produces are normal Revit views.
    /// </summary>
    public sealed class SpoolAssemblyBuilder
    {
        private readonly Document _doc;

        public SpoolAssemblyBuilder(Document doc) => _doc = doc;

        // ── Conflict detection ─────────────────────────────────────────────────

        /// <summary>Returns the subset of <paramref name="parts"/> that are
        /// already a member of some other AssemblyInstance. Callers should
        /// abort with the list when this is non-empty — Revit forbids a
        /// part being in two assemblies, and overwriting silently would
        /// risk damaging the user's existing assemblies.</summary>
        public List<ElementId> FindAssemblyConflicts(IEnumerable<ElementId> parts)
        {
            var conflicts = new List<ElementId>();
            foreach (var id in parts)
            {
                var e = _doc.GetElement(id);
                if (e == null) continue;
                try
                {
                    var asmId = e.AssemblyInstanceId;
                    if (asmId != null && asmId != ElementId.InvalidElementId)
                        conflicts.Add(id);
                }
                catch { /* defensive — some element types don't expose AssemblyInstanceId */ }
            }
            return conflicts;
        }

        // ── Create the assembly + its views + sheet ────────────────────────────

        /// <summary>Materialises the spool as an AssemblyInstance + assembly
        /// views + assembly sheet. Returns the IDs of everything created
        /// so the caller can populate <see cref="SpoolResult"/> and place
        /// viewports/schedules on the sheet.</summary>
        /// <summary>
        /// Materialises the spool as an AssemblyInstance + assembly views
        /// + assembly sheet. **Manages its own transactions internally** —
        /// the caller must NOT already have a Transaction open on
        /// <see cref="_doc"/>, only an outer TransactionGroup (so the
        /// individual subtransactions collapse to a single undo step).
        /// Splitting is necessary because Revit's assembly API requires
        /// AssemblyInstance.Create to be committed before AssemblyTypeName
        /// can be set — the backing AssemblyType element doesn't reach
        /// addressable state until the create transaction lands.
        /// </summary>
        public AssemblyBuildResult Build(
            IReadOnlyCollection<ElementId> partIds,
            IEnumerable<SpoolDirection> directions,
            string spoolName,
            ElementId titleblockTypeId,
            ElementId? viewTemplateId,
            List<string>? warnings = null)
        {
            warnings ??= new List<string>();
            var result = new AssemblyBuildResult();

            // ── Tx 1: create the AssemblyInstance ──────────────────────────────
            ElementId namingCategoryId = new ElementId(BuiltInCategory.OST_FabricationPipework);
            AssemblyInstance assembly;
            using (var tx = new Transaction(_doc, "Assembly: create"))
            {
                tx.Start();
                try
                {
                    assembly = AssemblyInstance.Create(_doc, partIds.ToList(), namingCategoryId);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Assembly creation failed: {ex.Message}");
                    tx.RollBack();
                    return result;
                }
                tx.Commit();
            }
            result.AssemblyInstanceId = assembly.Id;

            // ── Tx 2: name the assembly type ───────────────────────────────────
            // Now that the create transaction has committed, the
            // AssemblyType element is addressable. Try the instance
            // setter first; if it still refuses (some Revit catalogs
            // disagree on when the type is "ready"), rename via the
            // AssemblyType element's Name property as a fallback.
            using (var tx = new Transaction(_doc, "Assembly: rename type"))
            {
                tx.Start();
                bool named = false;
                try { assembly.AssemblyTypeName = spoolName; named = true; }
                catch { /* fall through to type-element rename */ }

                if (!named)
                {
                    try
                    {
                        var typeId = assembly.GetTypeId();
                        if (typeId != null && typeId != ElementId.InvalidElementId
                            && _doc.GetElement(typeId) is Element asmType)
                        {
                            asmType.Name = spoolName;
                            named = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Could not rename assembly type to '{spoolName}': {ex.Message}");
                    }
                }
                tx.Commit();
            }

            // ── Tx 3: create the requested assembly views ─────────────────────
            using (var tx = new Transaction(_doc, "Assembly: views"))
            {
                tx.Start();
                foreach (var d in directions)
                {
                    ElementId? viewId = null;
                    try
                    {
                        switch (d.Kind())
                        {
                            case SpoolViewKind.Plan:
                                viewId = CreateDetailView(assembly.Id, AssemblyDetailViewOrientation.ElevationTop);
                                break;
                            case SpoolViewKind.Section:
                                viewId = CreateDetailView(assembly.Id, MapSectionToOrientation(d));
                                break;
                            case SpoolViewKind.ThreeD:
                                viewId = CreateIsoView(assembly.Id, d);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Could not create assembly view for {d.Label()}: {ex.Message}");
                    }

                    if (viewId != null && viewId != ElementId.InvalidElementId)
                    {
                        result.ViewsByDirection[d] = viewId;

                        if (viewTemplateId != null && viewTemplateId != ElementId.InvalidElementId)
                        {
                            try
                            {
                                if (_doc.GetElement(viewId) is View v) v.ViewTemplateId = viewTemplateId;
                            }
                            catch (Exception ex)
                            {
                                warnings.Add($"View template not applied to {d.Label()}: {ex.Message}");
                            }
                        }
                    }
                }
                tx.Commit();
            }

            // ── Tx 4: create the assembly sheet ────────────────────────────────
            using (var tx = new Transaction(_doc, "Assembly: sheet"))
            {
                tx.Start();
                try
                {
                    var sheet = AssemblyViewUtils.CreateSheet(_doc, assembly.Id, titleblockTypeId);
                    result.SheetId = sheet?.Id;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Assembly sheet creation failed: {ex.Message}");
                }
                tx.Commit();
            }

            return result;
        }

        // ── Detail view (plan / section) ───────────────────────────────────────

        private ElementId CreateDetailView(ElementId assemblyId, AssemblyDetailViewOrientation orientation)
        {
            var view = AssemblyViewUtils.CreateDetailSection(_doc, assemblyId, orientation);
            return view?.Id ?? ElementId.InvalidElementId;
        }

        /// <summary>Maps our ortho direction enum to the matching
        /// AssemblyDetailViewOrientation. Front/Left/Right map cleanly;
        /// Revit doesn't expose a "Back" in our enum so we don't need it.</summary>
        private static AssemblyDetailViewOrientation MapSectionToOrientation(SpoolDirection d) =>
            d switch
            {
                SpoolDirection.Front => AssemblyDetailViewOrientation.ElevationFront,
                SpoolDirection.Left  => AssemblyDetailViewOrientation.ElevationLeft,
                SpoolDirection.Right => AssemblyDetailViewOrientation.ElevationRight,
                _                    => AssemblyDetailViewOrientation.ElevationFront,
            };

        // ── Iso (3D) view ──────────────────────────────────────────────────────

        /// <summary>One 3D orthographic assembly view per iso direction.
        /// Revit's assembly-view orientation enum has no NE/NW/SE/SW
        /// members, so we create a generic 3D ortho view via
        /// <see cref="AssemblyViewUtils.Create3DOrthographic"/> then set
        /// the view's orientation + lock manually — same approach the
        /// non-assembly path uses for its iso views.</summary>
        private ElementId CreateIsoView(ElementId assemblyId, SpoolDirection d)
        {
            var view = AssemblyViewUtils.Create3DOrthographic(_doc, assemblyId);
            if (view is not View3D v3) return view?.Id ?? ElementId.InvalidElementId;

            (XYZ forward, XYZ up) = d switch
            {
                SpoolDirection.SwIso => (new XYZ( 1,  1, -1).Normalize(), new XYZ( 1,  1, 2).Normalize()),
                SpoolDirection.SeIso => (new XYZ(-1,  1, -1).Normalize(), new XYZ(-1,  1, 2).Normalize()),
                SpoolDirection.NwIso => (new XYZ( 1, -1, -1).Normalize(), new XYZ( 1, -1, 2).Normalize()),
                SpoolDirection.NeIso => (new XYZ(-1, -1, -1).Normalize(), new XYZ(-1, -1, 2).Normalize()),
                _ => (XYZ.BasisZ.Negate(), XYZ.BasisY),
            };

            // Use the assembly's own bbox to seed the eye distance.
            // The 3D ortho view starts already framed on the assembly's
            // section box; we only need to swing the orientation.
            try
            {
                var bb = v3.GetSectionBox();
                var centre = bb.Transform.OfPoint((bb.Min + bb.Max) * 0.5);
                var diag   = bb.Max - bb.Min;
                double dist = Math.Max(diag.GetLength() * 2.0, 20.0);
                var eye    = centre - forward.Multiply(dist);
                v3.SetOrientation(new ViewOrientation3D(eye, up, forward));
            }
            catch { /* fall back to whatever default orientation Revit set */ }

            try { v3.SaveOrientationAndLock(); }
            catch { /* some VFTs/versions disallow locking — leave unlocked */ }

            return v3.Id;
        }
    }

    /// <summary>Output bundle from <see cref="SpoolAssemblyBuilder.Build"/>.
    /// The caller treats <see cref="ViewsByDirection"/> + <see cref="SheetId"/>
    /// exactly like the non-assembly path's view dictionary + sheet so
    /// the downstream viewport / schedule / tag logic doesn't need to
    /// know which path produced them.</summary>
    public sealed class AssemblyBuildResult
    {
        public ElementId? AssemblyInstanceId { get; set; }
        public ElementId? SheetId            { get; set; }
        public Dictionary<SpoolDirection, ElementId> ViewsByDirection { get; }
            = new Dictionary<SpoolDirection, ElementId>();
    }
}
