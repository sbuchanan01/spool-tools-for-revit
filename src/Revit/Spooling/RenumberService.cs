using Autodesk.Revit.DB;
using SpoolTools.Revit;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>
    /// Assigns sequential values to the "Item Number" parameter on a selection
    /// of FabricationParts. Optionally collapses identical parts to share the
    /// same number — "identical" defined as same family + CID + ProductListEntry
    /// + Item Description + Item Code.
    /// </summary>
    public sealed class RenumberService
    {
        private readonly Document _doc;
        public RenumberService(Document doc) => _doc = doc;

        public sealed class RenumberResult
        {
            public bool   Success       { get; init; }
            public int    PartsUpdated  { get; init; }
            public int    UniqueNumbers { get; init; }
            public int    Skipped       { get; init; }
            public string Message       { get; init; } = string.Empty;
        }

        public RenumberResult Renumber(
            IReadOnlyCollection<ElementId> ids,
            int startingNumber,
            bool useSameForIdentical,
            bool useLengthAsSeparator = false)
        {
            var parts = ids
                .Select(id => _doc.GetElement(id) as FabricationPart)
                .Where(p => p != null)
                .Cast<FabricationPart>()
                // Stable element-id order so successive runs produce the same
                // numbering for the same selection.
                .OrderBy(p => p.Id.Value)
                .ToList();

            if (parts.Count == 0)
                return new RenumberResult
                {
                    Success = false,
                    Message = "No fabrication parts found in the selection.",
                };

            int counter = startingNumber;
            var idToNumber  = new Dictionary<ElementId, int>();
            var keyToNumber = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var part in parts)
            {
                if (useSameForIdentical)
                {
                    string key = IdentityKey(part, useLengthAsSeparator);
                    if (keyToNumber.TryGetValue(key, out int existing))
                    {
                        idToNumber[part.Id] = existing;
                    }
                    else
                    {
                        idToNumber[part.Id] = counter;
                        keyToNumber[key]    = counter;
                        counter++;
                    }
                }
                else
                {
                    idToNumber[part.Id] = counter;
                    counter++;
                }
            }

            int written = 0, skipped = 0;
            using (var tx = new Transaction(_doc, "Renumber: write Item Number"))
            {
                tx.Start();
                foreach (var kv in idToNumber)
                {
                    var elem = _doc.GetElement(kv.Key);
                    if (elem == null) { skipped++; continue; }

                    var p = ParameterHelper.FindParameter(elem, "Item Number");
                    if (p == null || p.IsReadOnly) { skipped++; continue; }

                    switch (p.StorageType)
                    {
                        case StorageType.String:
                            p.Set(kv.Value.ToString());
                            written++;
                            break;
                        case StorageType.Integer:
                            p.Set(kv.Value);
                            written++;
                            break;
                        default:
                            skipped++;
                            break;
                    }
                }
                tx.Commit();
            }

            int unique = idToNumber.Values.Distinct().Count();
            var msg    = $"Renumbered {written} part(s) using {unique} unique number(s).";
            if (skipped > 0)
                msg += $"\n{skipped} skipped (Item Number parameter missing or wrong storage type).";

            return new RenumberResult
            {
                Success       = true,
                PartsUpdated  = written,
                UniqueNumbers = unique,
                Skipped       = skipped,
                Message       = msg,
            };
        }

        /// <summary>Identity key for the "same part" check. Combines CID
        /// (catalog type) + ProductListEntry (size variant) + Item Description
        /// + Item Code. When <paramref name="useLengthAsSeparator"/> is set,
        /// the centerline length (rounded to 1/16") is appended for PIPE parts
        /// only — so two 4'-0" pipes share a number but a 4'-0" and a 6'-0"
        /// pipe get distinct numbers. Non-pipe parts ignore length so they
        /// still collapse normally.</summary>
        private static string IdentityKey(FabricationPart part, bool useLengthAsSeparator)
        {
            int cid     = part.ItemCustomId;
            int product = -1;
            try { product = part.ProductListEntry; } catch { /* not all parts support it */ }

            string itemDesc = ParameterHelper.FindParameter(part, "Item Description")?.AsString() ?? "";
            string itemCode = ParameterHelper.FindParameter(part, "Item Code", "ADSK_Item Code")?.AsString() ?? "";

            string lengthPart = "";
            if (useLengthAsSeparator && IsPipe(part))
            {
                double ft = GetCenterlineLengthFt(part);
                // Quantize to 1/16" so tiny floating drift doesn't separate
                // two pipes that the fab catalog would call identical.
                double q  = Math.Round(ft * 192.0) / 192.0;
                lengthPart = q.ToString("F4", CultureInfo.InvariantCulture);
            }

            return $"{cid}|{product}|{itemDesc}|{itemCode}|{lengthPart}";
        }

        private static bool IsPipe(FabricationPart part) =>
            string.Equals(PartTypeClassifier.GetPcfType(part), "PIPE",
                          StringComparison.OrdinalIgnoreCase);

        private static double GetCenterlineLengthFt(FabricationPart part)
        {
            try { return part.CenterlineLength; } catch { }

            var p = ParameterHelper.FindParameter(part, "Length", "Cut Length");
            if (p != null && p.StorageType == StorageType.Double)
                return p.AsDouble();
            return 0;
        }
    }
}
