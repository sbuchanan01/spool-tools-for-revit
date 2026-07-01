using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolTools.Revit.Spooling;
using SpoolTools.UI;
using System.Collections.Generic;
using System.Linq;

namespace SpoolTools
{
    /// <summary>
    /// Ribbon command that opens the Spool Config dialog — a modeless
    /// editor for the project-level defaults shared between Create Spool
    /// and The Spooler (titleblock + drawable region, schedule, scale,
    /// directions, view template, tag family, leader defaults, batch
    /// templates).
    ///
    /// Same titleblock / schedule / tag-family / view-template discovery
    /// as <see cref="SpoolCommand"/>; identical orphan sweep so a temp
    /// setup sheet left over from a failed pick gets cleaned up.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SpoolConfigCommand : IExternalCommand
    {
        private static SpoolConfigDialog? _instance;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return OpenConfig(commandData.Application.ActiveUIDocument);
        }

        /// <summary>Gathers project choices, builds the modeless Spool
        /// Config dialog, and shows it. Reused by both the ribbon
        /// command (above) and the "Spool Config…" shortcut buttons in
        /// the per-run Create Spool and The Spooler dialogs. The
        /// single-instance guard means a second click just activates
        /// the existing window instead of stacking duplicates.
        /// <paramref name="onSaved"/> fires once the dialog commits its
        /// changes to ExtensibleStorage — per-run dialogs use it to
        /// refresh status text or rebind defaults; pass null for the
        /// stand-alone ribbon launch.</summary>
        public static Result OpenConfig(UIDocument uiDoc, System.Action? onSaved = null)
        {
            if (_instance != null)
            {
                _instance.Activate();
                if (onSaved != null) _instance.OnSaved = onSaved;
                return Result.Succeeded;
            }

            Document doc = uiDoc.Document;

            SweepOrphanTempSheets(doc);

            var titleblocks       = CollectTitleblocks(doc);
            var schedules         = CollectSchedules(doc);
            var tagFamilies       = CollectFabPipeTagFamilies(doc);
            var viewTemplates     = CollectViewTemplates(doc);
            var dimensionStyles   = CollectLinearDimensionStyles(doc);
            var regions           = SpoolTitleblockRegions.LoadAll(doc);
            var statusParamNames  = CollectFabricationTextParameters(doc);
            var settings          = SpoolSettings.Load(doc);

            if (titleblocks.Count == 0)
            {
                TaskDialog.Show("Spool Config",
                    "No titleblock families are loaded in this project. " +
                    "Load a titleblock and try again.");
                return Result.Cancelled;
            }

            var dialog = new SpoolConfigDialog(
                uiDoc, titleblocks, schedules, tagFamilies, viewTemplates,
                dimensionStyles, regions, statusParamNames, settings)
            {
                OnSaved = onSaved,
            };

            _instance = dialog;
            dialog.Closed += (_, _) => _instance = null;
            dialog.Show();

            return Result.Succeeded;
        }

        // ── Discovery (mirrors SpoolCommand) ────────────────────────────────────

        private static IReadOnlyList<TitleblockChoice> CollectTitleblocks(Document doc) =>
            new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .Cast<FamilySymbol>()
                .OrderBy(s => s.Family.Name)
                .ThenBy(s => s.Name)
                .Select(s => new TitleblockChoice(s.Id, FormatFamilyTypeName(s)))
                .ToList();

        private static IReadOnlyList<ScheduleChoice> CollectSchedules(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule
                            && s.Definition?.CategoryId.Value != (long)BuiltInCategory.OST_TitleBlocks)
                .OrderBy(s => s.Name)
                .Select(s => new ScheduleChoice(s.Id, s.Name))
                .ToList();

