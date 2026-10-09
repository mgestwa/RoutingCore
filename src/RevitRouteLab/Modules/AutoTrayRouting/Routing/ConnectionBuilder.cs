using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;

#nullable disable

namespace RevitRouteLab.AutoTrayRouting.Routing
{
    public class ConnectionBuilder
    {
        private readonly Document _doc;
        public ConnectionBuilder(Document doc) { _doc = doc; }

        public class BuildResult
        {
            public List<ElementId> NewElements { get; } = new List<ElementId>();
            public int Elbows { get; set; }
            public int Tees { get; set; }
            public double TotalLength { get; set; }
        }

        // Enum describing connection approach strategy
        private enum ConnectionApproach
        {
            DirectEnd,           // Direct connection to main end (ELBOW)
            PerpendicularTee,    // Perpendicular approach to main (TEE - currently works)
            ParallelTee,         // Parallel approach requiring dogleg (TEE - needs implementation)
            AngledComplex        // Complex angled approach (fallback)
        }

        private const double ShortTolFt = 1.0 / 384.0;
        private static bool _fittingsChecked = false;
        private static bool _hasFittings = true;

        // Adaptive thresholds influenced by tray dims set from caller
        public double TargetTrayWidthFt { get; set; }
        public double TargetTrayHeightFt { get; set; }
        public double MinLegFactor { get; set; } = 0.6; // used for post-fix cleanups
        public bool StrictFittingsOnly { get; set; } = true;

        // Hint from router: outward direction in XY from device back-face
    public XYZ PreferredOutwardDirXY { get; set; }

        // Store original endpoints before Revit modifies them (for elbow handedness decisions)
    private Dictionary<long, (XYZ p0, XYZ p1)> _originalEndpoints = new Dictionary<long, (XYZ p0, XYZ p1)>();

        private double CurrentMinLeg()
        {
            double dims = System.Math.Max(TargetTrayWidthFt, TargetTrayHeightFt);
            double byDims = MinLegFactor > 0 ? MinLegFactor * dims : 0.0;
            // Slightly larger than ShortTol to avoid short fragments around fittings
            double byTol = ShortTolFt * 4.0;
            return System.Math.Max(byDims, byTol);
        }

        public BuildResult Build(Element targetTray, IList<Line> path)
        {
            var result = new BuildResult();
            if (path == null || path.Count == 0) return result;

            System.Diagnostics.Debug.WriteLine($"[Build] ========== Build Started with {path.Count} segments ==========");

            EnsureFittingsAvailableOnce();

            // Determine dominant type and dimensions across connected network around target tray
            var targetMEPCurve = targetTray as MEPCurve;
            var dominant = DetermineDominantTypeAndDims(targetMEPCurve);
            var typeId = dominant.typeId != ElementId.InvalidElementId ? dominant.typeId : targetTray.GetTypeId();
            var levelId = targetTray.LevelId;
            var targetWidth = dominant.widthFt > 0 ? dominant.widthFt : TryGetParamFeet(targetTray, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            var targetHeight = dominant.heightFt > 0 ? dominant.heightFt : TryGetParamFeet(targetTray, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);

            // propagate target dims for adaptive thresholds
            TargetTrayWidthFt = targetWidth;
            TargetTrayHeightFt = targetHeight;

            // STEP 1: Create all trays WITHOUT regeneration (prevents Revit from modifying geometry)
            var createdTrays = new List<MEPCurve>();
            var joints = new List<XYZ>();

            for (int i = 0; i < path.Count; i++)
            {
                var seg = path[i];
                if (seg == null) continue;
                if (seg.Length < 2 * ShortTolFt) continue;

                System.Diagnostics.Debug.WriteLine($"[Build] Segment {i}: ({seg.GetEndPoint(0).X:F3}, {seg.GetEndPoint(0).Y:F3}, {seg.GetEndPoint(0).Z:F3}) -> ({seg.GetEndPoint(1).X:F3}, {seg.GetEndPoint(1).Y:F3}, {seg.GetEndPoint(1).Z:F3}), len={seg.Length * 304.8:F1}mm");

                // Detect if segment is vertical (Z changes significantly but X,Y stay same)
                var p0 = seg.GetEndPoint(0);
                var p1 = seg.GetEndPoint(1);
                double dZ = System.Math.Abs(p1.Z - p0.Z);
                double dXY = System.Math.Sqrt((p1.X - p0.X) * (p1.X - p0.X) + (p1.Y - p0.Y) * (p1.Y - p0.Y));
                bool isVertical = dZ > 0.01 && dXY < 0.01; // Vertical if Z changes but XY doesn't

                // For vertical segments, use level of LOWER point to prevent Revit from trimming
                ElementId segLevelId = levelId;
                if (isVertical)
                {
                    double lowerZ = System.Math.Min(p0.Z, p1.Z);
                    // Find level closest to lower Z but not above it
                    var levels = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                    foreach (var lv in levels)
                    {
                        if (lv.Elevation <= lowerZ)
                        {
                            segLevelId = lv.Id;
                        }
                        else break; // Stop at first level above
                    }
                    System.Diagnostics.Debug.WriteLine($"[Build]   Vertical segment: using level {_doc.GetElement(segLevelId)?.Name ?? "null"} at elevation {(_doc.GetElement(segLevelId) as Level)?.Elevation ?? 0:F3}");
                }

                var newTray = CableTray.Create(_doc, typeId, seg.GetEndPoint(0), seg.GetEndPoint(1), segLevelId);
                if (newTray != null)
                {
                    TrySetParamFeet(newTray, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, targetWidth);
                    TrySetParamFeet(newTray, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, targetHeight);

                    var locCurve = newTray.Location as LocationCurve;
                    if (locCurve?.Curve != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Build]   Created tray {newTray.Id}: ({locCurve.Curve.GetEndPoint(0).X:F3}, {locCurve.Curve.GetEndPoint(0).Y:F3}, {locCurve.Curve.GetEndPoint(0).Z:F3}) -> ({locCurve.Curve.GetEndPoint(1).X:F3}, {locCurve.Curve.GetEndPoint(1).Y:F3}, {locCurve.Curve.GetEndPoint(1).Z:F3})");
                    }

                    result.NewElements.Add(newTray.Id);
                    result.TotalLength += seg.Length;
                    createdTrays.Add(newTray as MEPCurve);
                    if (createdTrays.Count > 1)
                    {
                        joints.Add(seg.GetEndPoint(0)); // Joint between previous and current
                    }
                }
            }

            // STEP 2: Regenerate ONCE after all trays are created
            System.Diagnostics.Debug.WriteLine($"[Build] Regenerating after creating {createdTrays.Count} trays...");

            // Save original endpoints BEFORE regeneration (Revit may modify them!)
            var originalEndpoints = new List<(XYZ p0, XYZ p1)>();
            _originalEndpoints.Clear(); // Clear previous data
            for (int i = 0; i < createdTrays.Count; i++)
            {
                var saveLC = createdTrays[i].Location as LocationCurve;
                if (saveLC?.Curve != null)
                {
                    var endpoints = (saveLC.Curve.GetEndPoint(0), saveLC.Curve.GetEndPoint(1));
                    originalEndpoints.Add(endpoints);
                    _originalEndpoints[createdTrays[i].Id.Value] = endpoints; // Store by ElementId
                }
                else
                {
                    originalEndpoints.Add((null, null));
                }
            }

            _doc.Regenerate();

            // Force Revit to update LocationCurve by reading connectors (Revit may delay geometry updates)
            System.Diagnostics.Debug.WriteLine($"[Build] Forcing connector read to update LocationCurve...");
            for (int i = 0; i < createdTrays.Count; i++)
            {
                var cm = createdTrays[i].ConnectorManager;
                if (cm != null)
                {
                    foreach (Connector c in cm.Connectors)
                    {
                        var _ = c.Origin; // Force read
                    }
                }
            }

            // RESTORE original endpoints after regeneration if they changed
            bool anyRestored = false;
            for (int i = 0; i < createdTrays.Count; i++)
            {
                if (originalEndpoints[i].p0 == null) continue;

                var restoreLC = createdTrays[i].Location as LocationCurve;
                if (restoreLC?.Curve != null)
                {
                    var currentP0 = restoreLC.Curve.GetEndPoint(0);
                    var currentP1 = restoreLC.Curve.GetEndPoint(1);
                    var originalP0 = originalEndpoints[i].p0;
                    var originalP1 = originalEndpoints[i].p1;

                    // Check if Revit modified the endpoints
                    double dist0 = currentP0.DistanceTo(originalP0);
                    double dist1 = currentP1.DistanceTo(originalP1);

                    if (dist0 > 0.001 || dist1 > 0.001) // 0.3mm tolerance
                    {
                        System.Diagnostics.Debug.WriteLine($"[Build] Revit modified tray {createdTrays[i].Id}: restoring original geometry");
                        System.Diagnostics.Debug.WriteLine($"[Build]   Was: ({currentP0.X:F3}, {currentP0.Y:F3}, {currentP0.Z:F3}) -> ({currentP1.X:F3}, {currentP1.Y:F3}, {currentP1.Z:F3})");
                        System.Diagnostics.Debug.WriteLine($"[Build]   Now: ({originalP0.X:F3}, {originalP0.Y:F3}, {originalP0.Z:F3}) -> ({originalP1.X:F3}, {originalP1.Y:F3}, {originalP1.Z:F3})");

                        try
                        {
                            restoreLC.Curve = Line.CreateBound(originalP0, originalP1);
                            anyRestored = true;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Build]   FAILED to restore: {ex.Message}");
                        }
                    }
                }
            }

            // If we restored any geometry, regenerate again to apply changes
            if (anyRestored)
            {
                System.Diagnostics.Debug.WriteLine($"[Build] Regenerating after restore to apply geometry changes...");
                _doc.Regenerate();
            }

            // STEP 3: Now connect the trays with fittings
            for (int i = 1; i < createdTrays.Count; i++)
            {
                var prev = createdTrays[i - 1];
                var curr = createdTrays[i];
                var joint = joints[i - 1];

                System.Diagnostics.Debug.WriteLine($"[Build] Connecting tray {i - 1} and {i} at joint ({joint.X:F3}, {joint.Y:F3}, {joint.Z:F3})");

                bool elbowPlaced = false;
                if (_hasFittings)
                {
                    elbowPlaced = TryCreateElbowOrUnionAt(prev, curr, joint);

                    // If failed, try to gently nudge endpoints to coincide and retry
                    if (!elbowPlaced)
                    {
                        elbowPlaced = TryNudgeAndCreateElbowOrUnion(prev, curr, joint);
                    }
                }
                if (elbowPlaced) result.Elbows++;
                else TryConnectFallbackAt(prev, curr, joint);

                // Force regenerate after each fitting
                if (elbowPlaced)
                {
                    try { _doc.Regenerate(); } catch { }
                }
            }

            // RESTORE geometry after STEP 3 (Revit auto-regenerates after creating fittings)
            // Default: skip segments that have elbows on BOTH ends unless trim breaks the minimum leg
            System.Diagnostics.Debug.WriteLine($"[Build] Checking geometry after STEP 3 (after creating fittings)...");
            for (int i = 0; i < createdTrays.Count; i++)
            {
                if (originalEndpoints[i].p0 == null) continue;

                var checkLC = createdTrays[i].Location as LocationCurve;
                if (checkLC?.Curve != null)
                {
                    var currentP0 = checkLC.Curve.GetEndPoint(0);
                    var currentP1 = checkLC.Curve.GetEndPoint(1);
                    var originalP0 = originalEndpoints[i].p0;
                    var originalP1 = originalEndpoints[i].p1;

                    double dist0 = currentP0.DistanceTo(originalP0);
                    double dist1 = currentP1.DistanceTo(originalP1);

                    if (dist0 > 0.001 || dist1 > 0.001)
                    {
                        bool hasElbowAtBothEnds = HasElbowOnBothEnds(createdTrays[i]);
                        double currentLen = checkLC.Curve.Length;
                        double originalLen = originalP0.DistanceTo(originalP1);
                        double minLeg = CurrentMinLeg();
                        bool trimmedSignificant = (originalLen - currentLen) > ShortTolFt;
                        bool belowMinLeg = (currentLen + ShortTolFt) < minLeg;
                        bool targetLengthValid = (originalLen + ShortTolFt) >= minLeg;
                        bool shouldForceRestore = hasElbowAtBothEnds && targetLengthValid && trimmedSignificant;

                        if (hasElbowAtBothEnds)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[Build] STEP3: Tray {createdTrays[i].Id} elbowsBothEnds; current={currentLen * 304.8:F1}mm, original={originalLen * 304.8:F1}mm, minLeg={minLeg * 304.8:F1}mm, trimmedSig={trimmedSignificant}, belowMinLeg={belowMinLeg}, targetValid={targetLengthValid}, forceRestore={shouldForceRestore}");
                        }

                        if (hasElbowAtBothEnds && !shouldForceRestore)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Build] STEP3: Tray {createdTrays[i].Id} has elbows on both ends - skipping restore (Revit trimmed it intentionally)");
                        }
                        else
                        {
                            if (hasElbowAtBothEnds && shouldForceRestore)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Build] STEP3: Tray {createdTrays[i].Id} trimmed to {currentLen * 304.8:F1}mm (<min {minLeg * 304.8:F1}mm) with elbows on both ends - restoring to preserve straight run");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"[Build] STEP3: Revit modified tray {createdTrays[i].Id} after creating fittings: restoring");
                            }

                            System.Diagnostics.Debug.WriteLine($"[Build]   Was: ({currentP0.X:F3}, {currentP0.Y:F3}, {currentP0.Z:F3}) -> ({currentP1.X:F3}, {currentP1.Y:F3}, {currentP1.Z:F3})");
                            System.Diagnostics.Debug.WriteLine($"[Build]   Now: ({originalP0.X:F3}, {originalP0.Y:F3}, {originalP0.Z:F3}) -> ({originalP1.X:F3}, {originalP1.Y:F3}, {originalP1.Z:F3})");

