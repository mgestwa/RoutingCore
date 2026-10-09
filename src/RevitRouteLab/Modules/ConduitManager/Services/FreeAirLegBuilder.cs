using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using INP_IE.ConduitRouting.Models;

namespace INP_IE.ConduitManager.Services
{
    /// <summary>
    /// Buduje zejście conduitu od korytka do urządzenia, przy którym nie ma
    /// trasy kablowej. Trasa jest ortogonalna: przejazd poziomy nad punkt
    /// urządzenia, pion w jego osi i krótki skos tuż przy urządzeniu, który
    /// kasuje przesunięcie toru.
    /// </summary>
    public class FreeAirLegBuilder
    {
        private const double MmToFeet = 1.0 / 304.8;
        private const double FeetToMm = 304.8;
        private const double MinimumSegmentLength = 1.0 / 12.0;
        private const double MinimumFittingTangentLengthMm = 100.0;
        private const double MinimumFittingTangentDiameterFactor = 4.0;

        private readonly Document _doc;

        public FreeAirLegBuilder(Document doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// Zwraca odcinki zejścia uporządkowane zgodnie z kierunkiem trasy.
        /// Gdy <paramref name="fromDevice"/> jest ustawione, odcinki biegną od
        /// urządzenia do korytka (trasa zaczyna się przy urządzeniu), w
        /// przeciwnym razie od korytka do urządzenia.
        ///
        /// Limit długości nie jest tu sprawdzany: relacja może dostać zejście na
        /// obu końcach, więc o dopuszczalności rozstrzyga dopiero suma po całej
        /// trasie, którą widzi planner.
        /// </summary>
        public List<ConduitRunSegment> BuildLeg(
            XYZ trayPoint,
            Element attachTray,
            XYZ devicePoint,
            bool fromDevice,
            ConduitRoutingSettings settings)
        {
            var segments = new List<ConduitRunSegment>();
            if (trayPoint == null || devicePoint == null || attachTray == null)
            {
                return segments;
            }

            var path = BuildPath(trayPoint, attachTray, devicePoint, settings);
            if (GetPathLengthMm(path) <= 0)
            {
                return segments;
            }

            var levelId = GetNearestLevelId(devicePoint.Z);

            // Ostatni odcinek przy urządzeniu kasuje offset toru. Przy trasie
            // biegnącej od urządzenia skos jest pierwszy i offset narasta.
            for (var i = 0; i < path.Count - 1; i++)
            {
                var start = path[i];
                var end = path[i + 1];
                if (start.DistanceTo(end) < MinimumSegmentLength)
                {
                    return new List<ConduitRunSegment>();
                }

                var isTaper = i == path.Count - 2;
                segments.Add(new ConduitRunSegment(
                    start,
                    end,
                    attachTray.Id,
                    0,
                    levelId,
                    ConduitSegmentKind.FreeAir,
                    attachTray.Id,
                    1.0,
                    isTaper ? 0.0 : 1.0));
            }

            if (segments.Count == 0 || !fromDevice)
            {
                return segments;
            }

            return segments
                .Select(segment => new ConduitRunSegment(
                    segment.End,
                    segment.Start,
                    segment.SourceElementId,
                    segment.LayoutIndex,
                    segment.LevelId,
                    segment.Kind,
                    segment.WidthReferenceElementId,
                    segment.EndLaneFactor,
                    segment.StartLaneFactor))
                .Reverse()
                .ToList();
        }

        /// <summary>
        /// Łamana od korytka do urządzenia: przejazd poziomy na wysokości
        /// korytka, pion w osi urządzenia, a na końcu krótki odcinek, na którym
        /// kasowany jest offset toru.
        /// </summary>
        private static List<XYZ> BuildPath(
            XYZ trayPoint,
            Element attachTray,
            XYZ devicePoint,
            ConduitRoutingSettings settings)
        {
            var path = new List<XYZ> { trayPoint };
            var minimumFittingTangentFeet = GetMinimumFittingTangentFeet(settings);
            var minimumBetweenBendsFeet = 2.0 * minimumFittingTangentFeet;

            // Najpierw wychodzimy poza dolną albo górną płaszczyznę poziomego
            // korytka. Długi odcinek poziomy nie biegnie dzięki temu przez jego
            // bryłę. Odcinek pomiędzy dwoma kolankami musi mieć miejsce na oba
            // fittingi, inaczej Revit utworzy conduit, ale nie połączy trasy.
            var breakoutPoint = GetBreakoutPoint(
                trayPoint,
                attachTray,
                devicePoint,
                settings,
                minimumBetweenBendsFeet);
            if (breakoutPoint.DistanceTo(trayPoint) >= MinimumSegmentLength)
            {
                path.Add(breakoutPoint);
            }

            var taperLengthFeet = Math.Max(0, settings.FreeAirTaperLengthMm) * MmToFeet;
            var minimumFinalLegFeet = Math.Max(taperLengthFeet, minimumFittingTangentFeet);
            var airPlanePoint = path[path.Count - 1];
            var aboveDevice = new XYZ(devicePoint.X, devicePoint.Y, airPlanePoint.Z);

            // Ostatnie zejście jest jednym odcinkiem zbieżnym do osi urządzenia.
            // Osobny krótki skos wymagałby dodatkowego fittingu pod małym kątem,
            // którego Revit często nie potrafi utworzyć. Punkt nad urządzeniem
            // dodajemy tylko wtedy, gdy końcowy odcinek zachowa żądaną długość.
            if (aboveDevice.DistanceTo(airPlanePoint) >= minimumBetweenBendsFeet &&
                aboveDevice.DistanceTo(devicePoint) >= minimumFinalLegFeet)
            {
                path.Add(aboveDevice);
            }

            path.Add(devicePoint);
            if (!IsConnectablePath(path, minimumFittingTangentFeet))
            {
                return new List<XYZ>();
            }

            return path;
        }

        private static XYZ GetBreakoutPoint(
            XYZ trayPoint,
            Element attachTray,
            XYZ devicePoint,
            ConduitRoutingSettings settings,
            double minimumBetweenBendsFeet)
        {
            var line = (attachTray.Location as LocationCurve)?.Curve as Line;
            if (line == null)
            {
                return trayPoint;
            }

            var direction = (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
            if (Math.Abs(direction.Z) > 0.95)
            {
                // Dla pionowego korytka kierunek wyjścia zależy od obrotu
                // przekroju. Nie zgadujemy go z osi Z.
                return trayPoint;
            }

            var height = attachTray
                .get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)?
                .AsDouble() ?? 0;
            if (height <= 0)
            {
                return trayPoint;
            }

            var radius = Math.Max(0, settings?.DiameterMm ?? 0) * 0.5 * MmToFeet;
            var verticalDelta = devicePoint.Z - trayPoint.Z;
            var sign = verticalDelta > 0 ? 1.0 : -1.0;
            var offset = Math.Max(height * 0.5 + radius, minimumBetweenBendsFeet);
            return trayPoint + XYZ.BasisZ.Multiply(sign * offset);
        }

        private static double GetMinimumFittingTangentFeet(ConduitRoutingSettings settings)
        {
            var diameterMm = Math.Max(0, settings?.DiameterMm ?? 0);
            var lengthMm = Math.Max(
                MinimumFittingTangentLengthMm,
                MinimumFittingTangentDiameterFactor * diameterMm);
            return lengthMm * MmToFeet;
        }

        private static bool IsConnectablePath(
            IReadOnlyList<XYZ> path,
            double minimumFittingTangentFeet)
        {
            if (path == null || path.Count < 2)
            {
                return false;
            }

            for (var i = 0; i < path.Count - 1; i++)
            {
                var requiredLength = i == path.Count - 2
                    ? minimumFittingTangentFeet
                    : 2.0 * minimumFittingTangentFeet;
                if (path[i].DistanceTo(path[i + 1]) + 1e-9 < requiredLength)
                {
                    return false;
                }
            }

            return true;
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

        private ElementId GetNearestLevelId(double elevation)
        {
            var level = new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation - elevation))
                .FirstOrDefault();

            return level?.Id ?? ElementId.InvalidElementId;
        }
    }
}
