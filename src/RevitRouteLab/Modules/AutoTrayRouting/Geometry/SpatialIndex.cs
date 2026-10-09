using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitRouteLab.AutoTrayRouting.Geometry
{
    /// <summary>
    /// Simple spatial index for tray segments using their bounding boxes and levels for quick candidate filtering.
    /// </summary>
    public class SpatialIndex
    {
        public class Item
        {
            public Element Element { get; set; }
            public BoundingBoxXYZ Box { get; set; }
            public ElementId LevelId { get; set; }
        }

        private readonly List<Item> _items = new List<Item>();

        public SpatialIndex(IEnumerable<Element> trays)
        {
            foreach (var e in trays)
            {
                var bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                var lvlId = e.LevelId;
                _items.Add(new Item { Element = e, Box = bb, LevelId = lvlId });
            }
        }

        public IEnumerable<Item> QueryNear(XYZ p, double radius, ElementId preferredLevel)
        {
            var outline = new Outline(p - new XYZ(radius, radius, radius), p + new XYZ(radius, radius, radius));
            foreach (var it in _items)
            {
                if (it.Box != null && Intersects(it.Box, outline))
                    yield return it;
            }
        }

        private static bool Intersects(BoundingBoxXYZ bb, Outline outline)
        {
            // Basic AABB intersection test
            var min = bb.Min; var max = bb.Max;
            return !(max.X < outline.MinimumPoint.X || min.X > outline.MaximumPoint.X ||
                     max.Y < outline.MinimumPoint.Y || min.Y > outline.MaximumPoint.Y ||
                     max.Z < outline.MinimumPoint.Z || min.Z > outline.MaximumPoint.Z);
        }

        public static double ManhattanDistance(XYZ a, XYZ b)
        {
            var d = (a - b);
            return Math.Abs(d.X) + Math.Abs(d.Y) + Math.Abs(d.Z);
        }

        public static XYZ ClosestPointOnLine(XYZ p, LocationCurve lc)
        {
            var proj = lc.Curve.Project(p);
            return proj != null ? proj.XYZPoint : lc.Curve.Evaluate(0.5, true);
        }
    }
}