        private static IReadOnlyList<TagFamilyChoice> CollectFabPipeTagFamilies(Document doc) =>
            new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_FabricationPipeworkTags)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .OrderBy(s => s.Family.Name)
                .ThenBy(s => s.Name)
                .Select(s => new TagFamilyChoice(s.Id, FormatFamilyTypeName(s)))
                .ToList();

        private static string FormatFamilyTypeName(FamilySymbol s)
        {
            string family = s.Family?.Name ?? string.Empty;
            string type   = s.Name ?? string.Empty;
            return string.Equals(family, type, System.StringComparison.Ordinal)
                ? family
                : $"{family} — {type}";
        }

        /// <summary>Linear DimensionTypes available in the project.
        /// Filters out angular / radial / arc-length styles since the
        /// spool dim engine only emits straight linear dimensions for
        /// now. Sorted alphabetically.</summary>
        private static IReadOnlyList<DimensionStyleChoice> CollectLinearDimensionStyles(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(DimensionType))
                .Cast<DimensionType>()
                .Where(dt =>
                {
                    try { return dt.StyleType == DimensionStyleType.Linear; }
                    catch { return false; }
                })
                .OrderBy(dt => dt.Name)
                .Select(dt => new DimensionStyleChoice(dt.Id, dt.Name))
                .ToList();

        private static IReadOnlyList<ViewTemplateChoice> CollectViewTemplates(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.IsTemplate)
                .OrderBy(v => v.Name)
                .Select(v => new ViewTemplateChoice(v.Id, v.Name))
                .ToList();

        /// <summary>Collects writable Text-type project parameter names
        /// that the user could plausibly assign as the spool-status
        /// destination. Two sources, deduped:
        /// <list type="number">
        /// <item>Sample FabricationPart probe — the most reliable
        /// classifier (StorageType.String works across API versions);
        /// also guarantees the param actually appears on a fab part.</item>
        /// <item>ParameterBindings sweep — fallback for projects with
        /// no fab parts placed yet (newly-applied template). Definition-
        /// based classification is best-effort.</item>
        /// </list>
        /// Built-in parameters are filtered out via
        /// <c>InternalDefinition.BuiltInParameter</c>; only user-mappable
        /// project / shared params remain. Same approach as
        /// <see cref="SpoolTools.UI.PricingSourceDialog"/>.</summary>
        private static IReadOnlyList<string> CollectFabricationTextParameters(Document doc)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            // Source 1: probe a sample FabricationPart.
            try
            {
                var sample = new FilteredElementCollector(doc)
                    .OfClass(typeof(FabricationPart))
                    .FirstElement();
                if (sample != null)
                {
                    foreach (Parameter p in sample.Parameters)
                    {
                        if (p == null || p.Definition == null) continue;
                        if (p.IsReadOnly) continue;
                        if (p.StorageType != StorageType.String) continue;

                        bool isBuiltIn = false;
                        try
                        {
                            if (p.Definition is InternalDefinition idef)
                                isBuiltIn = idef.BuiltInParameter != BuiltInParameter.INVALID;
                        }
                        catch { }
                        if (isBuiltIn) continue;

                        var n = p.Definition.Name;
                        if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
                    }
                }
            }
            catch { }

            // Source 2: bindings sweep — fall through to catch empty
            // projects. We can't filter by StorageType here (Definition
            // doesn't expose it directly), so we filter by ForgeTypeId
            // when available, else accept all Text-ish definitions.
            try
            {
                var bindings = doc.ParameterBindings;
                var it = bindings.ForwardIterator();
                while (it.MoveNext())
                {
                    var def = it.Key as Definition;
                    if (def == null) continue;
                    string n = def.Name;
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    if (names.Contains(n)) continue;

                    // Best-effort String classification via Definition.GetDataType().
                    try
                    {
                        var dataType = def.GetDataType();
                        if (dataType != null && dataType == SpecTypeId.String.Text)
                            names.Add(n);
                    }
                    catch
                    {
                        // Older API surface — accept the definition; the
                        // VM's "(none)" fallback covers a mistaken pick.
                        names.Add(n);
                    }
                }
            }
            catch { }

            return names.ToList();
        }

        /// <summary>Deletes any orphan temp setup sheets left over from a
        /// prior region picker that didn't finish its cleanup (e.g.,
        /// Revit crashed mid-pick). Matched by the well-known
        /// <c>TMP_SPOOL_RGN_</c> prefix so we don't touch user sheets.</summary>
        private static void SweepOrphanTempSheets(Document doc)
        {
            const string SheetPrefix = "TMP_SPOOL_RGN_";

            var orphans = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => s.SheetNumber != null && s.SheetNumber.StartsWith(SheetPrefix))
                .Select(s => s.Id)
                .ToList();

            if (orphans.Count == 0) return;

            using var tx = new Transaction(doc, "Spool Config: sweep orphan temp sheets");
            tx.Start();
            foreach (var id in orphans) { try { doc.Delete(id); } catch { } }
            tx.Commit();
        }
    }
}
