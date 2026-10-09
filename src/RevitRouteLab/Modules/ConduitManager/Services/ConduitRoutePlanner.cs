using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RevitRouteLab.ConduitManager.Models;
using RevitRouteLab.ConduitRouting.Models;

namespace RevitRouteLab.ConduitManager.Services
{
    public class ConduitRoutePlanner
    {
        private const double MinimumSegmentLength = 1.0 / 12.0;
        private const double FeetToMillimeters = 304.8;

        private readonly Document _doc;

        public ConduitRoutePlanner(Document doc)
        {
            _doc = doc;
        }

        public ConduitRoutePlan CreatePlanForTrayNetwork(IList<Element> elements, ConduitRoutingSettings settings, ConduitRoutingReport report)
        {
            var segments = new List<ConduitRunSegment>();
            var trays = elements.Where(TrayElementClassifier.IsCableTray).Cast<CableTray>().ToList();
            var fittings = elements.Where(TrayElementClassifier.IsCableTrayFitting).ToList();
            var segmentBuilder = new ConduitSegmentBuilder(_doc);

            foreach (var tray in trays)
            {
                segments.AddRange(segmentBuilder.BuildTraySegments(tray, settings, report));
            }

            foreach (var fitting in fittings)
            {
                segments.AddRange(segmentBuilder.BuildFittingSegments(fitting, settings, report));
            }

            var allocator = new ConduitPositionAllocator(_doc);
            segments = allocator.Allocate(segments, settings, report);
            return CreatePlan(segments);
        }

        public ConduitRoutePlan CreatePlanBetweenTrayPoints(Element startElement, XYZ startPoint, Element endElement, XYZ endPoint, ConduitRoutingSettings settings, ConduitRoutingReport report)
        {
            var segments = BuildRawRouteBetweenTrayPoints(startElement, startPoint, endElement, endPoint, settings, report);
            if (segments.Count == 0)
            {
                return new ConduitRoutePlan();
            }

            var allocator = new ConduitPositionAllocator(_doc);
            segments = allocator.Allocate(segments, settings, report);
            if (segments.Count == 0)
            {
                report.Warnings.Add("Wyznaczona trasa nie zawiera wolnych odcinków możliwych do utworzenia jako conduit.");
                return new ConduitRoutePlan();
            }

            return CreatePlan(segments);
        }

        public ConduitRoutePlan CreatePlanBetweenTrayPointsViaTray(
            Element startElement,
            XYZ startPoint,
            Element endElement,
            XYZ endPoint,
            Element requiredTrayElement,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report)
        {
            var segments = BuildRawRouteBetweenTrayPointsViaTray(
                startElement,
                startPoint,
                endElement,
                endPoint,
                requiredTrayElement,
                settings,
                report);
            if (segments.Count == 0)
            {
                return new ConduitRoutePlan();
            }

            var allocator = new ConduitPositionAllocator(_doc);
            segments = allocator.Allocate(segments, settings, report);
            if (segments.Count == 0)
            {
                report.Warnings.Add("Wyznaczona trasa nie zawiera wolnych odcinków możliwych do utworzenia jako conduit.");
                return new ConduitRoutePlan();
            }

            return CreatePlan(segments);
        }

        /// <summary>
        /// Buduje surową ścieżkę między końcami trasy. Środek trasy zawsze biegnie
        /// po sieci korytek — tak samo jak dotychczas — a końce wymagające
        /// zejścia do urządzenia dostają dodatkowy odcinek prowadzony w
        /// powietrzu, doklejony na początku albo na końcu ścieżki.
        /// </summary>
        public List<ConduitRunSegment> BuildRawRoute(
            ConduitRouteEndpoint start,
            ConduitRouteEndpoint end,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report)
        {
            if (start == null || end == null)
            {
                report.Warnings.Add("Nie wyznaczono końców trasy.");
                return new List<ConduitRunSegment>();
            }

            var needsFreeAir =
                start.Kind == ConduitRouteEndpointKind.DeviceDrop ||
                end.Kind == ConduitRouteEndpointKind.DeviceDrop;
            if (needsFreeAir && (settings == null || !settings.AllowFreeAirLegs))
            {
                report.Warnings.Add("Trasa wymaga zejścia poza korytkiem, a ta opcja jest wyłączona.");
                return new List<ConduitRunSegment>();
            }

            var traySegments = BuildRawRouteBetweenTrayPoints(
                start.Tray, start.TrayPoint, end.Tray, end.TrayPoint, settings, report);
            if (traySegments.Count == 0 &&
                start.TrayPoint.DistanceTo(end.TrayPoint) >= MinimumSegmentLength)
            {
                return new List<ConduitRunSegment>();
            }

            var legBuilder = new FreeAirLegBuilder(_doc);
            var segments = new List<ConduitRunSegment>();
            var legs = new List<string>();

            if (start.Kind == ConduitRouteEndpointKind.DeviceDrop)
            {
                var leg = legBuilder.BuildLeg(start.TrayPoint, start.Tray, start.DevicePoint, true, settings);
                if (leg.Count == 0)
                {
                    report.Warnings.Add("Nie udało się zbudować zejścia od panelu do korytka.");
                    return new List<ConduitRunSegment>();
                }

                legs.Add($"panel {start.Device.Id.IntegerValue} → korytko {start.Tray.Id.IntegerValue}");
                segments.AddRange(leg);
            }

            segments.AddRange(traySegments);

            if (end.Kind == ConduitRouteEndpointKind.DeviceDrop)
            {
                var leg = legBuilder.BuildLeg(end.TrayPoint, end.Tray, end.DevicePoint, false, settings);
                if (leg.Count == 0)
                {
                    report.Warnings.Add("Nie udało się zbudować zejścia od korytka do odbioru.");
                    return new List<ConduitRunSegment>();
                }

                legs.Add($"korytko {end.Tray.Id.IntegerValue} → odbiór {end.Device.Id.IntegerValue}");
                segments.AddRange(leg);
            }

            if (!IsFreeAirShareAcceptable(segments, settings, report))
            {
                return new List<ConduitRunSegment>();
            }

            report.FreeAirLengthMm += MeasureLengthMm(segments, ConduitSegmentKind.FreeAir);
            report.FreeAirLegs.AddRange(legs);
            return segments;
        }

