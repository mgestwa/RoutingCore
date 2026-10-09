using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RevitRouteLab.ConduitManager.Models;
using RevitRouteLab.ConduitRouting.Models;

namespace RevitRouteLab.ConduitManager.Services
{
    public class ConduitAutomaticEndpointLocatorService
    {
        private const double FeetToMillimeters = 304.8;
        private const double MinimumTrayLengthFeet = 1.0 / 12.0;
        private const int MaxCandidatesPerEndpoint = 8;

        public const double SearchRadiusMm = 2500.0;

        private readonly Document _document;

        public ConduitAutomaticEndpointLocatorService(Document document)
        {
            _document = document;
        }

        public bool TryLocate(
            Element source,
            Element target,
            out ConduitAutomaticRouteEndpoints endpoints,
            out string error)
        {
            return TryLocate(source, target, null, out endpoints, out error);
        }

        /// <summary>
        /// Znajduje korytka przy panelu i odbiorze, które da się połączyć jedną
        /// trasą. Gdy ustawienia dopuszczają mostkowanie, test spójności
        /// uwzględnia również przerwy w trasie kablowej — inaczej relacja
        /// zostałaby odrzucona zanim planner w ogóle wystartuje.
        /// </summary>
        public bool TryLocate(
            Element source,
            Element target,
            ConduitRoutingSettings settings,
            out ConduitAutomaticRouteEndpoints endpoints,
            out string error)
        {
            endpoints = null!;
            error = string.Empty;

            if (source == null || target == null)
            {
                error = "Nie znaleziono panelu lub odbioru relacji.";
                return false;
            }

            var trays = new FilteredElementCollector(_document)
                .OfCategory(BuiltInCategory.OST_CableTray)
                .WhereElementIsNotElementType()
                .OfType<CableTray>()
                .ToList();
            if (trays.Count == 0)
            {
                error = "Model nie zawiera prostych odcinków korytek kablowych.";
                return false;
            }

            var allowFreeAir = settings != null && settings.AllowFreeAirLegs;
            var allowBridging = settings != null && settings.AllowTrayGapBridging;

            // Poziom 1 i 2: korytka w normalnym zasięgu. Po włączeniu
            // mostkowania fizyczne połączenia i dopuszczalne przerwy uczestniczą
            // w jednym rankingu kosztowym. Krótki mostek może więc wygrać z
            // wielokrotnie dłuższym objazdem, ale rozsądna trasa po korytkach
            // nadal jest preferowana przez karę kosztową mostka.
            var sourceCandidates = FindCandidates(source, trays, SearchRadiusMm);
            var targetCandidates = FindCandidates(target, trays, SearchRadiusMm);
            if (sourceCandidates.Count > 0 && targetCandidates.Count > 0 &&
                TryMatchCandidates(
                    sourceCandidates,
                    targetCandidates,
                    settings,
                    allowBridging,
                    out var start,
                    out var end,
                    out var routeLengthMm))
            {
                // Trasa obsłużona w całości przez korytka nie podlega limitowi
                // objazdu — ta reguła dotyczy wyłącznie trasy ratowanej dalekim
                // korytkiem. Zejście do urządzenia dostaje natomiast każdy
                // koniec, bo o to właśnie chodzi w tej opcji: conduit ma dobiec
                // do odbioru, a nie skończyć się na korytku obok niego.
                endpoints = BuildEndpoints(start, end, source, target, false, allowFreeAir);
                return true;
            }

            if (!allowFreeAir)
            {
                error = BuildUnreachableError(sourceCandidates, targetCandidates, settings);
                return false;
            }

            // Poziom 3 i 4: urządzenie nie ma korytka w zasięgu albo jego
            // korytka nie prowadzą do drugiego końca. Szukamy dalej, a brakujący
            // fragment zostanie poprowadzony jako zejście poza korytkiem.
            var attachRadiusMm = Math.Max(SearchRadiusMm, settings.FreeAirAttachRadiusMm);
            if (sourceCandidates.Count == 0)
            {
                error = $"Nie znaleziono korytka w promieniu {SearchRadiusMm:F0} mm od panelu.";
                return false;
            }

            var targetExtended = FindCandidates(
                target,
                trays,
                attachRadiusMm,
                settings,
                true);
            if (targetExtended.Count == 0)
            {
                error = $"Nie znaleziono korytka w promieniu {attachRadiusMm:F0} mm od odbioru.";
                return false;
            }

            var matched = TryMatchCandidates(
                sourceCandidates,
                targetExtended,
                settings,
                allowBridging,
                out start,
                out end,
                out routeLengthMm);

            if (!matched)
            {
                error = "Znaleziono korytka w rozszerzonym promieniu, ale nie prowadzą one do jednej sieci.";
                return false;
            }

            if (!IsDetourAcceptable(source, target, routeLengthMm, settings, out error))
            {
                return false;
            }

            // Zejście dostaje tylko ten koniec, który naprawdę wypadł poza
            // normalny zasięg albo musiał podpiąć się do innego korytka niż
            // znalezione przy urządzeniu. Drugi koniec kończy się na korytku
            // dokładnie tak jak dotychczas.
            endpoints = BuildEndpoints(start, end, source, target, false, true);
            return true;
        }

