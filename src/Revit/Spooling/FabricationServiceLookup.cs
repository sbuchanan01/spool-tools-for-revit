using Autodesk.Revit.DB;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>Shared utility for resolving a FabricationPart's
    /// Fabrication Service (abbreviation + full name). Used by The
    /// Spooler dialog (for the prominent Service display + live preview)
    /// and by the batch orchestrator (for {Service} token expansion
    /// per partition). Falls back gracefully when fields are missing
    /// — returns (null, null) for non-FabricationParts or when the
    /// catalog/service can't be reached.</summary>
    public static class FabricationServiceLookup
    {
        public static (string? Abbreviation, string? Name) Resolve(Document? doc, ElementId? id)
        {
            if (doc == null || id == null || id == ElementId.InvalidElementId) return (null, null);
            if (doc.GetElement(id) is not FabricationPart part) return (null, null);

            string? abbr = null;
            string? name = null;
            try
            {
                name = part.ServiceName;
                int serviceId = part.ServiceId;
                var cfg = FabricationConfiguration.GetFabricationConfiguration(doc);
                if (cfg != null && serviceId >= 0)
                {
                    var svc = cfg.GetService(serviceId);
                    abbr = svc?.Abbreviation;
                    if (string.IsNullOrWhiteSpace(name)) name = svc?.Name;
                }
            }
            catch
            {
                // Some catalogs / Revit versions vary; tolerate failure by
                // falling back to the ServiceName property alone.
            }
            return (string.IsNullOrWhiteSpace(abbr) ? null : abbr,
                    string.IsNullOrWhiteSpace(name) ? null : name);
        }
    }
}