        /// <summary>
        /// Zejście jest ostatnim odcinkiem trasy, nie sposobem jej prowadzenia.
        /// Relacja z zejściem na obu końcach potrafi ominąć sieć korytek i
        /// polecieć wprost od panelu do odbioru, więc trasa musi mieć realny
        /// fragment po korytku, a łączna droga w powietrzu — mieścić się w
        /// jednym limicie na całą relację, nie na pojedyncze zejście.
        /// </summary>
        private static bool IsFreeAirShareAcceptable(
            IReadOnlyList<ConduitRunSegment> segments,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report)
        {
            var freeAirLegLengthsMm = MeasureFreeAirLegLengthsMm(segments);
            var freeAirMm = freeAirLegLengthsMm.Sum();
            if (freeAirMm <= 0)
            {
                return true;
            }

            var trayBorneMm = segments
                .Where(segment => segment.Kind != ConduitSegmentKind.FreeAir)
                .Sum(segment => segment.Start.DistanceTo(segment.End)) * FeetToMillimeters;
            if (trayBorneMm < MinimumSegmentLength * FeetToMillimeters)
            {
                report.Warnings.Add(
                    "Trasa biegłaby w całości poza korytkami — panel i odbiór podpięły się do tego samego " +
                    "punktu sieci. Conduit prowadzony wprost od panelu do odbioru nie jest tworzony.");
                return false;
            }

            var limitMm = Math.Max(0, settings.MaxFreeAirLengthMm);
            var longestLegMm = freeAirLegLengthsMm.DefaultIfEmpty(0).Max();
            if (longestLegMm > limitMm)
            {
                report.Warnings.Add(
                    $"Najdłuższe zejście do urządzenia miałoby {longestLegMm:F0} mm, a limit wynosi {limitMm:F0} mm.");
                return false;
            }

            return true;
        }

        private static List<double> MeasureFreeAirLegLengthsMm(
            IReadOnlyList<ConduitRunSegment> segments)
        {
            var result = new List<double>();
            var currentFeet = 0.0;
            foreach (var segment in segments)
            {
                if (segment.Kind == ConduitSegmentKind.FreeAir)
                {
                    currentFeet += segment.Start.DistanceTo(segment.End);
                    continue;
                }

                if (currentFeet > 0)
                {
                    result.Add(currentFeet * FeetToMillimeters);
                    currentFeet = 0;
                }
            }

            if (currentFeet > 0)
            {
                result.Add(currentFeet * FeetToMillimeters);
            }

            return result;
        }

        private static double MeasureLengthMm(
            IReadOnlyList<ConduitRunSegment> segments,
            ConduitSegmentKind kind)
        {
            return segments
                .Where(segment => segment.Kind == kind)
                .Sum(segment => segment.Start.DistanceTo(segment.End)) * FeetToMillimeters;
        }

        /// <summary>
        /// Produces the ordered logical path along tray centerlines without
        /// applying any lane offset. Lane geometry is generated later, once the
        /// shared allocation context has assigned a collision-free lane.
        /// </summary>
        public List<ConduitRunSegment> BuildRawRouteBetweenTrayPoints(
            Element startElement,
            XYZ startPoint,
            Element endElement,
            XYZ endPoint,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report)
        {
            return BuildRawRouteBetweenTrayPointsCore(
                startElement, startPoint, endElement, endPoint, null, settings, report);
        }

