using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace SpoolTools.UI
{
    /// <summary>Safety-net modal shown when Create Spool or The Spooler
    /// launches with a pre-selection that already contains parts on an
    /// existing spool. See PCFExporter's equivalent for full behaviour
    /// notes.</summary>
    public partial class SpoolMembershipWarningDialog : Window
    {
        private readonly UIDocument _uiDoc;
        private readonly List<ElementId> _affectedIds;

        public string Heading { get; }
        public string Explanation { get; }
        public IReadOnlyList<SpoolGroupDisplay> Groups { get; }

        public SpoolMembershipWarningDialog(
            UIDocument uiDoc,
            IDictionary<string, List<ElementId>> spoolMembership,
            string toolName)
        {
            InitializeComponent();
            _uiDoc = uiDoc;
            _affectedIds = spoolMembership.SelectMany(kv => kv.Value).Distinct().ToList();

            int totalParts = _affectedIds.Count;
            int spoolCount = spoolMembership.Count;
            Heading =
                $"{totalParts} of your selected part(s) are already on {spoolCount} existing spool(s).";
            Explanation =
                $"If you continue, those parts will be included in the new spool that {toolName} " +
                $"creates. Their current Spool Number will be overwritten. " +
                $"Click Show in Model to highlight the affected parts in Revit before deciding.";

            Groups = spoolMembership
                .OrderBy(kv => kv.Key, System.StringComparer.OrdinalIgnoreCase)
                .Select(kv => new SpoolGroupDisplay(kv.Key, kv.Value.Count))
                .ToList();

            DataContext = this;
        }

        private void Show_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _uiDoc.Selection.SetElementIds(_affectedIds);
                _uiDoc.ShowElements(_affectedIds);
            }
            catch { }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }

    public sealed class SpoolGroupDisplay
    {
        public string SpoolNumber { get; }
        public int Count { get; }
        public string DisplayText => $"• Spool {SpoolNumber} — {Count} part(s)";

        public SpoolGroupDisplay(string spoolNumber, int count)
        {
            SpoolNumber = spoolNumber;
            Count = count;
        }
    }
}
