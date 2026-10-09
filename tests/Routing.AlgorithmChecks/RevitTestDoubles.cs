// Minimal data holders used only by algorithm tests. These do not simulate the
// Revit geometry, connector, document or transaction API, and are not deployed.
namespace Autodesk.Revit.DB
{
    public class XYZ { }
    public enum BuiltInCategory { OST_CableTrayFitting = -2008128 }
    public class ElementId
    {
        public ElementId(int value) => IntegerValue = value;
        public int IntegerValue { get; }
        public static ElementId InvalidElementId { get; } = new(-1);
    }
    public class Category { public ElementId Id { get; set; } = ElementId.InvalidElementId; }
    public class Element
    {
        public ElementId Id { get; set; } = ElementId.InvalidElementId;
        public Category? Category { get; set; }
    }
}
namespace Autodesk.Revit.DB.Electrical
{
    public class CableTray : Autodesk.Revit.DB.Element { }
}