        /// <summary>
        /// True, gdy wybrane korytko było widoczne już w normalnym promieniu
        /// wyszukiwania — czyli koniec trasy nie wymaga zejścia.
        /// </summary>
        private static bool IsAmongCandidates(List<TrayCandidate> candidates, TrayCandidate? chosen)
        {
            return chosen != null &&
                   candidates.Any(candidate =>
                       candidate.Tray.Id.IntegerValue == chosen.Tray.Id.IntegerValue);
        }

        /// <summary>
        /// Odrzuca trasy rażąco dłuższe od odległości między urządzeniami.
        /// Bez tego limitu odbiór bez korytka w pobliżu potrafi zostać podpięty
        /// przez sieć prowadzącą setki metrów naokoło, co jest technicznie
        /// poprawne, ale nie jest tym, czego oczekuje projektant.
        /// </summary>
        private static bool IsDetourAcceptable(
            Element source,
            Element target,
            double routeLengthMm,
            ConduitRoutingSettings settings,
            out string error)
        {
            error = string.Empty;
            var factor = settings?.MaxRouteDetourFactor ?? 0;
            if (factor <= 0 || routeLengthMm >= double.MaxValue)
            {
                return true;
            }

            if (!TryGetDevicePoint(source, out var sourcePoint) ||
                !TryGetDevicePoint(target, out var targetPoint))
            {
                return true;
            }

            var directMm = sourcePoint.DistanceTo(targetPoint) * FeetToMillimeters;
            if (directMm < MinimumTrayLengthFeet * FeetToMillimeters)
            {
                return true;
            }

            if (routeLengthMm <= directMm * factor)
            {
                return true;
            }

            error = string.Format(
                CultureInfo.CurrentCulture,
                "Najkrótsza trasa po korytkach ma {0:F1} m przy odległości {1:F1} m w linii prostej " +
                "({2:F1}× objazdu, limit {3:F1}×). Sprawdź, czy korytka przy odbiorze są połączone z resztą sieci.",
                routeLengthMm / 1000.0,
                directMm / 1000.0,
                routeLengthMm / directMm,
                factor);
            return false;
        }

