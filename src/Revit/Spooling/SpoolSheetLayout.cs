using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>
    /// Places spool viewports + schedule inside the user-picked drawable
    /// rectangles for the active titleblock (see <see cref="TitleblockRegion"/>).
    /// A 3×3 grid of equal cells fills the picked VIEW rectangle:
    /// <code>
    ///   NW Iso  Top    NE Iso
    ///   Left    Front  Right
    ///   SW Iso  (--)   SE Iso
    /// </code>
    /// The schedule's top-right is anchored at the top-right of the picked
    /// SCHEDULE rectangle. Both rectangles come from a one-time per-titleblock
    /// pick so the sheet outline + titleblock graphics are no longer assumed.
    /// </summary>
    public sealed class SpoolSheetLayout
    {
        public sealed class Cell
        {
            public XYZ    Centre      { get; init; } = XYZ.Zero;
            public double CellWidthFt { get; init; }
            public double CellHeightFt{ get; init; }
        }

        /// <summary>1/4-inch buffer kept inside each picked rectangle so views
        /// + schedule don't crowd the titleblock's drawn border. Same value
        /// used by the view grid and the schedule width constraint.</summary>
        public const double InnerBufferFt = 0.25 / 12.0;

        /// <summary>0.5-inch clearance on each side of every cell — gives ~1"
        /// of gutter between adjacent viewports regardless of cell size.
        /// Using an absolute value (not a fraction of cell) so small cells
        /// don't shrink the clearance proportionally and crowd.</summary>
        public const double CellClearancePerSideFt = 0.5 / 12.0;

        public static (int col, int row) GridPos(SpoolDirection d) => d switch
        {
            SpoolDirection.NwIso => (0, 0),
            SpoolDirection.Top   => (1, 0),
            SpoolDirection.NeIso => (2, 0),
            SpoolDirection.Left  => (0, 1),
            SpoolDirection.Front => (1, 1),
            SpoolDirection.Right => (2, 1),
            SpoolDirection.SwIso => (0, 2),
            SpoolDirection.SeIso => (2, 2),
            _ => (1, 1),
        };

        /// <summary>Per-direction position with the iso-slide rule applied: when
        /// Left isn't selected, the first west-side iso (NW/SW) takes the Left
        /// slot (col 0, row 1) so it lands beside Front instead of being stuck
        /// in a corner. Same on the east side for Right. First-in-list wins so
        /// two isos on the same side don't collide.</summary>
        public static Dictionary<SpoolDirection, (int col, int row)>
            EffectivePositions(IReadOnlyList<SpoolDirection> dirs)
        {
            var positions = dirs.ToDictionary(d => d, d => GridPos(d));

            bool hasLeft  = dirs.Contains(SpoolDirection.Left);
            bool hasRight = dirs.Contains(SpoolDirection.Right);

            if (!hasLeft)
            {
                foreach (var d in dirs)
                {
                    if (d is SpoolDirection.NwIso or SpoolDirection.SwIso)
                    {
                        positions[d] = (0, 1);
                        break;
                    }
                }
            }
            if (!hasRight)
            {
                foreach (var d in dirs)
                {
                    if (d is SpoolDirection.NeIso or SpoolDirection.SeIso)
                    {
                        positions[d] = (2, 1);
                        break;
                    }
                }
            }
            return positions;
        }

        /// <summary>Lays the view grid inside the user-picked view region. Only
        /// the rows and columns actually used by the chosen directions exist —
        /// unused tracks are collapsed so the remaining views expand to fill
        /// the picked rectangle. Third-angle relationships are preserved (Top
        /// stays above Front, Left stays left of Front, etc.) because the
        /// relative ordering of <see cref="GridPos"/> coordinates is preserved.
        /// </summary>
        public Dictionary<SpoolDirection, Cell> Compute(
            TitleblockRegion region,
            IEnumerable<SpoolDirection> dirs)
        {
            var dirList = dirs.ToList();
            var result  = new Dictionary<SpoolDirection, Cell>();
            if (dirList.Count == 0) return result;

            var positions = EffectivePositions(dirList);
            var usedRows  = new SortedSet<int>();
            var usedCols  = new SortedSet<int>();
            foreach (var d in dirList)
            {
                var (col, row) = positions[d];
                usedRows.Add(row);
                usedCols.Add(col);
            }
            var rowList = usedRows.ToList();
            var colList = usedCols.ToList();

            // Pull every cell in by 1/4" inside the picked region so cells
            // don't kiss the titleblock border.
            double areaMinX = region.ViewMin.X + InnerBufferFt;
            double areaMaxY = region.ViewMax.Y - InnerBufferFt;
            double areaW    = region.ViewWidthFt  - 2 * InnerBufferFt;
            double areaH    = region.ViewHeightFt - 2 * InnerBufferFt;

            double cellW = areaW / colList.Count;
            double cellH = areaH / rowList.Count;

            foreach (var d in dirList)
            {
                var (col, row) = positions[d];
                int colIdx = colList.IndexOf(col);
                int rowIdx = rowList.IndexOf(row);

                double cx = areaMinX + (colIdx + 0.5) * cellW;
                // rowList is sorted ascending, but row 0 in GridPos is the TOP
                // of the layout — so rowIdx 0 maps to highest Y in the region.
                double cy = areaMaxY - (rowIdx + 0.5) * cellH;

                result[d] = new Cell
                {
                    Centre       = new XYZ(cx, cy, 0),
                    // 0.5" clearance on each side -> ~1" gutter between cells.
                    CellWidthFt  = Math.Max(0.05, cellW - 2 * CellClearancePerSideFt),
                    CellHeightFt = Math.Max(0.05, cellH - 2 * CellClearancePerSideFt),
                };
            }
            return result;
        }

        /// <summary>Anchor point for the schedule: TOP-LEFT corner of the
        /// picked schedule rectangle, inset by the 1/4" inner buffer so the
        /// schedule edge isn't flush with the titleblock border.
        /// ScheduleSheetInstance.Create places the schedule's top-left at
        /// this point, so the schedule grows down + right from here.</summary>
        public static XYZ ScheduleAnchor(TitleblockRegion region) =>
            new XYZ(region.ScheduleMin.X + InnerBufferFt,
                    region.ScheduleMax.Y - InnerBufferFt, 0);

        /// <summary>Picks a single drawing scale that fits the largest view into its cell. Snaps
        /// to a common imperial denominator. Used only when the user picks "Auto Fit".
        /// A 20% tolerance lets us pick the next-finer scale when the geometry would only
        /// overflow the cell by a small fraction.
        /// <paramref name="bumpOneStepCoarser"/> moves the result one standard step coarser
        /// (e.g. 1"=1' -> 3/4"=1') — caller passes true for iso-only selections so the
        /// rendered iso has breathing room rather than filling the cell edge-to-edge.</summary>
        public static int ChooseScale(
            IDictionary<SpoolDirection, BoundingBoxXYZ?> viewExtents,
            IDictionary<SpoolDirection, Cell> cells,
            bool bumpOneStepCoarser = false)
        {
            const double Tolerance = 1.20;

            double maxDenominator = 12.0;
            foreach (var kv in viewExtents)
            {
                var bbox = kv.Value;
                if (bbox == null) continue;
                if (!cells.TryGetValue(kv.Key, out var cell)) continue;

                double worldW = bbox.Max.X - bbox.Min.X;
                double worldH = bbox.Max.Y - bbox.Min.Y;
                if (worldW <= 0 || worldH <= 0) continue;

                double needW = worldW / cell.CellWidthFt;
                double needH = worldH / cell.CellHeightFt;
                double need = Math.Max(needW, needH);
                if (need > maxDenominator) maxDenominator = need;
            }
            int[] standard = { 12, 16, 24, 32, 48, 64, 96, 128, 192, 384 };
            int chosenIdx = standard.Length - 1;
            for (int i = 0; i < standard.Length; i++)
            {
                if (standard[i] * Tolerance >= maxDenominator) { chosenIdx = i; break; }
            }
            if (bumpOneStepCoarser && chosenIdx < standard.Length - 1) chosenIdx++;
            return standard[chosenIdx];
        }
    }
}
