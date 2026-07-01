using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>
    /// Creates spool views for a given selection. Computes a tight bounding box, then for each
    /// requested direction creates a ViewPlan (Top), ViewSection (Front/Left/Right), or
    /// 3D iso (SW/SE/NW/NE), sets Discipline = Mechanical + Sub-Discipline = "Spool Views",
    /// and isolates the selection so only those elements show.
    /// </summary>
    public sealed class SpoolViewBuilder
    {
        private readonly Document _doc;

        public double MarginFt { get; set; } = 1.0;
        public string SubDisciplineValue { get; set; } = "Spool Views";
        /// <summary>Optional view template id to apply to every created
        /// spool view. Templates that don't apply to a given view kind
        /// (e.g., Floor Plan template on a 3D view) are silently skipped.</summary>
        public ElementId? ViewTemplateId { get; set; }

        public SpoolViewBuilder(Document doc) => _doc = doc;

        public Dictionary<SpoolDirection, ElementId> BuildViews(
            IEnumerable<SpoolDirection> dirs,
            IReadOnlyCollection<ElementId> selection,
            string spoolNumber,
            List<string>? warnings = null)
        {
            _warnings = warnings;
            var result = new Dictionary<SpoolDirection, ElementId>();
            var bbox   = ComputeSelectionBoundingBox(selection);
            if (bbox == null) return result;

            foreach (var d in dirs)
            {
                ElementId? id = d.Kind() switch
                {
                    SpoolViewKind.Plan    => CreatePlanView(d, bbox, selection, spoolNumber),
                    SpoolViewKind.Section => CreateSectionView(d, bbox, selection, spoolNumber),
                    SpoolViewKind.ThreeD  => CreateIsoView(d, bbox, selection, spoolNumber),
                    _ => null,
                };
                if (id != null) result[d] = id;
            }
            return result;
        }

        private List<string>? _warnings;

        // ── Plan (Top) ──────────────────────────────────────────────────────────

        private ElementId? CreatePlanView(
            SpoolDirection d, BoundingBoxXYZ bbox,
            IReadOnlyCollection<ElementId> selection, string spoolNumber)
        {
            var vft   = FirstViewFamilyType(ViewFamily.FloorPlan);
            var level = LevelClosestBelow(bbox.Min.Z) ?? LowestLevel();
            if (vft == null || level == null) return null;

            var plan = ViewPlan.Create(_doc, vft.Id, level.Id);
            plan.Name = ViewName(spoolNumber, d);

            plan.CropBoxActive  = true;
            plan.CropBoxVisible = false;

            var cb = plan.CropBox;
            cb.Min = new XYZ(bbox.Min.X - MarginFt, bbox.Min.Y - MarginFt, cb.Min.Z);
            cb.Max = new XYZ(bbox.Max.X + MarginFt, bbox.Max.Y + MarginFt, cb.Max.Z);
            plan.CropBox = cb;

            ApplySpoolViewSettings(plan, selection);
            return plan.Id;
        }

        // ── Section (Front / Left / Right) ──────────────────────────────────────

        private ElementId? CreateSectionView(
            SpoolDirection d, BoundingBoxXYZ bbox,
            IReadOnlyCollection<ElementId> selection, string spoolNumber)
        {
            var vft = FirstViewFamilyType(ViewFamily.Section);
            if (vft == null) return null;

            // Local frame: BasisX = view right, BasisY = view up, BasisZ = away from model (toward camera).
            (XYZ right, XYZ up, XYZ back) = d switch
            {
                // Front: camera at -Y, looking +Y
                SpoolDirection.Front => (new XYZ( 1, 0, 0), new XYZ(0, 0, 1), new XYZ( 0, -1,  0)),
                // Left:  camera at -X, looking +X
                SpoolDirection.Left  => (new XYZ( 0, -1, 0), new XYZ(0, 0, 1), new XYZ(-1,  0,  0)),
                // Right: camera at +X, looking -X
                SpoolDirection.Right => (new XYZ( 0,  1, 0), new XYZ(0, 0, 1), new XYZ( 1,  0,  0)),
                _ => (XYZ.BasisX, XYZ.BasisZ, XYZ.BasisY.Negate()),
            };

            var centre = (bbox.Min + bbox.Max) * 0.5;

            double halfW = HalfExtentAlongAxis(bbox, right) + MarginFt;
            double halfH = HalfExtentAlongAxis(bbox, up)    + MarginFt;
            double halfD = HalfExtentAlongAxis(bbox, back)  + MarginFt;

            var t = Transform.Identity;
            t.Origin = centre;
            t.BasisX = right;
            t.BasisY = up;
            t.BasisZ = back;

            var sectionBox = new BoundingBoxXYZ
            {
                Transform = t,
                Min = new XYZ(-halfW, -halfH, -halfD),
                Max = new XYZ( halfW,  halfH,  halfD),
            };

            var section = ViewSection.CreateSection(_doc, vft.Id, sectionBox);
            section.Name = ViewName(spoolNumber, d);

            ApplySpoolViewSettings(section, selection);
            return section.Id;
        }

        // ── 3D Iso (SW / SE / NW / NE) ──────────────────────────────────────────

        private ElementId? CreateIsoView(
            SpoolDirection d, BoundingBoxXYZ bbox,
            IReadOnlyCollection<ElementId> selection, string spoolNumber)
        {
            var vft = FirstViewFamilyType(ViewFamily.ThreeDimensional);
            if (vft == null) return null;

            (XYZ forward, XYZ up) = d switch
            {
                SpoolDirection.SwIso => (new XYZ( 1,  1, -1).Normalize(), new XYZ( 1,  1, 2).Normalize()),
                SpoolDirection.SeIso => (new XYZ(-1,  1, -1).Normalize(), new XYZ(-1,  1, 2).Normalize()),
                SpoolDirection.NwIso => (new XYZ( 1, -1, -1).Normalize(), new XYZ( 1, -1, 2).Normalize()),
                SpoolDirection.NeIso => (new XYZ(-1, -1, -1).Normalize(), new XYZ(-1, -1, 2).Normalize()),
                _ => (XYZ.BasisZ.Negate(), XYZ.BasisY),
            };

            var centre = (bbox.Min + bbox.Max) * 0.5;
            var diag   = bbox.Max - bbox.Min;
            double dist = Math.Max(diag.GetLength() * 2.0, 20.0);
            var eye    = centre - forward.Multiply(dist);

            var view = View3D.CreateIsometric(_doc, vft.Id);
            view.Name = ViewName(spoolNumber, d);
            view.SetOrientation(new ViewOrientation3D(eye, up, forward));

            var sb = new BoundingBoxXYZ
            {
                Min = new XYZ(bbox.Min.X - MarginFt, bbox.Min.Y - MarginFt, bbox.Min.Z - MarginFt),
                Max = new XYZ(bbox.Max.X + MarginFt, bbox.Max.Y + MarginFt, bbox.Max.Z + MarginFt),
            };
            view.SetSectionBox(sb);
            view.IsSectionBoxActive = true;

            // Lock the orientation so the user can't accidentally rotate the
            // iso. They can still unlock from the view's right-click menu.
            try { view.SaveOrientationAndLock(); }
            catch { /* some VFTs/versions disallow locking — leave unlocked */ }

            ApplySpoolViewSettings(view, selection);
            return view.Id;
        }

        // ── Interactive preview view (PreviewControl host) ──────────────────────

        /// <summary>Creates a throwaway 3D iso view used by the spool dialog's
        /// <see cref="Autodesk.Revit.UI.PreviewControl"/>. NE-iso orientation,
        /// section box tight around the selection (with margin), selection
        /// temporary-isolated. NOT locked (the whole point is for the user to
        /// orbit). Caller is responsible for deleting the view when done.
        /// To "update" on selection change, delete this view and call
        /// CreatePreviewView again — in-place updates via
        /// IsolateElementsTemporary + ConvertToPermanent don't reliably
        /// re-show previously-hidden elements that are now in the new
        /// selection.</summary>
        public ElementId CreatePreviewView(
            IReadOnlyCollection<ElementId> selection, string viewName)
        {
            var vft = FirstViewFamilyType(ViewFamily.ThreeDimensional);
            if (vft == null) return ElementId.InvalidElementId;

            var view = View3D.CreateIsometric(_doc, vft.Id);
            view.Name = viewName;

            // Default to SE iso (forward = -1,+1,-1) — matches how the user
            // most naturally reads a spool on the resulting sheet. They can
            // orbit freely from there in the embedded PreviewControl.
            var forward = new XYZ(-1,  1, -1).Normalize();
            var up      = new XYZ(-1,  1,  2).Normalize();

            var bbox = ComputeSelectionBoundingBox(selection);
            if (bbox != null)
            {
                var centre = (bbox.Min + bbox.Max) * 0.5;
                var diag   = bbox.Max - bbox.Min;
                double dist = Math.Max(diag.GetLength() * 2.0, 20.0);
                var eye    = centre - forward.Multiply(dist);
                view.SetOrientation(new ViewOrientation3D(eye, up, forward));

                var sb = new BoundingBoxXYZ
                {
                    Min = new XYZ(bbox.Min.X - MarginFt, bbox.Min.Y - MarginFt, bbox.Min.Z - MarginFt),
                    Max = new XYZ(bbox.Max.X + MarginFt, bbox.Max.Y + MarginFt, bbox.Max.Z + MarginFt),
                };
                view.SetSectionBox(sb);
                view.IsSectionBoxActive = true;
            }

            HidePreviewChrome(view);
            try
            {
                view.IsolateElementsTemporary(selection.ToList());
                // Convert to permanent so the red "Temporary Hide/Isolate"
                // banner + border don't show inside the PreviewControl.
                view.ConvertTemporaryHideIsolateToPermanent();
            }
            catch { }

            return view.Id;
        }

        /// <summary>Hides the cyan section-box outline category in the preview
        /// view. Clipping (controlled by IsSectionBoxActive) is unaffected —
        /// only the visible outline graphic goes away.</summary>
        private static void HidePreviewChrome(View view)
        {
            try
            {
                var sbCat = new ElementId(BuiltInCategory.OST_SectionBox);
                if (view.CanCategoryBeHidden(sbCat))
                    view.SetCategoryHidden(sbCat, true);
            }
            catch { /* category may not exist on this view kind — fine */ }
        }

        // ── Settings shared across all view types ───────────────────────────────

        private void ApplySpoolViewSettings(View view, IReadOnlyCollection<ElementId> selection)
        {
            try { view.Discipline = ViewDiscipline.Mechanical; } catch { /* some VFTs disallow */ }

            var sub = ParameterHelper.FindParameter(view, "Sub-Discipline", "Sub Discipline");
            if (sub != null && !sub.IsReadOnly && sub.StorageType == StorageType.String)
                sub.Set(SubDisciplineValue);

            // Apply optional view template. The setter throws ArgumentException
            // when the template's view-type is incompatible with this view —
            // surface that as a warning per-view rather than silently skipping,
            // so the user knows which views didn't take the template.
            if (ViewTemplateId != null && ViewTemplateId != ElementId.InvalidElementId)
            {
                try { view.ViewTemplateId = ViewTemplateId; }
                catch (Exception ex)
                {
                    var tplName = (_doc.GetElement(ViewTemplateId) as View)?.Name ?? "<unknown>";
                    _warnings?.Add(
                        $"View template '{tplName}' could not be applied to view " +
                        $"'{view.Name}': {ex.Message}");
                }
            }

            try
            {
                var ids = selection.ToList();
                view.IsolateElementsTemporary(ids);
                view.ConvertTemporaryHideIsolateToPermanent();
            }
            catch { /* view kind may not support isolate — leave open */ }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        public BoundingBoxXYZ? ComputeSelectionBoundingBox(IEnumerable<ElementId> ids)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
            bool any = false;

            foreach (var id in ids)
            {
                var e = _doc.GetElement(id);
                if (e == null) continue;
                var bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                any = true;
                if (bb.Min.X < minX) minX = bb.Min.X;
                if (bb.Min.Y < minY) minY = bb.Min.Y;
                if (bb.Min.Z < minZ) minZ = bb.Min.Z;
                if (bb.Max.X > maxX) maxX = bb.Max.X;
                if (bb.Max.Y > maxY) maxY = bb.Max.Y;
                if (bb.Max.Z > maxZ) maxZ = bb.Max.Z;
            }

            if (!any) return null;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ),
            };
        }

        private static double HalfExtentAlongAxis(BoundingBoxXYZ bbox, XYZ axis)
        {
            // Project each of the 8 corners onto |axis| and take the half-range.
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                double x = (i & 1) == 0 ? bbox.Min.X : bbox.Max.X;
                double y = (i & 2) == 0 ? bbox.Min.Y : bbox.Max.Y;
                double z = (i & 4) == 0 ? bbox.Min.Z : bbox.Max.Z;
                double p = new XYZ(x, y, z).DotProduct(axis);
                if (p < lo) lo = p;
                if (p > hi) hi = p;
            }
            return (hi - lo) * 0.5;
        }

        private ViewFamilyType? FirstViewFamilyType(ViewFamily family) =>
            new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == family);

        private Level? LevelClosestBelow(double z)
        {
            var levels = new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();
            return levels
                .Where(l => l.Elevation <= z + 1.0)
                .OrderByDescending(l => l.Elevation)
                .FirstOrDefault();
        }

        private Level? LowestLevel() =>
            new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault();

        private static string ViewName(string spoolNumber, SpoolDirection d) =>
            $"Spool {spoolNumber} - {d.ViewNameSuffix()}";
    }
}
