using System;
using Autodesk.Revit.DB;

namespace RevitRouteLab.ConduitManager.Services
{
    /// <summary>
    /// Wspólna matematyka narożników trasy. Używana zarówno przy prowadzeniu
    /// conduitu przez kształtkę korytka, jak i przy mostkowaniu przerwy między
    /// korytkami, gdzie kształtki brakuje.
    /// </summary>
    public static class ConduitBendPathBuilder
    {
        /// <summary>
        /// Wyznacza punkt narożnika dwóch osi. Zwraca false, gdy osie są
        /// równoległe albo mijają się dalej niż <paramref name="maxSkewFeet"/>,
        /// czyli nie da się ich połączyć jednym załamaniem.
        /// </summary>
        public static bool TryGetCorner(
            XYZ point0,
            XYZ direction0,
            XYZ point1,
            XYZ direction1,
            double maxSkewFeet,
            out XYZ corner)
        {
            corner = null;
            var a = direction0.DotProduct(direction0);
            var b = direction0.DotProduct(direction1);
            var c = direction1.DotProduct(direction1);
            var w0 = point0 - point1;
            var d = direction0.DotProduct(w0);
            var e = direction1.DotProduct(w0);
            var denominator = a * c - b * b;

            if (Math.Abs(denominator) < 1e-9)
            {
                return false;
            }

            var sc = (b * e - c * d) / denominator;
            var tc = (a * e - b * d) / denominator;
            var closest0 = point0 + direction0.Multiply(sc);
            var closest1 = point1 + direction1.Multiply(tc);

            if (closest0.DistanceTo(closest1) > maxSkewFeet)
            {
                return false;
            }

            corner = (closest0 + closest1).Multiply(0.5);
            return true;
        }
    }
}
