using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace SpoolTools.Revit
{
    /// <summary>Restricts pick-mode selection to FabricationPart elements only.</summary>
    internal class FabricationPartFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is FabricationPart;
        public bool AllowReference(Reference reference, XYZ position) => true;
    }
}
