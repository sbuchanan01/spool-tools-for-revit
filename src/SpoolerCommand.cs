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
    /// <summary>
    /// Ribbon entry point for "The Spooler" — the batch / multi-spool tool.
    /// Captures the user's pre-selection (FabricationParts only) and opens
    /// the standalone Spooler dialog. The dialog reads shared spool
    /// configuration (titleblock, schedule, view directions, scale, tag,
    /// leader settings, Include Welds, renumber prefs) from the project's
    /// persisted SpoolSettings store — the same store the single-spool
    /// dialog writes to.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SpoolerCommand : IExternalCommand
    {
        private static SpoolerDialog? _instance;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (_instance != null)
            {
                _instance.Activate();
                return Result.Succeeded;
            }

            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document   doc   = uiDoc.Document;

            // Sweep orphan preview views from any prior session that
            // crashed before its dialog could clean up. Identified by
            // the well-known TMP_SPOOLER_PREVIEW_ name prefix.
            SweepOrphanPreviewViews(doc);

            // Pre-selection: every FabricationPart currently selected in
            // Revit becomes the batch's working set. Walker stays within
            // this set; anything outside (or disconnected) is ignored or
            // surfaced as "unconnected" in the summary.
            var preselected = uiDoc.Selection.GetElementIds()
                .Where(id => doc.GetElement(id) is FabricationPart)
                .ToList();

            // Create a selection-scoped 3D preview view up front so the
            // dialog can host an interactive 3D viewport that color-codes
            // each partition as the user picks Start / Breaks. Skipped
            // when no parts are pre-selected — the dialog's empty-selection
            // badge handles that case instead.
            ElementId? previewViewId = null;
            if (preselected.Count > 0)
            {
                using var tx = new Transaction(doc, "Spooler: create preview view");
                tx.Start();
                try
                {
                    var builder = new SpoolViewBuilder(doc);
                    var id = builder.CreatePreviewView(
                        preselected,
                        "TMP_SPOOLER_PREVIEW_" + DateTime.Now.Ticks);
                    if (id != ElementId.InvalidElementId) previewViewId = id;
                }
                catch { /* preview is optional — dialog still works without it */ }
                tx.Commit();
            }

            // Clear the live Revit selection so the dialog's start/break
            // picks don't appear as already-highlighted, and so the
            // PreviewControl renders parts in their assigned partition
            // colors (not in Revit's selection-highlight blue).
            uiDoc.Selection.SetElementIds(new List<ElementId>());

            var dialog = new SpoolerDialog(uiDoc, preselected, previewViewId);
            _instance = dialog;
            dialog.Closed += (_, _) => _instance = null;
            dialog.Show();

            return Result.Succeeded;
        }

        /// <summary>Deletes any leftover TMP_SPOOLER_PREVIEW_* 3D views
        /// from prior sessions that didn't get a clean dialog-close
        /// cleanup (e.g., Revit crashed). Matched by name prefix.</summary>
        private static void SweepOrphanPreviewViews(Document doc)
        {
            const string Prefix = "TMP_SPOOLER_PREVIEW_";
            var orphans = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(v => v.Name != null && v.Name.StartsWith(Prefix))
                .Select(v => v.Id)
                .ToList();

            if (orphans.Count == 0) return;
            using var tx = new Transaction(doc, "Spooler: sweep orphan preview views");
            tx.Start();
            foreach (var id in orphans) { try { doc.Delete(id); } catch { } }
            tx.Commit();
        }
    }
}