        /// <summary>
        /// Szuka pary korytek dającej najkrótszą trasę. Ocenianie kandydatów po
        /// samej odległości do korytka jest mylące: korytko tuż przy odbiorze
        /// potrafi prowadzić do panelu setkami metrów naokoło, podczas gdy
        /// korytko kilka metrów dalej leży na krótkiej trasie. Dlatego dla
        /// każdego korytka przy panelu liczony jest jeden przebieg pathfindera,
        /// a wynik to koszt dojścia po korytkach powiększony o dojścia do obu
        /// urządzeń.
        /// </summary>
        private bool TryMatchCandidates(
            List<TrayCandidate> sourceCandidates,
            List<TrayCandidate> targetCandidates,
            ConduitRoutingSettings settings,
            bool allowGapBridging,
            out TrayCandidate? bestStart,
            out TrayCandidate? bestEnd,
            out double bestRouteLengthMm)
        {
            bestStart = null;
            bestEnd = null;
            bestRouteLengthMm = double.MaxValue;
            var bestScore = double.MaxValue;
            if (allowGapBridging)
            {
                return TryMatchCandidatesWithBridges(
                    sourceCandidates, targetCandidates, settings,
                    out bestStart, out bestEnd, out bestRouteLengthMm);
            }


            var networkCollector = new TrayNetworkCollector(_document);
            var graphBuilder = new TrayGraphBuilder();
            var pathfinder = new ConduitPathfinder();
            var rankedSources = sourceCandidates
                .OrderBy(candidate => candidate.DistanceMm)
                .Take(GetRankedCandidateLimit(settings))
                .ToList();

            foreach (var startCandidate in rankedSources)
            {
                var report = new ConduitRoutingReport();
                var network = networkCollector.CollectNetwork(
                    startCandidate.Tray,
                    targetCandidates[0].Tray,
                    settings,
                    report,
                    allowGapBridging);
                var networkIds = new HashSet<ElementId>(network.Elements.Select(element => element.Id));
                var reachable = targetCandidates
                    .Where(candidate => networkIds.Contains(candidate.Tray.Id))
                    .ToList();
                if (reachable.Count == 0)
                {
                    continue;
                }

                var graph = graphBuilder.Build(
                    network.Elements,
                    startCandidate.Tray,
                    startCandidate.ProjectedPoint,
                    null,
                    null,
                    network.Bridges,
                    settings,
                    null);
                var distances = pathfinder.ComputeDistances(graph, TrayGraphBuilder.StartNodeKey);

                foreach (var endCandidate in reachable)
                {
                    if (!TryGetTrayDistanceMm(distances, startCandidate, endCandidate, out var trayPathMm))
                    {
                        continue;
                    }

                    // Poziomy przelot w powietrzu jest kosztowny, ale pionowy spadek
                    // z korytka nad odbiorem nie dostaje tej samej kary. Dzięki temu
                    // główna trasa biegnąca nad urządzeniem wygrywa z odległym
                    // korytkiem pionowym przy porównywalnej długości.
                    var accessMm = startCandidate.DistanceMm + endCandidate.AccessLengthMm;
                    var score = trayPathMm + startCandidate.DistanceMm +
                                GetWeightedEndAccessMm(endCandidate, settings);
                    if (score >= bestScore)
                    {
                        continue;
                    }

                    bestScore = score;

                    // Do limitu objazdu i do raportu idzie długość bez kary —
                    // kara służy wyłącznie do porównywania kandydatów.
                    bestRouteLengthMm = trayPathMm + accessMm;
                    bestStart = startCandidate;
                    bestEnd = endCandidate;
                }
            }

            return bestStart != null && bestEnd != null;
        }
        /// <summary>
        /// Każdy cel dostaje własną zebraną sieć, aby mostki znalezione dla
        /// jednej pary kandydatów nie wpływały na ocenę pozostałych par.
        /// </summary>
        private bool TryMatchCandidatesWithBridges(
            List<TrayCandidate> sourceCandidates,
            List<TrayCandidate> targetCandidates,
            ConduitRoutingSettings settings,
            out TrayCandidate? bestStart,
            out TrayCandidate? bestEnd,
            out double bestRouteLengthMm)
        {
            bestStart = null;
            bestEnd = null;
            bestRouteLengthMm = double.MaxValue;
            var bestScore = double.MaxValue;
            var networkCollector = new TrayNetworkCollector(_document);
            var graphBuilder = new TrayGraphBuilder();
            var pathfinder = new ConduitPathfinder();
            var rankedSources = sourceCandidates
                .OrderBy(candidate => candidate.DistanceMm)
                .Take(GetRankedCandidateLimit(settings))
                .ToList();

            foreach (var startCandidate in rankedSources)
            {
                foreach (var endCandidate in targetCandidates)
                {
                    var network = networkCollector.CollectNetwork(
                        startCandidate.Tray,
                        endCandidate.Tray,
                        settings,
                        new ConduitRoutingReport(),
                        true);
                    if (!network.TargetReached)
                    {
                        continue;
                    }

                    var graph = graphBuilder.Build(
                        network.Elements,
                        startCandidate.Tray,
                        startCandidate.ProjectedPoint,
                        endCandidate.Tray,
                        endCandidate.ProjectedPoint,
                        network.Bridges,
                        settings,
                        null);
                    if (!pathfinder.TryFindShortestPath(
                            graph,
                            TrayGraphBuilder.StartNodeKey,
                            TrayGraphBuilder.EndNodeKey,
                            out var routeEdges))
                    {
                        continue;
                    }

                    var weightedTrayMm = routeEdges.Sum(edge => edge.Cost) * FeetToMillimeters;
                    var physicalTrayMm = routeEdges.Sum(GetPhysicalEdgeLengthFeet) * FeetToMillimeters;
                    var accessMm = startCandidate.DistanceMm + endCandidate.AccessLengthMm;
                    var score = weightedTrayMm + startCandidate.DistanceMm +
                                GetWeightedEndAccessMm(endCandidate, settings);
                    if (score >= bestScore)
                    {
                        continue;
                    }

                    bestScore = score;
                    bestRouteLengthMm = physicalTrayMm + accessMm;
                    bestStart = startCandidate;
                    bestEnd = endCandidate;
                }
            }

            return bestStart != null && bestEnd != null;
        }

