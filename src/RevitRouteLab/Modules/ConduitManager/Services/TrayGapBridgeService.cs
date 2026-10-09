using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitRouteLab.ConduitManager.Models;
using RevitRouteLab.ConduitRouting.Models;

namespace RevitRouteLab.ConduitManager.Services
{
    /// <summary>
    /// Wyszukuje przerwy w trasach kablowych, przez które conduit może przejść
    /// mimo braku kształtki łączącej. Kandydatami są wyłącznie wolne konektory
    /// korytek i kształtek — konektor już połączony z innym elementem trasy nie
    /// tworzy przerwy.
    /// </summary>
    public class TrayGapBridgeService
    {
        private const double MmToFeet = 1.0 / 304.8;
        private const double FeetToMm = 304.8;
        private const double MinimumSegmentLength = 1.0 / 12.0;
        private const double ParallelDotThreshold = 0.98;
        private const double CollinearLateralToleranceFeet = 5.0 * MmToFeet;
        private const double ForwardToleranceFeet = 1e-6;

        private readonly Document _document;

        public TrayGapBridgeService(Document document)
        {
            _document = document;
        }

        /// <summary>
        /// Zwraca mostki wychodzące z wolnych konektorów podanych elementów.
        /// Wynik jest uporządkowany deterministycznie, żeby ta sama sieć zawsze
        /// dawała ten sam zestaw przejść.
        /// </summary>
        public IReadOnlyList<TrayGapBridge> FindBridges(
            IEnumerable<Element> sourceElements,
            ConduitRoutingSettings settings)
        {
            var bridges = new Dictionary<string, TrayGapBridge>(StringComparer.Ordinal);
            if (sourceElements == null || settings == null || !settings.AllowTrayGapBridging)
            {
                return new List<TrayGapBridge>();
            }

            var maxGapFeet = Math.Max(0, settings.MaxGapMm) * MmToFeet;
            if (maxGapFeet <= 0)
            {
                return new List<TrayGapBridge>();
            }

            foreach (var element in sourceElements)
            {
                foreach (var connector in GetOpenTrayConnectors(element))
                {
                    foreach (var candidate in FindNearbyTrayElements(connector.Origin, maxGapFeet, element.Id))
                    {
                        foreach (var candidateConnector in GetOpenTrayConnectors(candidate))
                        {
                            if (connector.Origin.DistanceTo(candidateConnector.Origin) > maxGapFeet)
                            {
                                continue;
                            }

                            if (!TryBuildBridge(element, connector, candidate, candidateConnector, settings, out var bridge))
                            {
                                continue;
                            }

                            var key = BuildBridgeKey(bridge);
                            if (!bridges.ContainsKey(key))
                            {
                                bridges[key] = bridge;
                            }
                        }
                    }
                }
            }

            return bridges
                .OrderBy(pair => pair.Value.LengthMm)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value)
                .ToList();
        }

        /// <summary>
        /// Konektory, które nie są połączone z żadnym innym elementem trasy
        /// kablowej — tylko one mogą być początkiem albo końcem przerwy.
        /// </summary>
        private static IEnumerable<Connector> GetOpenTrayConnectors(Element element)
        {
            if (element == null)
            {
                yield break;
            }

            foreach (var connector in MepConnectorReader.GetConnectors(element))
            {
                var connectedToTray = false;
                foreach (Connector reference in connector.AllRefs)
                {
                    var owner = reference.Owner;
                    if (owner == null ||
                        owner.Id.IntegerValue == element.Id.IntegerValue ||
                        !TrayElementClassifier.IsTrayElement(owner))
                    {
                        continue;
                    }

                    connectedToTray = true;
                    break;
                }

                if (!connectedToTray)
                {
                    yield return connector;
                }
            }
        }

        private IEnumerable<Element> FindNearbyTrayElements(XYZ origin, double radiusFeet, ElementId excludedId)
        {
            var outline = new Outline(
                new XYZ(origin.X - radiusFeet, origin.Y - radiusFeet, origin.Z - radiusFeet),
                new XYZ(origin.X + radiusFeet, origin.Y + radiusFeet, origin.Z + radiusFeet));

            var categories = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_CableTray,
                BuiltInCategory.OST_CableTrayFitting
            };

