using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using INP_IE.ConduitRouting.Models;

namespace INP_IE.ConduitManager.Services
{
    public class ConduitPositionAllocator
    {
        private const double MmToFeet = 1.0 / 304.8;
        private const double FeetToMm = 304.8;
        private const double MinimumSegmentLength = 1.0 / 12.0;
        private const double ExistingConduitMinimumToleranceMm = 6.0;
        private const double ExistingConduitMaximumToleranceMm = 12.0;
        private const double DefaultExistingDiameterFeet = 25.0 * MmToFeet;
        private const double ParallelDotThreshold = 0.98;
        private const double DivergenceLookaheadFeet = 5.0;
        private const double DivergenceSideToleranceFeet = 0.1;

        /// <summary>
        /// Odległość, poniżej której dwa punkty uznajemy za wspólny styk trasy.
        /// Klucze węzłów grafu są zaokrąglane do 1e-6 stopy, więc punkty stykowe
        /// pokrywają się z dokładnością znacznie poniżej 0,03 mm.
        /// </summary>
        private const double JunctionTolerance = 1e-4;

        /// <summary>Jak daleko mogą się rozminąć osie toru, żeby dało się je zszyć jednym załamaniem.</summary>
        private const double MaximumWeldSkewFeet = 0.5;

        /// <summary>Minimalny zasięg szukania styku, gdy oba końce leżą niemal w tym samym punkcie.</summary>
        private const double MinimumWeldReachFeet = 1.0 / 12.0;

        /// <summary>
        /// Minimalna zgodność osi przekroju z osią przeniesioną z poprzedniego
        /// odcinka, przy której zwrot jest jeszcze przenoszony. Po przeniesieniu
        /// obie osie powinny być niemal równoległe, więc próg odsiewa wyłącznie
        /// przypadki realnego obrotu przekroju w modelu.
        /// </summary>
        private const double SideAlignmentThreshold = 0.1;

        private readonly Document _doc;
        private readonly Dictionary<int, bool> _trayElementCache = new Dictionary<int, bool>();

