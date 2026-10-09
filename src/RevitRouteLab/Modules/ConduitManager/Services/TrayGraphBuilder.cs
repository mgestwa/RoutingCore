using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using INP_IE.ConduitManager.Models;
using INP_IE.ConduitRouting.Models;

namespace INP_IE.ConduitManager.Services
{
    public class TrayGraphBuilder
    {
        public const string StartNodeKey = "start";
        public const string EndNodeKey = "end";

        private const double MinimumSegmentLength = 1.0 / 12.0;

        public TrayGraph Build(IList<Element> networkElements, CableTray startTray, XYZ startPoint, CableTray endTray, XYZ endPoint, ConduitRoutingReport report)
        {
            return Build(networkElements, startTray, startPoint, endTray, endPoint, null, null, report);
        }

        public TrayGraph Build(
            IList<Element> networkElements,
            CableTray startTray,
            XYZ startPoint,
            CableTray endTray,
            XYZ endPoint,
            IReadOnlyList<TrayGapBridge> bridges,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report)
        {
            var connectorsByElement = networkElements.ToDictionary(
                element => element.Id.IntegerValue,
                element => MepConnectorReader.GetConnectors(element)
                    .Select(connector => new ConnectorRouteNode(element, connector))
                    .ToList());

            var graph = new TrayGraph();

            foreach (var element in networkElements)
            {
                if (!connectorsByElement.TryGetValue(element.Id.IntegerValue, out var connectors) || connectors.Count < 2)
                {
                    continue;
                }

                if (element is CableTray)
                {
                    AddBidirectionalEdge(graph, connectors[0].Key, connectors[1].Key, element, connectors[0].Point, connectors[1].Point, true);
                }
                else if (TrayElementClassifier.IsCableTrayFitting(element))
                {
                    AddFittingGraphEdges(graph, element, connectors);
                }
            }

            foreach (var element in networkElements)
            {
                if (!connectorsByElement.TryGetValue(element.Id.IntegerValue, out var connectors))
                {
                    continue;
                }

                foreach (var connector in connectors)
                {
                    foreach (Connector reference in connector.Connector.AllRefs)
                    {
                        if (reference.Owner == null || reference.Owner.Id.IntegerValue == element.Id.IntegerValue || !TrayElementClassifier.IsTrayElement(reference.Owner))
                        {
                            continue;
                        }

                        var target = FindConnectorRouteNode(connectorsByElement, reference);
                        if (target == null)
                        {
                            continue;
                        }

                        AddBidirectionalEdge(graph, connector.Key, target.Key, null, connector.Point, target.Point, false);
                    }
                }
            }

            AddBridgeEdges(graph, bridges, settings);

            AddPointEdges(graph, connectorsByElement, StartNodeKey, startTray, startPoint);

            // Koniec trasy jest opcjonalny: przy ocenie kandydatów graf buduje
            // się raz, z samym punktem startowym, a koszty dojścia do wszystkich
            // korytek odczytuje się jednym przebiegiem pathfindera.
            if (endTray != null && endPoint != null)
            {
                AddPointEdges(graph, connectorsByElement, EndNodeKey, endTray, endPoint);

                if (startTray.Id.IntegerValue == endTray.Id.IntegerValue)
                {
                    AddBidirectionalEdge(graph, StartNodeKey, EndNodeKey, startTray, startPoint, endPoint, true);
                }

                if (!graph.ContainsNode(EndNodeKey) && report != null)
                {
                    report.Warnings.Add($"Nie dodano punktu końcowego do grafu. Korytko {endTray.Id.IntegerValue} nie ma odczytanych konektorów.");
                }
            }

            if (!graph.ContainsNode(StartNodeKey) && report != null)
            {
                report.Warnings.Add($"Nie dodano punktu startowego do grafu. Korytko {startTray.Id.IntegerValue} nie ma odczytanych konektorów.");
            }

            return graph;
        }

        /// <summary>
        /// Klucze węzłów, którymi element wchodzi do grafu. Pozwalają odczytać
        /// koszt dojścia do konkretnego korytka z tablicy odległości.
        /// </summary>
        public static IEnumerable<string> GetNodeKeys(Element element)
        {
            if (element == null)
            {
                yield break;
            }

            foreach (var connector in MepConnectorReader.GetConnectors(element))
            {
                yield return BuildNodeKey(element, connector.Origin);
            }
        }

        /// <summary>Returns the graph key for a connector point.</summary>
        public static string GetNodeKey(Element element, XYZ point)
        {
            return BuildNodeKey(element, point);
        }