                            bool restored = TryRestoreTrayWithReconnect(createdTrays[i], originalP0, originalP1);
                            if (!restored)
                            {
                                try
                                {
                                    checkLC.Curve = Line.CreateBound(originalP0, originalP1);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[Build]   FAILED to restore: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }

            // Final pass: re-check and fix open joints between newly created segments (handles tiny gaps)
            try { PostJoinPass(result); } catch { }

            // STEP 4: Connect last tray to target main tray
            var lc = targetTray.Location as LocationCurve;
            var lastTray = createdTrays.Count > 0 ? createdTrays[createdTrays.Count - 1] : null;
            if (lc != null && lastTray != null)
            {
                // Save endpoints before SECOND regeneration (Revit may modify again!)
                var step4SavedEndpoints = new List<(XYZ p0, XYZ p1)>();
                for (int i = 0; i < createdTrays.Count; i++)
                {
                    var step4SaveLC = createdTrays[i].Location as LocationCurve;
                    if (step4SaveLC?.Curve != null)
                    {
                        step4SavedEndpoints.Add((step4SaveLC.Curve.GetEndPoint(0), step4SaveLC.Curve.GetEndPoint(1)));
                    }
                    else
                    {
                        step4SavedEndpoints.Add((null, null));
                    }
                }

                _doc.Regenerate();

                // Force Revit to update LocationCurve by reading connectors
                System.Diagnostics.Debug.WriteLine($"[Build] STEP4: Forcing connector read to update LocationCurve...");
                for (int i = 0; i < createdTrays.Count; i++)
                {
                    var cm = createdTrays[i].ConnectorManager;
                    if (cm != null)
                    {
                        foreach (Connector c in cm.Connectors)
                        {
                            var _ = c.Origin; // Force read
                        }
                    }
                }

                // Restore endpoints after SECOND regeneration
                // Default: skip segments that have elbows on BOTH ends unless trim breaks the minimum leg
                bool step4AnyRestored = false;
                for (int i = 0; i < createdTrays.Count; i++)
                {
                    if (step4SavedEndpoints[i].p0 == null) continue;

                    var step4RestoreLC = createdTrays[i].Location as LocationCurve;
                    if (step4RestoreLC?.Curve != null)
                    {
                        var currentP0 = step4RestoreLC.Curve.GetEndPoint(0);
                        var currentP1 = step4RestoreLC.Curve.GetEndPoint(1);
                        var originalP0 = step4SavedEndpoints[i].p0;
                        var originalP1 = step4SavedEndpoints[i].p1;

                        double dist0 = currentP0.DistanceTo(originalP0);
                        double dist1 = currentP1.DistanceTo(originalP1);

                        if (dist0 > 0.001 || dist1 > 0.001)
                        {
                            bool hasElbowAtBothEnds = HasElbowOnBothEnds(createdTrays[i]);
                            double currentLen = step4RestoreLC.Curve.Length;
                            double originalLen = originalP0.DistanceTo(originalP1);
                            double minLeg = CurrentMinLeg();
                            bool trimmedSignificant = (originalLen - currentLen) > ShortTolFt;
                            bool belowMinLeg = (currentLen + ShortTolFt) < minLeg;
                            bool targetLengthValid = (originalLen + ShortTolFt) >= minLeg;
                            bool shouldForceRestore = hasElbowAtBothEnds && targetLengthValid && trimmedSignificant;

                            if (hasElbowAtBothEnds)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[Build] STEP4: Tray {createdTrays[i].Id} elbowsBothEnds; current={currentLen * 304.8:F1}mm, original={originalLen * 304.8:F1}mm, minLeg={minLeg * 304.8:F1}mm, trimmedSig={trimmedSignificant}, belowMinLeg={belowMinLeg}, targetValid={targetLengthValid}, forceRestore={shouldForceRestore}");
                            }

                            if (hasElbowAtBothEnds && !shouldForceRestore)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Build] STEP4: Tray {createdTrays[i].Id} has elbows on both ends - skipping restore (Revit trimmed it intentionally)");
                            }
                            else
                            {
                                if (hasElbowAtBothEnds && shouldForceRestore)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[Build] STEP4: Tray {createdTrays[i].Id} trimmed to {currentLen * 304.8:F1}mm (<min {minLeg * 304.8:F1}mm) with elbows on both ends - restoring to preserve straight run");
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"[Build] STEP4: Revit modified tray {createdTrays[i].Id} during 2nd Regenerate: restoring");
                                }

                                System.Diagnostics.Debug.WriteLine($"[Build]   Was: ({currentP0.X:F3}, {currentP0.Y:F3}, {currentP0.Z:F3}) -> ({currentP1.X:F3}, {currentP1.Y:F3}, {currentP1.Z:F3})");
                                System.Diagnostics.Debug.WriteLine($"[Build]   Now: ({originalP0.X:F3}, {originalP0.Y:F3}, {originalP0.Z:F3}) -> ({originalP1.X:F3}, {originalP1.Y:F3}, {originalP1.Z:F3})");

                                bool restored = TryRestoreTrayWithReconnect(createdTrays[i], originalP0, originalP1);
                                if (restored)
                                {
                                    step4AnyRestored = true;
                                }
                                else
                                {
                                    try
                                    {
                                        step4RestoreLC.Curve = Line.CreateBound(originalP0, originalP1);
                                        step4AnyRestored = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[Build]   FAILED to restore: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                }

                // If we restored any geometry, regenerate again to apply changes
                if (step4AnyRestored)
                {
                    System.Diagnostics.Debug.WriteLine($"[Build] STEP4: Regenerating after restore to apply geometry changes...");
                    _doc.Regenerate();
                }

                bool success = false;
                bool strictFailed = false;
                if (_hasFittings)
                {
                    success = TryConnectToExistingWithTee_Safe(lastTray, (MEPCurve)targetTray, lc, TargetTrayWidthFt, TargetTrayHeightFt, out strictFailed);
                }

                if (success)
                {
                    result.Tees++;
                }
                else if (strictFailed && StrictFittingsOnly)
                {
                    TaskDialog.Show("AutoRoute", "Nie udało się wstawić trójnika w trybie Strict. Sprawdź typ/rozmiar rodziny T lub zostaw więcej miejsca.");
                }
            }

            // Post-fix cleanup: merge colinear and remove tiny leftovers by adjusting curves
            try { CleanupShortFragments(result.NewElements, CurrentMinLeg()); } catch { }

            // Final cleanup: remove orphaned/duplicate fittings and invalid elements
            try { CleanupInvalidElements(result); } catch { }

            return result;
        }

        private void CleanupInvalidElements(BuildResult result)
        {
            if (result?.NewElements == null) return;

            var toDelete = new List<ElementId>();

            // Step 1: Find and remove orphaned or duplicate fittings
            try
            {
                var fittingCollector = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_CableTrayFitting)
                    .WhereElementIsNotElementType();

                var fittingsByLocation = new Dictionary<string, List<Element>>();

                foreach (Element fitting in fittingCollector)
                {
                    if (fitting.Location is LocationPoint lp)
                    {
                        // Group fittings by rounded location (to detect duplicates)
                        string locKey = $"{Math.Round(lp.Point.X, 3)}_{Math.Round(lp.Point.Y, 3)}_{Math.Round(lp.Point.Z, 3)}";

                        if (!fittingsByLocation.ContainsKey(locKey))
                            fittingsByLocation[locKey] = new List<Element>();

                        fittingsByLocation[locKey].Add(fitting);

                        // Check if fitting is orphaned (no or incomplete connections)
                        if (fitting is FamilyInstance fi)
                        {
                            var connMgr = fi.MEPModel?.ConnectorManager;
                            if (connMgr != null)
                            {
                                int connectedCount = 0;
                                int totalConnectors = 0;

                                foreach (Connector conn in connMgr.Connectors)
                                {
                                    totalConnectors++;
                                    if (conn.IsConnected) connectedCount++;
                                }

                                // If fitting has no connections or less than half connected, mark for deletion
                                if (connectedCount == 0 || (totalConnectors > 0 && connectedCount < totalConnectors / 2))
                                {
                                    System.Diagnostics.Debug.WriteLine($"[Cleanup] Orphaned fitting found: {fitting.Id} ({connectedCount}/{totalConnectors} connectors connected)");
                                    toDelete.Add(fitting.Id);
                                }
                            }
                        }
                    }
                }

                // Step 2: Remove duplicate fittings at same location (keep first, delete the rest)
                foreach (var kvp in fittingsByLocation)
                {
                    if (kvp.Value.Count > 1)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Cleanup] Found {kvp.Value.Count} fittings at location {kvp.Key}");
                        // Keep the first, delete the rest
                        for (int i = 1; i < kvp.Value.Count; i++)
                        {
                            if (!toDelete.Contains(kvp.Value[i].Id))
                            {
                                System.Diagnostics.Debug.WriteLine($"[Cleanup] Duplicate fitting: {kvp.Value[i].Id}");
                                toDelete.Add(kvp.Value[i].Id);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cleanup] Fitting cleanup failed: {ex.Message}");
            }

            // Step 3: Remove very short segments that shouldn't exist
            try
            {
                foreach (var id in result.NewElements)
                {
                    var elem = _doc.GetElement(id);
                    if (elem is MEPCurve mc)
                    {
                        var lc = mc.Location as LocationCurve;
                        if (lc?.Curve != null && lc.Curve.Length < ShortTolFt)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Cleanup] Very short segment found: {id} (length: {lc.Curve.Length * 304.8:F1}mm)");
                            toDelete.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cleanup] Segment cleanup failed: {ex.Message}");
            }

            // Step 4: Delete all marked elements
            if (toDelete.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[Cleanup] Deleting {toDelete.Count} invalid elements");
                try
                {
                    _doc.Delete(toDelete);
                    // Remove deleted IDs from result
                    result.NewElements.RemoveAll(id => toDelete.Contains(id));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Cleanup] Delete failed: {ex.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Cleanup] No invalid elements found");
            }
        }

