using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>
    /// Reads "Spool Number" off every fabrication pipework element in the project, grouped by value.
    /// Used by the dialog for duplicate detection and the "Used Spool Numbers" expander.
    /// </summary>
    public static class SpoolNumberRegistry
    {
        public const string SpoolNumberParam     = "Spool Number";
        public const string FabricationStatusParam = "Fabrication Status";
        public const string FabricationStatusValue = "Issued for Fabrication";

        public sealed class Entry
        {
            public string SpoolNumber { get; init; } = string.Empty;
            public List<ElementId> ElementIds { get; } = new();
            public int Count => ElementIds.Count;
        }

        public static IReadOnlyList<Entry> Collect(Document doc)
        {
            var byNumber = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

            foreach (var elem in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_FabricationPipework)
                .WhereElementIsNotElementType())
            {
                string? v = ParameterHelper.FindParameter(elem, SpoolNumberParam)?.AsString();
                if (string.IsNullOrWhiteSpace(v)) continue;
                v = v.Trim();

                if (!byNumber.TryGetValue(v, out var entry))
                {
                    entry = new Entry { SpoolNumber = v };
                    byNumber[v] = entry;
                }
                entry.ElementIds.Add(elem.Id);
            }

            return byNumber.Values
                .OrderBy(e => e.SpoolNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Reads the spool numbers currently set on the given elements (distinct, trimmed).
        /// Used to pre-fill the dialog when the user has a pre-existing spool selected.</summary>
        public static IReadOnlyList<string> CurrentValuesOn(Document doc, IEnumerable<ElementId> ids)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                var e = doc.GetElement(id);
                if (e == null) continue;
                string? v = ParameterHelper.FindParameter(e, SpoolNumberParam)?.AsString();
                if (string.IsNullOrWhiteSpace(v)) continue;
                seen.Add(v.Trim());
            }
            return seen.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Groups the given element ids by their current Spool
        /// Number parameter. Parts with no spool number are omitted.
        /// Used by the safety-net warning dialog.</summary>
        public static Dictionary<string, List<ElementId>> GroupByExistingSpool(
            Document doc, IEnumerable<ElementId> ids)
        {
            var result = new Dictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                var e = doc.GetElement(id);
                if (e == null) continue;
                string? v = ParameterHelper.FindParameter(e, SpoolNumberParam)?.AsString();
                if (string.IsNullOrWhiteSpace(v)) continue;
                var key = v.Trim();
                if (!result.TryGetValue(key, out var list))
                {
                    list = new List<ElementId>();
                    result[key] = list;
                }
                list.Add(id);
            }
            return result;
        }
    }
}
