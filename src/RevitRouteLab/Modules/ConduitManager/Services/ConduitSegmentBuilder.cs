using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using INP_IE.ConduitRouting.Models;

namespace INP_IE.ConduitManager.Services
{
    public class ConduitSegmentBuilder
    {
        private const double MmToFeet = 1.0 / 304.8;
        private const double MinimumSegmentLength = 1.0 / 12.0;
        private const double FittingCornerTrimRatio = 0.5;
        private const double FittingCornerSkewToleranceFeet = 0.5;

        private readonly Document _doc;

        public ConduitSegmentBuilder(Document doc)
        {
            _doc = doc;
        }

        public IEnumerable<ConduitRunSegment> BuildTraySegments(CableTray tray, ConduitRoutingSettings settings, ConduitRoutingReport report)
        {
            var locationCurve = tray.Location as LocationCurve;
            var line = locationCurve?.Curve as Line;
            if (line == null)
            {
                report.Warnings.Add($"Korytko {tray.Id.IntegerValue}: pominiÄ™to, bo nie jest prostym odcinkiem.");
                yield break;
            }

            var width = GetDoubleParameter(tray, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            var height = GetDoubleParameter(tray, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
            if (width <= 0 || height <= 0)
            {
                report.Warnings.Add($"Korytko {tray.Id.IntegerValue}: brak poprawnej szerokoĹ›ci/wysokoĹ›ci.");
                yield break;
            }

            var start = line.GetEndPoint(0);
            var end = line.GetEndPoint(1);
            foreach (var segment in BuildTraySegments(tray, start, end, settings, report))
            {
                yield return segment;
            }
        }

        public IEnumerable<ConduitRunSegment> BuildFittingSegments(Element fitting, XYZ start, XYZ end, ConduitRoutingSettings settings, ConduitRoutingReport report)
        {
            var connectors = MepConnectorReader.GetConnectors(fitting).ToList();
            if (connectors.Count < 2)
            {
                report.Warnings.Add($"KsztaĹ‚tka {fitting.Id.IntegerValue}: wykryto {connectors.Count} konektorĂłw.");
                yield break;
            }

            var connector0 = GetNearestConnector(connectors, start);
            var connector1 = GetNearestConnector(connectors, end);
            if (connector0 == null || connector1 == null || connector0.Origin.DistanceTo(connector1.Origin) < 1e-9)
            {
                yield break;
            }

            var width = GetDoubleParameter(fitting, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            var height = GetDoubleParameter(fitting, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
            if (width <= 0 || height <= 0)
            {
                var connectedTray = FindConnectedTray(new[] { connector0, connector1 });
                if (connectedTray != null)
                {
                    width = GetDoubleParameter(connectedTray, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                    height = GetDoubleParameter(connectedTray, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                }
            }

            if (width <= 0 || height <= 0)
            {
                report.Warnings.Add($"KsztaĹ‚tka {fitting.Id.IntegerValue}: brak wymiarĂłw przekroju.");
                yield break;
            }

            var levelId = GetNearestLevelId(start.Z);
            var path = BuildFittingPath(connector0, connector1);

            for (var pathIndex = 0; pathIndex < path.Count; pathIndex++)
            {
                var pathStart = path[pathIndex].Item1;
                var pathEnd = path[pathIndex].Item2;
                if (pathStart.DistanceTo(pathEnd) < MinimumSegmentLength)
                {
                    continue;
                }

                var direction = (pathEnd - pathStart).Normalize();
                var hasDeclaredCrossSection =
                    TryGetCrossSectionAxes(fitting, pathStart, out var side, out var up);
                if (!hasDeclaredCrossSection)
                {
                    side = GetSideVector(direction);
                    up = GetUpVector(direction, side);
                }

                var offsets = BuildOffsets(width, height, side, up, settings);

                for (var i = 0; i < offsets.Count; i++)
                {
                    var offset = offsets[i];
                    yield return new ConduitRunSegment(
                        pathStart + offset,
                        pathEnd + offset,
                        fitting.Id,
                        i,
                        levelId,
                        ConduitSegmentKind.FittingRun,
                        fitting.Id)
                    {
                        CrossSectionSide = hasDeclaredCrossSection ? side : null,
                        CrossSectionUp = hasDeclaredCrossSection ? up : null
                    };
                }
            }
        }

        public IEnumerable<ConduitRunSegment> BuildTraySegments(CableTray tray, XYZ start, XYZ end, ConduitRoutingSettings settings, ConduitRoutingReport report)
        {
            if (start.DistanceTo(end) < MinimumSegmentLength)
            {
                yield break;
            }

            var width = GetDoubleParameter(tray, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            var height = GetDoubleParameter(tray, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
            if (width <= 0 || height <= 0)
            {
                report.Warnings.Add($"Korytko {tray.Id.IntegerValue}: brak poprawnej szerokoĹ›ci/wysokoĹ›ci.");
                yield break;
            }

            var direction = (end - start).Normalize();

            // Przekrój odczytujemy z konektora korytka, a nie z kierunku biegu.
            // Na korytku pionowym iloczyn wektorowy z osią Z jest zerowy, więc
            // wyprowadzenie geometryczne gubi obrót korytka i tory wychodzą
            // ułożone w poprzek przekroju.
            var hasDeclaredCrossSection =
                TryGetCrossSectionAxes(tray, start, out var side, out var up);
            if (!hasDeclaredCrossSection)
            {
                side = GetSideVector(direction);
                up = GetUpVector(direction, side);
            }

            var levelId = GetNearestLevelId(start.Z);

            var offsets = BuildOffsets(width, height, side, up, settings);
            for (var i = 0; i < offsets.Count; i++)
            {
                var offset = offsets[i];
                yield return new ConduitRunSegment(start + offset, end + offset, tray.Id, i, levelId)
                {
                    CrossSectionSide = hasDeclaredCrossSection ? side : null,
                    CrossSectionUp = hasDeclaredCrossSection ? up : null
                };
            }
        }

        /// <summary>
        /// Wyznacza osie przekroju elementu: <paramref name="side"/> wzdłuż
        /// szerokości, <paramref name="up"/> wzdłuż wysokości.
        ///
        /// Konektor daje dwie osie przekroju, ale która z nich jest szerokością,
        /// zależy od konwencji rodziny — dlatego wybór nie opiera się na
        /// kolejności BasisX/BasisY. Na biegu poziomym rozstrzyga geometria:
        /// szerokość korytka jest pozioma, wysokość pionowa. Na pionie obie osie
        /// są poziome, więc rozstrzygają wymiary konektora zestawione z
        /// parametrami korytka. Gdy i to nie rozstrzyga (przekrój kwadratowy),
        /// metoda zwraca false i oś zostaje przeniesiona z poprzedniego odcinka.
        /// </summary>
        private static bool TryGetCrossSectionAxes(Element element, XYZ referencePoint, out XYZ side, out XYZ up)
        {
            side = null;
            up = null;

            Connector nearest = null;
            var bestDistance = double.MaxValue;
            foreach (var connector in MepConnectorReader.GetConnectors(element))
            {
                var distance = referencePoint == null ? 0 : connector.Origin.DistanceTo(referencePoint);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = connector;
                }
            }

            if (nearest == null)
            {
                return false;
            }

            try
            {
                var coordinateSystem = nearest.CoordinateSystem;
                var axisX = coordinateSystem.BasisX;
                var axisY = coordinateSystem.BasisY;
                if (axisX == null || axisY == null ||
                    axisX.GetLength() < 1e-9 || axisY.GetLength() < 1e-9)
                {
                    return false;
                }

                axisX = axisX.Normalize();
                axisY = axisY.Normalize();

                var runIsVertical = Math.Abs(coordinateSystem.BasisZ.Normalize().Z) > 0.95;
                if (!runIsVertical)
                {
                    // Bieg poziomy: szerokością jest ta oś, która leży bliżej
                    // poziomu. To rozstrzygnięcie nie zależy od żadnej konwencji.
                    if (Math.Abs(axisX.Z) <= Math.Abs(axisY.Z))
                    {
                        side = axisX;
                        up = axisY;
                    }
                    else
                    {
                        side = axisY;
                        up = axisX;
                    }

                    if (up.Z < -1e-9)
                    {
                        up = up.Negate();
                    }

                    return true;
                }

                var trayWidth = GetDoubleParameter(element, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                var trayHeight = GetDoubleParameter(element, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                if (trayWidth <= 0 || trayHeight <= 0 || Math.Abs(trayWidth - trayHeight) < 1e-6)
                {
                    return false;
                }

                // Connector.Width mierzy przekrój wzdłuż BasisX, Height wzdłuż
                // BasisY — porównanie z parametrami korytka wskazuje, która oś
                // odpowiada szerokości.
                if (Math.Abs(nearest.Width - trayWidth) <= Math.Abs(nearest.Height - trayWidth))
                {
                    side = axisX;
                    up = axisY;
                }
                else
                {
                    side = axisY;
                    up = axisX;
                }

                return true;
            }
            catch
            {
                side = null;
                up = null;
                return false;
            }
        }

        public IEnumerable<ConduitRunSegment> BuildFittingSegments(Element fitting, ConduitRoutingSettings settings, ConduitRoutingReport report)
        {
            var connectors = MepConnectorReader.GetConnectors(fitting).ToList();
            if (connectors.Count != 2)
            {
                report.Warnings.Add($"KsztaĹ‚tka {fitting.Id.IntegerValue}: wykryto {connectors.Count} konektorĂłw. Na tym etapie obsĹ‚ugiwane sÄ… ksztaĹ‚tki 2-konektorowe.");
                yield break;
            }

            var width = GetDoubleParameter(fitting, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            var height = GetDoubleParameter(fitting, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
            if (width <= 0 || height <= 0)
            {
                var connectedTray = FindConnectedTray(connectors);
                if (connectedTray != null)
                {
                    width = GetDoubleParameter(connectedTray, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                    height = GetDoubleParameter(connectedTray, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                }
            }

            if (width <= 0 || height <= 0)
            {
                report.Warnings.Add($"KsztaĹ‚tka {fitting.Id.IntegerValue}: brak wymiarĂłw przekroju.");
                yield break;
            }

            var connector0 = connectors[0];
            var connector1 = connectors[1];
            var p0 = connector0.Origin;
            var p1 = connector1.Origin;
            if (p0.DistanceTo(p1) < MinimumSegmentLength)
            {
                yield break;
            }

            var levelId = GetNearestLevelId(p0.Z);
            var path = BuildFittingPath(connector0, connector1);

            for (var pathIndex = 0; pathIndex < path.Count; pathIndex++)
            {
                var pathStart = path[pathIndex].Item1;
                var pathEnd = path[pathIndex].Item2;
                var direction = (pathEnd - pathStart).Normalize();
                var hasDeclaredCrossSection =
                    TryGetCrossSectionAxes(fitting, pathStart, out var side, out var up);
                if (!hasDeclaredCrossSection)
                {
                    side = GetSideVector(direction);
                    up = GetUpVector(direction, side);
                }

                var offsets = BuildOffsets(width, height, side, up, settings);

                for (var i = 0; i < offsets.Count; i++)
                {
                    var offset = offsets[i];
                    yield return new ConduitRunSegment(
                        pathStart + offset,
                        pathEnd + offset,
                        fitting.Id,
                        i,
                        levelId,
                        ConduitSegmentKind.FittingRun,
                        fitting.Id)
                    {
                        CrossSectionSide = hasDeclaredCrossSection ? side : null,
                        CrossSectionUp = hasDeclaredCrossSection ? up : null
                    };
                }
            }
        }

        /// <summary>
        /// Buduje odcinki conduitu na mostku nad przerwą w trasie kablowej.
        /// Mostek biegnie w powietrzu, więc przekrój referencyjny pożyczany jest
        /// od węższego korytka po jednej ze stron przerwy — dzięki temu conduit
        /// wchodzi w kolejne korytko dokładnie w tym samym torze, w którym z
        /// poprzedniego wyszedł.
        /// </summary>
        public IEnumerable<ConduitRunSegment> BuildBridgeSegments(
            IReadOnlyList<XYZ> path,
            ElementId widthReferenceElementId,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report)
        {
            if (path == null || path.Count < 2)
            {
                yield break;
            }

            var reference = widthReferenceElementId != null && widthReferenceElementId != ElementId.InvalidElementId
                ? _doc.GetElement(widthReferenceElementId)
                : null;
            var width = reference != null ? GetDoubleParameter(reference, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM) : 0;
            var height = reference != null ? GetDoubleParameter(reference, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM) : 0;
            if (width <= 0 || height <= 0)
            {
                // Bez znanego przekroju mostek prowadzi pojedynczy conduit w osi.
                width = settings.DiameterMm * MmToFeet;
                height = width;
                report.Warnings.Add(
                    "Mostek nad przerwą nie ma referencyjnego przekroju korytka — poprowadzono pojedynczy conduit w osi.");
            }

            var levelId = GetNearestLevelId(path[0].Z);

            // Mostek dziedziczy oś przekroju po korytku referencyjnym, dzięki
            // czemu tory wchodzą w kolejne korytko w tym samym ułożeniu.
            XYZ referenceSide = null;
            XYZ referenceUp = null;
            if (reference != null)
            {
                TryGetCrossSectionAxes(reference, path[0], out referenceSide, out referenceUp);
            }

            for (var pathIndex = 0; pathIndex < path.Count - 1; pathIndex++)
            {
                var pathStart = path[pathIndex];
                var pathEnd = path[pathIndex + 1];
                if (pathStart.DistanceTo(pathEnd) < MinimumSegmentLength)
                {
                    continue;
                }

                var direction = (pathEnd - pathStart).Normalize();
                var side = referenceSide ?? GetSideVector(direction);
                var up = referenceUp ?? GetUpVector(direction, side);
                var offsets = BuildOffsets(width, height, side, up, settings);

                for (var i = 0; i < offsets.Count; i++)
                {
                    var offset = offsets[i];
                    yield return new ConduitRunSegment(
                        pathStart + offset,
                        pathEnd + offset,
                        widthReferenceElementId ?? ElementId.InvalidElementId,
                        i,
                        levelId,
                        ConduitSegmentKind.Bridge,
                        widthReferenceElementId ?? ElementId.InvalidElementId)
                    {
                        CrossSectionSide = referenceSide,
                        CrossSectionUp = referenceUp
                    };
                }
            }
        }

        private static List<XYZ> BuildOffsets(double trayWidth, double trayHeight, XYZ side, XYZ up, ConduitRoutingSettings settings)
        {
            var diameter = settings.DiameterMm * MmToFeet;
            var spacing = settings.SpacingMm * MmToFeet;
            var margin = settings.MarginMm * MmToFeet;
            var offsets = new List<XYZ>();

            if (settings.LayoutMode == ConduitLayoutMode.Single)
            {
                offsets.Add(XYZ.Zero);
                return offsets;
            }

            if (settings.LayoutMode == ConduitLayoutMode.FixedCount)
            {
                var count = Math.Max(1, settings.FixedCount);
                var step = diameter + spacing;
                var start = -((count - 1) * step) / 2.0;
                for (var i = 0; i < count; i++)
                {
                    offsets.Add(side.Multiply(start + i * step));
                }

                return offsets;
            }

            var availableWidth = Math.Max(0, trayWidth - 2 * margin);
            var availableHeight = Math.Max(0, trayHeight - 2 * margin);
            var pitch = diameter + spacing;
            var columns = Math.Max(1, (int)Math.Floor((availableWidth + spacing) / pitch));
            var rows = Math.Max(1, (int)Math.Floor((availableHeight + spacing) / pitch));
            var startX = -((columns - 1) * pitch) / 2.0;
            var startZ = -((rows - 1) * pitch) / 2.0;

            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    offsets.Add(side.Multiply(startX + column * pitch) + up.Multiply(startZ + row * pitch));
                }
            }

            return offsets;
        }

        private static List<Tuple<XYZ, XYZ>> BuildFittingPath(Connector connector0, Connector connector1)
        {
            var p0 = connector0.Origin;
            var p1 = connector1.Origin;
            var directPath = new List<Tuple<XYZ, XYZ>> { Tuple.Create(p0, p1) };
            var direction0 = GetConnectorDirection(connector0);
            var direction1 = GetConnectorDirection(connector1);

            if (Math.Abs(direction0.DotProduct(direction1)) > 0.98)
            {
                return directPath;
            }

            if (!ConduitBendPathBuilder.TryGetCorner(p0, direction0, p1, direction1, FittingCornerSkewToleranceFeet, out var corner))
            {
                return directPath;
            }

            if (corner.DistanceTo(p0) < MinimumSegmentLength || corner.DistanceTo(p1) < MinimumSegmentLength)
            {
                return directPath;
            }

            var cornerPath = new List<Tuple<XYZ, XYZ>>
            {
                Tuple.Create(p0, corner),
                Tuple.Create(corner, p1)
            };

            var distance0 = corner.DistanceTo(p0);
            var distance1 = corner.DistanceTo(p1);
            var trim = Math.Min(distance0, distance1) * FittingCornerTrimRatio;
            var tangent0 = corner + (p0 - corner).Normalize().Multiply(trim);
            var tangent1 = corner + (p1 - corner).Normalize().Multiply(trim);

            if (p0.DistanceTo(tangent0) < MinimumSegmentLength ||
                tangent0.DistanceTo(tangent1) < MinimumSegmentLength ||
                tangent1.DistanceTo(p1) < MinimumSegmentLength)
            {
                return cornerPath;
            }

            return new List<Tuple<XYZ, XYZ>>
            {
                Tuple.Create(p0, tangent0),
                Tuple.Create(tangent0, tangent1),
                Tuple.Create(tangent1, p1)
            };
        }

        private static XYZ GetSideVector(XYZ direction)
        {
            var side = XYZ.BasisZ.CrossProduct(direction);
            if (side.GetLength() < 1e-9)
            {
                side = XYZ.BasisX.CrossProduct(direction);
            }

            side = side.Normalize();

            // Stabilizuje znak wektora bocznego, aby trasy biegnÄ…ce przeciwnie
            // (start/end zamienione) miaĹ‚y zgodny kierunek przesuniÄ™cia conduitĂłw.
            if (side.X < -1e-9 ||
                (Math.Abs(side.X) <= 1e-9 && side.Y < -1e-9) ||
                (Math.Abs(side.X) <= 1e-9 && Math.Abs(side.Y) <= 1e-9 && side.Z < 0))
            {
                side = side.Negate();
            }

            return side;
        }

        private static XYZ GetUpVector(XYZ direction, XYZ side)
        {
            var up = direction.CrossProduct(side).Normalize();
            if (up.Z < -1e-9)
            {
                up = up.Negate();
            }

            return up;
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

        private static Connector GetNearestConnector(IEnumerable<Connector> connectors, XYZ point)
        {
            return connectors
                .OrderBy(connector => connector.Origin.DistanceTo(point))
                .FirstOrDefault();
        }

        private CableTray FindConnectedTray(IEnumerable<Connector> fittingConnectors)
        {
            foreach (var connector in fittingConnectors)
            {
                foreach (Connector reference in connector.AllRefs)
                {
                    if (reference.Owner is CableTray tray)
                    {
                        return tray;
                    }
                }
            }

            return null;
        }


        private static double GetDoubleParameter(Element element, BuiltInParameter builtInParameter)
        {
            var parameter = element.get_Parameter(builtInParameter);
            return parameter?.AsDouble() ?? 0;
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