        private static double GetPhysicalEdgeLengthFeet(TrayGraphEdge edge)
        {
            if (edge.Kind != TrayGraphEdgeKind.Bridge || edge.BridgePath == null)
            {
                return edge.Start.DistanceTo(edge.End);
            }

            var length = 0.0;
            for (var i = 0; i + 1 < edge.BridgePath.Count; i++)
            {
                length += edge.BridgePath[i].DistanceTo(edge.BridgePath[i + 1]);
            }

            return length;
        }


        /// <summary>
        /// Koszt dojścia do korytka to najtańszy z jego konektorów. Punkt
        /// podłączenia leży gdzieś między nimi, więc odczyt jest przybliżeniem
        /// wystarczającym do porównania kandydatów.
        /// </summary>
        private static bool TryGetTrayDistanceMm(
            Dictionary<string, double> distances,
            TrayCandidate startCandidate,
            TrayCandidate candidate,
            out double distanceMm)
        {
            distanceMm = double.MaxValue;
            if (startCandidate.Tray.Id.IntegerValue == candidate.Tray.Id.IntegerValue)
            {
                distanceMm = startCandidate.ProjectedPoint.DistanceTo(candidate.ProjectedPoint) * FeetToMillimeters;
                return true;
            }

            foreach (var connector in MepConnectorReader.GetConnectors(candidate.Tray))
            {
                var key = TrayGraphBuilder.GetNodeKey(candidate.Tray, connector.Origin);
                if (distances.TryGetValue(key, out var distance))
                {
                    var toProjectedPoint = connector.Origin.DistanceTo(candidate.ProjectedPoint);
                    distanceMm = Math.Min(distanceMm, distance + toProjectedPoint);
                }
            }

            if (distanceMm == double.MaxValue)
            {
                return false;
            }

            distanceMm *= FeetToMillimeters;
            return true;
        }

        private static int GetRankedCandidateLimit(ConduitRoutingSettings settings)
        {
            var limit = settings?.MaxRankedStartCandidates ?? 3;
            return Math.Max(1, Math.Min(MaxCandidatesPerEndpoint, limit));
        }

        private static double GetFreeAirPenalty(ConduitRoutingSettings? settings)
        {
            return Math.Max(1.0, settings?.FreeAirCostPenaltyFactor ?? 1.0);
        }

        private static double GetWeightedEndAccessMm(
            TrayCandidate candidate,
            ConduitRoutingSettings? settings)
        {
            var penalty = GetFreeAirPenalty(settings);
            if (settings?.AllowFreeAirLegs != true)
            {
                return candidate.AccessLengthMm * penalty;
            }

            return candidate.VerticalAccessLengthMm +
                   candidate.HorizontalAccessLengthMm * penalty;
        }

        /// <summary>
        /// Składa końce trasy. Zejście dostaje wyłącznie ten koniec, który sam o
        /// nie prosi — pozostałe kończą się na korytku, dokładnie tak jak
        /// dotychczas.
        /// </summary>
        private static ConduitAutomaticRouteEndpoints BuildEndpoints(
            TrayCandidate? start,
            TrayCandidate? end,
            Element source,
            Element target,
            bool startNeedsFreeAir,
            bool endNeedsFreeAir)
        {
            return new ConduitAutomaticRouteEndpoints(
                BuildEndpoint(start!, source, startNeedsFreeAir),
                BuildEndpoint(end!, target, endNeedsFreeAir));
        }

