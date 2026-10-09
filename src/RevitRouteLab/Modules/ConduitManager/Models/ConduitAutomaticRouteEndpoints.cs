using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RevitRouteLab.ConduitManager.Models
{
    public class ConduitAutomaticRouteEndpoints
    {
        public ConduitAutomaticRouteEndpoints(
            CableTray startTray,
            XYZ startPoint,
            double startDistanceMm,
            CableTray endTray,
            XYZ endPoint,
            double endDistanceMm)
            : this(
                ConduitRouteEndpoint.OnTray(startTray, startPoint, startDistanceMm),
                ConduitRouteEndpoint.OnTray(endTray, endPoint, endDistanceMm))
        {
        }

        public ConduitAutomaticRouteEndpoints(ConduitRouteEndpoint start, ConduitRouteEndpoint end)
        {
            Start = start;
            End = end;
        }

        public ConduitRouteEndpoint Start { get; }

        public ConduitRouteEndpoint End { get; }

        public CableTray StartTray => Start.Tray;

        public XYZ StartPoint => Start.TrayPoint;

        public double StartDistanceMm => Start.DistanceMm;

        public CableTray EndTray => End.Tray;

        public XYZ EndPoint => End.TrayPoint;

        public double EndDistanceMm => End.DistanceMm;

        /// <summary>True, gdy przynajmniej jeden koniec wymaga zejścia poza korytkiem.</summary>
        public bool HasFreeAirLeg =>
            Start.Kind == ConduitRouteEndpointKind.DeviceDrop ||
            End.Kind == ConduitRouteEndpointKind.DeviceDrop;
    }
}
