using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using RevitRouteLab.ConduitManager.Services;

namespace RevitRouteLab.ConduitRouting.Services
{
    public class TrayElementSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            return TrayElementClassifier.IsTrayElement(elem);
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return true;
        }
    }
}
