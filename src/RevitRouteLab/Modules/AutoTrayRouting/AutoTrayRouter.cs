using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitRouteLab.AutoTrayRouting.Config;
using RevitRouteLab.AutoTrayRouting.Diagnostics;
using RevitRouteLab.AutoTrayRouting.Routing;
using RevitRouteLab.AutoTrayRouting.Search;
using RevitRouteLab.AutoTrayRouting.Services;

namespace RevitRouteLab.AutoTrayRouting
{
    /// <summary>
    /// Orchestrates the AutoRoute process for two selected elements.
    /// </summary>
    public class AutoTrayRouter
    {
        // Local routing toggles (can be later exposed via settings JSON)
        private const bool StrictFittingsOnlyDefault = true;
        private const double MinLegFactorDefault = 0.6;
        private const double ShortCurveTolFactorDefault = 4.0;

        public Result Run(UIDocument uiDoc, ElementId a, ElementId b, AutoTrayRoutingSettings cfg, RoutingOptions options = null)
        {
            var doc = uiDoc.Document;
            var logger = new RouterLogger();

            // Use default options if none provided (backward compatible)
            if (options == null)
            {
                options = new RoutingOptions { CreateCableTray = true, CreateConduit = false };
            }

            var e1 = doc.GetElement(a);
            var e2 = doc.GetElement(b);
            if (e1 == null || e2 == null) return Result.Failed;

            var trays = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_CableTray)
                .WhereElementIsNotElementType()
                .ToElements();

            if (!trays.Any())
            {
                TaskDialog.Show("AutoRoute", "Brak korytek w modelu.");
                return Result.Cancelled;
            }

            var resolver = new ServiceTypeResolver(cfg);
            var search = new CableTraySearchService(doc, cfg, trays);
            var planner = new PathPlanner(cfg)
            {
                MinLegFactor = MinLegFactorDefault,
                ShortCurveTolFactor = ShortCurveTolFactorDefault
            };
            var builder = new ConnectionBuilder(doc)
            {
                MinLegFactor = MinLegFactorDefault,
                StrictFittingsOnly = StrictFittingsOnlyDefault
            };

            var tg = new TransactionGroup(doc, "RevitRouteLab AutoRoute Cable Trays");
            tg.Start();

            try
            {
                RouteOne(uiDoc, cfg, logger, e1, resolver, search, planner, builder, options);
                RouteOne(uiDoc, cfg, logger, e2, resolver, search, planner, builder, options);

                tg.Assimilate();
            }
            catch (System.Exception ex)
            {
                tg.RollBack();
                TaskDialog.Show("AutoRoute", "Błąd: " + ex.Message);
                return Result.Failed;
            }

            logger.Info($"Config StrictFittingsOnly={builder.StrictFittingsOnly}, MinLegFactor={planner.MinLegFactor:F2}, ShortCurveTolFactor={planner.ShortCurveTolFactor:F2}");
            logger.ShowSummary("RevitRouteLab AutoRoute");
            return Result.Succeeded;
        }

        private static bool IsXAligned(Line s)
        {
            var d = s.Direction; return Math.Abs(d.X) > Math.Abs(d.Y) && Math.Abs(d.Y) < 1e-6;
        }
        private static bool IsYAligned(Line s)
        {
            var d = s.Direction; return Math.Abs(d.Y) > Math.Abs(d.X) && Math.Abs(d.X) < 1e-6;
        }