        public ConduitPositionAllocator(Document doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// Seeds the shared context with every conduit already present in the
        /// model so bulk allocation avoids existing runs from the first relation.
        /// </summary>
        public void SeedExistingConduits(ConduitAllocationContext context)
        {
            if (context == null)
            {
                return;
            }

            foreach (var conduit in new FilteredElementCollector(_doc).OfClass(typeof(Conduit)).Cast<Conduit>())
            {
                var line = (conduit.Location as LocationCurve)?.Curve as Line;
                if (line == null)
                {
                    continue;
                }

                context.AddExistingAxis(new ConduitOccupiedAxis(
                    conduit.Id,
                    ElementId.InvalidElementId,
                    line.GetEndPoint(0),
                    line.GetEndPoint(1),
                    GetExistingConduitDiameterFeet(conduit)));
            }
        }

        /// <summary>
        /// Assigns lanes to a whole batch of routes at once so that on every
        /// shared tray run the relations are ordered to match the side they
        /// branch off toward, which minimises crossings at junctions. Results are
        /// returned in the same order as the input paths.
        /// </summary>
        public IReadOnlyList<ConduitLaneAllocation> AllocateLanes(
            IReadOnlyList<IReadOnlyList<ConduitRunSegment>> paths,
            ConduitAllocationContext context,
            ConduitRoutingSettings settings)
        {
            var count = paths?.Count ?? 0;
            var results = new ConduitLaneAllocation[count];
            if (count == 0)
            {
                return results;
            }

            if (context == null)
            {
                context = new ConduitAllocationContext();
            }

            var sideFields = new XYZ[count][];
            for (var i = 0; i < count; i++)
            {
                sideFields[i] = BuildContinuousSideVectors(paths[i]);
            }

            var preferredLanes = BuildPreferredLanes(paths, sideFields);

            // Reserve central lanes first so branches fill outward around them and
            // the crossing-minimising order set by the preferred lanes survives.
            var order = Enumerable.Range(0, count)
                .OrderBy(index => Math.Abs(preferredLanes[index]))
                .ThenBy(index => preferredLanes[index])
                .ThenBy(index => index)
                .ToList();

            foreach (var index in order)
            {
                results[index] = PlaceLane(paths[index], sideFields[index], preferredLanes[index], context, settings);
            }

            return results;
        }

        public ConduitLaneAllocation AllocateLane(
            IReadOnlyList<ConduitRunSegment> rawPath,
            ConduitAllocationContext context,
            ConduitRoutingSettings settings)
        {
            return PlaceLane(rawPath, null, 0, context, settings);
        }

        private ConduitLaneAllocation PlaceLane(
            IReadOnlyList<ConduitRunSegment> rawPath,
            XYZ[] sides,
            int preferredLane,
            ConduitAllocationContext context,
            ConduitRoutingSettings settings)
        {
            var result = new ConduitLaneAllocation();
            if (rawPath == null || rawPath.Count == 0)
            {
                result.SkipReason = "Trasa nie zawiera odcinków możliwych do rozmieszczenia.";
                return result;
            }

            if (context == null)
            {
                context = new ConduitAllocationContext();
            }

            var diameterMm = Math.Max(0, settings.DiameterMm);
            var spacingMm = Math.Max(0, settings.SpacingMm);
            var marginMm = Math.Max(0, settings.MarginMm);
            var pitchMm = ConduitLaneMath.GetPitchMm(diameterMm, spacingMm);
            var pitchFeet = pitchMm * MmToFeet;
            var radiusFeet = diameterMm / 2.0 * MmToFeet;
            var spacingFeet = spacingMm * MmToFeet;

            var widthsMm = new double[rawPath.Count];

            // Odcinek prowadzony w powietrzu nie leży w żadnym korytku, więc nie
            // podlega sprawdzeniu zabudowy przekroju — inaczej zejście do
            // urządzenia rządziłoby szerokością toru dla całej trasy.
            var constrained = new bool[rawPath.Count];
            var minWidthMm = double.MaxValue;
            var narrowestElement = ElementId.InvalidElementId;
            var minHeightMm = double.MaxValue;
            var lowestHeightElement = ElementId.InvalidElementId;
            for (var i = 0; i < rawPath.Count; i++)
            {
                // Mostki nad przerwą nie mają własnego przekroju — pożyczają go
                // od węższego z korytek po obu stronach przerwy, dzięki czemu
                // tor przechodzi przez przerwę bez zmiany przesunięcia.
                var widthReference = rawPath[i].WidthReferenceElementId;
                var width = TrayCrossSectionReader.GetWidthMm(_doc, widthReference);
                var height = TrayCrossSectionReader.GetHeightMm(_doc, widthReference);
                widthsMm[i] = width;
                constrained[i] = rawPath[i].Kind != ConduitSegmentKind.FreeAir;
                if (constrained[i] && width > 0 && width < minWidthMm)
                {
                    minWidthMm = width;
                    narrowestElement = widthReference;
                }

                if (constrained[i] && height > 0 && height < minHeightMm)
                {
                    minHeightMm = height;
                    lowestHeightElement = widthReference;
                }
            }

            // Continuous lane-side vectors keep the offset on one physical side of
            // the whole route, so it never flips 180° at a direction change and
            // never crosses itself.
            sides = sides ?? BuildContinuousSideVectors(rawPath);

            var hasKnownWidth = minWidthMm != double.MaxValue;
            var boundingWidthMm = hasKnownWidth ? minWidthMm : 0;
            var hasKnownHeight = minHeightMm != double.MaxValue;
            var boundingHeightMm = hasKnownHeight ? minHeightMm : 0;

            // Odcinek, dla którego nie udało się odczytać przekroju (kształtka bez
            // wymiarów, mostek bez korytka referencyjnego), przyjmuje najwęższy
            // znany przekrój trasy. Wcześniej taki odcinek zamykał całą relację na
            // torze środkowym, więc pojedynczy conduit w modelu blokował ją
            // komunikatem o braku wolnego toru w korytku, które wcale nie było
            // wąskie.
            for (var i = 0; i < rawPath.Count; i++)
            {
                if (constrained[i] && widthsMm[i] <= 0)
                {
                    widthsMm[i] = boundingWidthMm;
                }
            }

            if (hasKnownHeight &&
                !ConduitLaneMath.FitsInsideTrayHeight(boundingHeightMm, diameterMm, marginMm))
            {
                result.SkipReason = BuildHeightCapacitySkipReason(
                    lowestHeightElement, boundingHeightMm, diameterMm, marginMm);
                return result;
            }

            // Bez ani jednego odczytanego przekroju nie da się zagwarantować
            // zabudowy, więc dopuszczamy wyłącznie oś elementu.
            var maxStep = hasKnownWidth
                ? ConduitLaneMath.GetMaximumLaneStep(boundingWidthMm, diameterMm, marginMm, pitchMm)
                : 0;
            if (maxStep < 0)
            {
                result.SkipReason = BuildCapacitySkipReason(narrowestElement, boundingWidthMm, diameterMm, marginMm);
                return result;
            }

            var testedLanes = 0;
            var lastBlocker = LaneBlockerKind.None;
            var lastBlockerElement = ElementId.InvalidElementId;
            foreach (var lane in BuildSearchOrder(preferredLane, maxStep))
            {
                var offsetMm = lane * pitchMm;
                var offsetFeet = lane * pitchFeet;
                testedLanes++;

                if (!TryPlaceLane(rawPath, widthsMm, constrained, sides, offsetMm, offsetFeet, diameterMm, marginMm,
                        radiusFeet, spacingFeet, context, out var shifted, out var blocker, out var blockerElement))
                {
                    if (lane == 0)
                    {
                        RecordCenterBlocker(result, blocker);
                    }

                    lastBlocker = KeepMostInformativeBlocker(lastBlocker, blocker);
                    if (blockerElement != null && blockerElement != ElementId.InvalidElementId &&
                        (blocker == LaneBlockerKind.ExistingConduit || blocker == LaneBlockerKind.Reservation))
                    {
                        lastBlockerElement = blockerElement;
                    }
                    continue;
                }

                result.Success = true;
                result.LaneIndex = lane;
                result.Segments = shifted;
                result.MaxTrayFillRatio = ComputeMaxFill(widthsMm, constrained, offsetMm, diameterMm, marginMm);
                context.Reserve(shifted
                    .Where((segment, index) => rawPath[index].Kind != ConduitSegmentKind.FreeAir && !segment.IsLaneConverging)
                    .Select(segment => new ConduitOccupiedAxis(
                        ElementId.InvalidElementId,
                        segment.SourceElementId,
                        segment.Start,
                        segment.End,
                        diameterMm * MmToFeet)));
                return result;
            }

            var reportedElement = lastBlockerElement != ElementId.InvalidElementId
                ? lastBlockerElement
                : narrowestElement;
            var reportedWidthMm = TrayCrossSectionReader.GetWidthMm(_doc, reportedElement);
            if (reportedWidthMm <= 0)
            {
                reportedWidthMm = boundingWidthMm;
            }

            result.SkipReason = BuildNoFreeLaneSkipReason(
                reportedElement, reportedWidthMm, diameterMm, spacingMm, marginMm, testedLanes, lastBlocker);
            return result;
        }

        /// <summary>
        /// Kolizja z konkretnym conduitem mówi projektantowi więcej niż sam brak
        /// miejsca w przekroju, więc to ona trafia do komunikatu.
        /// </summary>
        private static LaneBlockerKind KeepMostInformativeBlocker(
            LaneBlockerKind current,
            LaneBlockerKind candidate)
        {
            if (candidate == LaneBlockerKind.ExistingConduit || candidate == LaneBlockerKind.Reservation)
            {
                return candidate;
            }

            return current == LaneBlockerKind.None ? candidate : current;
        }

        private bool TryPlaceLane(
            IReadOnlyList<ConduitRunSegment> rawPath,
            double[] widthsMm,
            bool[] constrained,
            XYZ[] sides,
            double offsetMm,
            double offsetFeet,
            double diameterMm,
            double marginMm,
            double radiusFeet,
            double spacingFeet,
            ConduitAllocationContext context,
            out List<ConduitRunSegment> shifted,
            out LaneBlockerKind blocker,
            out ElementId blockerElement)
        {
            blockerElement = ElementId.InvalidElementId;
            shifted = new List<ConduitRunSegment>(rawPath.Count);
            blocker = LaneBlockerKind.None;

            var liftVectors = BuildContinuousLiftVectors(rawPath, sides);
            var supportOffsetFeet = radiusFeet +
                ConduitLaneMath.GetCrossSectionClearanceMm(marginMm) * MmToFeet;

            for (var i = 0; i < rawPath.Count; i++)
            {
                if (constrained[i] &&
                    !ConduitLaneMath.FitsInsideTrayWidth(widthsMm[i], diameterMm, marginMm, offsetMm))
                {
                    blocker = LaneBlockerKind.Capacity;
                    blockerElement = rawPath[i].WidthReferenceElementId;
                    return false;
                }

                shifted.Add(ShiftSegmentAlongSide(
                    rawPath[i], sides[i], offsetFeet, liftVectors[i], supportOffsetFeet));
            }

            // Przesunięcie liczone osobno dla każdego odcinka rozrywa trasę tam,
            // gdzie zmienia się oś przekroju. Zszycie styków przywraca ciągłość,
            // zanim tor pójdzie do testu zajętości i do modelu.
            WeldLaneJunctions(rawPath, shifted);

            for (var i = 0; i < shifted.Count; i++)
            {
                // Odcinki FreeAir nie są częścią przekroju korytka. Ich kolizja
                // nie może zajmować torów ani być raportowana jako zajętość korytka.
                // Osobno pomijamy skos wspólny dla wszystkich torów.
                if (rawPath[i].Kind == ConduitSegmentKind.FreeAir ||
                    shifted[i].IsLaneConverging)
                {
                    continue;
                }

                foreach (var occupied in context.OccupiedAxes)
                {
                    if (AxesConflict(shifted[i].Start, shifted[i].End, radiusFeet, occupied, spacingFeet))
                    {
                        blocker = occupied.IsReservation
                            ? LaneBlockerKind.Reservation
                            : LaneBlockerKind.ExistingConduit;
                        blockerElement = rawPath[i].WidthReferenceElementId;
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Skleja styki sąsiednich odcinków, które w surowej trasie miały wspólny
        /// punkt. Każdy odcinek jest przesuwany wzdłuż własnej osi przekroju, więc
        /// tam, gdzie oś się zmienia — na kształtce, na wejściu w pion, na
        /// mostku — koniec jednego odcinka i początek następnego rozjeżdżają się
        /// o pełną różnicę przesunięć. Styk wraca na przecięcie obu osi toru, a
        /// gdy osie są równoległe — na punkt pośredni.
        /// </summary>
        private static void WeldLaneJunctions(
            IReadOnlyList<ConduitRunSegment> rawPath,
            List<ConduitRunSegment> shifted)
        {
            for (var i = 0; i + 1 < shifted.Count; i++)
            {
                // Styk istnieje tylko tam, gdzie surowa trasa była ciągła i oba
                // końce niosą ten sam udział przesunięcia. Skos przy urządzeniu
                // celowo zbiega do osi i nie podlega zszywaniu.
                if (rawPath[i].End.DistanceTo(rawPath[i + 1].Start) > JunctionTolerance ||
                    Math.Abs(rawPath[i].EndLaneFactor - rawPath[i + 1].StartLaneFactor) > 1e-9)
                {
                    continue;
                }

                var before = shifted[i];
                var after = shifted[i + 1];
                if (before.End.DistanceTo(after.Start) < JunctionTolerance)
                {
                    continue;
                }

                if (!TryGetWeldPoint(before, after, out var weld))
                {
                    weld = (before.End + after.Start).Multiply(0.5);
                    if (before.Start.DistanceTo(weld) < MinimumSegmentLength ||
                        weld.DistanceTo(after.End) < MinimumSegmentLength)
                    {
                        continue;
                    }
                }

                shifted[i] = before.WithGeometry(before.Start, weld);
                shifted[i + 1] = after.WithGeometry(weld, after.End);
            }
        }

        private static bool TryGetWeldPoint(ConduitRunSegment before, ConduitRunSegment after, out XYZ weld)
        {
            weld = null;
            var beforeVector = before.End - before.Start;
            var afterVector = after.End - after.Start;
            if (beforeVector.GetLength() < 1e-9 || afterVector.GetLength() < 1e-9)
            {
                return false;
            }

            var beforeDirection = beforeVector.Normalize();
            var afterDirection = afterVector.Normalize();
            var midpoint = (before.End + after.Start).Multiply(0.5);

            // Osie równoległe nie mają przecięcia. Przy bardzo łagodnym załamaniu
            // przecięcie owszem istnieje, ale ucieka daleko poza narożnik i
            // wydłużyłoby conduit bardziej, niż wynosi cała rozbieżność styku —
            // w obu przypadkach styk ląduje pośrodku.
            var reachLimit = Math.Max(before.End.DistanceTo(after.Start) * 2.0, MinimumWeldReachFeet);
            if (Math.Abs(beforeDirection.DotProduct(afterDirection)) > ParallelDotThreshold ||
                !ConduitBendPathBuilder.TryGetCorner(
                    before.End, beforeDirection, after.Start, afterDirection, MaximumWeldSkewFeet, out weld) ||
                weld == null ||
                weld.DistanceTo(midpoint) > reachLimit ||
                !KeepsOrientation(before.Start, weld, beforeDirection, after.End, afterDirection))
            {
                weld = midpoint;
            }

            // Styk nie może odwrócić ani wyzerować żadnego z sąsiadów — przy
            // bardzo krótkim odcinku lepiej zostawić rozjazd, niż wyprodukować
            // conduit biegnący pod prąd trasy.
            return KeepsOrientation(before.Start, weld, beforeDirection, after.End, afterDirection);
        }

        private static bool KeepsOrientation(
            XYZ beforeStart,
            XYZ weld,
            XYZ beforeDirection,
            XYZ afterEnd,
            XYZ afterDirection)
        {
            return (weld - beforeStart).DotProduct(beforeDirection) > MinimumSegmentLength &&
                   (afterEnd - weld).DotProduct(afterDirection) > MinimumSegmentLength;
        }

        private static void RecordCenterBlocker(ConduitLaneAllocation result, LaneBlockerKind blocker)
        {
            switch (blocker)
            {
                case LaneBlockerKind.ExistingConduit:
                    result.CenterBlockedByModel = true;
                    break;
                case LaneBlockerKind.Reservation:
                    result.CenterBlockedByPlan = true;
                    break;
            }
        }

        private static double ComputeMaxFill(
            double[] widthsMm,
            bool[] constrained,
            double offsetMm,
            double diameterMm,
            double marginMm)
        {
            var maxFill = 0.0;
            for (var i = 0; i < widthsMm.Length; i++)
            {
                if (!constrained[i] || widthsMm[i] <= 0)
                {
                    continue;
                }

                var fill = ConduitLaneMath.GetTrayFillRatio(widthsMm[i], diameterMm, marginMm, offsetMm);
                if (fill > maxFill)
                {
                    maxFill = fill;
                }
            }

            return maxFill;
        }

        /// <summary>
        /// Assigns a preferred lane to every route. Routes sharing trays form a
        /// component; inside it every junction where two routes merge or split
        /// casts a vote for their relative order, and the permutation violating
        /// the fewest votes maps to centered lanes. Independent routes default
        /// to the center.
        /// </summary>
        private int[] BuildPreferredLanes(
            IReadOnlyList<IReadOnlyList<ConduitRunSegment>> paths,
            XYZ[][] sideFields)
        {
            var count = paths.Count;
            var preferred = new int[count];

            var routesByElement = new Dictionary<int, List<int>>();
            for (var i = 0; i < count; i++)
            {
                foreach (var elementId in GetDistinctElementIds(paths[i]))
                {
                    if (!routesByElement.TryGetValue(elementId, out var list))
                    {
                        list = new List<int>();
                        routesByElement[elementId] = list;
                    }

                    list.Add(i);
                }
            }

            var sharedByPair = new Dictionary<(int, int), HashSet<int>>();
            foreach (var entry in routesByElement)
            {
                var routes = entry.Value;
                for (var a = 0; a < routes.Count; a++)
                {
                    for (var b = a + 1; b < routes.Count; b++)
                    {
                        var key = routes[a] < routes[b]
                            ? (routes[a], routes[b])
                            : (routes[b], routes[a]);
                        if (!sharedByPair.TryGetValue(key, out var shared))
                        {
                            shared = new HashSet<int>();
                            sharedByPair[key] = shared;
                        }

                        shared.Add(entry.Key);
                    }
                }
            }

            var parents = Enumerable.Range(0, count).ToArray();
            foreach (var key in sharedByPair.Keys)
            {
                Union(parents, key.Item1, key.Item2);
            }

            // Votes and side agreement need a well-defined corridor direction, so
            // they only look at straight trays. A fitting (tee, cross) is a single
            // element traversed by different connector paths, and a frame taken
            // inside it points toward the branch instead of along the trunk.
            var traySharedByPair = new Dictionary<(int, int), HashSet<int>>();
            foreach (var entry in sharedByPair)
            {
                var sharedTrays = new HashSet<int>(entry.Value.Where(IsTrayElement));
                if (sharedTrays.Count > 0)
                {
                    traySharedByPair[entry.Key] = sharedTrays;
                }
            }

            HarmonizeSideFields(paths, sideFields, traySharedByPair);

            var votes = new int[count, count];
            foreach (var entry in traySharedByPair)
            {
                CollectCrossingVotes(paths, sideFields, entry.Key.Item1, entry.Key.Item2, entry.Value, votes);
            }

            var components = new Dictionary<int, List<int>>();
            for (var i = 0; i < count; i++)
            {
                var root = Find(parents, i);
                if (!components.TryGetValue(root, out var members))
                {
                    members = new List<int>();
                    components[root] = members;
                }

                members.Add(i);
            }

            foreach (var members in components.Values)
            {
                if (members.Count <= 1)
                {
                    preferred[members[0]] = 0;
                    continue;
                }

                var ordered = FindLeastCrossingOrder(members, votes);
                var centerOffset = (ordered.Count - 1) / 2;
                for (var rank = 0; rank < ordered.Count; rank++)
                {
                    preferred[ordered[rank]] = rank - centerOffset;
                }
            }

            return preferred;
        }

        private bool IsTrayElement(int elementId)
        {
            if (_trayElementCache.TryGetValue(elementId, out var isTray))
            {
                return isTray;
            }

            var element = _doc.GetElement(new ElementId(elementId));
            isTray = element != null && TrayElementClassifier.IsCableTray(element);
            _trayElementCache[elementId] = isTray;
            return isTray;
        }

        private static IEnumerable<int> GetDistinctElementIds(IReadOnlyList<ConduitRunSegment> path)
        {
            var seen = new HashSet<int>();
            foreach (var segment in path)
            {
                var id = segment.SourceElementId;
                if (id != null && id != ElementId.InvalidElementId && seen.Add(id.IntegerValue))
                {
                    yield return id.IntegerValue;
                }
            }
        }

        /// <summary>
        /// Two routes can traverse a shared tray in opposite directions, in which
        /// case their propagated side vectors point to opposite physical sides
        /// and equal lane numbers would land on opposite edges of the tray.
        /// Flipping a whole side field is only a sign convention, so a BFS over
        /// the pair-agreement graph picks signs that make every shared run agree.
        /// </summary>
        private static void HarmonizeSideFields(
            IReadOnlyList<IReadOnlyList<ConduitRunSegment>> paths,
            XYZ[][] sideFields,
            Dictionary<(int, int), HashSet<int>> sharedByPair)
        {
            var count = paths.Count;
            var agreements = new Dictionary<(int, int), int>();
            var neighbors = new Dictionary<int, List<int>>();
            foreach (var pair in sharedByPair)
            {
                var agreement = MeasureSideAgreement(
                    paths[pair.Key.Item1],
                    sideFields[pair.Key.Item1],
                    paths[pair.Key.Item2],
                    sideFields[pair.Key.Item2],
                    pair.Value);
                if (agreement == 0)
                {
                    continue;
                }

                agreements[pair.Key] = agreement;
                AddNeighbor(neighbors, pair.Key.Item1, pair.Key.Item2);
                AddNeighbor(neighbors, pair.Key.Item2, pair.Key.Item1);
            }

            var signs = new int[count];
            var queue = new Queue<int>();
            for (var start = 0; start < count; start++)
            {
                if (signs[start] != 0 || !neighbors.ContainsKey(start))
                {
                    continue;
                }

                signs[start] = 1;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    foreach (var next in neighbors[current])
                    {
                        if (signs[next] != 0)
                        {
                            continue;
                        }

                        var key = current < next ? (current, next) : (next, current);
                        signs[next] = signs[current] * agreements[key];
                        queue.Enqueue(next);
                    }
                }
            }

            for (var i = 0; i < count; i++)
            {
                if (signs[i] >= 0)
                {
                    continue;
                }

                var sides = sideFields[i];
                for (var k = 0; k < sides.Length; k++)
                {
                    sides[k] = sides[k]?.Negate();
                }
            }
        }

        private static void AddNeighbor(Dictionary<int, List<int>> neighbors, int from, int to)
        {
            if (!neighbors.TryGetValue(from, out var list))
            {
                list = new List<int>();
                neighbors[from] = list;
            }

            list.Add(to);
        }

        private static int MeasureSideAgreement(
            IReadOnlyList<ConduitRunSegment> pathA,
            XYZ[] sidesA,
            IReadOnlyList<ConduitRunSegment> pathB,
            XYZ[] sidesB,
            HashSet<int> sharedIds)
        {
            foreach (var elementId in sharedIds)
            {
                var indexA = FindSegmentOnElement(pathA, elementId);
                var indexB = FindSegmentOnElement(pathB, elementId);
                if (indexA < 0 || indexB < 0 || sidesA[indexA] == null || sidesB[indexB] == null)
                {
                    continue;
                }

                var dot = sidesA[indexA].DotProduct(sidesB[indexB]);
                if (Math.Abs(dot) < 0.5)
                {
                    continue;
                }

                return dot > 0 ? 1 : -1;
            }

            return 0;
        }

        private static int FindSegmentOnElement(IReadOnlyList<ConduitRunSegment> path, int elementId)
        {
            for (var index = 0; index < path.Count; index++)
            {
                var id = path[index].SourceElementId;
                if (id != null && id != ElementId.InvalidElementId && id.IntegerValue == elementId)
                {
                    return index;
                }
            }

            return -1;
        }

        /// <summary>
        /// At both ends of the tray run shared by two routes, measures which side
        /// each route continues toward and votes for the lane order that lets
        /// them separate without crossing. The lookahead uses real displacement,
        /// so branches that dive below the trunk still reveal the horizontal
        /// side they peel toward.
        /// </summary>
        private static void CollectCrossingVotes(
            IReadOnlyList<IReadOnlyList<ConduitRunSegment>> paths,
            XYZ[][] sideFields,
            int i,
            int j,
            HashSet<int> sharedIds,
            int[,] votes)
        {
            var boundsI = GetSharedRunBounds(paths[i], sharedIds);
            var boundsJ = GetSharedRunBounds(paths[j], sharedIds);
            if (boundsI.First < 0 || boundsJ.First < 0)
            {
                return;
            }

            AddJunctionVote(paths[i], sideFields[i], boundsI.Last, true, paths[j], boundsJ, votes, i, j);
            AddJunctionVote(paths[i], sideFields[i], boundsI.First, false, paths[j], boundsJ, votes, i, j);
        }

        private static void AddJunctionVote(
            IReadOnlyList<ConduitRunSegment> pathI,
            XYZ[] sidesI,
            int frameIndex,
            bool forward,
            IReadOnlyList<ConduitRunSegment> pathJ,
            (int First, int Last) boundsJ,
            int[,] votes,
            int i,
            int j)
        {
            var side = sidesI[frameIndex];
            if (side == null)
            {
                return;
            }

            var junctionI = forward ? pathI[frameIndex].End : pathI[frameIndex].Start;
            var pointI = WalkAwayFrom(pathI, forward ? frameIndex + 1 : frameIndex - 1, forward, junctionI);

            // Matching end of j's shared run: whichever lies closer to i's junction.
            var jRunStart = pathJ[boundsJ.First].Start;
            var jRunEnd = pathJ[boundsJ.Last].End;
            var jForward = jRunEnd.DistanceTo(junctionI) <= jRunStart.DistanceTo(junctionI);
            var junctionJ = jForward ? jRunEnd : jRunStart;
            var pointJ = WalkAwayFrom(pathJ, jForward ? boundsJ.Last + 1 : boundsJ.First - 1, jForward, junctionJ);

            var lateralI = (pointI - junctionI).DotProduct(side);
            var lateralJ = (pointJ - junctionJ).DotProduct(side);
            if (lateralI < lateralJ - DivergenceSideToleranceFeet)
            {
                votes[i, j]++;
            }
            else if (lateralJ < lateralI - DivergenceSideToleranceFeet)
            {
                votes[j, i]++;
            }
        }

        private static XYZ WalkAwayFrom(
            IReadOnlyList<ConduitRunSegment> path,
            int startIndex,
            bool forward,
            XYZ origin)
        {
            var point = origin;
            var step = forward ? 1 : -1;
            for (var index = startIndex; index >= 0 && index < path.Count; index += step)
            {
                point = forward ? path[index].End : path[index].Start;
                if (point.DistanceTo(origin) >= DivergenceLookaheadFeet)
                {
                    break;
                }
            }

            return point;
        }

        private static (int First, int Last) GetSharedRunBounds(
            IReadOnlyList<ConduitRunSegment> path,
            HashSet<int> sharedIds)
        {
            var first = -1;
            var last = -1;
            for (var index = 0; index < path.Count; index++)
            {
                var id = path[index].SourceElementId;
                if (id == null || id == ElementId.InvalidElementId || !sharedIds.Contains(id.IntegerValue))
                {
                    continue;
                }

                if (first < 0)
                {
                    first = index;
                }

                last = index;
            }

            return (first, last);
        }

        /// <summary>
        /// Order minimising the number of violated side votes: exact search for
        /// small components, greedy net-score order polished by adjacent swaps
        /// for larger ones. First in the order takes the lowest lane.
        /// </summary>
        private static List<int> FindLeastCrossingOrder(List<int> members, int[,] votes)
        {
            var order = members
                .OrderByDescending(index => members.Sum(other => votes[index, other] - votes[other, index]))
                .ThenBy(index => index)
                .ToList();
            var bestCost = CountVoteViolations(order, votes);
            if (bestCost == 0)
            {
                return order;
            }

            if (members.Count <= 7)
            {
                SearchOrder(new List<int>(), new List<int>(members), votes, order, ref bestCost);
                return order;
            }

            var improved = true;
            while (improved)
            {
                improved = false;
                for (var a = 0; a + 1 < order.Count; a++)
                {
                    if (votes[order[a + 1], order[a]] > votes[order[a], order[a + 1]])
                    {
                        var swap = order[a];
                        order[a] = order[a + 1];
                        order[a + 1] = swap;
                        improved = true;
                    }
                }
            }

            return order;
        }

        private static void SearchOrder(
            List<int> current,
            List<int> remaining,
            int[,] votes,
            List<int> best,
            ref int bestCost)
        {
            if (remaining.Count == 0)
            {
                var cost = CountVoteViolations(current, votes);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best.Clear();
                    best.AddRange(current);
                }

                return;
            }

            for (var index = 0; index < remaining.Count; index++)
            {
                var candidate = remaining[index];
                remaining.RemoveAt(index);
                current.Add(candidate);
                SearchOrder(current, remaining, votes, best, ref bestCost);
                current.RemoveAt(current.Count - 1);
                remaining.Insert(index, candidate);
            }
        }

        private static int CountVoteViolations(List<int> order, int[,] votes)
        {
            var cost = 0;
            for (var a = 0; a < order.Count; a++)
            {
                for (var b = a + 1; b < order.Count; b++)
                {
                    cost += votes[order[b], order[a]];
                }
            }

            return cost;
        }

        private static int Find(int[] parents, int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }

            return index;
        }

        private static void Union(int[] parents, int a, int b)
        {
            var rootA = Find(parents, a);
            var rootB = Find(parents, b);
            if (rootA != rootB)
            {
                parents[rootA] = rootB;
            }
        }

        /// <summary>
        /// Lane search order starting at the preferred lane and expanding outward,
        /// clamped to the tray capacity range.
        /// </summary>
        private static IEnumerable<int> BuildSearchOrder(int preferred, int maxStep)
        {
            if (maxStep < 0)
            {
                yield break;
            }

            var lo = -maxStep;
            var hi = maxStep;
            if (preferred >= lo && preferred <= hi)
            {
                yield return preferred;
            }

            for (var d = 1; preferred - d >= lo || preferred + d <= hi; d++)
            {
                var up = preferred + d;
                if (up >= lo && up <= hi)
                {
                    yield return up;
                }

                var down = preferred - d;
                if (down >= lo && down <= hi)
                {
                    yield return down;
                }
            }
        }

        /// <summary>
        /// Builds the second cross-section axis continuously through the route.
        /// A horizontal tray is the reliable anchor: its open side is global up.
        /// On vertical trays and fittings the axis is transported by the same
        /// rotation as the route direction, so it cannot swap with tray width.
        /// </summary>
        private static XYZ[] BuildContinuousLiftVectors(
            IReadOnlyList<ConduitRunSegment> path,
            XYZ[] sides)
        {
            var lifts = new XYZ[path.Count];
            var directions = new XYZ[path.Count];
            for (var i = 0; i < path.Count; i++)
            {
                var vector = path[i].End - path[i].Start;
                if (vector.GetLength() >= MinimumSegmentLength)
                {
                    directions[i] = vector.Normalize();
                }
            }

            var anchor = -1;
            for (var i = 0; i < path.Count; i++)
            {
                if (directions[i] != null && Math.Abs(directions[i].Z) < 0.05)
                {
                    anchor = i;
                    break;
                }
            }

            if (anchor < 0)
            {
                for (var i = 0; i < path.Count; i++)
                {
                    if (directions[i] != null && path[i].CrossSectionUp != null)
                    {
                        anchor = i;
                        break;
                    }
                }
            }

            if (anchor < 0)
            {
                anchor = Array.FindIndex(directions, direction => direction != null);
            }

            if (anchor < 0)
            {
                return lifts;
            }

            lifts[anchor] = ResolveLiftVector(
                path[anchor], directions[anchor], sides[anchor], null);

            for (var i = anchor + 1; i < path.Count; i++)
            {
                var expected = TransportSide(lifts[i - 1], directions[i - 1], directions[i]);
                lifts[i] = ResolveLiftVector(path[i], directions[i], sides[i], expected);
            }

            for (var i = anchor - 1; i >= 0; i--)
            {
                var expected = TransportSide(lifts[i + 1], directions[i + 1], directions[i]);
                lifts[i] = ResolveLiftVector(path[i], directions[i], sides[i], expected);
            }

            return lifts;
        }

        private static XYZ ResolveLiftVector(
            ConduitRunSegment segment,
            XYZ direction,
            XYZ side,
            XYZ expected)
        {
            if (direction == null)
            {
                return expected;
            }

            // For a horizontal run global Z is unambiguous. For every other run
            // the connector axis is only a hint because rectangular connector
            // families do not consistently map width to BasisX/BasisY.
            XYZ candidate = null;
            if (Math.Abs(direction.Z) < 0.05)
            {
                candidate = OrthogonalizeSide(XYZ.BasisZ, direction);
            }

            candidate = candidate ?? OrthogonalizeSide(segment.CrossSectionUp!, direction);
            if (expected != null)
            {
                if (candidate == null)
                {
                    return expected;
                }

                var alignment = expected.DotProduct(candidate);
                return Math.Abs(alignment) > SideAlignmentThreshold
                    ? (alignment >= 0 ? candidate : candidate.Negate())
                    : expected;
            }

            if (candidate == null && side != null)
            {
                candidate = OrthogonalizeSide(direction.CrossProduct(side), direction);
            }

            if (candidate == null)
            {
                candidate = OrthogonalizeSide(XYZ.BasisX, direction) ??
                            OrthogonalizeSide(XYZ.BasisY, direction);
            }

            if (candidate == null)
            {
                return null;
            }

            if (Math.Abs(candidate.Z) > 0.5)
            {
                return candidate.Z >= 0 ? candidate : candidate.Negate();
            }

            return StabilizeSign(candidate);
        }
        /// <summary>
        /// Wyznacza wektor boczny toru dla każdego odcinka trasy. Oś jest
        /// przenoszona wzdłuż trasy jak pas na jezdni: na każdym załamaniu
        /// poprzednia oś zostaje obrócona dokładnie tym obrotem, który zmienia
        /// kierunek biegu, więc tor okrąża narożnik i wchodzi w pion bez zmiany
        /// strony korytka i bez zamiany kolejności torów.
        ///
        /// Przeniesiona oś służy zarówno jako wynik — gdy element nie podaje
        /// własnego przekroju — jak i jako odniesienie dla zwrotu osi odczytanej
        /// z korytka. Sam iloczyn skalarny z poprzednim odcinkiem nie wystarcza:
        /// na załamaniu 90° obie osie są prostopadłe i porównanie nie niesie
        /// żadnej informacji, przez co tor przeskakiwał na drugą stronę korytka.
        /// </summary>
        private static XYZ[] BuildContinuousSideVectors(IReadOnlyList<ConduitRunSegment> path)
        {
            var sides = new XYZ[path.Count];
            var directions = new XYZ[path.Count];
            for (var i = 0; i < path.Count; i++)
            {
                var vector = path[i].End - path[i].Start;
                if (vector.GetLength() >= MinimumSegmentLength)
                {
                    directions[i] = vector.Normalize();
                }
            }

            // A straight horizontal tray gives an unambiguous width axis. Start
            // there even when the logical route begins on a vertical tray, then
            // transport the frame in both directions through every fitting.
            var anchor = -1;
            for (var i = 0; i < path.Count; i++)
            {
                if (path[i].Kind == ConduitSegmentKind.TrayRun &&
                    directions[i] != null &&
                    Math.Abs(directions[i].Z) < 0.05 &&
                    path[i].CrossSectionSide != null)
                {
                    anchor = i;
                    break;
                }
            }

            if (anchor < 0)
            {
                anchor = Array.FindIndex(path.ToArray(), segment => segment.CrossSectionSide != null);
            }

            if (anchor < 0)
            {
                anchor = Array.FindIndex(directions, direction => direction != null);
            }

            if (anchor < 0)
            {
                return sides;
            }

            sides[anchor] = ResolveSideVector(path[anchor], directions[anchor], null);
            for (var i = anchor + 1; i < path.Count; i++)
            {
                var expected = TransportSide(sides[i - 1], directions[i - 1], directions[i]);
                sides[i] = ResolveSideVector(path[i], directions[i], expected);
            }

            for (var i = anchor - 1; i >= 0; i--)
            {
                var expected = TransportSide(sides[i + 1], directions[i + 1], directions[i]);
                sides[i] = ResolveSideVector(path[i], directions[i], expected);
            }

            return sides;
        }

        private static XYZ ResolveSideVector(
            ConduitRunSegment segment,
            XYZ direction,
            XYZ expected)
        {
            if (direction == null)
            {
                return expected ?? XYZ.BasisY;
            }

            var candidate = OrthogonalizeSide(segment.CrossSectionSide, direction);
            if (expected == null)
            {
                return StabilizeSign(candidate ?? GetSideVector(direction));
            }

            if (candidate == null)
            {
                return expected;
            }

            var alignment = expected.DotProduct(candidate);
            return Math.Abs(alignment) > SideAlignmentThreshold
                ? (alignment >= 0 ? candidate : candidate.Negate())
                : expected;
        }
        /// <summary>
        /// Przenosi oś boczną z poprzedniego odcinka na bieżący, obracając ją
        /// obrotem prowadzącym poprzedni kierunek biegu na bieżący. Na odcinku
        /// prostym zwraca oś bez zmian, na załamaniu — oś obróconą razem z
        /// trasą. Null oznacza, że nie ma czego przenosić.
        /// </summary>
        private static XYZ TransportSide(XYZ previousSide, XYZ previousDirection, XYZ direction)
        {
            if (previousSide == null || previousDirection == null || direction == null)
            {
                return null;
            }

            var axis = previousDirection.CrossProduct(direction);
            if (axis.GetLength() < 1e-9)
            {
                // Bieg bez zmiany kierunku (albo zawrócenie, gdzie obrót jest
                // nieokreślony) — oś przechodzi dalej bez obrotu.
                return OrthogonalizeSide(previousSide, direction) ?? previousSide;
            }

            axis = axis.Normalize();
            var cos = Math.Max(-1.0, Math.Min(1.0, previousDirection.DotProduct(direction)));
            var sin = Math.Sqrt(Math.Max(0, 1 - cos * cos));

            // Wzór Rodriguesa: obrót wektora wokół osi o kąt między kierunkami.
            var rotated = previousSide.Multiply(cos) +
                          axis.CrossProduct(previousSide).Multiply(sin) +
                          axis.Multiply(axis.DotProduct(previousSide) * (1 - cos));
            return OrthogonalizeSide(rotated, direction) ?? previousSide;
        }

        private static ConduitRunSegment ShiftSegmentAlongSide(
            ConduitRunSegment segment,
            XYZ side,
            double distance,
            XYZ lift,
            double supportOffset)
        {
            var sideShift = side == null ? XYZ.Zero : side.Multiply(distance);
            var supportShift = lift == null ? XYZ.Zero : lift.Multiply(supportOffset);

            // Both offsets taper together at a device connector. This preserves
            // one shared junction at the tray and still reaches the device axis.
            var startShift = (sideShift + supportShift).Multiply(segment.StartLaneFactor);
            var endShift = (sideShift + supportShift).Multiply(segment.EndLaneFactor);
            return segment.WithGeometry(segment.Start + startShift, segment.End + endShift);
        }

        private static bool AxesConflict(
            XYZ candidateStart,
            XYZ candidateEnd,
            double candidateRadiusFeet,
            ConduitOccupiedAxis occupied,
            double spacingFeet)
        {
            var candidateVector = candidateEnd - candidateStart;
            var candidateLength = candidateVector.GetLength();
            if (candidateLength < MinimumSegmentLength)
            {
                return false;
            }

            var occupiedVector = occupied.End - occupied.Start;
            var occupiedLength = occupiedVector.GetLength();
            if (occupiedLength < MinimumSegmentLength)
            {
                return false;
            }

            var candidateDirection = candidateVector.Normalize();
            var occupiedDirection = occupiedVector.Normalize();

            // Only parallel axes share a lane; crossing axes at fittings are kept
            // apart by the stable lane index, not by this test.
            if (Math.Abs(candidateDirection.DotProduct(occupiedDirection)) < ParallelDotThreshold)
            {
                return false;
            }

            var requiredDistance = candidateRadiusFeet + occupied.DiameterFeet / 2.0 + Math.Max(0, spacingFeet);
            if (DistanceToLine(occupied.Start, candidateStart, candidateDirection) >= requiredDistance - 1e-9)
            {
                return false;
            }

            var occupied0 = (occupied.Start - candidateStart).DotProduct(candidateDirection);
            var occupied1 = (occupied.End - candidateStart).DotProduct(candidateDirection);
            var occupiedMin = Math.Min(occupied0, occupied1);
            var occupiedMax = Math.Max(occupied0, occupied1);
            var overlap = Math.Min(candidateLength, occupiedMax) - Math.Max(0, occupiedMin);
            return overlap > MinimumSegmentLength;
        }

        private static string BuildHeightCapacitySkipReason(
            ElementId elementId,
            double heightMm,
            double diameterMm,
            double marginMm)
        {
            var required = ConduitLaneMath.GetRequiredTrayHeightMm(diameterMm, marginMm);
            return string.Format(
                CultureInfo.CurrentCulture,
                "Pominięto: korytko {0} ma wysokość {1:F0} mm, a conduit z wymaganym luzem potrzebuje {2:F0} mm.",
                DescribeElement(elementId),
                heightMm,
                required);
        }
        private static string BuildCapacitySkipReason(ElementId elementId, double widthMm, double diameterMm, double marginMm)
        {
            var required = diameterMm + 2 * Math.Max(0, marginMm);
            return string.Format(
                CultureInfo.CurrentCulture,
                "Pominięto: korytko {0} ma szerokość {1:F0} mm, a wymagany przekrój z marginesami to {2:F0} mm.",
                DescribeElement(elementId),
                widthMm,
                required);
        }

        /// <summary>
        /// Komunikat musi rozróżniać brak miejsca w przekroju od zajętości przez
        /// inny conduit — inaczej projektant szuka wąskiego korytka tam, gdzie
        /// naprawdę stoi już czyjaś trasa.
        /// </summary>
        private static string BuildNoFreeLaneSkipReason(
            ElementId elementId,
            double widthMm,
            double diameterMm,
            double spacingMm,
            double marginMm,
            int testedLanes,
            LaneBlockerKind blocker)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "Pominięto: brak wolnego toru w korytku {0} (szerokość {1:F0} mm) dla conduitu Ø{2:F0} mm, " +
                "odstęp {3:F0} mm, margines {4:F0} mm. Sprawdzono torów: {5}, przeszkoda: {6}.",
                DescribeElement(elementId),
                widthMm,
                diameterMm,
                spacingMm,
                marginMm,
                testedLanes,
                DescribeBlocker(blocker));
        }

