using Autodesk.Revit.UI;
using SpoolTools.Revit;
using System;
using System.Reflection;

namespace SpoolTools
{
    /// <summary>
    /// Add-in entry point. Registers the "Spool Tools" ribbon tab with a
    /// single "Spooling" panel containing three buttons:
    ///
    ///   • Create Spool — single-spool dialog (SpoolCommand)
    ///   • The Spooler  — batch multi-spool tool (SpoolerCommand)
    ///   • Spool Config — project-level defaults (SpoolConfigCommand)
    ///
    /// Also wires up the modeless-dialog plumbing — a static
    /// ExternalEvent + handler that all three dialogs marshal Revit-side
    /// work through. The dialogs reference these as
    /// <c>SpoolToolsApp.SpoolHandler</c> / <c>SpoolToolsApp.SpoolEvent</c>.
    /// </summary>
    public class SpoolToolsApp : IExternalApplication
    {
        /// <summary>Shared handler all spool dialogs use to marshal
        /// closures onto the Revit API thread. Created once at startup;
        /// dialogs call <c>SetAction(...)</c> then <c>Raise()</c>.</summary>
        public static RevitEventHandler? SpoolHandler { get; private set; }
        public static ExternalEvent?     SpoolEvent   { get; private set; }

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                SpoolHandler = new RevitEventHandler();
                SpoolEvent   = ExternalEvent.Create(SpoolHandler);

                const string tabName   = "Spool Tools";
                const string panelName = "Spooling";

                try { app.CreateRibbonTab(tabName); }
                catch { /* tab may already exist if multiple add-ins share it */ }

                var panel = app.CreateRibbonPanel(tabName, panelName);

                string asm = Assembly.GetExecutingAssembly().Location;

                var createSpoolButton = new PushButtonData(
                    name:         "CreateSpool",
                    text:         "Create\nSpool",
                    assemblyName: asm,
                    className:    "SpoolTools.SpoolCommand")
                {
                    ToolTip = "Create a single spool sheet from selected fabrication parts.",
                    LongDescription =
                        "Assigns a unique Spool Number and configurable status to the " +
                        "selected parts, pins them, then creates the requested ortho " +
                        "and iso views laid out third-angle on a new sheet sized to " +
                        "the selection. Project-level defaults (titleblock, schedule, " +
                        "tag family, etc.) live in Spool Config.",
                    LargeImage = RibbonIconFactory.Spool(32),
                    Image      = RibbonIconFactory.Spool(16),
                };

                var spoolerButton = new PushButtonData(
                    name:         "TheSpooler",
                    text:         "The\nSpooler",
                    assemblyName: asm,
                    className:    "SpoolTools.SpoolerCommand")
                {
                    ToolTip = "Batch-create multiple spool sheets along a pipe network.",
                    LongDescription =
                        "Walks a connected pipe selection from a START element, splits " +
                        "it at user-picked BREAK elements (and at branches off tees), " +
                        "and creates a sheet per spool with auto-sequenced numbers " +
                        "from a token-based template (e.g. {Service}-{ID}-{N:00}). " +
                        "Reuses the shared Spool Config settings.",
                    LargeImage = RibbonIconFactory.Spooler(32),
                    Image      = RibbonIconFactory.Spooler(16),
                };

                var despoolerButton = new PushButtonData(
                    name:         "DeSpooler",
                    text:         "DeSpooler",
                    assemblyName: asm,
                    className:    "SpoolTools.DeSpoolerCommand")
                {
                    ToolTip = "Revert a spool: delete its sheets/views, clear Spool Number + status, unpin parts.",
                    LongDescription =
                        "Reads Spool Number from the selected fabrication parts, finds " +
                        "every part in the doc with a matching Spool Number, plus the " +
                        "sheet(s), view(s), and assembly the spool ships as. Shows a " +
                        "confirmation with counts before touching the model. Whole " +
                        "operation is one Ctrl+Z step.",
                    LargeImage = RibbonIconFactory.DeSpool(32),
                    Image      = RibbonIconFactory.DeSpool(16),
                };

                var spoolConfigButton = new PushButtonData(
                    name:         "SpoolConfig",
                    text:         "Spool\nConfig",
                    assemblyName: asm,
                    className:    "SpoolTools.SpoolConfigCommand")
                {
                    ToolTip = "Edit the project-level spool defaults.",
                    LongDescription =
                        "Modeless dialog for the SpoolSettings store: titleblock + " +
                        "drawable region, schedule, default view scale + directions, " +
                        "view template, tag family, leader defaults, spool limits, " +
                        "custom status param, and The Spooler's batch templates. " +
                        "Renumber preferences and auto-split rules stay in the " +
                        "per-run dialogs.",
                    LargeImage = RibbonIconFactory.SpoolGear(32),
                    Image      = RibbonIconFactory.SpoolGear(16),
                };

                panel.AddItem(createSpoolButton);
                panel.AddItem(spoolerButton);
                panel.AddItem(despoolerButton);
                panel.AddItem(spoolConfigButton);
            }
            catch (Exception ex)
            {
                // Surface startup errors visibly — silent failures here
                // are nightmarish to diagnose because the ribbon just
                // doesn't appear. TaskDialog forces a user-visible
                // notification.
                TaskDialog.Show("Spool Tools — startup error", ex.ToString());
                return Result.Failed;
            }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
    }
}