        private static ConduitRouteEndpoint BuildEndpoint(
            TrayCandidate candidate,
            Element device,
            bool allowFreeAir)
        {
            // Gdy zejście jest potrzebne, dobiega do urządzenia niezależnie od
            // tego, jak blisko wypadło korytko — inaczej trasa kończyłaby się w
            // powietrzu obok odbioru. O tym, żeby conduit nie omijał korytka,
            // decyduje kara za prowadzenie w powietrzu przy wyborze punktu
            // odejścia, a nie brak zejścia.
            if (!allowFreeAir || !TryGetDevicePoint(device, out var devicePoint))
            {
                return ConduitRouteEndpoint.OnTray(candidate.Tray, candidate.ProjectedPoint, candidate.DistanceMm);
            }

            if (devicePoint.DistanceTo(candidate.ProjectedPoint) < MinimumTrayLengthFeet)
            {
                return ConduitRouteEndpoint.OnTray(candidate.Tray, candidate.ProjectedPoint, candidate.DistanceMm);
            }

            return ConduitRouteEndpoint.OnDevice(
                candidate.Tray,
                candidate.ProjectedPoint,
                candidate.DistanceMm,
                device,
                devicePoint);
        }

        /// <summary>
        /// Punkt podłączenia na urządzeniu: najpierw konektor elektryczny, potem
        /// punkt wstawienia, na końcu środek bryły.
        /// </summary>
        private static bool TryGetDevicePoint(Element device, out XYZ point)
        {
            point = null!;
            if (device == null)
            {
                return false;
            }

            var referencePoints = GetReferencePoints(device);
            point = referencePoints.FirstOrDefault()!;
            return point != null;
        }

        private static string BuildUnreachableError(
            List<TrayCandidate> sourceCandidates,
            List<TrayCandidate> targetCandidates,
            ConduitRoutingSettings? settings)
        {
            if (sourceCandidates.Count == 0)
            {
                return $"Nie znaleziono korytka w promieniu {SearchRadiusMm:F0} mm od panelu.";
            }

            if (targetCandidates.Count == 0)
            {
                return $"Nie znaleziono korytka w promieniu {SearchRadiusMm:F0} mm od odbioru.";
            }

            return settings != null && settings.AllowTrayGapBridging
                ? "Znaleziono korytka przy elementach, ale nie da się ich połączyć nawet przez przerwy w trasie."
                : "Znaleziono korytka przy elementach, ale nie należą one do jednej połączonej sieci.";
        }

        /// <summary>
        /// Korytka w zasięgu obu końców relacji, bez testu spójności sieci.
        /// Służy do zasilenia podglądu przekroju w oknie ustawień, które musi
        /// powstać zanim sprawdzimy przejezdność trasy — to w nim użytkownik
        /// decyduje o mostkowaniu przerw.
        /// </summary>
        public IReadOnlyList<CableTray> FindNearbyTrays(Element source, Element target)
        {
            if (!TryFindCandidates(source, target, out var sourceCandidates, out var targetCandidates, out _))
            {
                return new List<CableTray>();
            }

            return sourceCandidates
                .Concat(targetCandidates)
                .Select(candidate => candidate.Tray)
                .GroupBy(tray => tray.Id.IntegerValue)
                .Select(group => group.First())
                .ToList();
        }

        private bool TryFindCandidates(
            Element source,
            Element target,
            out List<TrayCandidate> sourceCandidates,
            out List<TrayCandidate> targetCandidates,
            out string error)
        {
            sourceCandidates = new List<TrayCandidate>();
            targetCandidates = new List<TrayCandidate>();
            error = string.Empty;

            if (source == null || target == null)
            {
                error = "Nie znaleziono panelu lub odbioru relacji.";
                return false;
            }

            var trays = new FilteredElementCollector(_document)
                .OfCategory(BuiltInCategory.OST_CableTray)
                .WhereElementIsNotElementType()
                .OfType<CableTray>()
                .ToList();
            if (trays.Count == 0)
            {
                error = "Model nie zawiera prostych odcinków korytek kablowych.";
                return false;
            }

            sourceCandidates = FindCandidates(source, trays, SearchRadiusMm);
            if (sourceCandidates.Count == 0)
            {
                error = $"Nie znaleziono korytka w promieniu {SearchRadiusMm:F0} mm od panelu.";
                return false;
            }

            targetCandidates = FindCandidates(target, trays, SearchRadiusMm);
            if (targetCandidates.Count == 0)
            {
                error = $"Nie znaleziono korytka w promieniu {SearchRadiusMm:F0} mm od odbioru.";
                return false;
            }

            return true;
        }

