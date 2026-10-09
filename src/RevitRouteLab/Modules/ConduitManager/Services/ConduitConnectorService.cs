using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RevitRouteLab.ConduitManager.Models;
using RevitRouteLab.ConduitRouting.Models;

namespace RevitRouteLab.ConduitManager.Services
{
    public class ConduitConnectorService
    {
        private readonly Document _doc;

        public ConduitConnectorService(Document doc)
        {
            _doc = doc;
        }

        public void Connect(
            IReadOnlyList<ConduitCreatedSegment> createdSegments,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report)
        {
            var tolerance = RevitUnitConverter.ToFeet(settings.ConnectionToleranceMm);

            // Creation preserves route order. Only neighbouring planned segments
            // may be connected: a proximity scan can steal a connector from the
            // real neighbour when a route doubles back or several bends are close.
            var routes = createdSegments
                .Select((segment, index) => new { Segment = segment, Index = index })
                .GroupBy(item => item.Segment.PlannedSegment.LayoutIndex)
                .Select(group => group.OrderBy(item => item.Index)
                    .Select(item => item.Segment)
                    .ToList());

            foreach (var route in routes)
            {
                for (var i = 0; i + 1 < route.Count; i++)
                {
                    var before = route[i];
                    var after = route[i + 1];
                    var connectorA = GetNearestOpenConnector(
                        before.Conduit, before.PlannedSegment.End, tolerance);
                    var connectorB = GetNearestOpenConnector(
                        after.Conduit, after.PlannedSegment.Start, tolerance);
                    if (connectorA == null || connectorB == null ||
                        connectorA.Origin.DistanceTo(connectorB.Origin) > tolerance)
                    {
                        report.FailedConnections++;
                        continue;
                    }

                    if (TryConnect(connectorA, connectorB, out var fittingId))
                    {
                        report.CreatedConnections++;
                        if (fittingId != ElementId.InvalidElementId)
                        {
                            report.CreatedElementIds.Add(fittingId);
                        }
                    }
                    else
                    {
                        report.FailedConnections++;
                    }
                }
            }
        }

        private bool TryConnect(Connector connectorA, Connector connectorB, out ElementId fittingId)
        {
            fittingId = ElementId.InvalidElementId;

            try
            {
                if (connectorA.IsConnectedTo(connectorB))
                {
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                var directionA = GetConnectorDirection(connectorA);
                var directionB = GetConnectorDirection(connectorB);
                var dot = Math.Abs(directionA.DotProduct(directionB));

                FamilyInstance fitting = dot > 0.98
                    ? _doc.Create.NewUnionFitting(connectorA, connectorB)
                    : _doc.Create.NewElbowFitting(connectorA, connectorB);

                if (fitting != null)
                {
                    fittingId = fitting.Id;
                    return true;
                }
            }
            catch
            {
                try
                {
                    var fitting = _doc.Create.NewElbowFitting(connectorA, connectorB);
                    if (fitting != null)
                    {
                        fittingId = fitting.Id;
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static Connector GetNearestOpenConnector(Conduit conduit, XYZ point, double tolerance)
        {
            if (point == null)
            {
                return null;
            }

            Connector nearest = null;
            var minDistance = double.MaxValue;

            foreach (Connector connector in conduit.ConnectorManager.Connectors)
            {
                if (connector.IsConnected)
                {
                    continue;
                }

                var distance = connector.Origin.DistanceTo(point);
                if (distance < minDistance && distance <= tolerance)
                {
                    minDistance = distance;
                    nearest = connector;
                }
            }

            return nearest;
        }

        private static XYZ GetConnectorDirection(Connector connector)
        {
            try
            {
                return connector.CoordinateSystem.BasisZ.Normalize();
            }
            catch
            {
                return XYZ.BasisX;
            }
        }

    }
}

