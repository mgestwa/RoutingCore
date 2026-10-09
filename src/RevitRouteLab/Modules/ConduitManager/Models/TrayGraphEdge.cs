using System.Collections.Generic;
using Autodesk.Revit.DB;
using INP_IE.ConduitManager.Services;

namespace INP_IE.ConduitManager.Models
{
    /// <summary>
    /// Rodzaj krawędzi grafu korytek. <see cref="TrayRun"/> i <see cref="FittingRun"/>
    /// prowadzą po elemencie modelu, <see cref="Connection"/> to zerowej długości
    /// przeskok między stykającymi się konektorami, a <see cref="Bridge"/> to
    /// mostek nad przerwą w trasie kablowej — nie ma elementu w modelu i niesie
    /// własną geometrię.
    /// </summary>
    public enum TrayGraphEdgeKind
    {
        TrayRun,
        FittingRun,
        Connection,
        Bridge
    }

    public class TrayGraphEdge
    {
        public TrayGraphEdge(string from, string to, Element element, XYZ start, XYZ end, bool createsSegment, double cost)
            : this(from, to, element, start, end, createsSegment, cost, ResolveKind(element, createsSegment), null, null)
        {
        }

        public TrayGraphEdge(
            string from,
            string to,
            Element element,
            XYZ start,
            XYZ end,
            bool createsSegment,
            double cost,
            TrayGraphEdgeKind kind,
            IReadOnlyList<XYZ> bridgePath,
            ElementId widthReferenceElementId)
        {
            From = from;
            To = to;
            Element = element;
            Start = start;
            End = end;
            CreatesSegment = createsSegment;
            Cost = cost;
            Kind = kind;
            BridgePath = bridgePath;
            WidthReferenceElementId = widthReferenceElementId ?? element?.Id ?? ElementId.InvalidElementId;
        }

        public string From { get; }

        public string To { get; }

        public Element Element { get; }

        public XYZ Start { get; }

        public XYZ End { get; }

        public bool CreatesSegment { get; }

        public double Cost { get; }

        public TrayGraphEdgeKind Kind { get; }

        /// <summary>
        /// Uporządkowana łamana mostka od <see cref="Start"/> do <see cref="End"/>.
        /// Null dla krawędzi prowadzonych po elemencie modelu.
        /// </summary>
        public IReadOnlyList<XYZ> BridgePath { get; }

        /// <summary>Element, z którego mostek pożycza przekrój przy przydzielaniu torów.</summary>
        public ElementId WidthReferenceElementId { get; }

        private static TrayGraphEdgeKind ResolveKind(Element element, bool createsSegment)
        {
            if (!createsSegment || element == null)
            {
                return TrayGraphEdgeKind.Connection;
            }

            return TrayElementClassifier.IsCableTray(element)
                ? TrayGraphEdgeKind.TrayRun
                : TrayGraphEdgeKind.FittingRun;
        }
    }
}
