using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI.Selection;

namespace INP_IE.ConduitRouting.Services
{
    /// <summary>
    /// Pozwala wskazać wyłącznie proste odcinki korytek, które mogą być
    /// reprezentowane jako pojedyncza krawędź grafu trasowania.
    /// </summary>
    public class StraightCableTraySelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            return elem is CableTray &&
                   (elem.Location as LocationCurve)?.Curve is Line;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return true;
        }
    }
}
