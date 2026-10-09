using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace INP_IE.ConduitManager.Services
{
    public static class TrayElementClassifier
    {
        public static bool IsTrayElement(Element element)
        {
            return IsCableTray(element) || IsCableTrayFitting(element);
        }

        public static bool IsCableTray(Element element)
        {
            return element is CableTray;
        }

        public static bool IsCableTrayFitting(Element element)
        {
            return element?.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_CableTrayFitting;
        }
    }
}