        public List<ConduitRunSegment> BuildRawRouteBetweenTrayPointsViaTray(
            Element startElement,
            XYZ startPoint,
            Element endElement,
            XYZ endPoint,
            Element requiredTrayElement,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report)
        {
            return BuildRawRouteBetweenTrayPointsCore(
                startElement, startPoint, endElement, endPoint, requiredTrayElement, settings, report);
        }

        private List<ConduitRunSegment> BuildRawRouteBetweenTrayPointsCore(
            Element startElement,
            XYZ startPoint,
            Element endElement,
            XYZ endPoint,
            Element? requiredTrayElement,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report)
        {
            if (!(startElement is CableTray startTray) || !(endElement is CableTray endTray))
            {
                report.Warnings.Add("Na tym etapie punkt początkowy i końcowy muszą być wskazane na prostych korytkach kablowych.");
                return new List<ConduitRunSegment>();
            }

            var requiredTray = requiredTrayElement as CableTray;
            if (requiredTrayElement != null && requiredTray == null)
            {
                report.Warnings.Add("Korytko wymuszone musi być prostym odcinkiem korytka kablowego.");
                return new List<ConduitRunSegment>();
            }

            if (requiredTray != null &&
                !((requiredTray.Location as LocationCurve)?.Curve is Line))
            {
                report.Warnings.Add("Korytko wymuszone nie jest prostym odcinkiem korytka kablowego.");
                return new List<ConduitRunSegment>();
            }

            if (!TryProjectPointToTray(startTray, startPoint, out var projectedStart, report) ||
                !TryProjectPointToTray(endTray, endPoint, out var projectedEnd, report))
            {
                return new List<ConduitRunSegment>();
            }

            var networkCollector = new TrayNetworkCollector(_doc);
            var network = requiredTray == null
                ? networkCollector.CollectNetwork(startTray, endTray, settings, report)
                : networkCollector.CollectNetwork(
                    startTray,
                    new Element[] { requiredTray, endTray },
                    settings,
                    report);
            if (!network.TargetReached)
            {
                report.Warnings.Add(settings != null && settings.AllowTrayGapBridging
                    ? "Nie znaleziono połączenia między wskazanymi korytkami — również po uwzględnieniu przerw w trasie."
                    : "Nie znaleziono połączenia między wskazanymi korytkami.");
                return new List<ConduitRunSegment>();
            }

            var graphBuilder = new TrayGraphBuilder();
            var graph = graphBuilder.Build(
                network.Elements, startTray, projectedStart, endTray, projectedEnd, network.Bridges, settings, report);
            var pathfinder = new ConduitPathfinder();
            List<TrayGraphEdge> routeEdges;
            var pathFound = requiredTray == null
                ? pathfinder.TryFindShortestPath(
                    graph,
                    TrayGraphBuilder.StartNodeKey,
                    TrayGraphBuilder.EndNodeKey,
                    out routeEdges)
                : pathfinder.TryFindShortestPathViaElement(
                    graph,
                    TrayGraphBuilder.StartNodeKey,
                    TrayGraphBuilder.EndNodeKey,
                    requiredTray.Id.IntegerValue,
                    out routeEdges);
            if (!pathFound)
            {
                report.Warnings.Add("Nie udało się wyznaczyć trasy conduitu po sieci korytek.");
                return new List<ConduitRunSegment>();
            }

            if (requiredTray != null && !TryAcceptSimpleConstrainedRoute(routeEdges, report))
            {
                return new List<ConduitRunSegment>();
            }

            if (!TryAcceptBridgeCount(routeEdges, settings, report))
            {
                return new List<ConduitRunSegment>();
            }

            var routeElementIds = routeEdges
                .Where(edge => edge.CreatesSegment && edge.Element != null)
                .Select(edge => edge.Element.Id.IntegerValue)
                .Distinct()
                .ToList();

            report.SelectedCableTrays = routeElementIds
                .Select(id => _doc.GetElement(new ElementId(id)))
                .Count(TrayElementClassifier.IsCableTray);
            report.SelectedFittings = routeElementIds
                .Select(id => _doc.GetElement(new ElementId(id)))
                .Count(TrayElementClassifier.IsCableTrayFitting);
            TrayServiceTypeAnalyzer.AnalyzeRoute(routeEdges, report);

            var segmentBuilder = new ConduitSegmentBuilder(_doc);
            var segments = new List<ConduitRunSegment>();
            foreach (var edge in routeEdges.Where(edge => edge.CreatesSegment))
            {
                if (edge.Kind == TrayGraphEdgeKind.Bridge)
                {
                    segments.AddRange(segmentBuilder.BuildBridgeSegments(
                        edge.BridgePath, edge.WidthReferenceElementId, settings, report));
                }
                else if (edge.Element is CableTray tray)
                {
                    segments.AddRange(segmentBuilder.BuildTraySegments(tray, edge.Start, edge.End, settings, report));
                }
                else if (edge.Element != null && TrayElementClassifier.IsCableTrayFitting(edge.Element))
                {
                    segments.AddRange(segmentBuilder.BuildFittingSegments(edge.Element, edge.Start, edge.End, settings, report));
                }
            }

            ReportUsedBridges(routeEdges, network.Bridges, report);
            return segments;
        }