            return new FilteredElementCollector(_document)
                .WherePasses(new ElementMulticategoryFilter(categories))
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .Where(element => element.Id.IntegerValue != excludedId.IntegerValue)
                .ToList();
        }

        private bool TryBuildBridge(
            Element fromElement,
            Connector fromConnector,
            Element toElement,
            Connector toConnector,
            ConduitRoutingSettings settings,
            out TrayGapBridge bridge)
        {
            bridge = null;

            var pA = fromConnector.Origin;
            var pB = toConnector.Origin;
            var dA = GetConnectorDirection(fromConnector);
            var dB = GetConnectorDirection(toConnector);
            if (dA == null || dB == null)
            {
                return false;
            }

            var maxLateralFeet = Math.Max(0, settings.MaxGapLateralOffsetMm) * MmToFeet;
            var dot = dA.DotProduct(dB);

            List<XYZ> path;
            TrayGapBridgeShape shape;

            if (dot < -ParallelDotThreshold)
            {
                // Konektory patrzą na siebie: albo prosto w osi, albo z bocznym
                // rozminięciem, które trzeba objechać ścieżką Z.
                var along = (pB - pA).DotProduct(dA);
                if (along <= ForwardToleranceFeet)
                {
                    return false;
                }

                var lateral = DistanceToLine(pB, pA, dA);
                if (lateral <= CollinearLateralToleranceFeet)
                {
                    path = new List<XYZ> { pA, pB };
                    shape = TrayGapBridgeShape.Straight;
                }
                else if (lateral <= maxLateralFeet)
                {
                    var m1 = pA + dA.Multiply(along / 2.0);
                    var m2 = pB + dB.Multiply(along / 2.0);
                    path = new List<XYZ> { pA, m1, m2, pB };
                    shape = TrayGapBridgeShape.Offset;
                }
                else
                {
                    return false;
                }
            }
            else if (Math.Abs(dot) < ParallelDotThreshold)
            {
                // Korytka pod kątem: mostek przechodzi przez punkt, w którym
                // spotykają się ich osie — tam, gdzie normalnie stałaby kształtka.
                if (!ConduitBendPathBuilder.TryGetCorner(pA, dA, pB, dB, maxLateralFeet, out var corner))
                {
                    return false;
                }

                if ((corner - pA).DotProduct(dA) <= ForwardToleranceFeet ||
                    (corner - pB).DotProduct(dB) <= ForwardToleranceFeet)
                {
                    return false;
                }

                path = corner.DistanceTo(pA) < MinimumSegmentLength || corner.DistanceTo(pB) < MinimumSegmentLength
                    ? new List<XYZ> { pA, pB }
                    : new List<XYZ> { pA, corner, pB };
                shape = path.Count == 2 ? TrayGapBridgeShape.Straight : TrayGapBridgeShape.Corner;
            }
            else
            {
                // Konektory patrzą w tę samą stronę — nie są zwrócone do siebie.
                return false;
            }

            var lengthMm = GetPathLengthMm(path);
            if (lengthMm <= 0 || lengthMm > settings.MaxGapMm)
            {
                return false;
            }

            bridge = new TrayGapBridge(
                fromElement,
                pA,
                toElement,
                pB,
                path,
                lengthMm,
                shape,
                PickWidthReference(fromElement, toElement));
            return true;
        }

        /// <summary>
        /// Mostek dziedziczy przekrój po węższym z korytek po obu stronach
        /// przerwy — tor musi zmieścić się również w tym węższym.
        /// </summary>
        private ElementId PickWidthReference(Element fromElement, Element toElement)
        {
            var fromWidth = TrayCrossSectionReader.GetWidthMm(_document, fromElement.Id);
            var toWidth = TrayCrossSectionReader.GetWidthMm(_document, toElement.Id);

            if (fromWidth <= 0)
            {
                return toElement.Id;
            }

            if (toWidth <= 0)
            {
                return fromElement.Id;
            }

            return fromWidth <= toWidth ? fromElement.Id : toElement.Id;
        }

        private static double GetPathLengthMm(IReadOnlyList<XYZ> path)
        {
            var length = 0.0;
            for (var i = 0; i < path.Count - 1; i++)
            {
                length += path[i].DistanceTo(path[i + 1]);
            }

            return length * FeetToMm;
        }

        private static double DistanceToLine(XYZ point, XYZ linePoint, XYZ lineDirection)
        {
            return (point - linePoint).CrossProduct(lineDirection).GetLength();
        }

        private static XYZ GetConnectorDirection(Connector connector)
        {
            try
            {
                return connector.CoordinateSystem.BasisZ.Normalize();
            }
            catch
            {
                return null;
            }
        }

        private static string BuildBridgeKey(TrayGapBridge bridge)
        {
            var a = FormatEndpoint(bridge.FromElement.Id, bridge.FromPoint);
            var b = FormatEndpoint(bridge.ToElement.Id, bridge.ToPoint);
            return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
        }

        private static string FormatEndpoint(ElementId elementId, XYZ point)
        {
            return $"{elementId.IntegerValue}:{Math.Round(point.X, 6)}:{Math.Round(point.Y, 6)}:{Math.Round(point.Z, 6)}";
        }
    }
}