        // Compute how much of the plan crosses the device bounding box (XY). Lower is better.
        private static double ComputeBBoxOverlapLength(Element src, IList<Line> segs)
        {
            try
            {
                var bb = src.get_BoundingBox(null);
                if (bb == null || segs == null) return 0.0;
                double minX = Math.Min(bb.Min.X, bb.Max.X);
                double maxX = Math.Max(bb.Min.X, bb.Max.X);
                double minY = Math.Min(bb.Min.Y, bb.Max.Y);
                double maxY = Math.Max(bb.Min.Y, bb.Max.Y);
                double overlap = 0.0;
                foreach (var s in segs)
                {
                    if (s == null) continue;
                    var p0 = s.GetEndPoint(0); var p1 = s.GetEndPoint(1);
                    // Only consider horizontal XY segments
                    if (Math.Abs(p0.Z - p1.Z) > 1e-6) continue;
                    if (IsXAligned(s))
                    {
                        double y = p0.Y; if (y < minY - 1e-6 || y > maxY + 1e-6) continue;
                        double a = Math.Min(p0.X, p1.X); double b = Math.Max(p0.X, p1.X);
                        double lo = Math.Max(a, minX); double hi = Math.Min(b, maxX);
                        if (hi > lo) overlap += (hi - lo);
                    }
                    else if (IsYAligned(s))
                    {
                        double x = p0.X; if (x < minX - 1e-6 || x > maxX + 1e-6) continue;
                        double a = Math.Min(p0.Y, p1.Y); double b = Math.Max(p0.Y, p1.Y);
                        double lo = Math.Max(a, minY); double hi = Math.Min(b, maxY);
                        if (hi > lo) overlap += (hi - lo);
                    }
                }
                return overlap;
            }
            catch { return 0.0; }
        }

        // Signed outward score as before
        private static double ComputeOutwardScore(Element src, IList<Line> segs, XYZ trayDirXY)
        {
            try
            {
                if (src == null || segs == null || segs.Count == 0) return double.NegativeInfinity;
                var bb = src.get_BoundingBox(null); if (bb == null) return double.NegativeInfinity;

                XYZ back = XYZ.BasisY * -1.0;
                var fi = src as FamilyInstance;
                if (fi?.FacingOrientation != null)
                {
                    var vFront = fi.FacingOrientation; back = new XYZ(-vFront.X, -vFront.Y, 0.0);
                }
                double bl = Math.Sqrt(back.X * back.X + back.Y * back.Y);
                if (bl < 1e-9) back = new XYZ(0, -1, 0); else back = new XYZ(back.X / bl, back.Y / bl, 0);

                var t = new XYZ(trayDirXY.X, trayDirXY.Y, 0);
                double tlen = Math.Sqrt(t.X * t.X + t.Y * t.Y);
                if (tlen < 1e-9) t = new XYZ(1, 0, 0); else t = new XYZ(t.X / tlen, t.Y / tlen, 0);

                Line aligned = null;
                foreach (var s in segs)
                {
                    if (s == null) continue; var d = s.Direction; var dxy = new XYZ(d.X, d.Y, 0);
                    var dl = Math.Sqrt(dxy.X * dxy.X + dxy.Y * dxy.Y); if (dl < 1e-9) continue;
                    dxy = new XYZ(dxy.X / dl, dxy.Y / dl, 0);
                    if (Math.Abs(dxy.X * t.X + dxy.Y * t.Y) > 0.95) { aligned = s; break; }
                }
                if (aligned == null) return double.NegativeInfinity;

                var a0 = aligned.GetEndPoint(0); var a1 = aligned.GetEndPoint(1);
                var mid = new XYZ((a0.X + a1.X) * 0.5, (a0.Y + a1.Y) * 0.5, 0);
                var center = new XYZ((bb.Min.X + bb.Max.X) * 0.5, (bb.Min.Y + bb.Max.Y) * 0.5, 0);
                var v = new XYZ(mid.X - center.X, mid.Y - center.Y, 0);
                double score = v.X * back.X + v.Y * back.Y;

                // Small bias for more outside distance when already outside of bbox band
                bool trayInX = Math.Abs(t.X) > Math.Abs(t.Y);
                if (trayInX)
                {
                    double y = a0.Y; double minY = Math.Min(bb.Min.Y, bb.Max.Y); double maxY = Math.Max(bb.Min.Y, bb.Max.Y);
                    double extra = 0.0; if (y < minY) extra = (minY - y); else if (y > maxY) extra = (y - maxY);
                    score += extra * 0.25;
                }
                else
                {
                    double x = a0.X; double minX = Math.Min(bb.Min.X, bb.Max.X); double maxX = Math.Max(bb.Min.X, bb.Max.X);
                    double extra = 0.0; if (x < minX) extra = (minX - x); else if (x > maxX) extra = (x - maxX);
                    score += extra * 0.25;
                }
                return score;
            }
            catch { return double.NegativeInfinity; }
        }

