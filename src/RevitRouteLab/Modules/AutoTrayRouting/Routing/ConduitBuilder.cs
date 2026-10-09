using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using INP_IE.AutoTrayRouting.Config;

namespace INP_IE.AutoTrayRouting.Routing
{
    /// <summary>
    /// Builds conduit routes along planned paths
    /// </summary>
    public class ConduitBuilder
    {
        private readonly Document _doc;

        public ConduitBuilder(Document doc)
        {
            _doc = doc;
        }

        public class ConduitBuildResult
        {
            public List<ElementId> NewElements { get; } = new List<ElementId>();
            public int Elbows { get; set; }
            public double TotalLength { get; set; }
        }

        /// <summary>
        /// Build conduit along the specified path
        /// </summary>
        /// <param name="path">Planned path segments</param>
        /// <param name="config">Conduit configuration</param>
        /// <returns>Build result with created elements</returns>
        public ConduitBuildResult Build(IList<Line> path, ConduitConfig config)
        {
            var result = new ConduitBuildResult();
            if (path == null || path.Count == 0) return result;

            System.Diagnostics.Debug.WriteLine($"[ConduitBuild] ========== Conduit Build Started with {path.Count} segments ==========");

            // Get or find conduit type
            var conduitTypeId = config.ConduitTypeId;
            if (conduitTypeId == null || conduitTypeId == ElementId.InvalidElementId)
            {
                conduitTypeId = GetDefaultConduitType();
                if (conduitTypeId == ElementId.InvalidElementId)
                {
                    System.Diagnostics.Debug.WriteLine("[ConduitBuild] ERROR: No conduit type found in project");
                    return result;
                }
            }

            // Get level for conduit placement (use first segment elevation)
            var firstPoint = path[0].GetEndPoint(0);
            var levelId = GetNearestLevel(firstPoint.Z);

            var createdConduits = new List<Conduit>();
            var joints = new List<XYZ>();

            // STEP 1: Create all conduit segments
            for (int i = 0; i < path.Count; i++)
            {
                var seg = path[i];
                var p0 = seg.GetEndPoint(0);
                var p1 = seg.GetEndPoint(1);

                System.Diagnostics.Debug.WriteLine($"[ConduitBuild] Segment {i}: ({p0.X:F3}, {p0.Y:F3}, {p0.Z:F3}) -> ({p1.X:F3}, {p1.Y:F3}, {p1.Z:F3}), len={seg.Length * 304.8:F1}mm");

                try
                {
                    var conduit = Conduit.Create(_doc, conduitTypeId, p0, p1, levelId);
                    if (conduit != null)
                    {
                        createdConduits.Add(conduit);
                        result.NewElements.Add(conduit.Id);
                        result.TotalLength += seg.Length;

                        System.Diagnostics.Debug.WriteLine($"[ConduitBuild]   Created conduit {conduit.Id}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ConduitBuild]   ERROR creating conduit: {ex.Message}");
                }

                // Store joint location between segments
                if (i < path.Count - 1)
                {
                    joints.Add(p1);
                }
            }

            if (createdConduits.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[ConduitBuild] No conduits created - aborting");
                return result;
            }

            // STEP 2: Regenerate to update connectors
            System.Diagnostics.Debug.WriteLine($"[ConduitBuild] Regenerating after creating {createdConduits.Count} conduits...");
            _doc.Regenerate();

            // STEP 3: Create elbows at joints
            for (int i = 0; i < joints.Count && i < createdConduits.Count - 1; i++)
            {
                var conduitA = createdConduits[i];
                var conduitB = createdConduits[i + 1];
                var joint = joints[i];

                System.Diagnostics.Debug.WriteLine($"[ConduitBuild] Connecting conduit {i} and {i + 1} at joint ({joint.X:F3}, {joint.Y:F3}, {joint.Z:F3})");

                if (TryCreateElbow(conduitA, conduitB, joint, out var elbowId))
                {
                    result.Elbows++;
                    result.NewElements.Add(elbowId);
                    System.Diagnostics.Debug.WriteLine($"[ConduitBuild]   Created elbow {elbowId}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[ConduitBuild]   Failed to create elbow - conduits remain disconnected");
                }
            }

            System.Diagnostics.Debug.WriteLine($"[ConduitBuild] ========== Conduit Build Completed: {createdConduits.Count} conduits, {result.Elbows} elbows ==========");
            return result;
        }

        private bool TryCreateElbow(Conduit conduitA, Conduit conduitB, XYZ joint, out ElementId elbowId)
        {
            elbowId = ElementId.InvalidElementId;

            try
            {
                // Get connectors near the joint
                var connectorA = GetNearestConnector(conduitA, joint);
                var connectorB = GetNearestConnector(conduitB, joint);

                if (connectorA == null || connectorB == null)
                {
                    System.Diagnostics.Debug.WriteLine("[ConduitBuild]   Missing connector (A or B null)");
                    return false;
                }

                double distA = connectorA.Origin.DistanceTo(joint);
                double distB = connectorB.Origin.DistanceTo(joint);
                System.Diagnostics.Debug.WriteLine($"[ConduitBuild]   Connector distances: A={distA * 304.8:F1}mm, B={distB * 304.8:F1}mm");

                // Check if connectors are close enough
                const double maxDist = 0.01; // ~3mm tolerance
                if (distA > maxDist || distB > maxDist)
                {
                    System.Diagnostics.Debug.WriteLine($"[ConduitBuild]   Connectors too far from joint (max {maxDist * 304.8:F1}mm)");
                    return false;
                }

                // Create elbow fitting
                var fitting = _doc.Create.NewElbowFitting(connectorA, connectorB);
                if (fitting != null)
                {
                    elbowId = fitting.Id;
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConduitBuild]   Elbow creation failed: {ex.Message}");
            }

            return false;
        }

        private Connector GetNearestConnector(Conduit conduit, XYZ point)
        {
            try
            {
                var cm = conduit.ConnectorManager;
                if (cm == null) return null;

                Connector nearest = null;
                double minDist = double.MaxValue;

                foreach (Connector c in cm.Connectors)
                {
                    double dist = c.Origin.DistanceTo(point);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearest = c;
                    }
                }

                return nearest;
            }
            catch
            {
                return null;
            }
        }

        private ElementId GetDefaultConduitType()
        {
            try
            {
                var conduitType = new FilteredElementCollector(_doc)
                    .OfClass(typeof(ConduitType))
                    .FirstElement();

                return conduitType?.Id ?? ElementId.InvalidElementId;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
        }

        private ElementId GetNearestLevel(double elevationZ)
        {
            try
            {
                var levels = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => Math.Abs(l.Elevation - elevationZ))
                    .ToList();

                return levels.FirstOrDefault()?.Id ?? ElementId.InvalidElementId;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
        }
    }
}
