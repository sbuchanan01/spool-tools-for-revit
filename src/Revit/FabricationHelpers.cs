using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpoolTools.Revit
{
    /// <summary>Five fabrication-API helpers extracted from PCF Exporter's
    /// 3,500-line FabricationCreator. The spool tool only needs these for
    /// the Field-Weld replacement step (SpoolerWeldPostProcessor): the
    /// rest of the original file is import-pipeline logic with PCF data
    /// model dependencies that we deliberately don't carry over.</summary>
    internal static class FabricationHelpers
    {
        // ── Weld positioning ─────────────────────────────────────────────────

        /// <summary>Positions a weld FabricationPart at <paramref name="weldCenter"/>,
        /// oriented so its axis aligns with <paramref name="pipeAxis"/>. Welds
        /// return the base Location type — neither LocationPoint nor LocationCurve —
        /// so we derive current centre + axis from the part's connectors.</summary>
        internal static void PositionWeld(Document doc, FabricationPart part,
            XYZ weldCenter, XYZ pipeAxis, List<string> notes)
        {
            if (!part.IsValidObject) { notes.Add("[WELD-GEO] invalid object"); return; }

            XYZ? currentCenter = null;
            XYZ? currentAxis   = null;

            try
            {
                var mgr = part.ConnectorManager;
                if (mgr != null)
                {
                    var allConns = new List<Connector>();
                    foreach (Connector c in mgr.Connectors)
                        allConns.Add(c);

                    if (allConns.Count >= 2)
                    {
                        XYZ o0 = allConns[0].Origin;
                        XYZ o1 = allConns[1].Origin;
                        currentCenter = (o0 + o1) * 0.5;
                        double span   = o0.DistanceTo(o1);
                        currentAxis   = span > 1e-9 ? (o1 - o0).Normalize() : null;
                    }
                    else if (allConns.Count == 1)
                    {
                        currentCenter = allConns[0].Origin;
                    }
                }
            }
            catch (Exception ex)
            {
                notes.Add($"[WELD-GEO] connector read failed: {ex.Message}");
            }

            if (currentCenter == null) { notes.Add("[WELD-GEO] no position source, skipping"); return; }

            if (currentAxis != null && pipeAxis.GetLength() > 1e-9)
            {
                XYZ normAxis = pipeAxis.Normalize();
                double dot = Math.Max(-1.0, Math.Min(1.0, currentAxis.DotProduct(normAxis)));

                if (Math.Abs(dot - 1.0) > 1e-9)
                {
                    XYZ    rotVec;
                    double angle;
                    if (Math.Abs(dot + 1.0) < 1e-9)
                    {
                        rotVec = (Math.Abs(currentAxis.X) < 0.9
                                     ? currentAxis.CrossProduct(XYZ.BasisX)
                                     : currentAxis.CrossProduct(XYZ.BasisY)).Normalize();
                        angle  = Math.PI;
                    }
                    else
                    {
                        rotVec = currentAxis.CrossProduct(normAxis).Normalize();
                        angle  = Math.Acos(dot);
                    }
                    if (rotVec.GetLength() > 1e-9)
                        ElementTransformUtils.RotateElement(doc, part.Id,
                            Line.CreateUnbound(currentCenter, rotVec), angle);
                }
            }

            XYZ delta = weldCenter - currentCenter;
            if (delta.GetLength() > 1e-9)
                ElementTransformUtils.MoveElement(doc, part.Id, delta);
        }

        // ── Dimension application ────────────────────────────────────────────

        /// <summary>Sets size + length on a freshly-created weld part by
        /// driving either the catalog's diameter dim directly or, when
        /// the part is product-list-based, by re-indexing ProductListEntry
        /// to the matching catalog entry.</summary>
        internal static void ApplyDimensions(
            Document doc, FabricationPart part, double boreFeet, double lengthFeet,
            string? sizeName, List<string> notes, bool verbose)
        {
            try
            {
                var dims = part.GetDimensions();
                FabricationDimensionDefinition? lenDim = null;
                bool productEntryChanged = false;

                foreach (var dim in dims)
                {
                    bool calc = false;
                    try { calc = part.IsDimensionCalculated(dim); } catch { }
                    if (calc) continue;

                    if (dim.Type == FabricationDimensionType.Length)
                    {
                        lenDim = dim;
                    }
                    else if (dim.Type == FabricationDimensionType.Diameter)
                    {
                        bool sizeSet = false;
                        try { part.SetDimensionValue(dim, boreFeet); sizeSet = true; } catch { }

                        if (!sizeSet)
                        {
                            bool isPL = false;
                            try { isPL = part.IsProductList(); } catch { }
                            if (isPL)
                            {
                                int idx = FindProductListEntry(part, sizeName, boreFeet, notes, verbose);
                                if (idx >= 0)
                                {
                                    int currentIdx;
                                    try { currentIdx = part.ProductListEntry; }
                                    catch { currentIdx = -2; }
                                    if (idx != currentIdx)
                                    {
                                        try { part.ProductListEntry = idx; productEntryChanged = true; }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }
                }

                if (productEntryChanged) doc.Regenerate();

                dims = part.GetDimensions();
                foreach (var dim in dims)
                {
                    bool calc = false;
                    try { calc = part.IsDimensionCalculated(dim); } catch { }
                    if (!calc && dim.Type == FabricationDimensionType.Length && lengthFeet > 0.001)
                    {
                        try { part.SetDimensionValue(dim, lengthFeet); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                if (verbose) notes.Add($"[DIMS] failed: {ex.Message}");
            }
        }

        // ── Product-list lookup ──────────────────────────────────────────────

        private static int FindProductListEntry(
            FabricationPart part, string? sizeName, double boreFeet,
            List<string> notes, bool verbose)
        {
            int count = 0;
            try { count = part.GetProductListEntryCount(); } catch { return -1; }
            if (count == 0) return -1;

            string normTarget = NormaliseSize(sizeName ?? "");
            double boreInches = boreFeet * 12.0;
            double boreMm     = boreFeet * 304.8;

            var names = new string[count];
            for (int i = 0; i < count; i++)
            {
                try { names[i] = part.GetProductListEntryName(i) ?? ""; }
                catch { names[i] = ""; }
            }

            if (!string.IsNullOrEmpty(normTarget))
                for (int i = 0; i < count; i++)
                    if (string.Equals(NormaliseSize(names[i]), normTarget, StringComparison.OrdinalIgnoreCase))
                        return i;

            if (!string.IsNullOrEmpty(normTarget))
                for (int i = 0; i < count; i++)
                    if (NormaliseSize(names[i]).IndexOf(normTarget, StringComparison.OrdinalIgnoreCase) >= 0)
                        return i;

            for (int i = 0; i < count; i++)
            {
                if (TryParseSizeToInches(names[i], out double val))
                {
                    if (Math.Abs(val - boreInches) < 0.1 ||
                        Math.Abs(val - boreMm)     < 1.0)
                        return i;
                }
            }
            return -1;
        }

        internal static bool TryParseSizeToInches(string s, out double inches)
        {
            inches = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string trimmed = s.Trim().TrimEnd('"', '\'', ' ', 'm', 'M', 'n', 'N');
            if (trimmed.Length == 0) return false;

            if (double.TryParse(trimmed,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double dec))
            {
                inches = dec;
                return true;
            }

            string normalised = trimmed.Replace('-', ' ');
            var parts = normalised.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            double whole = 0;
            string? fracPart = null;
            if (parts.Length == 1 && parts[0].Contains('/'))
            {
                fracPart = parts[0];
            }
            else if (parts.Length == 2 &&
                     double.TryParse(parts[0],
                         System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture,
                         out whole) &&
                     parts[1].Contains('/'))
            {
                fracPart = parts[1];
            }
            else return false;

            var fracTokens = fracPart.Split('/');
            if (fracTokens.Length != 2) return false;
            if (!double.TryParse(fracTokens[0],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double num)) return false;
            if (!double.TryParse(fracTokens[1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double den) || den == 0) return false;

            inches = whole + num / den;
            return true;
        }

        // ── Size string normalisation ────────────────────────────────────────

        /// <summary>Normalises an inch-symbol variant so '' (two
        /// apostrophes) and " (double-quote) compare equal. Also trims
        /// whitespace.</summary>
        internal static string NormaliseSize(string s) =>
            (s ?? "").Trim().Replace("''", "\"");

        // ── Condition-by-name lookup ─────────────────────────────────────────

        /// <summary>Finds the condition index whose name best matches the
        /// supplied size string. Both sides are normalised before
        /// comparison. Match passes: exact → contains-target → contained-in.
        /// Returns -1 if nothing matches.</summary>
        internal static int FindConditionByName(FabricationServiceButton button, string sizeName)
        {
            if (string.IsNullOrWhiteSpace(sizeName)) return -1;
            sizeName = NormaliseSize(sizeName);
            try
            {
                var names = new string[button.ConditionCount];
                for (int c = 0; c < button.ConditionCount; c++)
                    names[c] = NormaliseSize(button.GetConditionName(c) ?? "");

                for (int c = 0; c < names.Length; c++)
                    if (string.Equals(names[c], sizeName, StringComparison.OrdinalIgnoreCase))
                        return c;

                for (int c = 0; c < names.Length; c++)
                    if (names[c].IndexOf(sizeName, StringComparison.OrdinalIgnoreCase) >= 0)
                        return c;

                for (int c = 0; c < names.Length; c++)
                    if (!string.IsNullOrEmpty(names[c]) &&
                        sizeName.IndexOf(names[c], StringComparison.OrdinalIgnoreCase) >= 0)
                        return c;
            }
            catch { }
            return -1;
        }
    }
}
