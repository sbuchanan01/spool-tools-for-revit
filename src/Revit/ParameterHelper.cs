using Autodesk.Revit.DB;
using System;
using System.Linq;

namespace SpoolTools.Revit
{
    /// <summary>Tiny extraction of the PCF Exporter's parameter lookup
    /// helper — kept as its own file so the spool code can move into
    /// the standalone without pulling in 900+ lines of unrelated
    /// ExportCommand machinery. Behaviour is identical: first tries
    /// case-sensitive LookupParameter (cheap), then falls back to a
    /// case-insensitive scan across the element's full parameter set
    /// (slower, but catches catalog variations).</summary>
    internal static class ParameterHelper
    {
        public static Parameter? FindParameter(Element element, params string[] names)
        {
            foreach (string name in names)
            {
                var p = element.LookupParameter(name);
                if (p != null) return p;
            }
            return element.Parameters
                .Cast<Parameter>()
                .FirstOrDefault(p => names.Any(n => string.Equals(
                    p.Definition.Name, n, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