        /// <summary>
        /// Wpina wykryte przerwy jako osobny rodzaj krawędzi. Koszt jest
        /// świadomie zawyżony, żeby rozsądne realne połączenie wygrywało z
        /// przejściem przez przerwę, ale bardzo długi objazd mógł przegrać
        /// z krótkim mostkiem.
        /// </summary>
        private static void AddBridgeEdges(TrayGraph graph, IReadOnlyList<TrayGapBridge> bridges, ConduitRoutingSettings settings)
        {
            if (bridges == null || bridges.Count == 0)
            {
                return;
            }

            var factor = settings == null ? 1.0 : Math.Max(1.0, settings.GapCostPenaltyFactor);
            var fixedPenaltyFeet = settings == null ? 0 : Math.Max(0, settings.GapFixedPenaltyMm) / 304.8;

            foreach (var bridge in bridges)
            {
                var fromKey = BuildNodeKey(bridge.FromElement, bridge.FromPoint);
                var toKey = BuildNodeKey(bridge.ToElement, bridge.ToPoint);
                var lengthFeet = bridge.LengthMm / 304.8;
                var cost = lengthFeet * factor + fixedPenaltyFeet;
                var forwardPath = bridge.Path;
                var reversePath = bridge.Path.Reverse().ToList();

                graph.AddEdge(new TrayGraphEdge(
                    fromKey, toKey, null, bridge.FromPoint, bridge.ToPoint, true, cost,
                    TrayGraphEdgeKind.Bridge, forwardPath, bridge.WidthReferenceElementId));
                graph.AddEdge(new TrayGraphEdge(
                    toKey, fromKey, null, bridge.ToPoint, bridge.FromPoint, true, cost,
                    TrayGraphEdgeKind.Bridge, reversePath, bridge.WidthReferenceElementId));
            }
        }

        private static void AddFittingGraphEdges(TrayGraph graph, Element fitting, IList<ConnectorRouteNode> connectors)
        {
            for (var i = 0; i < connectors.Count; i++)
            {
                for (var j = i + 1; j < connectors.Count; j++)
                {
                    AddBidirectionalEdge(graph, connectors[i].Key, connectors[j].Key, fitting, connectors[i].Point, connectors[j].Point, true);
                }
            }
        }

        private static void AddPointEdges(TrayGraph graph, Dictionary<int, List<ConnectorRouteNode>> connectorsByElement, string pointKey, CableTray tray, XYZ point)
        {
            if (!connectorsByElement.TryGetValue(tray.Id.IntegerValue, out var connectors))
            {
                return;
            }

            foreach (var connector in connectors)
            {
                AddBidirectionalEdge(graph, pointKey, connector.Key, tray, point, connector.Point, true);
            }
        }

        private static ConnectorRouteNode FindConnectorRouteNode(Dictionary<int, List<ConnectorRouteNode>> connectorsByElement, Connector connector)
        {
            if (!connectorsByElement.TryGetValue(connector.Owner.Id.IntegerValue, out var candidates))
            {
                return null;
            }

            return candidates
                .OrderBy(candidate => candidate.Point.DistanceTo(connector.Origin))
                .FirstOrDefault(candidate => candidate.Point.DistanceTo(connector.Origin) < 0.01);
        }

        private static void AddBidirectionalEdge(TrayGraph graph, string key0, string key1, Element element, XYZ point0, XYZ point1, bool createsSegment)
        {
            graph.AddEdge(CreateEdge(key0, key1, element, point0, point1, createsSegment));
            graph.AddEdge(CreateEdge(key1, key0, element, point1, point0, createsSegment));
        }

        private static TrayGraphEdge CreateEdge(string from, string to, Element element, XYZ start, XYZ end, bool createsSegment)
        {
            var cost = Math.Max(start.DistanceTo(end), createsSegment ? MinimumSegmentLength : 0);
            return new TrayGraphEdge(from, to, element, start, end, createsSegment, cost);
        }


        /// <summary>
        /// Klucz węzła grafu. Mostki muszą trafić dokładnie w te same węzły co
        /// konektory, z których wychodzą, więc format jest współdzielony.
        /// </summary>
        private static string BuildNodeKey(Element element, XYZ point)
        {
            return $"{element.Id.IntegerValue}:{Math.Round(point.X, 6)}:{Math.Round(point.Y, 6)}:{Math.Round(point.Z, 6)}";
        }

        private class ConnectorRouteNode
        {
            public ConnectorRouteNode(Element element, Connector connector)
            {
                Element = element;
                Connector = connector;
                Point = connector.Origin;
                Key = BuildNodeKey(element, Point);
            }

            public Element Element { get; }

            public Connector Connector { get; }

            public XYZ Point { get; }

            public string Key { get; }
        }
    }
}