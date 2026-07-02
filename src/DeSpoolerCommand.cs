using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolTools.Revit.Spooling;
using System.Linq;

namespace SpoolTools
{
    /// <summary>DeSpooler ribbon command — the destructive inverse of
    /// Create Spool / The Spooler. Selects any part on a spool, finds
    /// all related sheets/views/parts, confirms with counts, then
    /// deletes + clears + unpins in a single transaction.</summary>
    [Transaction(TransactionMode.Manual)]
    public class DeSpoolerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document   doc   = uiDoc.Document;

            var settings = SpoolSettings.Load(doc);

            var preselected = uiDoc.Selection.GetElementIds()
                .Where(id => doc.GetElement(id) is FabricationPart)
                .ToList();

            if (preselected.Count == 0)
            {
                TaskDialog.Show("DeSpooler",
                    "Select at least one fabrication part that belongs to a spool, then run DeSpooler.");
                return Result.Cancelled;
            }

            var spoolNumbers = SpoolNumberRegistry.CurrentValuesOn(doc, preselected);
            if (spoolNumbers.Count == 0)
            {
                TaskDialog.Show("DeSpooler",
                    "None of the selected parts have a Spool Number set. Nothing to despool.");
                return Result.Cancelled;
            }

            var service = new DeSpoolerService(doc);
            var plan    = service.BuildPlan(spoolNumbers);

            if (plan.IsEmpty)
            {
                TaskDialog.Show("DeSpooler",
                    "Selected parts reference spool numbers, but no matching parts / sheets / views " +
                    "were found in the model. Nothing to despool.");
                return Result.Cancelled;
            }

            var confirm = new TaskDialog("DeSpooler — Confirm")
            {
                MainInstruction = $"Despool {plan.SpoolNumbers.Count} spool(s)?",
                MainContent =
                    $"The following will be deleted or reset:\n\n" +
                    $"    • Fabrication parts: {plan.PartIds.Count} (Spool Number + status cleared, unpinned)\n" +
                    (plan.AssemblyIds.Count > 0
                        ? $"    • Assemblies: {plan.AssemblyIds.Count} (deleted — cascades to their sheets and views)\n"
                        : string.Empty) +
                    $"    • Sheets: {plan.SheetIds.Count}\n" +
                    $"    • Views: {plan.ViewIds.Count}\n\n" +
                    $"Spool numbers being reverted:\n    {string.Join(", ", plan.SpoolNumbers)}\n\n" +
                    "This action is wrapped in a single undo step and can be reversed with Ctrl+Z.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel,
            };
            if (confirm.Show() != TaskDialogResult.Yes)
                return Result.Cancelled;

            var result = service.Execute(plan, settings.SpoolStatusParamName);

            var summary = new TaskDialog("DeSpooler — Complete")
            {
                MainInstruction = result.Success
                    ? "Despool complete."
                    : "Despool finished with warnings.",
                MainContent =
                    $"    • Parts unpinned: {result.UnpinnedCount}\n" +
                    $"    • Spool Numbers cleared: {result.SpoolNumbersClearedCount}\n" +
                    $"    • Status values cleared: {result.StatusValuesClearedCount}\n" +
                    $"    • Assemblies deleted: {result.AssembliesDeletedCount}\n" +
                    $"    • Sheets deleted: {result.SheetsDeletedCount}\n" +
                    $"    • Views deleted: {result.ViewsDeletedCount}" +
                    (result.Warnings.Count > 0
                        ? "\n\nWarnings:\n    • " + string.Join("\n    • ", result.Warnings)
                        : string.Empty),
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            summary.Show();

            return result.Success ? Result.Succeeded : Result.Failed;
        }
    }
}