        private void CleanupShortFragments(List<ElementId> ids, double minLeg)
        {
            foreach (var id in ids)
            {
                var e = _doc.GetElement(id) as MEPCurve; if (e == null) continue;
                var lc = e.Location as LocationCurve; if (lc?.Curve == null) continue;
                if (lc.Curve.Length < minLeg)
                {
                    // Try to absorb by extending neighbor along same line, not by breaking more
                    var cm = e.ConnectorManager; if (cm == null) continue;
                    Connector a = null, b = null; foreach (Connector c in cm.Connectors) { if (c.ConnectorType == ConnectorType.End) { if (a == null) a = c; else b = c; } }
                    if (a != null && b != null)
                    {
                        foreach (var c in new[] { a, b })
                        {
                            foreach (Connector r in c.AllRefs)
                            {
                                if (r?.Owner is MEPCurve neigh)
                                {
                                    var nl = neigh.Location as LocationCurve; if (nl?.Curve == null) continue;
                                    // If colinear, shift neighbor endpoint to remove the tiny segment
                                    if (IsColinear(lc.Curve as Line, nl.Curve as Line))
                                    {
                                        var otherEnd = c == a ? b.Origin : a.Origin;
                                        try { nl.Curve = Line.CreateBound(nl.Curve.GetEndPoint(0), otherEnd); } catch { }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private static bool IsColinear(Line a, Line b)
        {
            if (a == null || b == null) return false;
            var da = a.Direction.Normalize();
            var db = b.Direction.Normalize();
            return da.IsAlmostEqualTo(db) || da.IsAlmostEqualTo(-db);
        }

        private bool FittingExistsAt(XYZ joint, Connector ca, Connector cb)
        {
            // Check if connectors are already connected through a fitting
            if (ca.IsConnected && cb.IsConnected)
            {
                foreach (Connector refA in ca.AllRefs)
                {
                    if (refA.Owner is FamilyInstance fitting &&
                        fitting.Category?.Id.Value == (int)BuiltInCategory.OST_CableTrayFitting)
                    {
                        // Check if this fitting also connects to cb
                        var fittingConnectors = fitting.MEPModel?.ConnectorManager?.Connectors;
                        if (fittingConnectors != null)
                        {
                            foreach (Connector fc in fittingConnectors)
                            {
                                foreach (Connector fcRef in fc.AllRefs)
                                {
                                    if (fcRef.Owner?.Id == cb.Owner?.Id && fcRef.Id == cb.Id)
                                    {
                                        // Found a fitting connecting both ca and cb
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Also check if there's a fitting element at the joint location
            const double fittingProximity = 0.05; // ~15mm tolerance for fitting location
            try
            {
                var collector = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_CableTrayFitting)
                    .WhereElementIsNotElementType();

                foreach (Element elem in collector)
                {
                    if (elem.Location is LocationPoint lp)
                    {
                        if (lp.Point.DistanceTo(joint) < fittingProximity)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Fitting] Found existing fitting {elem.Id} at joint location (distance: {lp.Point.DistanceTo(joint) * 304.8:F1}mm)");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Fitting] FittingExistsAt check failed: {ex.Message}");
            }

            return false;
        }

        private static bool IsVerticalCurve(Line l)
        {
            if (l == null) return false;
            var d = l.Direction; return Math.Abs(d.Z) > 0.5 && Math.Abs(d.X) < 1e-6 && Math.Abs(d.Y) < 1e-6;
        }
        private static bool IsHorizontalCurve(Line l)
        {
            if (l == null) return false;
            var d = l.Direction; return Math.Abs(d.Z) < 1e-6 && (Math.Abs(d.X) > 1e-6 || Math.Abs(d.Y) > 1e-6);
        }

        private bool HasElbowOnBothEnds(MEPCurve tray)
        {
            if (tray == null) return false;
            try
            {
                var cm = tray.ConnectorManager;
                if (cm == null || cm.Connectors.Size != 2) return false;

                int elbowCount = 0;
                foreach (Connector c in cm.Connectors)
                {
                    if (!c.IsConnected) continue;

                    foreach (Connector refConn in c.AllRefs)
                    {
                        if (refConn?.Owner is FamilyInstance fi)
                        {
                            var famName = fi.Symbol?.FamilyName ?? string.Empty;
                            if (famName.Contains("Łuk") || famName.Contains("Elbow"))
                            {
                                elbowCount++;
                                break;
                            }
                        }
                    }
                }

                return elbowCount >= 2;
            }
            catch
            {
                return false;
            }
        }

        private bool TryRestoreTrayWithReconnect(MEPCurve tray, XYZ p0, XYZ p1)
        {
            if (tray == null || p0 == null || p1 == null) return false;

            var lc = tray.Location as LocationCurve;
            if (lc?.Curve == null) return false;

            var cm = tray.ConnectorManager;
            if (cm == null) return false;

            var connections = new List<(Connector self, Connector other)>();
            foreach (Connector c in cm.Connectors)
            {
                Connector other = null;
                foreach (Connector refConn in c.AllRefs)
                {
                    if (refConn?.Owner?.Id != c.Owner?.Id)
                    {
                        other = refConn;
                        break;
                    }
                }
                if (other != null)
                {
                    connections.Add((c, other));
                }
            }

            try
            {
                foreach (var pair in connections)
                {
                    try { pair.self.DisconnectFrom(pair.other); } catch { }
                }

                lc.Curve = Line.CreateBound(p0, p1);

                _doc.Regenerate();

                foreach (var pair in connections)
                {
                    try { pair.self.ConnectTo(pair.other); }
                    catch (Exception reconnectEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Build]   Reconnect failed: {reconnectEx.Message}");
                    }
                }

                _doc.Regenerate();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Build]   FAILED to restore with reconnect: {ex.Message}");
                return false;
            }
        }

        private bool TryRestoreTrayWithReconnect(MEPCurve tray)
        {
            if (tray == null) return false;
            if (_originalEndpoints.TryGetValue(tray.Id.Value, out var endpoints))
            {
                return TryRestoreTrayWithReconnect(tray, endpoints.p0, endpoints.p1);
            }
            return false;
        }

        private FamilySymbol FindVerticalElbowSymbol(bool preferInternal, string currentTypeName)
        {
            try
            {
                string wanted = preferInternal ? "wewn" : "zewn"; // substring match
                string famHint = currentTypeName?.ToLowerInvariant() ?? string.Empty;
                bool isLadder = famHint.Contains("drabina");
                var types = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_CableTrayFitting)
                    .WhereElementIsElementType()
                    .OfType<FamilySymbol>()
                    .ToList();
                foreach (var t in types)
                {
                    var name = (t.Name ?? string.Empty).ToLowerInvariant();
                    if (!name.Contains("łuk") || !name.Contains("pionowy")) continue;
                    if (isLadder && !name.Contains("drabina")) continue;
                    if (!isLadder && name.Contains("drabina")) continue;
                    if (name.Contains(wanted)) return t;
                }
            }
            catch { }
            return null;
        }

        private Line GetSnapshotLine(MEPCurve curve, out bool usedOriginal)
        {
            usedOriginal = false;
            if (curve == null) return null;

            try
            {
                if (_originalEndpoints.TryGetValue(curve.Id.Value, out var endpoints))
                {
                    double len = endpoints.p0.DistanceTo(endpoints.p1);
                    if (len > 1e-6)
                    {
                        usedOriginal = true;
                        return Line.CreateBound(endpoints.p0, endpoints.p1);
                    }
                }
            }
            catch { }

            var lc = curve.Location as LocationCurve;
            return lc?.Curve as Line;
        }

        private static XYZ NormalizeVector(XYZ v)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            if (len < 1e-9) return XYZ.Zero;
            return new XYZ(v.X / len, v.Y / len, v.Z / len);
        }

        private static XYZ GetFarDirectionFromJointXY(Line line, XYZ joint)
        {
            if (line == null) return XYZ.Zero;

            var p0 = line.GetEndPoint(0);
            var p1 = line.GetEndPoint(1);
            bool near0 = p0.DistanceTo(joint) <= p1.DistanceTo(joint);
            var far = near0 ? p1 : p0;

            var vec = new XYZ(far.X - joint.X, far.Y - joint.Y, 0.0);
            return NormalizeVector(vec);
        }

        private XYZ GetFarDirectionForHorizontal(MEPCurve curve, XYZ joint, out double nearDist, out double farDist, out bool usedOriginal)
        {
            nearDist = 0.0;
            farDist = 0.0;
            usedOriginal = false;

            var line = GetSnapshotLine(curve, out usedOriginal);
            if (line == null) return XYZ.Zero;

            var p0 = line.GetEndPoint(0);
            var p1 = line.GetEndPoint(1);

            double d0 = p0.DistanceTo(joint);
            double d1 = p1.DistanceTo(joint);

            bool near0 = d0 <= d1;
            var near = near0 ? p0 : p1;
            var far = near0 ? p1 : p0;

            nearDist = near.DistanceTo(joint);
            farDist = far.DistanceTo(joint);

            var vec = new XYZ(far.X - joint.X, far.Y - joint.Y, 0.0);
            double len = Math.Sqrt(vec.X * vec.X + vec.Y * vec.Y);
            if (len < 1e-9)
            {
                // If joint coincides with far end, fallback to segment direction
                vec = new XYZ(far.X - near.X, far.Y - near.Y, 0.0);
                len = Math.Sqrt(vec.X * vec.X + vec.Y * vec.Y);
                if (len < 1e-9) return XYZ.Zero;
            }

            return new XYZ(vec.X / len, vec.Y / len, 0.0);
        }

        private bool ShouldSwapConnectorsForHorizontalElbow(MEPCurve a, MEPCurve b, XYZ joint, Connector ca, Connector cb)
        {
            try
            {
                // Check if this is a horizontal-horizontal elbow
                bool usedOriginalA, usedOriginalB;
                var lca = GetSnapshotLine(a, out usedOriginalA);
                var lcb = GetSnapshotLine(b, out usedOriginalB);
                if (lca == null || lcb == null) return false;

                bool aH = IsHorizontalCurve(lca);
                bool bH = IsHorizontalCurve(lcb);
                if (!(aH && bH)) return false; // Not a horizontal-horizontal pair

                // Get directions of both horizontal segments
                var dirA = lca.Direction;
                var dirB = lcb.Direction;

                // Check if they're perpendicular
                double dot = Math.Abs(dirA.X * dirB.X + dirA.Y * dirB.Y);
                if (dot > 0.01) return false; // Not perpendicular, skip

                System.Diagnostics.Debug.WriteLine($"[Fitting] Horizontal elbow orientation PRE-CHECK:");
                System.Diagnostics.Debug.WriteLine($"  Using original snapshot A={usedOriginalA}, B={usedOriginalB}");
                System.Diagnostics.Debug.WriteLine($"  Joint: ({joint.X:F3}, {joint.Y:F3}, {joint.Z:F3})");
                System.Diagnostics.Debug.WriteLine($"  Segment A dir: ({dirA.X:F3}, {dirA.Y:F3}, {dirA.Z:F3})");
                System.Diagnostics.Debug.WriteLine($"  Segment B dir: ({dirB.X:F3}, {dirB.Y:F3}, {dirB.Z:F3})");

                double nearA, farA, nearB, farB;
                var dirFromJointA = GetFarDirectionForHorizontal(a, joint, out nearA, out farA, out var usedOriginalLineA);
                var dirFromJointB = GetFarDirectionForHorizontal(b, joint, out nearB, out farB, out var usedOriginalLineB);

                System.Diagnostics.Debug.WriteLine($"  Segment A distances: near={nearA * 304.8:F1}mm, far={farA * 304.8:F1}mm, usingOriginal={usedOriginalLineA}");
                System.Diagnostics.Debug.WriteLine($"  Segment B distances: near={nearB * 304.8:F1}mm, far={farB * 304.8:F1}mm, usingOriginal={usedOriginalLineB}");

                if (dirFromJointA.GetLength() < 1e-6 || dirFromJointB.GetLength() < 1e-6)
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Horizontal elbow orientation: skipping swap (unable to derive direction)");
                    return false;
                }

                System.Diagnostics.Debug.WriteLine($"  A far end dir from joint: ({dirFromJointA.X:F3}, {dirFromJointA.Y:F3})");
                System.Diagnostics.Debug.WriteLine($"  B far end dir from joint: ({dirFromJointB.X:F3}, {dirFromJointB.Y:F3})");

                // Calculate cross product to determine if current connector order would create inward or outward curve
                // For 2D: cross = A.x * B.y - A.y * B.x
                // Positive cross = counter-clockwise (external curve) - GOOD, don't swap
                // Negative cross = clockwise (internal curve) - BAD, need to swap
                double cross = dirFromJointA.X * dirFromJointB.Y - dirFromJointA.Y * dirFromJointB.X;

                System.Diagnostics.Debug.WriteLine($"  Cross product: {cross:F3}");

                // If cross is negative, we need to swap connector order to get external curve
                if (cross < -0.01)
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Horizontal elbow would be INWARD with current order (cross={cross:F3})");
                    System.Diagnostics.Debug.WriteLine($"[Fitting] => SWAPPING connector order to get OUTWARD orientation");
                    return true; // Swap connectors
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Horizontal elbow will be OUTWARD with current order (cross={cross:F3})");
                    return false; // Don't swap
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Fitting] Horizontal elbow orientation pre-check failed: {ex.Message}");
                return false; // On error, don't swap
            }
        }

        private void TryFixVerticalElbowHandedness(FamilyInstance created, MEPCurve a, MEPCurve b, XYZ joint)
        {
            try
            {
                if (created == null) return;
                if (PreferredOutwardDirXY == null) return;

                // Try to use original endpoints if available, otherwise use current
                bool aV, aH, bV, bH;
                Line vLine, hLine;
                XYZ v0, v1, h0, h1;

                var aId = a.Id.Value;
                var bId = b.Id.Value;

                if (_originalEndpoints.ContainsKey(aId) && _originalEndpoints.ContainsKey(bId))
                {
                    // Use original endpoints (before Revit modification)
                    var origA = _originalEndpoints[aId];
                    var origB = _originalEndpoints[bId];
                    var lineA = Line.CreateBound(origA.p0, origA.p1);
                    var lineB = Line.CreateBound(origB.p0, origB.p1);

                    aV = IsVerticalCurve(lineA); aH = IsHorizontalCurve(lineA);
                    bV = IsVerticalCurve(lineB); bH = IsHorizontalCurve(lineB);
                    if (!((aV && bH) || (aH && bV))) return; // not vertical-horizontal pair

                    vLine = aV ? lineA : lineB;
                    hLine = aH ? lineA : lineB;
                    v0 = vLine.GetEndPoint(0); v1 = vLine.GetEndPoint(1);
                    h0 = hLine.GetEndPoint(0); h1 = hLine.GetEndPoint(1);
                }
                else
                {
                    // Fallback to current geometry if original not available
                    var lca = (a.Location as LocationCurve)?.Curve as Line;
                    var lcb = (b.Location as LocationCurve)?.Curve as Line;
                    if (lca == null || lcb == null) return;

                    aV = IsVerticalCurve(lca); aH = IsHorizontalCurve(lca);
                    bV = IsVerticalCurve(lcb); bH = IsHorizontalCurve(lcb);
                    if (!((aV && bH) || (aH && bV))) return; // not vertical-horizontal pair

                    vLine = aV ? lca : lcb;
                    hLine = aH ? lca : lcb;
                    v0 = vLine.GetEndPoint(0); v1 = vLine.GetEndPoint(1);
                    h0 = hLine.GetEndPoint(0); h1 = hLine.GetEndPoint(1);
                }

                // Get the base of the vertical segment (device end)
                bool vNear0 = v0.DistanceTo(joint) > v1.DistanceTo(joint); // far end is device end
                var deviceEnd = vNear0 ? v0 : v1;

                // Get the far end of horizontal segment
                bool hNear0 = h0.DistanceTo(joint) <= h1.DistanceTo(joint);
                var horizontalFarEnd = hNear0 ? h1 : h0;

                // For vertical elbow orientation:
                // Revit orients the elbow family based on connector flow direction
                // We need to compensate by choosing the opposite type for opposite flow directions

                // Check the direction of horizontal segment FROM joint
                var horizDirFromJoint = new XYZ(horizontalFarEnd.X - joint.X, horizontalFarEnd.Y - joint.Y, 0);
                double horizDirLen = Math.Sqrt(horizDirFromJoint.X * horizDirFromJoint.X + horizDirFromJoint.Y * horizDirFromJoint.Y);
                if (horizDirLen > 0.01)
                {
                    horizDirFromJoint = new XYZ(horizDirFromJoint.X / horizDirLen, horizDirFromJoint.Y / horizDirLen, 0);
                }

                // Determine if horizontal goes in "positive" direction (X+ or Y+)
                bool positiveDirection = (Math.Abs(horizDirFromJoint.X) > 0.5 && horizDirFromJoint.X > 0) ||
                                        (Math.Abs(horizDirFromJoint.Y) > 0.5 && horizDirFromJoint.Y > 0);

                // For NEGATIVE directions (X- or Y-), use EXTERNAL
                // For POSITIVE directions (X+ or Y+), use INTERNAL (to achieve same visual effect)
                bool desiredExternal = !positiveDirection;

                System.Diagnostics.Debug.WriteLine($"[Fitting] Elbow handedness check:");
                System.Diagnostics.Debug.WriteLine($"  Device end: ({deviceEnd.X:F3}, {deviceEnd.Y:F3}, {deviceEnd.Z:F3})");
                System.Diagnostics.Debug.WriteLine($"  Joint: ({joint.X:F3}, {joint.Y:F3}, {joint.Z:F3})");
                System.Diagnostics.Debug.WriteLine($"  Horizontal far end: ({horizontalFarEnd.X:F3}, {horizontalFarEnd.Y:F3}, {horizontalFarEnd.Z:F3})");
                System.Diagnostics.Debug.WriteLine($"  Horizontal dir from joint: ({horizDirFromJoint.X:F3}, {horizDirFromJoint.Y:F3})");
                System.Diagnostics.Debug.WriteLine($"  positiveDirection: {positiveDirection}, desiredExternal: {desiredExternal}");

                var curName = created.Symbol?.Name ?? string.Empty;
                var curLower = curName.ToLowerInvariant();
                bool curIsExternal = curLower.Contains("zewn");

                System.Diagnostics.Debug.WriteLine($"  Current type: {curName}, curIsExternal: {curIsExternal}");

                // Since we now force -trayDir lateral in path planning, both sides should use EXTERNAL
                // No need to switch based on direction anymore - just use external always
                bool forceExternal = true;
                if ((forceExternal && !curIsExternal))
                {
                    var newSymbol = FindVerticalElbowSymbol(preferInternal: false, currentTypeName: curName);
                    if (newSymbol != null && !newSymbol.Id.Equals(created.Symbol?.Id))
                    {
                        try
                        {
                            if (!newSymbol.IsActive) newSymbol.Activate();
                            created.Symbol = newSymbol;
                            System.Diagnostics.Debug.WriteLine($"[Fitting] Switched vertical elbow type to zewnętrzny ({newSymbol.Name})");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Fitting] Failed to switch elbow type: {ex.Message}");
                        }
                    }
                }
            }
            catch { }
        }

        private bool TryCreateElbowWithValidation(MEPCurve a, MEPCurve b, XYZ joint, XYZ prefDirA, XYZ prefDirB, bool preferSwapFirst)
        {
            var orders = preferSwapFirst ? new[] { true, false } : new[] { false, true };

            foreach (bool swap in orders)
            {
                var ca = GetBestConnectorForDirection(a, joint, prefDirA ?? XYZ.Zero) ??
                         GetNearestOpenConnector(a, joint) ??
                         GetNearestConnectorAny(a, joint);
                var cb = GetBestConnectorForDirection(b, joint, prefDirB ?? XYZ.Zero) ??
                         GetNearestOpenConnector(b, joint) ??
                         GetNearestConnectorAny(b, joint);

                if (ca == null || cb == null)
                {
                    System.Diagnostics.Debug.WriteLine("[Fitting] TryCreateElbowWithValidation: missing connector (ca or cb null)");
                    return false;
                }

                if (AreDirectlyConnected(ca, cb))
                {
                    System.Diagnostics.Debug.WriteLine("[Fitting] TryCreateElbowWithValidation: connectors already connected, disconnecting");
                    TryDisconnectPair(ca, cb);
                    _doc.Regenerate();

                    ca = GetNearestConnectorAny(a, joint);
                    cb = GetNearestConnectorAny(b, joint);
                    if (ca == null || cb == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[Fitting] TryCreateElbowWithValidation: connectors missing after disconnect");
                        return false;
                    }
                }

                var first = swap ? cb : ca;
                var second = swap ? ca : cb;

                if (TryCreateElbowInstance(first, second, out var elbow))
                {
                    if (!ValidateHorizontalElbowResult(elbow, a, b, joint))
                    {
                        System.Diagnostics.Debug.WriteLine("[Fitting] Elbow orientation invalid, trying alternate connector order");
                        continue;
                    }

                    TryFixVerticalElbowHandedness(elbow, a, b, joint);
                    return true;
                }
            }

            return false;
        }

        private bool ValidateHorizontalElbowResult(FamilyInstance elbow, MEPCurve a, MEPCurve b, XYZ joint)
        {
            try
            {
                if (elbow == null) return false;

                if (!TryGetHorizontalSpacerCandidate(a, b, out var spacer, out var desiredLength))
                {
                    return true; // nothing to validate
                }

                // ADAPTIVE VALIDATION APPROACH:
                // Po utworzeniu kolana, Revit API przesuwa konektory zgodnie z geometrią kolana.
                // Offset konektora od punktu joint może wynosić nawet promień gięcia (200-600mm),
                // co jest NORMALNYM zachowaniem, a nie błędem.
                // Zamiast sztywnego limitu 6mm, stosujemy adaptywną tolerancję:

                var jointConnector = GetNearestConnectorAny(spacer, joint);
                if (jointConnector == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Spacer joint connector is null - accepting elbow (will validate by length)");
                    // Kontynuuj walidację tylko długością segmentu poniżej
                }
                else
                {
                    double jointOffset = jointConnector.Origin.DistanceTo(joint);
                    double offsetMm = jointOffset * 304.8;

                    // Adaptywna tolerancja bazująca na geometrii kolana:
                    // - Minimum: promień gięcia + szerokość korytka
                    // - To zapewnia że offset wynikający z normalnej geometrii kolana jest akceptowany
                    double bendRadiusFt = Math.Max(TargetTrayWidthFt, TargetTrayHeightFt) * 1.5; // typowy promień to 1.5x większy wymiar
                    double adaptiveTolerance = bendRadiusFt + TargetTrayWidthFt; // promień + szerokość
                    double minTolerance = 6.0 / 304.8; // minimum 6mm dla bardzo małych korytek
                    double finalTolerance = Math.Max(adaptiveTolerance, minTolerance);

                    System.Diagnostics.Debug.WriteLine($"[Fitting] Spacer joint connector offset {offsetMm:F1}mm, adaptive tolerance {finalTolerance*304.8:F1}mm (bendR≈{bendRadiusFt*304.8:F0}mm)");

                    if (jointOffset > finalTolerance)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Fitting] Spacer joint connector offset {offsetMm:F1}mm exceeds adaptive tolerance {finalTolerance*304.8:F1}mm - rejecting elbow");
                        return false;
                    }
                }

                var lc = spacer.Location as LocationCurve;
                if (!(lc?.Curve is Line currentLine)) return true;

                double currentLength = currentLine.Length;
                double minLeg = CurrentMinLeg();
                double shrinkTolFt = 6.0 / 304.8; // ~6mm allowance
                double allowedShrink = Math.Max(shrinkTolFt, desiredLength * 0.1);
                double minAcceptable = Math.Max(minLeg, desiredLength - allowedShrink);

                if (currentLength + ShortTolFt < minAcceptable)
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Horizontal spacer collapsed ({currentLength * 304.8:F1}mm < {minAcceptable * 304.8:F1}mm). Attempting restore without removing elbow.");

                    bool restoredWithElbow = TryRestoreTrayWithReconnect(spacer);
                    if (restoredWithElbow)
                    {
                        _doc.Regenerate();

                        var recheckLc = spacer.Location as LocationCurve;
                        if (recheckLc?.Curve is Line recheckLine)
                        {
                            double healedLength = recheckLine.Length;
                            if (healedLength + ShortTolFt >= minAcceptable)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Fitting] Spacer restored to {healedLength * 304.8:F1}mm with elbow kept in place.");
                                return true;
                            }
                            System.Diagnostics.Debug.WriteLine($"[Fitting] Spacer still too short after restore ({healedLength * 304.8:F1}mm < {minAcceptable * 304.8:F1}mm). Removing elbow.");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("[Fitting] Spacer restore succeeded but curve unavailable for verification - keeping elbow.");
                            return true;
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[Fitting] Restore without removal failed - removing elbow.");
                    }

                    try { _doc.Delete(elbow.Id); } catch { }

                    bool restored = TryRestoreTrayWithReconnect(spacer);
                    System.Diagnostics.Debug.WriteLine(restored
                        ? "[Fitting] Spacer restored to original endpoints after invalid elbow"
                        : "[Fitting] Failed to restore spacer after invalid elbow");

                    _doc.Regenerate();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Fitting] Horizontal elbow validation failed: {ex.Message}");
                return true;
            }
        }

        private bool TryGetHorizontalSpacerCandidate(MEPCurve a, MEPCurve b, out MEPCurve spacer, out double desiredLength)
        {
            spacer = null;
            desiredLength = 0.0;

            foreach (var candidate in new[] { a, b })
            {
                if (candidate == null) continue;
                if (!HasElbowOnBothEnds(candidate)) continue;

                if (_originalEndpoints.TryGetValue(candidate.Id.Value, out var endpoints))
                {
                    var line = Line.CreateBound(endpoints.p0, endpoints.p1);
                    if (line != null && IsHorizontalCurve(line))
                    {
                        spacer = candidate;
                        desiredLength = line.Length;
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryCreateElbowInstance(Connector a, Connector b, out FamilyInstance fi)
        {
            fi = null;
            try
            {
                fi = _doc.Create.NewElbowFitting(a, b);
                if (fi != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] SUCCESS: Created fitting with ID {fi.Id}, Type: {fi.Name}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Fitting] CreateElbowFitting (instance) failed: {ex.InnerException?.Message ?? ex.Message}");
            }
            return false;
        }

        private bool TryCreateElbowOrUnionAt(MEPCurve a, MEPCurve b, XYZ joint)
        {
            // Determine preferred directions from the joint into each curve
            XYZ prefDirA = null, prefDirB = null;
            try
            {
                var lcA0 = a.Location as LocationCurve; var lcB0 = b.Location as LocationCurve;
                if (lcA0?.Curve != null)
                {
                    var pa0 = lcA0.Curve.GetEndPoint(0); var pa1 = lcA0.Curve.GetEndPoint(1);
                    bool near0 = pa0.DistanceTo(joint) <= pa1.DistanceTo(joint);
                    var dir = near0 ? (pa1 - pa0) : (pa0 - pa1);
                    prefDirA = new XYZ(dir.X, dir.Y, dir.Z);
                }
                if (lcB0?.Curve != null)
                {
                    var pb0 = lcB0.Curve.GetEndPoint(0); var pb1 = lcB0.Curve.GetEndPoint(1);
                    bool near0 = pb0.DistanceTo(joint) <= pb1.DistanceTo(joint);
                    var dir = near0 ? (pb1 - pb0) : (pb0 - pb1);
                    prefDirB = new XYZ(dir.X, dir.Y, dir.Z);
                }
            }
            catch { }

            // Choose connectors guided by the preferred directions
            var ca = GetBestConnectorForDirection(a, joint, prefDirA ?? XYZ.Zero) ?? GetNearestOpenConnector(a, joint) ?? GetNearestConnectorAny(a, joint);
            var cb = GetBestConnectorForDirection(b, joint, prefDirB ?? XYZ.Zero) ?? GetNearestOpenConnector(b, joint) ?? GetNearestConnectorAny(b, joint);

            if (ca == null || cb == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Fitting] Cannot create elbow/union: Missing connector (ca={ca != null}, cb={cb != null})");
                return false;
            }

            // Check if fitting already exists at this location
            if (FittingExistsAt(joint, ca, cb))
            {
                System.Diagnostics.Debug.WriteLine($"[Fitting] Fitting already exists at joint location, skipping duplicate creation");
                return true; // Return true because fitting exists (goal achieved)
            }

            // Decide union vs elbow based on colinearity of curves
            bool preferUnion = false;
            try
            {
                var lca = (a.Location as LocationCurve)?.Curve as Line;
                var lcb = (b.Location as LocationCurve)?.Curve as Line;
                if (lca != null && lcb != null)
                {
                    preferUnion = IsColinear(lca, lcb);
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Colinear check (preferUnion) = {preferUnion}");
                }
            }
            catch { }

            double initialDist = ca.Origin.DistanceTo(cb.Origin);
            System.Diagnostics.Debug.WriteLine($"[Fitting] Initial connector distance: {initialDist * 304.8:F2}mm");

            // If connectors are too far apart, attempt EXACT axis-safe nudge to the joint first
            const double exactTryThreshold = 0.15; // ~45 mm
            if (initialDist > exactTryThreshold)
            {
                bool exA = ForceBranchEndpointToExact(a, joint);
                bool exB = ForceBranchEndpointToExact(b, joint);
                if (exA || exB)
                {
                    _doc.Regenerate();
                    ca = GetNearestConnectorAny(a, joint);
                    cb = GetNearestConnectorAny(b, joint);
                    if (ca != null && cb != null)
                    {
                        double d2 = ca.Origin.DistanceTo(cb.Origin);
                        System.Diagnostics.Debug.WriteLine($"[Fitting] After exact nudge, connector distance: {d2 * 304.8:F2}mm");
                    }
                }
            }

            // If connectors are still too far apart, try to nudge along axis
            const double maxAllowedDistance = 0.003; // ~1mm in feet
            if (ca != null && cb != null)
            {
                double currentDist = ca.Origin.DistanceTo(cb.Origin);
                if (currentDist > maxAllowedDistance)
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Connectors too far apart ({currentDist * 304.8:F2}mm > {maxAllowedDistance * 304.8:F2}mm), nudging to joint...");

                    // Find which end of each curve to nudge
                    var lcA = a.Location as LocationCurve;
                    var lcB = b.Location as LocationCurve;

                    if (lcA?.Curve != null && lcB?.Curve != null)
                    {
                        int endA = lcA.Curve.GetEndPoint(0).DistanceTo(joint) < lcA.Curve.GetEndPoint(1).DistanceTo(joint) ? 0 : 1;
                        int endB = lcB.Curve.GetEndPoint(0).DistanceTo(joint) < lcB.Curve.GetEndPoint(1).DistanceTo(joint) ? 0 : 1;

                        bool nudgedA = NudgeEndToAxisPreserving(a, endA, joint, out _);
                        bool nudgedB = NudgeEndToAxisPreserving(b, endB, joint, out _);

                        if (nudgedA || nudgedB)
                        {
                            _doc.Regenerate();

                            // Re-get connectors after nudging
                            ca = GetNearestConnectorAny(a, joint);
                            cb = GetNearestConnectorAny(b, joint);

                            if (ca != null && cb != null)
                            {
                                double newDist = ca.Origin.DistanceTo(cb.Origin);
                                System.Diagnostics.Debug.WriteLine($"[Fitting] After nudge, connector distance: {newDist * 304.8:F2}mm");

                                if (newDist > maxAllowedDistance)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[Fitting] WARNING: Still too far apart after nudge!");
                                }
                            }
                        }
                    }
                }
            }

            if (AreDirectlyConnected(ca, cb))
            {
                System.Diagnostics.Debug.WriteLine($"[Fitting] Connectors already connected, disconnecting first...");
                TryDisconnectPair(ca, cb);
                _doc.Regenerate();
            }

            // Prefer union only when segments are colinear; otherwise go straight for elbow
            if (preferUnion)
            {
                System.Diagnostics.Debug.WriteLine($"[Fitting] Attempting Union first (segments colinear)...");
                if (TryCreateFittingOnAnyApi("CreateUnionFitting", ca, cb)) return true;
                System.Diagnostics.Debug.WriteLine($"[Fitting] Union failed, trying Elbow...");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Fitting] Skipping Union (segments not colinear), trying Elbow...");
            }

            // Create elbow honoring horizontal orientation and straight spacer preservation
            bool shouldSwapForHorizontal = ShouldSwapConnectorsForHorizontalElbow(a, b, joint, ca, cb);

            if (TryCreateElbowWithValidation(a, b, joint, prefDirA, prefDirB, shouldSwapForHorizontal))
            {
                return true;
            }

            System.Diagnostics.Debug.WriteLine("[Fitting] Wszystkie próby stworzenia połączenia (Union/Elbow) nie powiodły się.");
            return false;
        }

        private void TryConnectFallbackAt(MEPCurve a, MEPCurve b, XYZ joint)
        {
            if (StrictFittingsOnly) return; // do not force hard connections in strict mode
            var ca = GetNearestOpenConnector(a, joint) ?? GetNearestConnectorAny(a, joint);
            var cb = GetNearestOpenConnector(b, joint) ?? GetNearestConnectorAny(b, joint);
            if (ca == null || cb == null) return;
            try { ca.ConnectTo(cb); } catch { }
        }

    private (ElementId typeId, double widthFt, double heightFt) DetermineDominantTypeAndDims(MEPCurve seed)
        {
            if (seed == null) return (ElementId.InvalidElementId, 0, 0);
            var visited = new HashSet<ElementId>();
            var q = new Queue<MEPCurve>();
            q.Enqueue(seed);
            visited.Add(seed.Id);

            var lengthByType = new Dictionary<ElementId, double>();
            var dimsByType = new Dictionary<ElementId, (double w, double h)> ();

            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                var lc = cur.Location as LocationCurve; if (lc == null) continue;
                var tId = cur.GetTypeId();
                var len = 0.0; try { len = lc.Curve.Length; } catch { }
                if (!lengthByType.ContainsKey(tId)) lengthByType[tId] = 0.0;
                lengthByType[tId] += len;

                // Record dims for this type once
                if (!dimsByType.ContainsKey(tId))
                {
                    var w = TryGetParamFeet(cur, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                    var h = TryGetParamFeet(cur, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                    if (w <= 0 || h <= 0)
                    {
                        var et = _doc.GetElement(tId) as ElementType;
                        w = w > 0 ? w : TryGetParamFeet(et, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                        h = h > 0 ? h : TryGetParamFeet(et, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                    }
                    dimsByType[tId] = (w, h);
                }

                // traverse connected trays
                var cm = cur.ConnectorManager; if (cm == null) continue;
                foreach (Connector c in cm.Connectors)
                {
                    foreach (Connector refc in c.AllRefs)
                    {
                        if (refc?.Owner is MEPCurve mc && mc.Category != null && mc.Category.Id.Value == (int)BuiltInCategory.OST_CableTray)
                        {
                            if (visited.Add(mc.Id)) q.Enqueue(mc);
                        }
                    }
                }
            }

            if (lengthByType.Count == 0) return (seed.GetTypeId(), TryGetParamFeet(seed, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM), TryGetParamFeet(seed, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM));
            var bestType = lengthByType.OrderByDescending(kv => kv.Value).First().Key;
            var dims = dimsByType.TryGetValue(bestType, out var d) ? d : (0.0, 0.0);
            return (bestType, dims.Item1, dims.Item2);
        }

        private static double TryGetParamFeet(Element e, BuiltInParameter bip)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p == null) return 0;
                return p.StorageType == StorageType.Double ? p.AsDouble() : 0;
            }
            catch { return 0; }
        }

        private static void TrySetParamFeet(Element e, BuiltInParameter bip, double value)
        {
            try
            {
                if (value <= 0) return;
                var p = e.get_Parameter(bip);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double)
                {
                    p.Set(value);
                }
            }
            catch { }
        }

        private void EnsureFittingsAvailableOnce()
        {
            if (_fittingsChecked) return;
            _fittingsChecked = true;

            System.Diagnostics.Debug.WriteLine($"[Fitting] ========== Fitting Discovery Started ==========");
            System.Diagnostics.Debug.WriteLine($"[Fitting] Revit API Assembly: {typeof(Autodesk.Revit.DB.Document).Assembly.GetName().Version}");

            try
            {
                var fittingTypes = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_CableTrayFitting)
                    .WhereElementIsElementType()
                    .ToList();

                _hasFittings = fittingTypes.Any();

                if (!_hasFittings)
                {
                    TaskDialog.Show("AutoRoute", "W projekcie nie znaleziono typów kształtek korytek (kolanka/te, łączniki). Załaduj odpowiednie rodziny kształtek, aby wstawiać połączenia automatycznie.");
                }
                else
                {
                    // Log available fitting types for debugging
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Found {fittingTypes.Count} fitting types in project:");
                    foreach (var ft in fittingTypes.Take(10))
                    {
                        var familyName = (ft as FamilySymbol)?.FamilyName ?? ft.Name;
                        System.Diagnostics.Debug.WriteLine($"  - {familyName}: {ft.Name}");
                    }
                }

                // Log API info
                System.Diagnostics.Debug.WriteLine($"[Fitting] Using Document.Create.NewElbowFitting/NewTeeFitting/NewUnionFitting API");

            }
            catch (Exception ex)
            {
                _hasFittings = false;
                System.Diagnostics.Debug.WriteLine($"[Fitting] EnsureFittingsAvailableOnce error: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine($"[Fitting] ========== Fitting Discovery Completed ==========");
        }

        private bool TryConnectToExistingWithTee_Safe(MEPCurve newcomer, MEPCurve main, LocationCurve mainLc, double trayWidthFt, double trayHeightFt, out bool strictFailed)
        {
            strictFailed = false;

            XYZ mainEnd0 = mainLc.Curve.GetEndPoint(0);
            XYZ mainEnd1 = mainLc.Curve.GetEndPoint(1);
            double mainLength = mainLc.Curve.Length;
            // cache main direction for resolving split results even if Revit replaces the element
            XYZ mainDir = (mainEnd1 - mainEnd0); if (mainDir.GetLength() > 0) mainDir = mainDir.Normalize(); else mainDir = XYZ.BasisX;

            var newcomerLc = newcomer.Location as LocationCurve;
            if (newcomerLc?.Curve == null) return false;

            XYZ newEnd0 = newcomerLc.Curve.GetEndPoint(0);
            XYZ newEnd1 = newcomerLc.Curve.GetEndPoint(1);

            // Analyze connection geometry to determine strategy
            var approach = AnalyzeConnectionGeometry(newcomer, main, mainLc, trayWidthFt, trayHeightFt);
            
            System.Diagnostics.Debug.WriteLine($"[Connect] ========== Connection Analysis ==========");
            System.Diagnostics.Debug.WriteLine($"[Connect] Approach strategy: {approach}");
            System.Diagnostics.Debug.WriteLine($"[Connect] Main: ({mainEnd0.X:F2}, {mainEnd0.Y:F2}, {mainEnd0.Z:F2}) - ({mainEnd1.X:F2}, {mainEnd1.Y:F2}, {mainEnd1.Z:F2})");
            System.Diagnostics.Debug.WriteLine($"[Connect] Newcomer: ({newEnd0.X:F2}, {newEnd0.Y:F2}, {newEnd0.Z:F2}) - ({newEnd1.X:F2}, {newEnd1.Y:F2}, {newEnd1.Z:F2})");

            // Route to appropriate handler based on approach strategy
            switch (approach)
            {
                case ConnectionApproach.DirectEnd:
                    return HandleDirectEndConnection(newcomer, main, mainLc, trayWidthFt, trayHeightFt, out strictFailed);

                case ConnectionApproach.PerpendicularTee:
                    return HandlePerpendicularTeeConnection(newcomer, main, mainLc, mainDir, trayWidthFt, trayHeightFt, out strictFailed);

                case ConnectionApproach.ParallelTee:
                    return HandleParallelTeeConnection(newcomer, main, mainLc, mainDir, trayWidthFt, trayHeightFt, out strictFailed);

                case ConnectionApproach.AngledComplex:
                default:
                    System.Diagnostics.Debug.WriteLine($"[Connect] Complex/angled approach - using fallback perpendicular logic");
                    return HandlePerpendicularTeeConnection(newcomer, main, mainLc, mainDir, trayWidthFt, trayHeightFt, out strictFailed);
            }
        }

        private ConnectionApproach AnalyzeConnectionGeometry(MEPCurve newcomer, MEPCurve main, LocationCurve mainLc, double trayWidthFt, double trayHeightFt)
        {
            var newcomerLc = newcomer.Location as LocationCurve;
            if (newcomerLc?.Curve == null || mainLc?.Curve == null)
                return ConnectionApproach.AngledComplex;

            XYZ mainEnd0 = mainLc.Curve.GetEndPoint(0);
            XYZ mainEnd1 = mainLc.Curve.GetEndPoint(1);
            XYZ newEnd0 = newcomerLc.Curve.GetEndPoint(0);
            XYZ newEnd1 = newcomerLc.Curve.GetEndPoint(1);

            double mainLength = mainLc.Curve.Length;
            double sizeTol = 3.0 * Math.Max(trayWidthFt, trayHeightFt);
            double endTol = Math.Min(mainLength * 0.15, sizeTol);

            // Get main horizontal direction for angle analysis
            var mainDir = (mainEnd1 - mainEnd0);
            var mainDirXY = new XYZ(mainDir.X, mainDir.Y, 0.0);
            double mainDirLen = mainDirXY.GetLength();

            // Check projection location on main
            var pr0 = mainLc.Curve.Project(newEnd0);
            var pr1 = mainLc.Curve.Project(newEnd1);

            double p0 = mainLc.Curve.GetEndParameter(0);
            double p1 = mainLc.Curve.GetEndParameter(1);
            double pRange = Math.Abs(p1 - p0);

            bool projectionNearEnd = false;
            if (pr0 != null)
            {
                double normParam0 = Math.Abs(pr0.Parameter - p0) / pRange;
                if (normParam0 < 0.15 || normParam0 > 0.85) projectionNearEnd = true;
            }
            if (pr1 != null)
            {
                double normParam1 = Math.Abs(pr1.Parameter - p0) / pRange;
                if (normParam1 < 0.15 || normParam1 > 0.85) projectionNearEnd = true;
            }

            // STEP 1: Analyze approach angle FIRST (before checking distance)
            // This ensures perpendicular/parallel approaches are correctly identified
            // even when endpoints are close together

            if (mainDirLen >= 0.001)  // Main is not vertical
            {
                mainDirXY = mainDirXY.Normalize();

                // Get newcomer horizontal direction (from end closer to main)
                var pr = pr0 != null && pr1 != null ? 
                    (newEnd0.DistanceTo(pr0.XYZPoint) < newEnd1.DistanceTo(pr1.XYZPoint) ? pr0 : pr1) : 
                    (pr0 ?? pr1);

                if (pr != null)
                {
                    XYZ newcomerEndNearMain = newEnd0.DistanceTo(pr.XYZPoint) < newEnd1.DistanceTo(pr.XYZPoint) ? newEnd0 : newEnd1;
                    XYZ newcomerOtherEnd = newcomerEndNearMain.IsAlmostEqualTo(newEnd0) ? newEnd1 : newEnd0;

                    var newcomerDir = (newcomerEndNearMain - newcomerOtherEnd);
                    var newcomerDirXY = new XYZ(newcomerDir.X, newcomerDir.Y, 0.0);
                    double newcomerDirLen = newcomerDirXY.GetLength();

                    if (newcomerDirLen >= 0.001)  // Newcomer is not vertical
                    {
                        newcomerDirXY = newcomerDirXY.Normalize();

                        // Calculate angle between directions in XY plane
                        double dotProduct = mainDirXY.DotProduct(newcomerDirXY);
                        double angle = Math.Acos(Math.Abs(dotProduct)) * 180.0 / Math.PI;

                        System.Diagnostics.Debug.WriteLine($"[Connect] Main dir XY: ({mainDirXY.X:F3}, {mainDirXY.Y:F3})");
                        System.Diagnostics.Debug.WriteLine($"[Connect] Newcomer dir XY: ({newcomerDirXY.X:F3}, {newcomerDirXY.Y:F3})");
                        System.Diagnostics.Debug.WriteLine($"[Connect] Angle between: {angle:F1}°");

                        // CRITICAL: Check if attach point is near end of main
                        // If perpendicular approach to main END, use ELBOW (DirectEnd) not TEE
                        if (angle >= 60.0 && projectionNearEnd)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Connect] PERPENDICULAR to main END detected - using DirectEnd (ELBOW)");
                            return ConnectionApproach.DirectEnd;
                        }

                        // Angle classification (checked BEFORE distance):
                        // 0-30°: Parallel (needs dogleg)
                        // 30-60°: Angled (complex)
                        // 60-90°: Perpendicular (TEE connection - only if NOT near end)

                        if (angle <= 30.0)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Connect] PARALLEL approach detected (angle={angle:F1}°)");
                            return ConnectionApproach.ParallelTee;
                        }
                        else if (angle >= 60.0)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Connect] PERPENDICULAR approach detected (angle={angle:F1}°)");
                            return ConnectionApproach.PerpendicularTee;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[Connect] ANGLED approach detected (angle={angle:F1}°)");
                            return ConnectionApproach.AngledComplex;
                        }
                    }
                    else
                    {
                        // Newcomer is vertical - perpendicular in XY plane
                        // But check if near end
                        if (projectionNearEnd)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Connect] Newcomer vertical near main END - using DirectEnd");
                            return ConnectionApproach.DirectEnd;
                        }
                        System.Diagnostics.Debug.WriteLine($"[Connect] Newcomer is vertical - using PerpendicularTee");
                        return ConnectionApproach.PerpendicularTee;
                    }
                }
            }
            else
            {
                // Main is vertical - always perpendicular in XY plane
                // But check if near end
                if (projectionNearEnd)
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] Main vertical, projection near end - using DirectEnd");
                    return ConnectionApproach.DirectEnd;
                }
                System.Diagnostics.Debug.WriteLine($"[Connect] Main is vertical - using PerpendicularTee");
                return ConnectionApproach.PerpendicularTee;
            }

            // STEP 2: Check if connecting to end (only as fallback)
            // This is now checked AFTER angle analysis
            double dist_new0_main0 = newEnd0.DistanceTo(mainEnd0);
            double dist_new0_main1 = newEnd0.DistanceTo(mainEnd1);
            double dist_new1_main0 = newEnd1.DistanceTo(mainEnd0);
            double dist_new1_main1 = newEnd1.DistanceTo(mainEnd1);

            double minEndToEndDist = Math.Min(
                Math.Min(dist_new0_main0, dist_new0_main1),
                Math.Min(dist_new1_main0, dist_new1_main1)
            );

            // Check for dogleg requirement
            XYZ chosenNewEnd;
            XYZ chosenMainEnd;
            if (minEndToEndDist == dist_new0_main0) { chosenNewEnd = newEnd0; chosenMainEnd = mainEnd0; }
            else if (minEndToEndDist == dist_new0_main1) { chosenNewEnd = newEnd0; chosenMainEnd = mainEnd1; }
            else if (minEndToEndDist == dist_new1_main0) { chosenNewEnd = newEnd1; chosenMainEnd = mainEnd0; }
            else { chosenNewEnd = newEnd1; chosenMainEnd = mainEnd1; }

            const double axisTol = 0.01;
            double ddx = Math.Abs(chosenNewEnd.X - chosenMainEnd.X);
            double ddy = Math.Abs(chosenNewEnd.Y - chosenMainEnd.Y);
            double ddz = Math.Abs(chosenNewEnd.Z - chosenMainEnd.Z);
            int nonZeroAxes = (ddx > axisTol ? 1 : 0) + (ddy > axisTol ? 1 : 0) + (ddz > axisTol ? 1 : 0);

            // Only use DirectEnd if very close AND aligned (not perpendicular)
            if (minEndToEndDist < endTol || (projectionNearEnd && nonZeroAxes < 2))
            {
                System.Diagnostics.Debug.WriteLine($"[Connect] Close to end (dist={minEndToEndDist * 304.8:F1}mm) - using DirectEnd");
                return ConnectionApproach.DirectEnd;
            }

            // Default fallback
            return ConnectionApproach.AngledComplex;
        }

        private bool HandleDirectEndConnection(MEPCurve newcomer, MEPCurve main, LocationCurve mainLc, double trayWidthFt, double trayHeightFt, out bool strictFailed)
        {
            strictFailed = false;
            System.Diagnostics.Debug.WriteLine($"[Connect] Using DIRECT END strategy (ELBOW)");

            XYZ mainEnd0 = mainLc.Curve.GetEndPoint(0);
            XYZ mainEnd1 = mainLc.Curve.GetEndPoint(1);

            var newcomerLc = newcomer.Location as LocationCurve;
            if (newcomerLc?.Curve == null) return false;

            XYZ newEnd0 = newcomerLc.Curve.GetEndPoint(0);
            XYZ newEnd1 = newcomerLc.Curve.GetEndPoint(1);

            // Find closest end pair
            double dist_new0_main0 = newEnd0.DistanceTo(mainEnd0);
            double dist_new0_main1 = newEnd0.DistanceTo(mainEnd1);
            double dist_new1_main0 = newEnd1.DistanceTo(mainEnd0);
            double dist_new1_main1 = newEnd1.DistanceTo(mainEnd1);

            double minEndToEndDist = Math.Min(
                Math.Min(dist_new0_main0, dist_new0_main1),
                Math.Min(dist_new1_main0, dist_new1_main1)
            );

            XYZ targetMainEnd;
            XYZ targetNewcomerEnd;

            if (minEndToEndDist == dist_new0_main0)
            {
                targetMainEnd = mainEnd0;
                targetNewcomerEnd = newEnd0;
            }
            else if (minEndToEndDist == dist_new0_main1)
            {
                targetMainEnd = mainEnd1;
                targetNewcomerEnd = newEnd0;
            }
            else if (minEndToEndDist == dist_new1_main0)
            {
                targetMainEnd = mainEnd0;
                targetNewcomerEnd = newEnd1;
            }
            else
            {
                targetMainEnd = mainEnd1;
                targetNewcomerEnd = newEnd1;
            }

            System.Diagnostics.Debug.WriteLine($"[Connect] Target connection: newcomer end ({targetNewcomerEnd.X:F2}, {targetNewcomerEnd.Y:F2}, {targetNewcomerEnd.Z:F2}) to main end ({targetMainEnd.X:F2}, {targetMainEnd.Y:F2}, {targetMainEnd.Z:F2})");

            // Check if we need an intermediate straight segment
            // This happens when newcomer and main are perpendicular and not aligned
            var mainDir = (mainLc.Curve as Line)?.Direction;
            var newcomerDir = (newcomerLc.Curve as Line)?.Direction;

            if (mainDir != null && newcomerDir != null)
            {
                // Check if perpendicular (horizontal to horizontal)
                double dotProduct = Math.Abs(mainDir.DotProduct(newcomerDir));
                if (dotProduct < 0.01) // Nearly perpendicular
                {
                    // Get the newcomer's anchor point (opposite end from target)
                    XYZ newcomerAnchor = targetNewcomerEnd.IsAlmostEqualTo(newEnd0) ? newEnd1 : newEnd0;

                    // Check if direct connection would create angled segment
                    double dx = Math.Abs(targetMainEnd.X - newcomerAnchor.X);
                    double dy = Math.Abs(targetMainEnd.Y - newcomerAnchor.Y);
                    double dz = Math.Abs(targetMainEnd.Z - newcomerAnchor.Z);

                    const double axisTol = 0.01; // ~3mm tolerance
                    bool isAxisAligned = (dx < axisTol && dy < axisTol) ||  // Z-axis (vertical)
                                         (dx < axisTol && dz < axisTol) ||  // Y-axis
                                         (dy < axisTol && dz < axisTol);    // X-axis

                    if (!isAxisAligned)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Connect] Direct connection would create angled segment - creating intermediate straight segment");
                        System.Diagnostics.Debug.WriteLine($"[Connect] Delta: dX={dx * 304.8:F1}mm, dY={dy * 304.8:F1}mm, dZ={dz * 304.8:F1}mm");

                        // Create intermediate point aligned with main tray direction
                        // The intermediate segment should align with the main tray direction
                        XYZ mainDirXY = new XYZ(mainDir.X, mainDir.Y, 0).Normalize();
                        XYZ intermediatePoint;

                        // Project the connection onto the plane perpendicular to main direction
                        // Intermediate point should be aligned with targetMainEnd in main direction
                        // and aligned with targetNewcomerEnd in perpendicular direction
                        if (Math.Abs(mainDir.X) > Math.Abs(mainDir.Y))
                        {
                            // Main is primarily X-aligned, so intermediate varies in Y
                            intermediatePoint = new XYZ(targetMainEnd.X, targetNewcomerEnd.Y, targetMainEnd.Z);
                        }
                        else
                        {
                            // Main is primarily Y-aligned, so intermediate varies in X
                            intermediatePoint = new XYZ(targetNewcomerEnd.X, targetMainEnd.Y, targetMainEnd.Z);
                        }

                        System.Diagnostics.Debug.WriteLine($"[Connect] Intermediate point: ({intermediatePoint.X:F3}, {intermediatePoint.Y:F3}, {intermediatePoint.Z:F3})");

                        // Check if intermediate segment would be long enough
                        double segmentLength = targetNewcomerEnd.DistanceTo(intermediatePoint);
                        double minLeg = CurrentMinLeg();

                        if (segmentLength >= minLeg)
                        {
                            // Extend newcomer to intermediate point first
                            bool extendSuccess = TryExtendNewcomerWithIntermediateSegment(
                                newcomer, targetNewcomerEnd, intermediatePoint, targetMainEnd,
                                trayWidthFt, trayHeightFt, out MEPCurve extendedSegment);

                            if (extendSuccess && extendedSegment != null)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Connect] Successfully created intermediate segment, now connecting to main");
                                _doc.Regenerate();

                                // Now connect the extended segment to main
                                var extendedConn = GetNearestConnectorAny(extendedSegment, targetMainEnd);
                                var mainEndConn = GetNearestConnectorAny(main, targetMainEnd);

                                if (extendedConn != null && mainEndConn != null)
                                {
                                    if (TryCreateFittingOnAnyApi("CreateElbowFitting", extendedConn, mainEndConn))
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[Connect] SUCCESS: Elbow connection with intermediate segment");
                                        return true;
                                    }
                                }
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[Connect] Intermediate segment too short ({segmentLength * 304.8:F1}mm < {minLeg * 304.8:F1}mm), using direct connection");
                        }
                    }
                }
            }

            // Try EXACT axis-safe nudge (original logic)
            ForceBranchEndpointToExact(newcomer, targetMainEnd);
            _doc.Regenerate();

            // Get direction for connector selection
            var newcomerUpdatedLc = newcomer.Location as LocationCurve;
            XYZ directionToMain = null;
            if (newcomerUpdatedLc?.Curve != null)
            {
                var newEnd0Updated = newcomerUpdatedLc.Curve.GetEndPoint(0);
                var newEnd1Updated = newcomerUpdatedLc.Curve.GetEndPoint(1);
                bool isEnd0Closer = newEnd0Updated.DistanceTo(targetMainEnd) < newEnd1Updated.DistanceTo(targetMainEnd);
                directionToMain = isEnd0Closer ?
                    (newEnd1Updated - newEnd0Updated).Normalize() :
                    (newEnd0Updated - newEnd1Updated).Normalize();
            }

            var branchConn = GetBestConnectorForDirection(newcomer, targetMainEnd, directionToMain);
            var endConn = GetNearestConnectorAny(main, targetMainEnd);

            if (branchConn != null && endConn != null)
            {
                if (AreDirectlyConnected(branchConn, endConn))
                {
                    TryDisconnectPair(branchConn, endConn);
                    _doc.Regenerate();
                }

                if (TryCreateFittingOnAnyApi("CreateElbowFitting", branchConn, endConn))
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] SUCCESS: Elbow connection at end");
                    return true;
                }

                if (!StrictFittingsOnly)
                {
                    try
                    {
                        branchConn.ConnectTo(endConn);
                        System.Diagnostics.Debug.WriteLine($"[Connect] SUCCESS: Direct connection at end");
                        return true;
                    }
                    catch { }
                }
            }

            System.Diagnostics.Debug.WriteLine($"[Connect] ELBOW strategy failed");
            if (StrictFittingsOnly) { strictFailed = true; }
            return false;
        }

        private bool TryExtendNewcomerWithIntermediateSegment(
            MEPCurve newcomer,
            XYZ newcomerEnd,
            XYZ intermediatePoint,
            XYZ finalTarget,
            double trayWidthFt,
            double trayHeightFt,
            out MEPCurve extendedSegment)
        {
            extendedSegment = null;

            try
            {
                // Create two intermediate segments:
                // 1. newcomerEnd -> intermediatePoint (perpendicular to newcomer)
                // 2. intermediatePoint -> finalTarget (aligned with main tray)
                var typeId = newcomer.GetTypeId();
                var levelId = newcomer.LevelId;

                var segment1Line = Line.CreateBound(newcomerEnd, intermediatePoint);
                if (segment1Line.Length < CurrentMinLeg())
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] First intermediate segment too short ({segment1Line.Length * 304.8:F1}mm)");
                    return false;
                }

                var segment2Line = Line.CreateBound(intermediatePoint, finalTarget);
                if (segment2Line.Length < CurrentMinLeg())
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] Second intermediate segment too short ({segment2Line.Length * 304.8:F1}mm)");
                    return false;
                }

                // Get connector from newcomer at the end point
                var conn1 = GetNearestConnectorAny(newcomer, newcomerEnd);
                if (conn1 == null) return false;

                // Create first intermediate segment (perpendicular to newcomer)
                var intermediateTray1 = CableTray.Create(_doc, typeId, newcomerEnd, intermediatePoint, levelId);
                if (intermediateTray1 == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] Failed to create first intermediate tray segment");
                    return false;
                }

                TrySetParamFeet(intermediateTray1, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, trayWidthFt);
                TrySetParamFeet(intermediateTray1, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, trayHeightFt);

                System.Diagnostics.Debug.WriteLine($"[Connect] Created first intermediate segment {intermediateTray1.Id}: ({newcomerEnd.X:F3}, {newcomerEnd.Y:F3}, {newcomerEnd.Z:F3}) -> ({intermediatePoint.X:F3}, {intermediatePoint.Y:F3}, {intermediatePoint.Z:F3})");

                // Create second intermediate segment (aligned with main tray)
                var intermediateTray2 = CableTray.Create(_doc, typeId, intermediatePoint, finalTarget, levelId);
                if (intermediateTray2 == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] Failed to create second intermediate tray segment");
                    _doc.Delete(intermediateTray1.Id); // Clean up first segment
                    return false;
                }

                TrySetParamFeet(intermediateTray2, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, trayWidthFt);
                TrySetParamFeet(intermediateTray2, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, trayHeightFt);

                System.Diagnostics.Debug.WriteLine($"[Connect] Created second intermediate segment {intermediateTray2.Id}: ({intermediatePoint.X:F3}, {intermediatePoint.Y:F3}, {intermediatePoint.Z:F3}) -> ({finalTarget.X:F3}, {finalTarget.Y:F3}, {finalTarget.Z:F3})");

                _doc.Regenerate();

                // Connect newcomer to first intermediate segment with elbow
                var conn2 = GetNearestConnectorAny(intermediateTray1, newcomerEnd);
                if (conn2 != null && conn1 != null)
                {
                    if (TryCreateFittingOnAnyApi("CreateElbowFitting", conn1, conn2))
                    {
                        System.Diagnostics.Debug.WriteLine($"[Connect] Created vertical elbow between newcomer and first intermediate segment");
                    }
                }

                // Connect first intermediate segment to second intermediate segment with elbow
                var conn3 = GetNearestConnectorAny(intermediateTray1, intermediatePoint);
                var conn4 = GetNearestConnectorAny(intermediateTray2, intermediatePoint);
                if (conn3 != null && conn4 != null)
                {
                    if (TryCreateFittingOnAnyApi("CreateElbowFitting", conn3, conn4))
                    {
                        System.Diagnostics.Debug.WriteLine($"[Connect] Created horizontal elbow between intermediate segments");
                    }
                }

                // Return the second intermediate segment (the one that will connect to main)
                extendedSegment = intermediateTray2 as MEPCurve;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Connect] Failed to create intermediate segments: {ex.Message}");
                return false;
            }
        }

        private bool HandlePerpendicularTeeConnection(MEPCurve newcomer, MEPCurve main, LocationCurve mainLc, XYZ mainDir, double trayWidthFt, double trayHeightFt, out bool strictFailed)
        {
            strictFailed = false;
            System.Diagnostics.Debug.WriteLine($"[Connect] Using PERPENDICULAR TEE strategy");

            var newcomerLc = newcomer.Location as LocationCurve;
            if (newcomerLc?.Curve == null) return false;

            XYZ newEnd0 = newcomerLc.Curve.GetEndPoint(0);
            XYZ newEnd1 = newcomerLc.Curve.GetEndPoint(1);

            // Project newcomer ends onto main to find attachment point
            var pr0 = mainLc.Curve.Project(newEnd0);
            var pr1 = mainLc.Curve.Project(newEnd1);

            double projDist0 = pr0 != null ? newEnd0.DistanceTo(pr0.XYZPoint) : double.MaxValue;
            double projDist1 = pr1 != null ? newEnd1.DistanceTo(pr1.XYZPoint) : double.MaxValue;

            var pr = projDist0 <= projDist1 ? pr0 : pr1;
            XYZ closestNewcomerEnd = projDist0 <= projDist1 ? newEnd0 : newEnd1;

            if (pr == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Connect] Cannot project newcomer onto main");
                return false;
            }

            double parMp = pr.Parameter;
            var attach = mainLc.Curve.Evaluate(parMp, false);

            System.Diagnostics.Debug.WriteLine($"[Connect] Attach point: ({attach.X:F2}, {attach.Y:F2}, {attach.Z:F2})");

            // Nudge branch endpoint to attach point
            ForceBranchEndpointToExact(newcomer, attach);
            _doc.Regenerate();

            var branchConnTee = GetNearestConnectorAny(newcomer, attach);
            if (branchConnTee == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Connect] Cannot find branch connector at attach point");
                return false;
            }

            // Break main curve at attach point
            double curveLen = 0.0;
            try { curveLen = mainLc.Curve.Length; } catch { }
            double size = Math.Max(trayWidthFt, trayHeightFt);
            double clampFrac = Math.Max(ShortTolFt * 4.0 / Math.Max(curveLen, ShortTolFt), (1.2 * size) / Math.Max(curveLen, size));

            MEPCurve partA = null, partB = null;
            var success = SafeBreakCurve(main, parMp, clampFrac, retries: 2, out partA, out partB);
            
            if (!success || partA == null || partB == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Connect] SafeBreakCurve failed - trying manual split");
                success = TryManualSplitCurve(main, attach, out partA, out partB);
            }

            // Re-resolve split parts
            if (!ResolveSplitPartsNearAttach(newcomer, attach, mainDir, out var ra, out var rb))
            {
                System.Diagnostics.Debug.WriteLine($"[Connect] Using original split results");
            }
            else
            {
                partA = ra;
                partB = rb;
                success = partA != null && partB != null;
            }

            if (success)
            {
                _doc.Regenerate();
                var connA = GetNearestConnectorAny(partA, attach);
                var connB = GetNearestConnectorAny(partB, attach);

                if (connA != null && connB != null)
                {
                    if (TryCreateTeeFittingWithPermutations(connA, connB, branchConnTee))
                    {
                        System.Diagnostics.Debug.WriteLine($"[Connect] SUCCESS: Perpendicular TEE connection");
                        return true;
                    }

                    if (!StrictFittingsOnly)
                    {
                        try { branchConnTee.ConnectTo(connA); } catch { }
                        try
                        {
                            connB.ConnectTo(connA);
                            System.Diagnostics.Debug.WriteLine($"[Connect] SUCCESS: Direct connections as fallback");
                            return true;
                        }
                        catch { }
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"[Connect] Perpendicular TEE strategy failed");
            if (StrictFittingsOnly) { strictFailed = true; }
            return false;
        }

        private bool HandleParallelTeeConnection(MEPCurve newcomer, MEPCurve main, LocationCurve mainLc, XYZ mainDir, double trayWidthFt, double trayHeightFt, out bool strictFailed)
        {
            strictFailed = false;
            System.Diagnostics.Debug.WriteLine($"[Connect] ========== PARALLEL TEE Strategy ==========");
            System.Diagnostics.Debug.WriteLine($"[Connect] This requires dogleg: parallel segment + perpendicular segment + TEE");

            var newcomerLc = newcomer.Location as LocationCurve;
            if (newcomerLc?.Curve == null) return false;

            XYZ newEnd0 = newcomerLc.Curve.GetEndPoint(0);
            XYZ newEnd1 = newcomerLc.Curve.GetEndPoint(1);

            // 1. Find attachment point on main (projection)
            var pr0 = mainLc.Curve.Project(newEnd0);
            var pr1 = mainLc.Curve.Project(newEnd1);

            double projDist0 = pr0 != null ? newEnd0.DistanceTo(pr0.XYZPoint) : double.MaxValue;
            double projDist1 = pr1 != null ? newEnd1.DistanceTo(pr1.XYZPoint) : double.MaxValue;

            var pr = projDist0 <= projDist1 ? pr0 : pr1;
            XYZ newcomerEndNearMain = projDist0 <= projDist1 ? newEnd0 : newEnd1;
            XYZ newcomerOtherEnd = newcomerEndNearMain.IsAlmostEqualTo(newEnd0) ? newEnd1 : newEnd0;

            if (pr == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Connect] Cannot project newcomer onto main");
                return false;
            }

            var attachPoint = pr.XYZPoint;
            System.Diagnostics.Debug.WriteLine($"[Connect] Attachment point on main: ({attachPoint.X:F3}, {attachPoint.Y:F3}, {attachPoint.Z:F3})");

            // 2. Calculate perpendicular direction to main (in XY plane)
            var mainDirXY = new XYZ(mainDir.X, mainDir.Y, 0.0);
            double mainDirLen = mainDirXY.GetLength();
            if (mainDirLen < 0.001)
            {
                System.Diagnostics.Debug.WriteLine($"[Connect] Main is vertical - cannot determine perpendicular");
                return false;
            }
            mainDirXY = mainDirXY.Normalize();

            // Perpendicular in XY plane: rotate 90 degrees
            var perpDirXY = new XYZ(-mainDirXY.Y, mainDirXY.X, 0.0);
            System.Diagnostics.Debug.WriteLine($"[Connect] Main direction XY: ({mainDirXY.X:F3}, {mainDirXY.Y:F3})");
            System.Diagnostics.Debug.WriteLine($"[Connect] Perpendicular XY: ({perpDirXY.X:F3}, {perpDirXY.Y:F3})");

            // 3. Calculate approach point (before main, at safe distance)
            double safeDistance = Math.Max(trayWidthFt * 2.5, CurrentMinLeg() * 1.5);
            
            // Determine which side of main the newcomer is on
            var toNewcomer = (newcomerEndNearMain - attachPoint);
            var toNewcomerXY = new XYZ(toNewcomer.X, toNewcomer.Y, 0.0);
            double toNewcomerLen = toNewcomerXY.GetLength();
            
            if (toNewcomerLen > 0.001)
            {
                toNewcomerXY = toNewcomerXY.Normalize();
                // Choose perpendicular direction towards newcomer
                double dot = perpDirXY.DotProduct(toNewcomerXY);
                if (dot < 0) perpDirXY = -perpDirXY;
            }

            var approachPoint = new XYZ(
                attachPoint.X + perpDirXY.X * safeDistance,
                attachPoint.Y + perpDirXY.Y * safeDistance,
                newcomerEndNearMain.Z  // Keep Z from newcomer
            );

            System.Diagnostics.Debug.WriteLine($"[Connect] Approach point: ({approachPoint.X:F3}, {approachPoint.Y:F3}, {approachPoint.Z:F3}), safe distance: {safeDistance * 304.8:F1}mm");

            // 4. Calculate turn point (where parallel becomes perpendicular)
            // Turn point is at the intersection of:
            // - Line through newcomerEndNearMain parallel to main
            // - Line through approachPoint perpendicular to main
            
            var turnPoint = CalculateTurnPoint(newcomerEndNearMain, mainDirXY, approachPoint, perpDirXY);
            System.Diagnostics.Debug.WriteLine($"[Connect] Turn point: ({turnPoint.X:F3}, {turnPoint.Y:F3}, {turnPoint.Z:F3})");

            // 5. Check if we need to create additional segments
            double distToTurn = newcomerEndNearMain.DistanceTo(turnPoint);
            double distTurnToApproach = turnPoint.DistanceTo(approachPoint);

            bool needsParallelSegment = distToTurn > ShortTolFt * 2;
            bool needsPerpendicularSegment = distTurnToApproach > ShortTolFt * 2;

            System.Diagnostics.Debug.WriteLine($"[Connect] Distance to turn: {distToTurn * 304.8:F1}mm, needs parallel segment: {needsParallelSegment}");
            System.Diagnostics.Debug.WriteLine($"[Connect] Distance turn to approach: {distTurnToApproach * 304.8:F1}mm, needs perpendicular segment: {needsPerpendicularSegment}");

            // 6. Create additional segments
            var typeId = newcomer.GetTypeId();
            var levelId = newcomer.LevelId;

            MEPCurve parallelSegment = null;
            MEPCurve perpendicularSegment = null;
            MEPCurve lastSegment = newcomer;

            try
            {
                if (needsParallelSegment)
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] Creating parallel segment: ({newcomerEndNearMain.X:F3}, {newcomerEndNearMain.Y:F3}, {newcomerEndNearMain.Z:F3}) to ({turnPoint.X:F3}, {turnPoint.Y:F3}, {turnPoint.Z:F3})");
                    
                    parallelSegment = CableTray.Create(_doc, typeId, newcomerEndNearMain, turnPoint, levelId) as MEPCurve;
                    
                    if (parallelSegment != null)
                    {
                        TrySetParamFeet(parallelSegment, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, trayWidthFt);
                        TrySetParamFeet(parallelSegment, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, trayHeightFt);
                        
                        _doc.Regenerate();
                        
                        // Connect with elbow at turn point
                        bool elbowCreated = TryCreateElbowOrUnionAt(newcomer, parallelSegment, newcomerEndNearMain);
                        System.Diagnostics.Debug.WriteLine($"[Connect] Parallel segment created, elbow at start: {elbowCreated}");
                        
                        lastSegment = parallelSegment;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[Connect] FAILED to create parallel segment");
                        strictFailed = true;
                        return false;
                    }
                }

                if (needsPerpendicularSegment)
                {
                    var startPoint = parallelSegment != null ? turnPoint : newcomerEndNearMain;
                    
                    System.Diagnostics.Debug.WriteLine($"[Connect] Creating perpendicular segment: ({startPoint.X:F3}, {startPoint.Y:F3}, {startPoint.Z:F3}) to ({approachPoint.X:F3}, {approachPoint.Y:F3}, {approachPoint.Z:F3})");
                    
                    perpendicularSegment = CableTray.Create(_doc, typeId, startPoint, approachPoint, levelId) as MEPCurve;
                    
                    if (perpendicularSegment != null)
                    {
                        TrySetParamFeet(perpendicularSegment, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, trayWidthFt);
                        TrySetParamFeet(perpendicularSegment, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, trayHeightFt);
                        
                        _doc.Regenerate();
                        
                        // Connect with elbow at turn point (horizontal turn)
                        bool elbowCreated = TryCreateElbowOrUnionAt(lastSegment, perpendicularSegment, startPoint);
                        System.Diagnostics.Debug.WriteLine($"[Connect] Perpendicular segment created, elbow at turn: {elbowCreated}");
                        
                        lastSegment = perpendicularSegment;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[Connect] FAILED to create perpendicular segment");
                        strictFailed = true;
                        return false;
                    }
                }

                // 7. Now connect the last segment to main with TEE
                System.Diagnostics.Debug.WriteLine($"[Connect] Final step: connecting last segment to main with TEE");
                
                // Nudge last segment to attach point
                ForceBranchEndpointToExact(lastSegment, attachPoint);
                _doc.Regenerate();

                var branchConnTee = GetNearestConnectorAny(lastSegment, attachPoint);
                if (branchConnTee == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] Cannot find branch connector at attach point");
                    strictFailed = true;
                    return false;
                }

                // Break main at attach point
                double curveLen = 0.0;
                try { curveLen = mainLc.Curve.Length; } catch { }
                double size = Math.Max(trayWidthFt, trayHeightFt);
                double clampFrac = Math.Max(ShortTolFt * 4.0 / Math.Max(curveLen, ShortTolFt), (1.2 * size) / Math.Max(curveLen, size));

                double parMp = pr.Parameter;
                MEPCurve partA = null, partB = null;
                var success = SafeBreakCurve(main, parMp, clampFrac, retries: 2, out partA, out partB);
                
                if (!success || partA == null || partB == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] SafeBreakCurve failed - trying manual split");
                    success = TryManualSplitCurve(main, attachPoint, out partA, out partB);
                }

                // Re-resolve split parts
                if (!ResolveSplitPartsNearAttach(lastSegment, attachPoint, mainDir, out var ra, out var rb))
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] Using original split results");
                }
                else
                {
                    partA = ra;
                    partB = rb;
                    success = partA != null && partB != null;
                }

                if (success)
                {
                    _doc.Regenerate();
                    var connA = GetNearestConnectorAny(partA, attachPoint);
                    var connB = GetNearestConnectorAny(partB, attachPoint);

                    if (connA != null && connB != null)
                    {
                        if (TryCreateTeeFittingWithPermutations(connA, connB, branchConnTee))
                        {
                            System.Diagnostics.Debug.WriteLine($"[Connect] SUCCESS: Parallel TEE with dogleg ==========");
                            return true;
                        }

                        if (!StrictFittingsOnly)
                        {
                            try { branchConnTee.ConnectTo(connA); } catch { }
                            try
                            {
                                connB.ConnectTo(connA);
                                System.Diagnostics.Debug.WriteLine($"[Connect] SUCCESS: Direct connections as fallback");
                                return true;
                            }
                            catch { }
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[Connect] Failed to create TEE connection");
                strictFailed = true;
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Connect] Exception in HandleParallelTeeConnection: {ex.Message}");
                strictFailed = true;
                return false;
            }
        }

        private XYZ CalculateTurnPoint(XYZ startPoint, XYZ parallelDir, XYZ targetPoint, XYZ perpDir)
        {
            // Calculate intersection of two lines in XY plane:
            // Line 1: through startPoint, direction parallelDir
            // Line 2: through targetPoint, direction perpDir
            
            // Parametric form:
            // L1: P = startPoint + t * parallelDir
            // L2: P = targetPoint + s * perpDir
            
            // At intersection: startPoint + t * parallelDir = targetPoint + s * perpDir
            // Solve for t using cross product method
            
            // In 2D (XY plane):
            // startPoint.X + t * parallelDir.X = targetPoint.X + s * perpDir.X
            // startPoint.Y + t * parallelDir.Y = targetPoint.Y + s * perpDir.Y
            
            // Rearrange:
            // t * parallelDir.X - s * perpDir.X = targetPoint.X - startPoint.X
            // t * parallelDir.Y - s * perpDir.Y = targetPoint.Y - startPoint.Y
            
            double dx = targetPoint.X - startPoint.X;
            double dy = targetPoint.Y - startPoint.Y;
            
            // Determinant
            double det = parallelDir.X * (-perpDir.Y) - parallelDir.Y * (-perpDir.X);
            
            if (Math.Abs(det) < 0.0001)
            {
                // Lines are parallel - shouldn't happen but fallback
                System.Diagnostics.Debug.WriteLine($"[Connect] WARNING: Lines are parallel in CalculateTurnPoint");
                return new XYZ(
                    (startPoint.X + targetPoint.X) * 0.5,
                    (startPoint.Y + targetPoint.Y) * 0.5,
                    startPoint.Z
                );
            }
            
            // Solve using Cramer's rule
            double t = (dx * (-perpDir.Y) - dy * (-perpDir.X)) / det;
            
            var turnPoint = new XYZ(
                startPoint.X + t * parallelDir.X,
                startPoint.Y + t * parallelDir.Y,
                startPoint.Z  // Keep Z from start
            );
            
            System.Diagnostics.Debug.WriteLine($"[Connect] Turn point calculation: t={t:F3}");
            
            return turnPoint;
        }

        // ===== HELPER METHODS (restored after refactoring) =====

        private bool ResolveSplitPartsNearAttach(MEPCurve newcomer, XYZ attach, XYZ mainDir, out MEPCurve partA, out MEPCurve partB)
        {
            partA = null; partB = null;
            try
            {
                double baseSize = Math.Max(CurrentMinLeg(), Math.Max(TargetTrayWidthFt, TargetTrayHeightFt));
                double radius = baseSize * 1.6; // small neighborhood around attach

                var collector = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_CableTray)
                    .WhereElementIsNotElementType();

                // normalize main XY dir
                var mxy = new XYZ(mainDir.X, mainDir.Y, 0.0);
                double mlen = Math.Sqrt(mxy.X * mxy.X + mxy.Y * mxy.Y);
                if (mlen < 1e-9) mxy = new XYZ(1, 0, 0); else mxy = new XYZ(mxy.X / mlen, mxy.Y / mlen, 0);

                foreach (var e in collector)
                {
                    var mc = e as MEPCurve; if (mc == null) continue;
                    if (newcomer != null && mc.Id == newcomer.Id) continue; // exclude branch

                    var cm = mc.ConnectorManager; if (cm == null) continue;
                    bool endNear = false;
                    foreach (Connector c in cm.Connectors)
                    {
                        if (c.ConnectorType == ConnectorType.End && c.Origin.DistanceTo(attach) <= radius) { endNear = true; break; }
                    }
                    if (!endNear) continue;

                    var lc = mc.Location as LocationCurve; if (lc?.Curve == null) continue;
                    XYZ dir;
                    if (lc.Curve is Line ln) dir = ln.Direction; else { var q0 = lc.Curve.GetEndPoint(0); var q1 = lc.Curve.GetEndPoint(1); dir = (q1 - q0); }
                    var dxy = new XYZ(dir.X, dir.Y, 0.0);
                    double dlen = Math.Sqrt(dxy.X * dxy.X + dxy.Y * dxy.Y);
                    if (dlen < 1e-9) continue;
                    dxy = new XYZ(dxy.X / dlen, dxy.Y / dlen, 0);

                    double dot = Math.Abs(dxy.X * mxy.X + dxy.Y * mxy.Y);
                    if (dot < 0.95) continue; // must align with main

                    if (partA == null) partA = mc; else if (partB == null && mc.Id != partA.Id) { partB = mc; break; }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Connect] ResolveSplitPartsNearAttach failed: {ex.Message}");
            }
            return partA != null && partB != null;
        }

        private bool ForceBranchEndpointToExact(MEPCurve branch, XYZ targetPoint)
        {
            // For ELBOW connections, we need EXACT positioning, but MUST remain axis-aligned!
            try
            {
                var lc = branch.Location as LocationCurve;
                if (lc?.Curve == null) return false;
                var line = lc.Curve as Line;
                if (line == null) return false;

                if (HasElbowOnBothEnds(branch))
                {
                    if (_originalEndpoints.TryGetValue(branch.Id.Value, out var orig) && orig.p0 != null && orig.p1 != null)
                    {
                        double desiredLen = orig.p0.DistanceTo(orig.p1);
                        double currentLen = line.Length;
                        double targetDist = Math.Min(orig.p0.DistanceTo(targetPoint), orig.p1.DistanceTo(targetPoint));
                        bool targetMatchesOriginal = targetDist < ShortTolFt * 4.0;

                        if (!targetMatchesOriginal && desiredLen > ShortTolFt && Math.Abs(desiredLen - currentLen) > ShortTolFt * 0.25)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Connect] ForceBranchEndpointToExact: skipping for {branch.Id} (elbows both ends, preserving straight run)");
                            return false;
                        }
                    }
                    else
                    {
                        double currentLen = line.Length;
                        if (currentLen > ShortTolFt)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Connect] ForceBranchEndpointToExact: no snapshot for {branch.Id}, skipping exact move to protect spacer");
                            return false;
                        }
                    }
                }

                var p0 = line.GetEndPoint(0);
                var p1 = line.GetEndPoint(1);

                // Determine which end to move (the closer one)
                double d0 = p0.DistanceTo(targetPoint);
                double d1 = p1.DistanceTo(targetPoint);

                XYZ anchor, oldEnd;
                int endIndex;

                if (d0 <= d1)
                {
                    endIndex = 0;
                    anchor = p1;
                    oldEnd = p0;
                }
                else
                {
                    endIndex = 1;
                    anchor = p0;
                    oldEnd = p1;
                }

                // CRITICAL: Cable trays MUST be axis-aligned (no angled segments!)
                // Check if targetPoint is axis-aligned with anchor
                double dx = Math.Abs(targetPoint.X - anchor.X);
                double dy = Math.Abs(targetPoint.Y - anchor.Y);
                double dz = Math.Abs(targetPoint.Z - anchor.Z);

                const double axisTol = 0.01; // ~3mm tolerance for axis alignment

                bool isAxisAligned = (dx < axisTol && dy < axisTol) ||  // Z-axis (vertical)
                                     (dx < axisTol && dz < axisTol) ||  // Y-axis
                                     (dy < axisTol && dz < axisTol);    // X-axis

                if (!isAxisAligned)
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] Direct nudge would create angled segment - trying connector connection without nudge");
                    System.Diagnostics.Debug.WriteLine($"[Connect] Anchor: ({anchor.X:F3}, {anchor.Y:F3}, {anchor.Z:F3})");
                    System.Diagnostics.Debug.WriteLine($"[Connect] Target: ({targetPoint.X:F3}, {targetPoint.Y:F3}, {targetPoint.Z:F3})");
                    System.Diagnostics.Debug.WriteLine($"[Connect] Delta: dX={dx * 304.8:F1}mm, dY={dy * 304.8:F1}mm, dZ={dz * 304.8:F1}mm");

                    // Cannot create axis-aligned segment directly
                    // Let the fitting creation proceed without nudging - Revit will auto-route if needed
                    return false; // Return false to skip nudge, but allow fitting attempt
                }

                // Move end EXACTLY to targetPoint
                XYZ moved = targetPoint;

                // Create new line
                var candidate = endIndex == 0 ? Line.CreateBound(moved, anchor) : Line.CreateBound(anchor, moved);

                // Validate minimum length
                if (candidate.Length < CurrentMinLeg())
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] Cannot nudge exactly: resulting segment too short ({candidate.Length * 304.8:F1}mm < {CurrentMinLeg() * 304.8:F1}mm)");
                    return false;
                }

                // Check if unchanged
                if (oldEnd.IsAlmostEqualTo(moved))
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] Endpoint already at target");
                    return true;
                }

                System.Diagnostics.Debug.WriteLine($"[Connect] Nudging EXACTLY: curve {branch.Id}, end {endIndex} from " +
                    $"({oldEnd.X:F3}, {oldEnd.Y:F3}, {oldEnd.Z:F3}) to ({moved.X:F3}, {moved.Y:F3}, {moved.Z:F3})");

                lc.Curve = candidate;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Connect] ForceBranchEndpointToExact failed: {ex.Message}");
                return false;
            }
        }

        private bool SafeBreakCurve(MEPCurve main, double parameter, double clampFraction, int retries, out MEPCurve partA, out MEPCurve partB)
        {
            partA = null; partB = null;
            var lc = main.Location as LocationCurve; if (lc?.Curve == null) return false;
            double p0 = lc.Curve.GetEndParameter(0);
            double p1 = lc.Curve.GetEndParameter(1);
            if (System.Math.Abs(p1 - p0) < 1e-9) return false;

            double par = parameter;
            double span = System.Math.Abs(p1 - p0);
            double clamp = System.Math.Max(span * clampFraction, span * 1e-4);

            for (int i = 0; i <= retries; i++)
            {
                double p = System.Math.Max(p0 + clamp, System.Math.Min(p1 - clamp, par));
                XYZ pt = lc.Curve.Evaluate(p, false);
                if (TryBreakCurveReflective(_doc, main, pt, out partA, out partB))
                {
                    // verify both parts are not too short
                    var la = (partA.Location as LocationCurve)?.Curve?.Length ?? 0.0;
                    var lb = (partB.Location as LocationCurve)?.Curve?.Length ?? 0.0;
                    double minLeg = CurrentMinLeg();
                    if (la >= minLeg && lb >= minLeg) return true;
                }
                clamp *= 1.5; // widen clamp and retry
            }
            return false;
        }

        private bool TryManualSplitCurve(MEPCurve main, XYZ attach, out MEPCurve partA, out MEPCurve partB)
        {
            partA = null; partB = null;
            try
            {
                var lc = main.Location as LocationCurve; if (lc?.Curve == null) return false;
                var line = lc.Curve as Line; if (line == null) return false;

                // Project attach onto the main line to guarantee colinearity
                var proj = line.Project(attach);
                var at = proj != null ? proj.XYZPoint : attach;

                var p0 = line.GetEndPoint(0);
                var p1 = line.GetEndPoint(1);
                double minLeg = CurrentMinLeg();

                // Ensure both parts will be long enough
                if (p0.DistanceTo(at) < minLeg || p1.DistanceTo(at) < minLeg)
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] Manual split aborted: one part would be too short");
                    return false;
                }

                // Shorten the original to [p0, at] and create a new tray [at, p1]
                var typeId = main.GetTypeId();
                var levelId = main.LevelId;
                var w = TryGetParamFeet(main, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                var h = TryGetParamFeet(main, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);

                // Update original
                lc.Curve = Line.CreateBound(p0, at);

                // Create new part
                var newElem = CableTray.Create(_doc, typeId, at, p1, levelId) as MEPCurve;
                if (newElem == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Connect] Manual split failed: could not create new cable tray part");
                    return false;
                }
                TrySetParamFeet(newElem, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, w);
                TrySetParamFeet(newElem, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, h);

                partA = main;
                partB = newElem;
                System.Diagnostics.Debug.WriteLine($"[Connect] Manual split SUCCESS at attach point");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Connect] Manual split failed: {ex.Message}");
                return false;
            }
        }

        private bool TryCreateTeeFittingWithPermutations(Connector connA, Connector connB, Connector branchConn)
        {
            // Document.Create.NewTeeFitting(conn1, conn2, conn3)
            // Typically: (main1, main2, branch) but try different permutations if needed
            var permutations = new[] {
                new[] { connA, connB, branchConn },  // Standard: main, main, branch
                new[] { connA, branchConn, connB },  // Branch in middle
                new[] { branchConn, connA, connB },  // Branch first
            };

            System.Diagnostics.Debug.WriteLine($"[Fitting] Trying NewTeeFitting with {permutations.Length} permutations...");

            for (int i = 0; i < permutations.Length; i++)
            {
                System.Diagnostics.Debug.WriteLine($"[Fitting] Permutation {i + 1}/{permutations.Length}: connectors at distances " +
                    $"{permutations[i][0].Origin.DistanceTo(permutations[i][1].Origin) * 304.8:F1}mm, " +
                    $"{permutations[i][1].Origin.DistanceTo(permutations[i][2].Origin) * 304.8:F1}mm");

                try
                {
                    var fitting = _doc.Create.NewTeeFitting(permutations[i][0], permutations[i][1], permutations[i][2]);

                    if (fitting != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Fitting] SUCCESS: NewTeeFitting with permutation {i + 1}, ID={fitting.Id}, Type={fitting.Name}");
                        return true;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[Fitting] Permutation {i + 1} returned null");
                    }
                }
                catch (Exception ex)
                {
                    var errorMsg = ex.InnerException?.Message ?? ex.Message;
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Permutation {i + 1} failed: {errorMsg}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"[Fitting] All NewTeeFitting permutations failed");
            return false;
        }

        private bool TryCreateFittingOnAnyApi(string methodName, params Connector[] connectors)
        {
            // Validate connector sizes before attempting to create fitting
            if (!ValidateConnectorCompatibility(connectors, methodName))
            {
                return false;
            }

            // In Revit 2024, fittings are created via Document.Create.NewXXXFitting methods
            System.Diagnostics.Debug.WriteLine($"[Fitting] Creating {methodName} using Document.Create API");

            try
            {
                FamilyInstance fitting = null;

                if (methodName == "CreateElbowFitting" && connectors.Length == 2)
                {
                    fitting = _doc.Create.NewElbowFitting(connectors[0], connectors[1]);
                }
                else if (methodName == "CreateTeeFitting" && connectors.Length == 3)
                {
                    fitting = _doc.Create.NewTeeFitting(connectors[0], connectors[1], connectors[2]);
                }
                else if (methodName == "CreateUnionFitting" && connectors.Length == 2)
                {
                    fitting = _doc.Create.NewUnionFitting(connectors[0], connectors[1]);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Unknown method {methodName} or wrong connector count {connectors.Length}");
                    return false;
                }

                if (fitting != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] SUCCESS: Created fitting with ID {fitting.Id}, Type: {fitting.Name}");
                    return true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Document.Create.New{methodName.Replace("Create", "")} returned null");
                    return false;
                }
            }
            catch (Exception ex)
            {
                var errorMsg = ex.InnerException?.Message ?? ex.Message;
                System.Diagnostics.Debug.WriteLine($"[Fitting] Document.Create failed: {errorMsg}");

                // Check for common errors
                if (errorMsg.Contains("family") || errorMsg.Contains("type"))
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Hint: Missing or incompatible fitting family in project");
                }
                if (errorMsg.Contains("distance") || errorMsg.Contains("location"))
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Hint: Connectors may be too far apart. Distance: {connectors[0].Origin.DistanceTo(connectors[1].Origin) * 304.8:F1}mm");
                }

                return false;
            }
        }

        private bool ValidateConnectorCompatibility(Connector[] connectors, string methodName)
        {
            if (connectors == null || connectors.Length == 0) return false;

            // Check for null connectors
            for (int i = 0; i < connectors.Length; i++)
            {
                if (connectors[i] == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Validation failed: Connector {i} is null");
                    return false;
                }
            }

            // Log connector shapes for diagnostics
            var shape0 = connectors[0].Shape;
            System.Diagnostics.Debug.WriteLine($"[Fitting] Connector shapes: {string.Join(", ", connectors.Select(c => c.Shape.ToString()))}");

            // Check connector sizes compatibility based on shape type
            const double sizeTolerance = 0.01; // ~3mm tolerance

            // For rectangular connectors (cable trays), compare width and height
            if (shape0 == ConnectorProfileType.Rectangular)
            {
                double refWidth = connectors[0].Width;
                double refHeight = connectors[0].Height;
                System.Diagnostics.Debug.WriteLine($"[Fitting] Reference connector: W={refWidth * 304.8:F1}mm, H={refHeight * 304.8:F1}mm");

                for (int i = 1; i < connectors.Length; i++)
                {
                    if (connectors[i].Shape != ConnectorProfileType.Rectangular)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Fitting] Validation failed: Shape mismatch C0=Rectangular vs C{i}={connectors[i].Shape}");
                        return false;
                    }

                    double diffW = System.Math.Abs(connectors[i].Width - refWidth);
                    double diffH = System.Math.Abs(connectors[i].Height - refHeight);

                    if (diffW > sizeTolerance || diffH > sizeTolerance)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Fitting] Validation failed for {methodName}: Size mismatch. " +
                            $"C0: W={refWidth * 304.8:F1}mm H={refHeight * 304.8:F1}mm vs " +
                            $"C{i}: W={connectors[i].Width * 304.8:F1}mm H={connectors[i].Height * 304.8:F1}mm");
                        return false;
                    }
                }
            }
            // For round connectors (conduits), compare radius
            else if (shape0 == ConnectorProfileType.Round)
            {
                double refRadius = connectors[0].Radius;
                System.Diagnostics.Debug.WriteLine($"[Fitting] Reference connector: R={refRadius * 304.8:F1}mm");

                for (int i = 1; i < connectors.Length; i++)
                {
                    if (connectors[i].Shape != ConnectorProfileType.Round)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Fitting] Validation failed: Shape mismatch C0=Round vs C{i}={connectors[i].Shape}");
                        return false;
                    }

                    double diff = System.Math.Abs(connectors[i].Radius - refRadius);
                    if (diff > sizeTolerance)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Fitting] Validation failed for {methodName}: Radius mismatch " +
                            $"C0={refRadius * 304.8:F1}mm vs C{i}={connectors[i].Radius * 304.8:F1}mm");
                        return false;
                    }
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Fitting] Warning: Unsupported connector shape: {shape0}");
                // Don't fail for other shapes, let Revit API handle it
            }

            // For CreateTeeFitting, validate we have exactly 3 connectors
            if (methodName == "CreateTeeFitting" && connectors.Length != 3)
            {
                System.Diagnostics.Debug.WriteLine($"[Fitting] Validation failed: CreateTeeFitting requires 3 connectors, got {connectors.Length}");
                return false;
            }

            // For CreateElbowFitting/CreateUnionFitting, validate we have exactly 2 connectors
            if ((methodName == "CreateElbowFitting" || methodName == "CreateUnionFitting") && connectors.Length != 2)
            {
                System.Diagnostics.Debug.WriteLine($"[Fitting] Validation failed: {methodName} requires 2 connectors, got {connectors.Length}");
                return false;
            }

            return true;
        }

        private static IEnumerable<Connector> GetOpenConnectors(MEPCurve c)
        {
            var cm = c?.ConnectorManager; if (cm == null) yield break;
            foreach (Connector con in cm.Connectors)
            {
                if (!con.IsConnected) yield return con;
            }
        }

        private static Connector GetSingleOpenConnector(MEPCurve c) => GetOpenConnectors(c).FirstOrDefault();

        private static Connector GetNearestOpenConnector(MEPCurve c, XYZ p)
        {
            Connector best = null; double bestD = double.MaxValue;
            foreach (var con in GetOpenConnectors(c))
            {
                var d = con.Origin.DistanceTo(p);
                if (d < bestD) { bestD = d; best = con; }
            }
            return best;
        }

        private static Connector GetNearestConnectorAny(MEPCurve c, XYZ p)
        {
            var cm = c?.ConnectorManager; if (cm == null) return null;
            Connector best = null; double bestD = double.MaxValue;
            foreach (Connector con in cm.Connectors)
            {
                var d = con.Origin.DistanceTo(p);
                if (d < bestD) { bestD = d; best = con; }
            }
            return best;
        }

        private static Connector GetBestConnectorForDirection(MEPCurve c, XYZ point, XYZ preferredDirection)
        {
            // Get nearest connector first
            var nearest = GetNearestConnectorAny(c, point);
            if (nearest == null) return null;

            // If preferred direction is not specified or zero, just return nearest
            if (preferredDirection == null || preferredDirection.GetLength() < 0.001)
                return nearest;

            var cm = c?.ConnectorManager;
            if (cm == null) return nearest;

            // Among connectors close to the point, pick the one best aligned with preferred direction
            const double proximityTol = 0.05; // ~15mm
            double nearestDist = nearest.Origin.DistanceTo(point);

            Connector bestAligned = null;
            double bestAlignment = 0.0; // Only consider positive alignment

            foreach (Connector con in cm.Connectors)
            {
                double dist = con.Origin.DistanceTo(point);

                // Only consider connectors within proximity tolerance
                if (dist <= nearestDist + proximityTol)
                {
                    try
                    {
                        var conDir = con.CoordinateSystem?.BasisZ;
                        if (conDir != null)
                        {
                          // Calculate alignment: dot product of normalized vectors
                          var alignment = conDir.Normalize().DotProduct(preferredDirection.Normalize());

                          // Prefer connectors pointing in the preferred direction (alignment > 0)
                          if (alignment > bestAlignment)
                          {
                            bestAlignment = alignment;
                            bestAligned = con;
                          }
                        }
                    }
                    catch { }
                }
            }

            if (bestAligned != null)
            {
                System.Diagnostics.Debug.WriteLine($"[Connector] Best aligned connector: alignment={bestAlignment:F2}");
                return bestAligned;
            }

            // Fallback to nearest if none aligned positively
            System.Diagnostics.Debug.WriteLine($"[Connector] Falling back to nearest connector (no positive alignment found)");
            return nearest;
        }

        private static bool AreDirectlyConnected(Connector a, Connector b)
        {
            if (a == null || b == null) return false;
            if (!a.IsConnected || !b.IsConnected) return false;
            foreach (Connector other in a.AllRefs)
            {
                if (other.Owner?.Id == b.Owner?.Id && other.Id == b.Id) return true;
            }
            return false;
        }

        private static void TryDisconnectPair(Connector a, Connector b)
        {
            try { a.DisconnectFrom(b); } catch { }
            try { b.DisconnectFrom(a); } catch { }
        }

        private static bool TryBreakCurveReflective(Document doc, MEPCurve main, XYZ point, out MEPCurve partA, out MEPCurve partB)
        {
            partA = null; partB = null;
            var candidates = new[] {
                "Autodesk.Revit.DB.MEPCurveUtils, RevitAPI",
                "Autodesk.Revit.DB.Electrical.ElectricalUtils, RevitAPI",
                "Autodesk.Revit.DB.Mechanical.MechanicalUtils, RevitAPI",
                "Autodesk.Revit.DB.Plumbing.PlumbingUtils, RevitAPI"
            };

            foreach (var typeName in candidates)
            {
                var t = Type.GetType(typeName, false);
                if (t == null) continue;
                var m = t.GetMethod("BreakCurve", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Document), typeof(ElementId), typeof(XYZ) }, null);
                if (m == null) continue;
                try
                {
                    var res = m.Invoke(null, new object[] { doc, main.Id, point });
                    if (res is ElementId newId && newId != ElementId.InvalidElementId)
                    {
                        partA = (MEPCurve)doc.GetElement(main.Id);
                        partB = (MEPCurve)doc.GetElement(newId);
                        return partA != null && partB != null;
                    }
                }
                catch { }
            }
            return false;
        }

        private void PostJoinPass(BuildResult result)
        {
            if (result?.NewElements == null || result.NewElements.Count < 2) return;
            var curves = result.NewElements
                .Select(id => _doc.GetElement(id) as MEPCurve)
                .Where(m => m != null)
                .ToList();

            double joinTol = Math.Max(ShortTolFt * 6.0, CurrentMinLeg() * 0.05); // ~few mm to 5% of min leg

            for (int i = 1; i < curves.Count; i++)
            {
                var a = curves[i - 1];
                var b = curves[i];
                if (a == null || b == null) continue;

                var lcA = a.Location as LocationCurve; var lcB = b.Location as LocationCurve;
                if (lcA?.Curve == null || lcB?.Curve == null) continue;

                // pick closest pair of endpoints
                var endsA = new[] { lcA.Curve.GetEndPoint(0), lcA.Curve.GetEndPoint(1) };
                var endsB = new[] { lcB.Curve.GetEndPoint(0), lcB.Curve.GetEndPoint(1) };
                double best = double.MaxValue; XYZ pa = null, pb = null; int ia = -1, ib = -1;
                for (int ea = 0; ea < 2; ea++)
                {
                    for (int eb = 0; eb < 2; eb++)
                    {
                        double d = endsA[ea].DistanceTo(endsB[eb]);
                        if (d < best)
                        {
                            best = d; pa = endsA[ea]; pb = endsB[eb]; ia = ea; ib = eb;
                        }
                    }
                }

                // Check if fitting already exists between these segments
                var joint = (pa + pb) * 0.5;
                var connA = GetNearestConnectorAny(a, joint);
                var connB = GetNearestConnectorAny(b, joint);

                if (connA != null && connB != null && FittingExistsAt(joint, connA, connB))
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] PostJoinPass: Fitting already exists between {a.Id} and {b.Id}, skipping");
                    continue; // Skip if fitting already exists
                }

                // If already extremely close, try simple elbow/union first
                bool alreadyOk = TryCreateElbowOrUnionAt(a, b, joint);
                if (alreadyOk) { result.Elbows++; continue; }

                // If small gap, nudge both ends to the midpoint then retry
                if (best <= joinTol)
                {
                    // Try EXACT nudge first to ensure overlap
                    ForceBranchEndpointToExact(a, joint);
                    ForceBranchEndpointToExact(b, joint);
                    _doc.Regenerate();

                    if (TryCreateElbowOrUnionAt(a, b, joint) || TryNudgeAndCreateElbowOrUnion(a, b, joint))
                    {
                        result.Elbows++;
                    }
                    else
                    {
                        TryConnectFallbackAt(a, b, joint);
                    }
                }
            }
        }

        private bool TryNudgeAndCreateElbowOrUnion(MEPCurve a, MEPCurve b, XYZ joint)
        {
            // Try to move endpoints a little towards joint and retry (axis-preserving)
            var lcA = a.Location as LocationCurve; var lcB = b.Location as LocationCurve;
            if (lcA?.Curve == null || lcB?.Curve == null) return false;

            // find nearest ends to joint
            int ia = lcA.Curve.GetEndPoint(0).DistanceTo(joint) <= lcA.Curve.GetEndPoint(1).DistanceTo(joint) ? 0 : 1;
            int ib = lcB.Curve.GetEndPoint(0).DistanceTo(joint) <= lcB.Curve.GetEndPoint(1).DistanceTo(joint) ? 0 : 1;

            bool movedA = NudgeEndToAxisPreserving(a, ia, joint, out _);
            bool movedB = NudgeEndToAxisPreserving(b, ib, joint, out _);
            if (movedA || movedB)
            {
                _doc.Regenerate();
                if (TryCreateElbowOrUnionAt(a, b, joint)) return true;
            }
            return false;
        }

        private bool NudgeEndToAxisPreserving(MEPCurve c, int endIndex, XYZ targetPoint, out Line newLine)
        {
            newLine = null;
            try
            {
                var lc = c.Location as LocationCurve; if (lc?.Curve == null) return false;
                var line = lc.Curve as Line; if (line == null) return false;

                if (HasElbowOnBothEnds(c))
                {
                    if (_originalEndpoints.TryGetValue(c.Id.Value, out var orig) && orig.p0 != null && orig.p1 != null)
                    {
                        double targetDist = Math.Min(orig.p0.DistanceTo(targetPoint), orig.p1.DistanceTo(targetPoint));
                        if (targetDist >= ShortTolFt * 4.0)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Fitting] Skipping axis nudge for curve {c.Id} (target not near original endpoint, preserving spacer)");
                            return false;
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[Fitting] Skipping axis nudge for curve {c.Id} (no snapshot, preserving spacer)");
                        return false;
                    }
                }

                var p0 = line.GetEndPoint(0);
                var p1 = line.GetEndPoint(1);

                XYZ anchor = endIndex == 0 ? p1 : p0;
                XYZ oldEnd = endIndex == 0 ? p0 : p1;

                // CRITICAL: Preserve axis - cable trays must be horizontal/vertical only, no angled segments!
                // Determine dominant axis of the line
                var dir = line.Direction;
                double absX = Math.Abs(dir.X);
                double absY = Math.Abs(dir.Y);
                double absZ = Math.Abs(dir.Z);

                int axis; // 0=X, 1=Y, 2=Z
                if (absX >= absY && absX >= absZ) axis = 0;
                else if (absY >= absZ) axis = 1;
                else axis = 2;

                // Move end along the dominant axis only, preserving other coordinates from anchor.
                XYZ moved;
                if (axis == 0) moved = new XYZ(targetPoint.X, anchor.Y, anchor.Z);  // X-axis: move X, keep Y,Z
                else if (axis == 1) moved = new XYZ(anchor.X, targetPoint.Y, anchor.Z);  // Y-axis: move Y, keep X,Z
                else moved = new XYZ(anchor.X, anchor.Y, targetPoint.Z);  // Z-axis: move Z, keep X,Y

                // Keep minimal length
                var candidate = endIndex == 0 ? Line.CreateBound(moved, anchor) : Line.CreateBound(anchor, moved);
                if (candidate.Length < CurrentMinLeg())
                {
                    System.Diagnostics.Debug.WriteLine($"[Fitting] Cannot nudge: resulting segment too short ({candidate.Length * 304.8:F1}mm < {CurrentMinLeg() * 304.8:F1}mm)");
                    return false;
                }

                // No-op if unchanged
                if (oldEnd.IsAlmostEqualTo(moved))
                {
                    return false;
                }

                System.Diagnostics.Debug.WriteLine($"[Fitting] Nudging curve {c.Id}: moving end {endIndex} along axis {(axis == 0 ? "X" : axis == 1 ? "Y" : "Z")} from " +
                    $"({oldEnd.X:F3}, {oldEnd.Y:F3}, {oldEnd.Z:F3}) to ({moved.X:F3}, {moved.Y:F3}, {moved.Z:F3})");

                lc.Curve = candidate;
                newLine = candidate;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Fitting] NudgeEndToAxisPreserving failed: {ex.Message}");
            }
            return false;
        }
    }
}