        private static string DescribeBlocker(LaneBlockerKind blocker)
        {
            switch (blocker)
            {
                case LaneBlockerKind.ExistingConduit:
                    return "conduit już istniejący w modelu";
                case LaneBlockerKind.Reservation:
                    return "inna trasa z tej samej operacji";
                case LaneBlockerKind.Capacity:
                    return "przekrój korytka";
                default:
                    return "nieokreślona";
            }
        }

        private static string DescribeElement(ElementId elementId)
        {
            return elementId == null || elementId == ElementId.InvalidElementId
                ? "?"
                : elementId.IntegerValue.ToString(CultureInfo.CurrentCulture);
        }

        private double GetExistingConduitDiameterFeet(Conduit conduit)
        {
            var outer = GetDoubleParameter(conduit, BuiltInParameter.RBS_CONDUIT_OUTER_DIAM_PARAM);
            if (outer > 0)
            {
                return outer;
            }

            var nominal = GetDoubleParameter(conduit, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
            return nominal > 0 ? nominal : DefaultExistingDiameterFeet;
        }

        private static double GetDoubleParameter(Element element, BuiltInParameter builtInParameter)
        {
            var parameter = element.get_Parameter(builtInParameter);
            return parameter?.AsDouble() ?? 0;
        }

        // ----------------------------------------------------------------------
        // Legacy overlap-avoidance allocation used by the "fill tray network"
        // feature. It shifts whole conduit groups away from existing conduits and
        // is intentionally left untouched by the lane-based bulk allocation.
        // ----------------------------------------------------------------------

        public List<ConduitRunSegment> Allocate(IEnumerable<ConduitRunSegment> segments, ConduitRoutingSettings settings, ConduitRoutingReport report)
        {
            var segmentList = segments.ToList();
            if (segmentList.Count == 0)
            {
                return segmentList;
            }

            var existingConduits = GetExistingConduitAxes().ToList();
            if (existingConduits.Count == 0)
            {
                return segmentList;
            }

            var tolerance = GetExistingConduitMatchTolerance(settings);
            var occupiedAxes = existingConduits.ToList();
            var freeSegments = new List<ConduitRunSegment>();
            var skipped = 0;
            var shifted = 0;

            foreach (var segmentGroup in BuildConduitAllocationGroups(segmentList))
            {
                var groupSegments = segmentGroup.ToList();
                if (!TryFindFreeSegments(groupSegments, occupiedAxes, tolerance, settings, out var acceptedSegments, out var wasShifted))
                {
                    skipped += groupSegments.Count;
                    continue;
                }

                if (wasShifted)
                {
                    shifted += acceptedSegments.Count;
                }

                freeSegments.AddRange(acceptedSegments);
                occupiedAxes.AddRange(acceptedSegments.Select(segment => new ExistingConduitAxis(ElementId.InvalidElementId, segment.Start, segment.End)));
            }

            if (skipped > 0)
            {
                report.SkippedExistingConduits += skipped;
                report.Warnings.Add($"Pominięto {skipped} odcinków, dla których nie znaleziono wolnej pozycji conduitu.");
            }

            if (shifted > 0)
            {
                report.Warnings.Add($"Przesunięto {shifted} odcinków na najbliższą wolną pozycję obok zajętych conduitów.");
            }

            return freeSegments;
        }

        private static IEnumerable<List<ConduitRunSegment>> BuildConduitAllocationGroups(IList<ConduitRunSegment> segments)
        {
            return segments
                .GroupBy(segment => segment.LayoutIndex)
                .OrderBy(group => group.Key)
                .Select(group => group.ToList());
        }

        private static bool TryFindFreeSegments(IList<ConduitRunSegment> segments, IEnumerable<ExistingConduitAxis> occupiedAxes, double tolerance, ConduitRoutingSettings settings, out List<ConduitRunSegment> freeSegments, out bool wasShifted)
        {
            freeSegments = null;
            wasShifted = false;

            if (!segments.Any(segment => IsSegmentOccupiedByExistingConduit(segment, occupiedAxes, tolerance)))
            {
                freeSegments = segments.ToList();
                return true;
            }

            var pitch = Math.Max(settings.DiameterMm + settings.SpacingMm, settings.DiameterMm) * MmToFeet;
            if (pitch <= 0)
            {
                return false;
            }

            foreach (var step in BuildSideShiftSteps())
            {
                var candidateSegments = segments
                    .Select(segment => ShiftSegmentSideways(segment, step * pitch))
                    .ToList();

                if (candidateSegments.Any(segment => IsSegmentOccupiedByExistingConduit(segment, occupiedAxes, tolerance)))
                {
                    continue;
                }

                freeSegments = candidateSegments;
                wasShifted = true;
                return true;
            }

            return false;
        }

        private static IEnumerable<int> BuildSideShiftSteps()
        {
            for (var step = 1; step <= 12; step++)
            {
                yield return step;
                yield return -step;
            }
        }

        private static ConduitRunSegment ShiftSegmentSideways(ConduitRunSegment segment, double distance)
        {
            if (Math.Abs(distance) < 1e-9)
            {
                return segment.WithGeometry(segment.Start, segment.End);
            }

            var vector = segment.End - segment.Start;
            if (vector.GetLength() < MinimumSegmentLength)
            {
                return segment.WithGeometry(segment.Start, segment.End);
            }

            var direction = vector.Normalize();
            var side = GetSideVector(direction);
            var shift = side.Multiply(distance);
            return segment.WithGeometry(segment.Start + shift, segment.End + shift);
        }

        private IEnumerable<ExistingConduitAxis> GetExistingConduitAxes()
        {
            foreach (var conduit in new FilteredElementCollector(_doc).OfClass(typeof(Conduit)).Cast<Conduit>())
            {
                var locationCurve = conduit.Location as LocationCurve;
                var line = locationCurve?.Curve as Line;
                if (line == null)
                {
                    continue;
                }

                yield return new ExistingConduitAxis(conduit.Id, line.GetEndPoint(0), line.GetEndPoint(1));
            }
        }

        private static double GetExistingConduitMatchTolerance(ConduitRoutingSettings settings)
        {
            var toleranceMm = Math.Max(ExistingConduitMinimumToleranceMm, Math.Min(ExistingConduitMaximumToleranceMm, settings.DiameterMm * 0.4));
            return toleranceMm * MmToFeet;
        }

        private static bool IsSegmentOccupiedByExistingConduit(ConduitRunSegment segment, IEnumerable<ExistingConduitAxis> existingConduits, double tolerance)
        {
            var segmentVector = segment.End - segment.Start;
            var segmentLength = segmentVector.GetLength();
            if (segmentLength < MinimumSegmentLength)
            {
                return false;
            }

            var segmentDirection = segmentVector.Normalize();
            foreach (var existing in existingConduits)
            {
                if (IsSegmentOccupiedByExistingConduit(segment.Start, segment.End, segmentDirection, segmentLength, existing, tolerance))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSegmentOccupiedByExistingConduit(XYZ segmentStart, XYZ segmentEnd, XYZ segmentDirection, double segmentLength, ExistingConduitAxis existing, double tolerance)
        {
            var existingVector = existing.End - existing.Start;
            var existingLength = existingVector.GetLength();
            if (existingLength < MinimumSegmentLength)
            {
                return false;
            }

            var existingDirection = existingVector.Normalize();
            if (Math.Abs(segmentDirection.DotProduct(existingDirection)) < ParallelDotThreshold)
            {
                return false;
            }

            if (DistanceToLine(segmentStart, existing.Start, existingDirection) > tolerance ||
                DistanceToLine(segmentEnd, existing.Start, existingDirection) > tolerance)
            {
                return false;
            }

            var existing0 = (existing.Start - segmentStart).DotProduct(segmentDirection);
            var existing1 = (existing.End - segmentStart).DotProduct(segmentDirection);
            var existingMin = Math.Min(existing0, existing1);
            var existingMax = Math.Max(existing0, existing1);
            var overlap = Math.Min(segmentLength, existingMax) - Math.Max(0, existingMin);
            var requiredOverlap = Math.Max(MinimumSegmentLength, Math.Min(segmentLength, existingLength) * 0.5);

            return overlap >= requiredOverlap;
        }

        private static double DistanceToLine(XYZ point, XYZ linePoint, XYZ lineDirection)
        {
            return (point - linePoint).CrossProduct(lineDirection).GetLength();
        }

        private static XYZ GetSideVector(XYZ direction)
        {
            var side = XYZ.BasisZ.CrossProduct(direction);
            if (side.GetLength() < 1e-9)
            {
                side = XYZ.BasisX.CrossProduct(direction);
            }

            return StabilizeSign(side.Normalize());
        }

        /// <summary>
        /// Sprowadza oś przekroju odczytaną z elementu do kierunku prostopadłego
        /// do biegu odcinka. Zwraca null, gdy oś nic nie wnosi — pokrywa się z
        /// biegiem albo nie została odczytana — i kierunek trzeba wyprowadzić
        /// geometrycznie.
        /// </summary>
        private static XYZ OrthogonalizeSide(XYZ declared, XYZ direction)
        {
            if (declared == null || declared.GetLength() < 1e-9)
            {
                return null;
            }

            var side = declared.Normalize();
            var along = side.DotProduct(direction);
            var perpendicular = side - direction.Multiply(along);
            return perpendicular.GetLength() < 1e-6 ? null : perpendicular.Normalize();
        }

        /// <summary>
        /// Ustala zwrot wektora niezależnie od tego, z której strony trasa
        /// wchodzi w element. Dzięki temu dwie trasy biegnące przeciwnie
        /// numerują tory tak samo.
        /// </summary>
        private static XYZ StabilizeSign(XYZ vector)
        {
            if (vector.X < -1e-9 ||
                (Math.Abs(vector.X) <= 1e-9 && vector.Y < -1e-9) ||
                (Math.Abs(vector.X) <= 1e-9 && Math.Abs(vector.Y) <= 1e-9 && vector.Z < 0))
            {
                return vector.Negate();
            }

            return vector;
        }

        private enum LaneBlockerKind
        {
            None,
            Capacity,
            ExistingConduit,
            Reservation
        }

        private class ExistingConduitAxis
        {
            public ExistingConduitAxis(ElementId id, XYZ start, XYZ end)
            {
                Id = id;
                Start = start;
                End = end;
            }

            public ElementId Id { get; }

            public XYZ Start { get; }

            public XYZ End { get; }
        }
    }

    /// <summary>
    /// Result of assigning a lane to one relation. On success it carries the
    /// shifted axes and reporting data; on failure it carries a skip reason.
    /// </summary>
    public sealed class ConduitLaneAllocation
    {
        public bool Success { get; set; }

        public int LaneIndex { get; set; }

        public List<ConduitRunSegment> Segments { get; set; } = new List<ConduitRunSegment>();

        /// <summary>Closest approach to a tray edge along the path (0..~1).</summary>
        public double MaxTrayFillRatio { get; set; }

        /// <summary>The center lane was blocked by a conduit already in the model.</summary>
        public bool CenterBlockedByModel { get; set; }

        /// <summary>The center lane was blocked by another relation in this batch.</summary>
        public bool CenterBlockedByPlan { get; set; }

        public string SkipReason { get; set; } = string.Empty;
    }
}