        /// <summary>
        /// Odrzuca trasę, która musiałaby przeskoczyć więcej przerw, niż na to
        /// pozwalają ustawienia. Limit dotyczy wybranej trasy, a nie liczby
        /// przerw wykrytych w modelu.
        /// </summary>
        private static bool TryAcceptSimpleConstrainedRoute(
            IReadOnlyList<TrayGraphEdge> routeEdges,
            ConduitRoutingReport report)
        {
            if (routeEdges == null || routeEdges.Count == 0)
            {
                return false;
            }

            var visitedNodes = new HashSet<string>(StringComparer.Ordinal)
            {
                routeEdges[0].From
            };
            foreach (var edge in routeEdges)
            {
                if (visitedNodes.Add(edge.To))
                {
                    continue;
                }

                report.Warnings.Add(
                    "Wymuszone korytko wymagałoby zawrócenia i ponownego przejścia po tej samej części sieci. " +
                    "Nie utworzono nakładających się conduitów.");
                return false;
            }

            return true;
        }

        private static bool TryAcceptBridgeCount(
            IEnumerable<TrayGraphEdge> routeEdges,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report)
        {
            var bridgeCount = routeEdges.Count(edge => edge.Kind == TrayGraphEdgeKind.Bridge);
            var limit = settings == null ? 0 : Math.Max(0, settings.MaxBridgesPerRoute);
            if (bridgeCount <= limit)
            {
                return true;
            }

            report.Warnings.Add(
                $"Trasa wymagałaby przejścia przez {bridgeCount} przerw w trasie kablowej, a limit wynosi {limit}.");
            return false;
        }

        private static void ReportUsedBridges(
            IEnumerable<TrayGraphEdge> routeEdges,
            IReadOnlyList<TrayGapBridge> bridges,
            ConduitRoutingReport report)
        {
            if (bridges == null || bridges.Count == 0)
            {
                return;
            }

            foreach (var edge in routeEdges.Where(edge => edge.Kind == TrayGraphEdgeKind.Bridge))
            {
                var bridge = bridges.FirstOrDefault(candidate =>
                    (candidate.FromPoint.DistanceTo(edge.Start) < 1e-6 && candidate.ToPoint.DistanceTo(edge.End) < 1e-6) ||
                    (candidate.FromPoint.DistanceTo(edge.End) < 1e-6 && candidate.ToPoint.DistanceTo(edge.Start) < 1e-6));
                if (bridge == null)
                {
                    continue;
                }

                var description = bridge.Describe();
                if (!report.BridgedGaps.Contains(description))
                {
                    report.BridgedGaps.Add(description);
                }
            }
        }

        public static ConduitRoutePlan CreatePlan(IEnumerable<ConduitRunSegment> segments)
        {
            var plan = new ConduitRoutePlan();
            foreach (var segment in segments)
            {
                plan.AddSegment(new ConduitPlannedSegment(
                    segment.Start,
                    segment.End,
                    segment.SourceElementId,
                    segment.LayoutIndex,
                    segment.LevelId));
            }

            return plan;
        }

        private static bool TryProjectPointToTray(CableTray tray, XYZ point, out XYZ projectedPoint, ConduitRoutingReport report)
        {
            projectedPoint = null;
            var locationCurve = tray.Location as LocationCurve;
            var line = locationCurve?.Curve as Line;
            if (line == null)
            {
                report.Warnings.Add($"Korytko {tray.Id.IntegerValue}: punkt wskazano na elemencie, ktĂłry nie jest prostym odcinkiem.");
                return false;
            }

            var start = line.GetEndPoint(0);
            var end = line.GetEndPoint(1);
            var vector = end - start;
            var length = vector.GetLength();
            if (length < MinimumSegmentLength)
            {
                report.Warnings.Add($"Korytko {tray.Id.IntegerValue}: zbyt krĂłtki odcinek do wyznaczenia punktu.");
                return false;
            }

            var direction = vector.Normalize();
            var distance = (point - start).DotProduct(direction);
            distance = Math.Max(0, Math.Min(length, distance));
            projectedPoint = start + direction.Multiply(distance);
            return true;
        }
    }
}
