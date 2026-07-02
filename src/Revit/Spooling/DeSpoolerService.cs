using Autodesk.Revit.DB;
using SpoolTools.Revit;
using System.Collections.Generic;
using System.Linq;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>DeSpooler business logic — mirror of PCFExporter's
    /// service. Reverses what Create Spool and The Spooler produce.
    /// See PCFExporter equivalent for full behaviour notes.</summary>
    public sealed class DeSpoolerService
    {
        private readonly Document _doc;

        public DeSpoolerService(Document doc)
        {
            _doc = doc;
        }

        public DeSpoolPlan BuildPlan(IReadOnlyCollection<string> spoolNumbers)
        {
            var plan = new DeSpoolPlan
            {
                SpoolNumbers = spoolNumbers
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim())
                    .Distinct(System.StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };

            if (plan.SpoolNumbers.Count == 0) return plan;

            var lookup = new HashSet<string>(plan.SpoolNumbers, System.StringComparer.OrdinalIgnoreCase);

            var allFabParts = new FilteredElementCollector(_doc)
                .OfClass(typeof(FabricationPart))
                .Cast<FabricationPart>()
                .ToList();
            foreach (var p in allFabParts)
            {
                string? v = ParameterHelper.FindParameter(p, SpoolNumberRegistry.SpoolNumberParam)?.AsString();
                if (string.IsNullOrWhiteSpace(v)) continue;
                if (lookup.Contains(v.Trim()))
                    plan.PartIds.Add(p.Id);
            }

            var partSet = new HashSet<ElementId>(plan.PartIds);
            var allAssemblies = new FilteredElementCollector(_doc)
                .OfClass(typeof(AssemblyInstance))
                .Cast<AssemblyInstance>()
                .ToList();
            foreach (var a in allAssemblies)
            {
                var members = a.GetMemberIds();
                if (members == null || members.Count == 0) continue;
                if (members.Any(m => partSet.Contains(m)))
                    plan.AssemblyIds.Add(a.Id);
            }

            var assemblyOwnedSheetIds = new HashSet<ElementId>();
            foreach (var aid in plan.AssemblyIds)
            {
                var asm = _doc.GetElement(aid) as AssemblyInstance;
                if (asm == null) continue;
                foreach (ElementId dep in asm.GetMemberIds())
                    assemblyOwnedSheetIds.Add(dep);
            }

            var allSheets = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .ToList();
            foreach (var s in allSheets)
            {
                if (s == null || s.IsPlaceholder) continue;
                if (assemblyOwnedSheetIds.Contains(s.Id)) continue;
                string name = s.Name ?? "";
                foreach (var sn in plan.SpoolNumbers)
                {
                    if (name.IndexOf(sn, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        plan.SheetIds.Add(s.Id);
                        break;
                    }
                }
            }

            foreach (var sid in plan.SheetIds)
            {
                var sheet = _doc.GetElement(sid) as ViewSheet;
                if (sheet == null) continue;
                foreach (ElementId vpId in sheet.GetAllViewports())
                {
                    var vp = _doc.GetElement(vpId) as Viewport;
                    if (vp == null) continue;
                    var v = _doc.GetElement(vp.ViewId) as View;
                    if (v == null) continue;
                    if (v.ViewType == ViewType.Schedule) continue;
                    plan.ViewIds.Add(v.Id);
                }
            }

            return plan;
        }

        public DeSpoolResult Execute(DeSpoolPlan plan, string statusParamName)
        {
            var result = new DeSpoolResult();
            if (plan.SpoolNumbers.Count == 0) return result;

            using var tx = new Transaction(_doc, "DeSpooler: revert spool");
            tx.Start();
            try
            {
                foreach (var pid in plan.PartIds)
                {
                    var p = _doc.GetElement(pid);
                    if (p == null) continue;
                    try { p.Pinned = false; result.UnpinnedCount++; } catch { }
                    TryClearString(p, SpoolNumberRegistry.SpoolNumberParam, ref result.SpoolNumbersClearedCount);
                    if (!string.IsNullOrWhiteSpace(statusParamName))
                        TryClearString(p, statusParamName, ref result.StatusValuesClearedCount);
                }

                foreach (var aid in plan.AssemblyIds)
                {
                    try { _doc.Delete(aid); result.AssembliesDeletedCount++; }
                    catch { result.Warnings.Add($"Failed to delete assembly {aid.Value}."); }
                }

                foreach (var vid in plan.ViewIds)
                {
                    if (_doc.GetElement(vid) == null) continue;
                    try { _doc.Delete(vid); result.ViewsDeletedCount++; }
                    catch { result.Warnings.Add($"Failed to delete view {vid.Value}."); }
                }

                foreach (var sid in plan.SheetIds)
                {
                    if (_doc.GetElement(sid) == null) continue;
                    try { _doc.Delete(sid); result.SheetsDeletedCount++; }
                    catch { result.Warnings.Add($"Failed to delete sheet {sid.Value}."); }
                }

                tx.Commit();
                result.Success = true;
            }
            catch (System.Exception ex)
            {
                if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                result.Success = false;
                result.Warnings.Add(ex.Message);
            }
            return result;
        }

        private static void TryClearString(Element e, string paramName, ref int counter)
        {
            try
            {
                var p = ParameterHelper.FindParameter(e, paramName);
                if (p == null || p.IsReadOnly) return;
                if (p.StorageType != StorageType.String) return;
                if (string.IsNullOrEmpty(p.AsString())) return;
                p.Set(string.Empty);
                counter++;
            }
            catch { }
        }
    }

    public sealed class DeSpoolPlan
    {
        public IReadOnlyList<string> SpoolNumbers { get; set; } = new List<string>();
        public List<ElementId> PartIds       { get; } = new();
        public List<ElementId> AssemblyIds   { get; } = new();
        public List<ElementId> SheetIds      { get; } = new();
        public List<ElementId> ViewIds       { get; } = new();

        public bool IsEmpty => PartIds.Count == 0
                            && AssemblyIds.Count == 0
                            && SheetIds.Count == 0
                            && ViewIds.Count == 0;
    }

    public sealed class DeSpoolResult
    {
        public bool Success;
        public int UnpinnedCount;
        public int SpoolNumbersClearedCount;
        public int StatusValuesClearedCount;
        public int AssembliesDeletedCount;
        public int SheetsDeletedCount;
        public int ViewsDeletedCount;
        public List<string> Warnings { get; } = new();
    }
}