        private void RouteOne(UIDocument uiDoc, AutoTrayRoutingSettings cfg, RouterLogger logger, Element src,
            ServiceTypeResolver resolver, CableTraySearchService search, PathPlanner planner, ConnectionBuilder builder, RoutingOptions options)
        {
            var doc = uiDoc.Document;
            var desired = resolver.Resolve(src, out bool matched);
            if (!matched) logger.Warn($"Brak dopasowania Service Type dla {src.Name}; użyto domyślnego: {cfg.DefaultServiceType}");

            var cand = search.FindBestCandidate(src, desired);
            if (cand == null)
            {
                TaskDialog.Show("AutoRoute", $"Brak korytka w promieniu {(cfg.SearchRadiusFt*304.8/1000.0):F1} m. Zwiększ searchRadius lub wybierz inne elementy.");
                return;
            }

            // propagate tray dims to planner and builder for adaptive thresholds
            planner.TargetTrayWidthFt = cand.WidthFt;
            planner.TargetTrayHeightFt = cand.HeightFt;
            builder.TargetTrayWidthFt = cand.WidthFt;
            builder.TargetTrayHeightFt = cand.HeightFt;

            // provide outward direction to the builder
            var backHint = search.GetBackFaceInfo(src);
            var outXY = new XYZ(backHint.BackDir.X, backHint.BackDir.Y, 0);
            double outLen = Math.Sqrt(outXY.X * outXY.X + outXY.Y * outXY.Y);
            if (outLen > 1e-9) outXY = new XYZ(outXY.X / outLen, outXY.Y / outLen, 0);
            builder.PreferredOutwardDirXY = outXY;

            var connector = CableTraySearchService.GetSourceConnector(src);
            IList<Line> segsBest = null; double bestLen = double.MaxValue;

            // Variant A: strict riser (vertical then straight)
            IList<Line> segsRiser = null; IList<Line> segsOpt = null;
            XYZ startPoint;
            if (connector != null) startPoint = connector.Origin; else startPoint = backHint.Point;

            segsRiser = planner.PlanRiserStraight(startPoint, cand.AttachPoint.Z, cand.AttachPoint);

            // Variant B: optimized with lateral step and tray alignment
            // Normalize tray direction in XY plane
            var trayDir2D = new XYZ(cand.TrayDirXY.X, cand.TrayDirXY.Y, 0);
            double trayDirLen = System.Math.Sqrt(trayDir2D.X * trayDir2D.X + trayDir2D.Y * trayDir2D.Y);
            if (trayDirLen < 1e-9) trayDir2D = new XYZ(0, 1, 0); else trayDir2D = new XYZ(trayDir2D.X / trayDirLen, trayDir2D.Y / trayDirLen, 0);

            // Perpendicular in 2D
            var perpDir1 = new XYZ(-trayDir2D.Y, trayDir2D.X, 0);
            var perpDir2 = new XYZ(trayDir2D.Y, -trayDir2D.X, 0);

            bool lateralIsParallelToTray = false;

            // Check if attach at end
            bool attachAtTrayEnd = false;
            var trayLoc = cand.Tray.Location as LocationCurve;
            if (trayLoc != null)
            {
                var trayCurve = trayLoc.Curve;
                var trayStart = trayCurve.GetEndPoint(0);
                var trayEnd = trayCurve.GetEndPoint(1);
                var distToStart = cand.AttachPoint.DistanceTo(trayStart);
                var distToEnd = cand.AttachPoint.DistanceTo(trayEnd);
                var minDistToEnd = System.Math.Min(distToStart, distToEnd);
                attachAtTrayEnd = minDistToEnd < 1.0;
                System.Diagnostics.Debug.WriteLine($"[AutoRouter] Attach distance to tray ends: start={distToStart*304.8:F1}mm, end={distToEnd*304.8:F1}mm, min={minDistToEnd*304.8:F1}mm, attachAtEnd={attachAtTrayEnd}");
            }

            if (connector != null)
            {
                var startPt = connector.Origin;
                // Vector perf analysis omitted for brevity; decide side scenario based on perpendicular alignment check
                bool alignedPerpendicular;
                if (Math.Abs(trayDir2D.X) > 0.5)
                    alignedPerpendicular = Math.Abs(startPt.Y - cand.AttachPoint.Y) < 0.1;
                else
                    alignedPerpendicular = Math.Abs(startPt.X - cand.AttachPoint.X) < 0.1;

                if (!attachAtTrayEnd && !alignedPerpendicular)
                {
                    lateralIsParallelToTray = true;
                    var latCandidates = new[] { trayDir2D, new XYZ(-trayDir2D.X, -trayDir2D.Y, 0) };
                    IList<Line> best = null; double bestOverlap = double.MaxValue; double bestScore = double.NegativeInfinity; double bestLenLocal = double.MaxValue; XYZ chosenLat = latCandidates[0];
                    foreach (var lat in latCandidates)
                    {
                        var s = planner.PlanAtTrayElevationOptimized(startPt, cand.AttachPoint.Z, cand.AttachPoint, lat, cand.TrayDirXY, lateralIsParallelToTray: true);
                        double overlap = ComputeBBoxOverlapLength(src, s);
                        double score = ComputeOutwardScore(src, s, cand.TrayDirXY);
                        double L = 0; foreach (var ln in s) L += ln.Length;
                        System.Diagnostics.Debug.WriteLine($"[AutoRouter] Side candidate lat=({lat.X:F2},{lat.Y:F2}) → overlap={overlap*304.8:F1}mm, outwardScore={score*304.8:F1}mm, len={L*304.8:F1}mm");
                        // PREFER higher outward score instead of lower overlap for consistent elbow orientation
                        if (score > bestScore + 1e-6 || (Math.Abs(score - bestScore) < 1e-6 && (overlap < bestOverlap || (Math.Abs(overlap - bestOverlap) < 1e-6 && L < bestLenLocal))))
                        {
                            bestOverlap = overlap; bestScore = score; bestLenLocal = L; best = s; chosenLat = lat;
                        }
                    }
                    segsOpt = best; bestLen = bestLenLocal;
                    System.Diagnostics.Debug.WriteLine($"[AutoRouter] Side approach: picked lateral {(chosenLat.IsAlmostEqualTo(trayDir2D) ? "+trayDir" : "-trayDir")} (max outward score)");
                }
                else
                {
                    foreach (var lat in new[] { perpDir1, perpDir2 })
                    {
                        var s = planner.PlanAtTrayElevationOptimized(startPt, cand.AttachPoint.Z, cand.AttachPoint, lat, cand.TrayDirXY, lateralIsParallelToTray: false);
                        double L = 0; foreach (var ln in s) L += ln.Length;
                        if (segsOpt == null || L < bestLen) { segsOpt = s; bestLen = L; }
                    }
                }
            }
            else
            {
                var backInfo = search.GetBackFaceInfo(src);
                bool alignedPerpendicular;
                if (Math.Abs(trayDir2D.X) > 0.5)
                    alignedPerpendicular = Math.Abs(backInfo.Point.Y - cand.AttachPoint.Y) < 0.1;
                else
                    alignedPerpendicular = Math.Abs(backInfo.Point.X - cand.AttachPoint.X) < 0.1;

                if (!attachAtTrayEnd && !alignedPerpendicular)
                {
                    lateralIsParallelToTray = true;
                    var latCandidates = new[] { trayDir2D, new XYZ(-trayDir2D.X, -trayDir2D.Y, 0) };
                    IList<Line> best = null; double bestOverlap = double.MaxValue; double bestScore = double.NegativeInfinity; double bestLenLocal = double.MaxValue; XYZ chosenLat = latCandidates[0];
                    foreach (var lat in latCandidates)
                    {
                        var s = planner.PlanAtTrayElevationOptimized(backInfo.Point, cand.AttachPoint.Z, cand.AttachPoint, lat, cand.TrayDirXY, lateralIsParallelToTray: true);
                        double overlap = ComputeBBoxOverlapLength(src, s);
                        double score = ComputeOutwardScore(src, s, cand.TrayDirXY);
                        double L = 0; foreach (var ln in s) L += ln.Length;
                        System.Diagnostics.Debug.WriteLine($"[AutoRouter] Side (no connector) lat=({lat.X:F2},{lat.Y:F2}) → overlap={overlap*304.8:F1}mm, outwardScore={score*304.8:F1}mm, len={L*304.8:F1}mm");
                        if (overlap + 1e-6 < bestOverlap || (Math.Abs(overlap - bestOverlap) < 1e-6 && (score > bestScore || (Math.Abs(score - bestScore) < 1e-6 && L < bestLenLocal))))
                        {
                            bestOverlap = overlap; bestScore = score; bestLenLocal = L; best = s; chosenLat = lat;
                        }
                    }
                    segsOpt = best; bestLen = bestLenLocal;
                    System.Diagnostics.Debug.WriteLine($"[AutoRouter] Side approach (no connector): picked lateral {(chosenLat.IsAlmostEqualTo(trayDir2D) ? "+trayDir" : "-trayDir")} (min bbox overlap)");
                }
                else
                {
                    foreach (var lat in new[] { perpDir1, perpDir2 })
                    {
                        var s = planner.PlanAtTrayElevationOptimized(backInfo.Point, cand.AttachPoint.Z, cand.AttachPoint, lat, cand.TrayDirXY, lateralIsParallelToTray: false);
                        double L = 0; foreach (var ln in s) L += ln.Length;
                        if (segsOpt == null || L < bestLen) { segsOpt = s; bestLen = L; }
                    }
                }
            }

            double lenR = 0; foreach (var ln in segsRiser) lenR += ln.Length;
            double lenO = 0; if (segsOpt != null) foreach (var ln in segsOpt) lenO += ln.Length;

            System.Diagnostics.Debug.WriteLine($"[AutoRouter] segsRiser count: {(segsRiser?.Count ?? 0)}, lenR: {lenR*304.8:F1}mm");
            System.Diagnostics.Debug.WriteLine($"[AutoRouter] segsOpt count: {(segsOpt?.Count ?? 0)}, lenO: {lenO*304.8:F1}mm");

            double dotAbs = 1.0;
            if (segsRiser != null && segsRiser.Count > 0)
            {
                var last = segsRiser[segsRiser.Count - 1];
                var d = last.Direction;
                var dXY = new XYZ(d.X, d.Y, 0.0);
                double len = Math.Sqrt(dXY.X * dXY.X + dXY.Y * dXY.Y);
                if (len > 1e-9)
                {
                    var u = new XYZ(dXY.X / len, dXY.Y / len, 0);
                    var t = cand.TrayDirXY;
                    var tLen = Math.Sqrt(t.X * t.X + t.Y * t.Y);
                    if (tLen < 1e-9) t = new XYZ(1, 0, 0);
                    else t = new XYZ(t.X / tLen, t.Y / tLen, 0);
                    dotAbs = Math.Abs(u.X * t.X + u.Y * t.Y);
                }
            }

            System.Diagnostics.Debug.WriteLine($"[AutoRouter] dotAbs: {dotAbs:F3}");

            if (lateralIsParallelToTray)
            {
                segsBest = segsOpt ?? segsRiser;
                System.Diagnostics.Debug.WriteLine($"[AutoRouter] Choosing OPTIMIZED (device FROM SIDE, lateral parallel to tray, attachAtEnd={attachAtTrayEnd})");
            }
            else if (attachAtTrayEnd)
            {
                segsBest = segsRiser;
                System.Diagnostics.Debug.WriteLine($"[AutoRouter] Choosing RISER (device IN FRONT, attach at tray end - DirectEnd/Elbow scenario)");
            }
            else if (dotAbs >= 0.25)
            {
                segsBest = segsOpt ?? segsRiser;
                System.Diagnostics.Debug.WriteLine($"[AutoRouter] Choosing OPTIMIZED (riser not perpendicular, |dot|={dotAbs:F2})");
            }
            else
            {
                segsBest = (lenO + planner.TargetTrayWidthFt < lenR * 0.85) ? (segsOpt ?? segsRiser) : segsRiser;
                System.Diagnostics.Debug.WriteLine($"[AutoRouter] Riser is perpendicular (|dot|={dotAbs:F2}). lenO+trayW={lenO + planner.TargetTrayWidthFt:F3}, lenR*0.85={lenR * 0.85:F3}");
            }

            double dims = Math.Max(cand.WidthFt, cand.HeightFt);
            double minLegEst = Math.Max(cfg.MinBendRadiusFt, Math.Max(MinLegFactorDefault * dims, (1.0/384.0) * ShortCurveTolFactorDefault));
            logger.Info($"{src.Name}: tray W={cand.WidthFt*304.8:F0}mm H={cand.HeightFt*304.8:F0}mm, bendR={cfg.MinBendRadiusFt*304.8:F0}mm, minLeg≈{minLegEst*304.8:F0}mm, chosen={(segsBest==segsRiser?"riser":"optimized")}");

            System.Diagnostics.Debug.WriteLine($"[AutoRouter] segsRiser count: {(segsRiser?.Count ?? 0)}, segsOpt count: {(segsOpt?.Count ?? 0)}, segsBest count: {(segsBest?.Count ?? 0)}");
            if (segsBest != null)
            {
                for (int i = 0; i < segsBest.Count; i++)
                {
                    var seg = segsBest[i];
                    var p0 = seg.GetEndPoint(0);
                    var p1 = seg.GetEndPoint(1);
                    System.Diagnostics.Debug.WriteLine($"[AutoRouter] segsBest[{i}]: ({p0.X:F3}, {p0.Y:F3}, {p0.Z:F3}) -> ({p1.X:F3}, {p1.Y:F3}, {p1.Z:F3}), len={seg.Length*304.8:F1}mm");
                }
            }

            using (var tx = new Transaction(doc, $"AutoRoute connect {src.Id}"))
            {
                tx.Start();
                var fho = tx.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new ShortCurvePreprocessor());
                tx.SetFailureHandlingOptions(fho);

                // CABLE TRAY
                ConnectionBuilder.BuildResult trayResult = null;
                if (options.CreateCableTray)
                {
                    var targetTray = cand.Tray;
                    trayResult = builder.Build(targetTray, segsBest);

                    foreach (var id in trayResult.NewElements)
                    {
                        var e = doc.GetElement(id);
                        SetParam(e, BuiltInParameter.RBS_CTC_SERVICE_TYPE, desired);
                        SetParam(e, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, $"RevitRouteLab_AutoRoute {System.DateTime.Now:yyyy-MM-dd HH:mm}");
                    }
                }

                // CONDUIT
                ConduitBuilder.ConduitBuildResult conduitResult = null;
                if (options.CreateConduit)
                {
                    var conduitBuilder = new ConduitBuilder(doc);
                    conduitResult = conduitBuilder.Build(segsBest, options.ConduitConfig);

                    foreach (var id in conduitResult.NewElements)
                    {
                        var e = doc.GetElement(id);
                        // Set conduit diameter
                        SetParam(e, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, options.ConduitConfig.DiameterFt);
                        // Set service type if specified
                        if (!string.IsNullOrEmpty(options.ConduitConfig.ServiceType))
                        {
                            SetParam(e, BuiltInParameter.RBS_ELEC_CIRCUIT_TYPE, options.ConduitConfig.ServiceType);
                        }
                        SetParam(e, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, $"RevitRouteLab_AutoRoute_Conduit {System.DateTime.Now:yyyy-MM-dd HH:mm}");
                    }
                }

                tx.Commit();

                // Log results
                string trayInfo = trayResult != null ? $"tray: {trayResult.TotalLength * 304.8 / 1000.0:F2}m ({trayResult.Elbows} elbows, {trayResult.Tees} tees)" : "tray: skip";
                string conduitInfo = conduitResult != null ? $"conduit: {conduitResult.TotalLength * 304.8 / 1000.0:F2}m ({conduitResult.Elbows} elbows)" : "conduit: skip";
                logger.Info($"{src.Name}: {trayInfo}, {conduitInfo}, service '{desired}'");
            }
        }

        private static void SetParam(Element e, BuiltInParameter bip, string value)
        {
            var p = e.get_Parameter(bip);
            if (p != null && !p.IsReadOnly) p.Set(value);
        }

        private static void SetParam(Element e, BuiltInParameter bip, double value)
        {
            var p = e.get_Parameter(bip);
            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double) p.Set(value);
        }

        private class ShortCurvePreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                var msgs = failuresAccessor.GetFailureMessages();
                foreach (var f in msgs)
                {
                    try
                    {
                        if (f.GetSeverity() == FailureSeverity.Warning)
                        {
                            failuresAccessor.DeleteWarning(f);
                        }
                    }
                    catch { }
                }
                return FailureProcessingResult.Continue;
            }
        }
    }
}
