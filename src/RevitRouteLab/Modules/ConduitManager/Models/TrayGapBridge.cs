using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace INP_IE.ConduitManager.Models
{
    /// <summary>
    /// Sposób, w jaki mostek pokonuje przerwę między dwoma korytkami.
    /// </summary>
    public enum TrayGapBridgeShape
    {
        /// <summary>Korytka leżą w jednej osi — mostek to jeden prosty odcinek.</summary>
        Straight,

        /// <summary>Osie korytek przecinają się pod kątem — mostek ma jedno załamanie.</summary>
        Corner,

        /// <summary>Korytka są równoległe, ale rozminięte w bok — mostek ma dwa załamania.</summary>
        Offset
    }

    /// <summary>
    /// Przejście conduitu przez przerwę w trasie kablowej, w miejscu gdzie w
    /// modelu brakuje kształtki łączącej dwa korytka. Geometria jest wyznaczana
    /// raz, przy wykrywaniu przerwy, i niesiona dalej przez graf aż do budowy
    /// odcinków.
    /// </summary>
    public sealed class TrayGapBridge
    {
        public TrayGapBridge(
            Element fromElement,
            XYZ fromPoint,
            Element toElement,
            XYZ toPoint,
            IReadOnlyList<XYZ> path,
            double lengthMm,
            TrayGapBridgeShape shape,
            ElementId widthReferenceElementId)
        {
            FromElement = fromElement;
            FromPoint = fromPoint;
            ToElement = toElement;
            ToPoint = toPoint;
            Path = path;
            LengthMm = lengthMm;
            Shape = shape;
            WidthReferenceElementId = widthReferenceElementId;
        }

        public Element FromElement { get; }

        public XYZ FromPoint { get; }

        public Element ToElement { get; }

        public XYZ ToPoint { get; }

        /// <summary>Łamana od <see cref="FromPoint"/> do <see cref="ToPoint"/> włącznie.</summary>
        public IReadOnlyList<XYZ> Path { get; }

        public double LengthMm { get; }

        public TrayGapBridgeShape Shape { get; }

        public ElementId WidthReferenceElementId { get; }

        public string Describe()
        {
            var shape = Shape == TrayGapBridgeShape.Straight
                ? "w osi"
                : Shape == TrayGapBridgeShape.Corner
                    ? "narożnik"
                    : "z przesunięciem";
            return $"korytko {FromElement.Id.IntegerValue} ↔ {ToElement.Id.IntegerValue}, przerwa {LengthMm:F0} mm ({shape})";
        }
    }
}
