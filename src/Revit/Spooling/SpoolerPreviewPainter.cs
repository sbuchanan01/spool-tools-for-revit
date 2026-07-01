using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>
    /// Color-codes a throwaway 3D preview view by partition so the user
    /// can see at a glance how their Start + Breaks picks split the run
    /// into spools. Walks the network via <see cref="SpoolerNetworkWalker"/>
    /// and applies <see cref="OverrideGraphicSettings"/> per element.
    /// Unconnected parts (selection members that the walk never reached)
    /// get a distinct warning color to flag them visually.
    ///
    /// The painter does not OWN the view — it expects an existing
    /// preview view id created via <see cref="SpoolViewBuilder.CreatePreviewView"/>.
    /// All mutations happen inside an opened transaction; callers are
    /// responsible for the surrounding transaction lifecycle (this class
    /// runs on the Revit thread already, called from an ExternalEvent
    /// handler).
    /// </summary>
    public sealed class SpoolerPreviewPainter
    {
        /// <summary>12-color cycle for partition shading. Picked for
        /// high mutual contrast (red/green/blue plus warm/cool spread)
        /// so adjacent partition indices remain visually distinct even
        /// for runs with many breaks. Cycles for batches &gt; 12.</summary>
        private static readonly Color[] PartitionColors =
        {
            new(231,  76,  60),   // red
            new( 52, 152, 219),   // blue
            new( 46, 204, 113),   // green
            new(241, 196,  15),   // yellow
            new(155,  89, 182),   // purple
            new( 26, 188, 156),   // teal
            new(230, 126,  34),   // orange
            new(233,  30,  99),   // pink
            new( 96, 125, 139),   // blue-grey
            new(121,  85,  72),   // brown
            new(  0, 188, 212),   // cyan
            new(255,  87,  34),   // deep orange
        };

        /// <summary>Reserved warning hue for unconnected parts — bright
        /// orange-yellow that doesn't clash with any partition color in
        /// the cycle above. Catches the eye without reading as "error
        /// red", since being unconnected is informational, not failure.</summary>
        private static readonly Color UnconnectedColor = new(255, 152, 0);

        private readonly Document _doc;
        private ElementId _solidFillIdCache = ElementId.InvalidElementId;
        private bool      _solidFillResolved;

        public SpoolerPreviewPainter(Document doc) => _doc = doc;

        /// <summary>Walks the network, then applies a colored override to
        /// every part in the selection based on which partition it landed
        /// in. Before Start is set, falls back to clearing overrides
        /// (all parts at their natural appearance). Wraps everything in
        /// one transaction.</summary>
        public void ApplyColors(
            ElementId viewId,
            IReadOnlyCollection<ElementId> selection,
            ElementId start,
            IReadOnlyCollection<ElementId> breaks)
        {
            if (viewId == null || viewId == ElementId.InvalidElementId) return;
            if (_doc.GetElement(viewId) is not View view) return;
            if (selection == null || selection.Count == 0) return;

            var solidFillId = GetSolidFillPatternId();

            using var tx = new Transaction(_doc, "Spooler: paint preview by partition");
            tx.Start();

            // Reset every selection part to default overrides first so
            // stale colors from a previous walk don't bleed through when
            // the user moves Start or picks new Breaks.
            var defaultOgs = new OverrideGraphicSettings();
            foreach (var id in selection)
            {
                try { view.SetElementOverrides(id, defaultOgs); } catch { }
            }

            // Before Start: nothing to walk, leave defaults.
            if (start == ElementId.InvalidElementId)
            {
                tx.Commit();
                return;
            }

            var walk = SpoolerNetworkWalker.Walk(_doc, selection, start, breaks);

            foreach (var partition in walk.Spools)
            {
                var color = PartitionColors[(partition.Index - 1) % PartitionColors.Length];
                var ogs   = MakeOgs(color, solidFillId);
                foreach (var partId in partition.Parts)
                {
                    try { view.SetElementOverrides(partId, ogs); } catch { }
                }
            }

            var unconnOgs = MakeOgs(UnconnectedColor, solidFillId);
            foreach (var partId in walk.Unconnected)
            {
                try { view.SetElementOverrides(partId, unconnOgs); } catch { }
            }

            tx.Commit();
        }

        private static OverrideGraphicSettings MakeOgs(Color color, ElementId solidFillId)
        {
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetProjectionLineWeight(4);
            ogs.SetCutLineColor(color);
            ogs.SetCutLineWeight(4);
            if (solidFillId != ElementId.InvalidElementId)
            {
                ogs.SetSurfaceForegroundPatternColor(color);
                ogs.SetSurfaceForegroundPatternId(solidFillId);
                ogs.SetCutForegroundPatternColor(color);
                ogs.SetCutForegroundPatternId(solidFillId);
            }
            return ogs;
        }

        /// <summary>Lazy lookup of the project's solid-fill drafting
        /// pattern id. Cached so we don't FilteredElementCollector
        /// every paint pass (called many times when the user iterates
        /// on Start / Breaks picks).</summary>
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
                        try { return f.GetFillPattern()?.IsSolidFill == true; }
                        catch { return false; }
                    });
                _solidFillIdCache = pat?.Id ?? ElementId.InvalidElementId;
            }
            catch { _solidFillIdCache = ElementId.InvalidElementId; }
            _solidFillResolved = true;
            return _solidFillIdCache;
        }
    }
}
