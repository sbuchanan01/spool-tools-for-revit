using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolTools.Revit.Spooling;
using SpoolTools.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpoolTools
{
    [Transaction(TransactionMode.Manual)]
    public class SpoolCommand : IExternalCommand
    {
        private static SpoolDialog? _instance;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (_instance != null)
            {
                _instance.Activate();
                return Result.Succeeded;
            }

            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document   doc   = uiDoc.Document;

            // Sweep any orphan temp setup sheets left over from a prior picker
            // run that didn't complete its cleanup (e.g., Revit crashed mid-pick).
            // Identified by the well-known SheetNumber prefix.
            SweepOrphanTempSheets(doc);

            var existing    = SpoolNumberRegistry.Collect(doc);
            var titleblocks = CollectTitleblocks(doc);
            var schedules   = CollectSchedules(doc);
            var tagFamilies = CollectFabPipeTagFamilies(doc);
            var viewTemplates = CollectViewTemplates(doc);
            var regions     = SpoolTitleblockRegions.LoadAll(doc);
            var settings    = SpoolSettings.Load(doc);

            if (titleblocks.Count == 0)
            {
                TaskDialog.Show("Create Spool",
                    "No titleblock families are loaded in this project. " +
                    "Load a titleblock and try again.");
                return Result.Cancelled;
            }

            // Pre-load Revit selection (FabricationParts only). Compute before
            // dialog construction so we can also seed the preview view.
            var preselected = uiDoc.Selection.GetElementIds()
                .Where(id => doc.GetElement(id) is FabricationPart)
                .ToList();
            var spoolValues = SpoolNumberRegistry.CurrentValuesOn(doc, preselected);

            // Create a throwaway 3D preview view if we have pre-selection so the
            // dialog can host an interactive orbit/zoom view via PreviewControl.
            ElementId? previewViewId = null;
            if (preselected.Count > 0)
            {
                using var tx = new Transaction(doc, "Spool: create preview view");
                tx.Start();
                var builder = new SpoolViewBuilder(doc);
                var id = builder.CreatePreviewView(
                    preselected,
                    "TMP_SPOOL_PREVIEW_" + DateTime.Now.Ticks);
                if (id != ElementId.InvalidElementId) previewViewId = id;
                tx.Commit();
            }

            // Clear the live Revit selection so the PreviewControl renders the
            // parts in their natural appearance, not highlighted as selected.
            // The dialog tracks the spool selection internally via
            // ViewModel.SelectedIds, so the doc-level selection isn't needed.
            uiDoc.Selection.SetElementIds(new List<ElementId>());

            var dialog = new SpoolDialog(uiDoc, existing, titleblocks, schedules, tagFamilies, viewTemplates, regions, settings, previewViewId);
            dialog.LoadSelection(preselected, spoolValues);

            _instance = dialog;
            dialog.Closed += (_, _) => _instance = null;
            dialog.Show();

            return Result.Succeeded;
        }

        // ── Title block + schedule discovery ────────────────────────────────────

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
                .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && s.Definition?.CategoryId.Value != (long)BuiltInCategory.OST_TitleBlocks)
                .OrderBy(s => s.Name)
                .Select(s => new ScheduleChoice(s.Id, s.Name))
                .ToList();

        /// <summary>Tag FamilySymbols loaded in the project that target MEP
        /// Fabrication Pipework (category OST_FabricationPipeworkTags).</summary>
        private static IReadOnlyList<TagFamilyChoice> CollectFabPipeTagFamilies(Document doc) =>
            new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_FabricationPipeworkTags)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .OrderBy(s => s.Family.Name)
                .ThenBy(s => s.Name)
                .Select(s => new TagFamilyChoice(s.Id, FormatFamilyTypeName(s)))
                .ToList();

        /// <summary>FamilyName — TypeName, but collapses to just FamilyName
        /// when the type name is the same string (common for fab tags and
        /// some titleblocks where there's a single default type matching
        /// the family name).</summary>
        private static string FormatFamilyTypeName(FamilySymbol s)
        {
            string family = s.Family?.Name ?? string.Empty;
            string type   = s.Name ?? string.Empty;
            return string.Equals(family, type, StringComparison.Ordinal)
                ? family
                : $"{family} — {type}";
        }

        /// <summary>All view templates in the project (Views with IsTemplate=true).
        /// Listed by name regardless of which view family they target — the
        /// spool service silently skips a template on a view kind it can't
        /// apply to.</summary>
        private static IReadOnlyList<ViewTemplateChoice> CollectViewTemplates(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.IsTemplate)
                .OrderBy(v => v.Name)
                .Select(v => new ViewTemplateChoice(v.Id, v.Name))
                .ToList();

        /// <summary>Deletes any leftover temp objects from a prior run that
        /// didn't complete its own cleanup: setup sheets (region picker) and
        /// preview views (dialog PreviewControl host). Matched by well-known
        /// name prefixes.</summary>
        private static void SweepOrphanTempSheets(Document doc)
        {
            const string SheetPrefix = "TMP_SPOOL_RGN_";
            const string ViewPrefix  = "TMP_SPOOL_PREVIEW_";

            var orphanSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => s.SheetNumber != null && s.SheetNumber.StartsWith(SheetPrefix))
                .Select(s => s.Id)
                .ToList();

            var orphanViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(v => v.Name != null && v.Name.StartsWith(ViewPrefix))
                .Select(v => v.Id)
                .ToList();

            if (orphanSheets.Count == 0 && orphanViews.Count == 0) return;

            using var tx = new Transaction(doc, "Spool: sweep orphan temp sheets + preview views");
            tx.Start();
            foreach (var id in orphanSheets) { try { doc.Delete(id); } catch { } }
            foreach (var id in orphanViews)  { try { doc.Delete(id); } catch { } }
            tx.Commit();
        }
    }
}
