using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>
    /// User-picked drawable rectangles inside a specific titleblock. Two rectangles
    /// per titleblock type: where viewports may live, and where the schedule sits.
    /// Coordinates are in sheet feet (XYZ with Z = 0) relative to the sheet origin,
    /// so they work on every sheet that uses the same titleblock symbol.
    /// </summary>
    public sealed class TitleblockRegion
    {
        public long TitleblockTypeId { get; init; }
        public XYZ ViewMin     { get; init; } = XYZ.Zero;
        public XYZ ViewMax     { get; init; } = XYZ.Zero;
        public XYZ ScheduleMin { get; init; } = XYZ.Zero;
        public XYZ ScheduleMax { get; init; } = XYZ.Zero;

        public double ViewWidthFt  => ViewMax.X - ViewMin.X;
        public double ViewHeightFt => ViewMax.Y - ViewMin.Y;
    }

    /// <summary>
    /// Per-project ExtensibleStorage for titleblock drawable regions. Stored on
    /// ProjectInfo as a flat, semicolon-separated record list — same encoding
    /// pattern as <see cref="SpoolSettings"/> to avoid pulling in JSON.
    /// </summary>
    public static class SpoolTitleblockRegions
    {
        private static readonly Guid SchemaGuid = new Guid("5DC4D9EF-6DF5-45DB-A2C2-177BBF0042C4");
        private const string SchemaName = "SpoolTools_SpoolTitleblockRegions";
        private const string FieldData  = "Data";

        public static IReadOnlyDictionary<long, TitleblockRegion> LoadAll(Document doc)
        {
            var info = new FilteredElementCollector(doc).OfClass(typeof(ProjectInfo)).FirstOrDefault();
            if (info == null) return new Dictionary<long, TitleblockRegion>();

            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return new Dictionary<long, TitleblockRegion>();

            var ent = info.GetEntity(schema);
            if (ent == null || !ent.IsValid()) return new Dictionary<long, TitleblockRegion>();

            return Deserialize(ent.Get<string>(FieldData) ?? string.Empty);
        }

        public static TitleblockRegion? Get(Document doc, long titleblockTypeId)
        {
            var all = LoadAll(doc);
            return all.TryGetValue(titleblockTypeId, out var r) ? r : null;
        }

        /// <summary>Upserts the region for one titleblock and writes back. Caller is
        /// responsible for opening the surrounding Transaction.</summary>
        public static void Set(Document doc, TitleblockRegion region)
        {
            var info = new FilteredElementCollector(doc).OfClass(typeof(ProjectInfo)).FirstOrDefault();
            if (info == null) return;

            var dict = new Dictionary<long, TitleblockRegion>(LoadAll(doc));
            dict[region.TitleblockTypeId] = region;

            var schema = Schema.Lookup(SchemaGuid) ?? CreateSchema();
            var ent = new Entity(schema);
            ent.Set(FieldData, Serialize(dict));
            info.SetEntity(ent);
        }

        private static Schema CreateSchema()
        {
            var b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName(SchemaName);
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddSimpleField(FieldData, typeof(string));
            return b.Finish();
        }

        // Record format per region (9 doubles total — the Y values in min/max are
        // stored explicitly even though Y=V on a sheet, kept verbose for clarity):
        //   tb,vMinX,vMinY,vMaxX,vMaxY,sMinX,sMinY,sMaxX,sMaxY
        // Records separated by ';'.

        private static string Serialize(IDictionary<long, TitleblockRegion> regions)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var r in regions.Values)
            {
                if (!first) sb.Append(';');
                first = false;
                sb.Append(r.TitleblockTypeId.ToString(CultureInfo.InvariantCulture));
                sb.Append(','); sb.Append(F(r.ViewMin.X));
                sb.Append(','); sb.Append(F(r.ViewMin.Y));
                sb.Append(','); sb.Append(F(r.ViewMax.X));
                sb.Append(','); sb.Append(F(r.ViewMax.Y));
                sb.Append(','); sb.Append(F(r.ScheduleMin.X));
                sb.Append(','); sb.Append(F(r.ScheduleMin.Y));
                sb.Append(','); sb.Append(F(r.ScheduleMax.X));
                sb.Append(','); sb.Append(F(r.ScheduleMax.Y));
            }
            return sb.ToString();
        }

        private static IReadOnlyDictionary<long, TitleblockRegion> Deserialize(string text)
        {
            var result = new Dictionary<long, TitleblockRegion>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            foreach (var record in text.Split(';'))
            {
                var parts = record.Split(',');
                if (parts.Length < 9) continue;
                if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long tb)) continue;

                if (!TryD(parts[1], out var vminX)) continue;
                if (!TryD(parts[2], out var vminY)) continue;
                if (!TryD(parts[3], out var vmaxX)) continue;
                if (!TryD(parts[4], out var vmaxY)) continue;
                if (!TryD(parts[5], out var sminX)) continue;
                if (!TryD(parts[6], out var sminY)) continue;
                if (!TryD(parts[7], out var smaxX)) continue;
                if (!TryD(parts[8], out var smaxY)) continue;

                result[tb] = new TitleblockRegion
                {
                    TitleblockTypeId = tb,
                    ViewMin     = new XYZ(vminX, vminY, 0),
                    ViewMax     = new XYZ(vmaxX, vmaxY, 0),
                    ScheduleMin = new XYZ(sminX, sminY, 0),
                    ScheduleMax = new XYZ(smaxX, smaxY, 0),
                };
            }
            return result;
        }

        private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static bool TryD(string s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