        private static List<TrayCandidate> FindCandidates(
            Element element,
            IEnumerable<CableTray> trays,
            double searchRadiusMm,
            ConduitRoutingSettings? settings = null,
            bool preferOverhead = false)
        {
            if (!TryGetDevicePoint(element, out var primaryPoint))
            {
                return new List<TrayCandidate>();
            }

            var referencePoints = new[] { primaryPoint };
            var candidates = new List<TrayCandidate>();

            foreach (var tray in trays)
            {
                TrayCandidate? bestForTray = null;
                foreach (var referencePoint in referencePoints)
                {
                    if (!TryProjectToTray(tray, referencePoint, out var projectedPoint))
                    {
                        continue;
                    }

                    var distanceMm = referencePoint.DistanceTo(projectedPoint) * FeetToMillimeters;
                    var delta = referencePoint - projectedPoint;
                    var horizontalFeet = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
                    var horizontalAccessLengthMm = horizontalFeet * FeetToMillimeters;
                    var verticalAccessLengthMm = Math.Abs(delta.Z) * FeetToMillimeters;
                    var accessLengthMm = horizontalAccessLengthMm + verticalAccessLengthMm;

                    if (distanceMm > searchRadiusMm ||
                        (bestForTray != null && distanceMm >= bestForTray.DistanceMm))
                    {
                        continue;
                    }

                    bestForTray = new TrayCandidate(
                        tray,
                        projectedPoint,
                        distanceMm,
                        accessLengthMm,
                        horizontalAccessLengthMm,
                        verticalAccessLengthMm);
                }

                if (bestForTray != null)
                {
                    candidates.Add(bestForTray);
                }
            }

            var ordered = preferOverhead
                ? candidates
                    .OrderBy(candidate => GetWeightedEndAccessMm(candidate, settings))
                    .ThenBy(candidate => candidate.DistanceMm)
                : candidates
                    .OrderBy(candidate => candidate.DistanceMm);

            return ordered
                .Take(MaxCandidatesPerEndpoint)
                .ToList();
        }

        private static List<XYZ> GetReferencePoints(Element element)
        {
            var result = new List<XYZ>();
            foreach (var connector in MepConnectorReader.GetConnectors(element))
            {
                if (connector.Domain == Domain.DomainElectrical)
                {
                    AddPoint(result, connector.Origin);
                }
            }

            if (element.Location is LocationPoint locationPoint)
            {
                AddPoint(result, locationPoint.Point);
            }
            else if (element.Location is LocationCurve locationCurve && locationCurve.Curve != null)
            {
                AddPoint(result, locationCurve.Curve.Evaluate(0.5, true));
            }

            var boundingBox = element.get_BoundingBox(null);
            if (boundingBox != null)
            {
                AddPoint(result, (boundingBox.Min + boundingBox.Max).Multiply(0.5));
            }

            return result;
        }

        private static void AddPoint(ICollection<XYZ> points, XYZ point)
        {
            if (point == null || points.Any(existing => existing.DistanceTo(point) < 0.000001))
            {
                return;
            }

            points.Add(point);
        }

        private static bool TryProjectToTray(CableTray tray, XYZ point, out XYZ projectedPoint)
        {
            projectedPoint = null!;
            var line = (tray.Location as LocationCurve)?.Curve as Line;
            if (line == null)
            {
                return false;
            }

            var start = line.GetEndPoint(0);
            var end = line.GetEndPoint(1);
            var vector = end - start;
            var length = vector.GetLength();
            if (length < MinimumTrayLengthFeet)
            {
                return false;
            }

            var direction = vector.Normalize();
            var distance = (point - start).DotProduct(direction);
            distance = Math.Max(0, Math.Min(length, distance));
            projectedPoint = start + direction.Multiply(distance);
            return true;
        }

        private class TrayCandidate
        {
            public TrayCandidate(
                CableTray tray,
                XYZ projectedPoint,
                double distanceMm,
                double accessLengthMm,
                double horizontalAccessLengthMm,
                double verticalAccessLengthMm)
            {
                Tray = tray;
                ProjectedPoint = projectedPoint;
                DistanceMm = distanceMm;
                AccessLengthMm = accessLengthMm;
                HorizontalAccessLengthMm = horizontalAccessLengthMm;
                VerticalAccessLengthMm = verticalAccessLengthMm;
            }

            public CableTray Tray { get; }

            public XYZ ProjectedPoint { get; }

            public double DistanceMm { get; }

            public double AccessLengthMm { get; }

            public double HorizontalAccessLengthMm { get; }

            public double VerticalAccessLengthMm { get; }
        }
    }
}