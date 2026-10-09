using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using INP_IE.AutoTrayRouting.Config;

namespace INP_IE.AutoTrayRouting.Routing
{
    /// <summary>
    /// Path planner with micro-optimization: ensures sufficient horizontal lead for an elbow
    /// and biases the approach to align with tray direction to minimize bending.
    /// Also enforces bend radius by adding extra straight lengths where needed.
    /// Ensures continuity of segments so connectors meet exactly (no gaps/overlaps).
    /// </summary>
    public class PathPlanner
    {
        private readonly AutoTrayRoutingSettings _cfg;
        public PathPlanner(AutoTrayRoutingSettings cfg) { _cfg = cfg; }

        // Adaptive context passed from caller (target tray dims)
        public double TargetTrayWidthFt { get; set; }
        public double TargetTrayHeightFt { get; set; }

        // Tunables requested in task (k factors)
        public double MinLegFactor { get; set; } = 0.6; // fraction of max(width,height)
        public double ShortCurveTolFactor { get; set; } = 3.0; // multiplier over base tol

        private const double BaseShortTolFt = 1.0 / 384.0; // ~0.8 mm

        private double CurrentShortTol() => BaseShortTolFt * (ShortCurveTolFactor > 0 ? ShortCurveTolFactor : 1.0);

        private double CurrentMinLeg()
        {
            double dims = System.Math.Max(TargetTrayWidthFt, TargetTrayHeightFt);
            double byDims = MinLegFactor > 0 ? MinLegFactor * dims : 0.0;
            double byTol = CurrentShortTol();
            double byBend = _cfg.MinBendRadiusFt;
            double min = byTol;
            if (byBend > min) min = byBend;
            if (byDims > min) min = byDims;
            // also respect configured absolute MinStraightFt if larger
            if (_cfg.MinStraightFt > min) min = _cfg.MinStraightFt;
            return min;
        }

        /// <summary>
        /// Strict riser: vertical up/down to targetZ and then a single horizontal segment straight to attachPoint (same Z).
        /// This guarantees vertical->elbow->horizontal geometry and is robust when the point moves.
        /// </summary>
        public IList<Line> PlanRiserStraight(XYZ startOnDevice, double targetZ, XYZ attachPoint)
        {
            var segs = new List<Line>();

            double minLeg = CurrentMinLeg();
            double shortTol = CurrentShortTol();

            System.Diagnostics.Debug.WriteLine($"[PathPlan] PlanRiserStraight START");
            System.Diagnostics.Debug.WriteLine($"[PathPlan]   startOnDevice: ({startOnDevice.X:F3}, {startOnDevice.Y:F3}, {startOnDevice.Z:F3})");
            System.Diagnostics.Debug.WriteLine($"[PathPlan]   targetZ: {targetZ:F3}, attachPoint: ({attachPoint.X:F3}, {attachPoint.Y:F3}, {attachPoint.Z:F3})");

            // 1) Vertical leg to targetZ - DON'T extend beyond targetZ (causes Z mismatch with horizontal)
            var pUp = new XYZ(startOnDevice.X, startOnDevice.Y, targetZ);
            if (!startOnDevice.IsAlmostEqualTo(pUp))
            {
                segs.Add(Line.CreateBound(startOnDevice, pUp));
                System.Diagnostics.Debug.WriteLine($"[PathPlan] 1. Vertical: ({startOnDevice.X:F3}, {startOnDevice.Y:F3}, {startOnDevice.Z:F3}) -> ({pUp.X:F3}, {pUp.Y:F3}, {pUp.Z:F3}), len={Line.CreateBound(startOnDevice, pUp).Length * 304.8:F1}mm");
            }

            // 2) Horizontal straight to XY of attachPoint at targetZ (always use targetZ, not pUp.Z)
            var pTail = new XYZ(attachPoint.X, attachPoint.Y, targetZ);
            if (!pTail.IsAlmostEqualTo(pUp))
            {
                var horiz = Line.CreateBound(pUp, pTail);
                // Nie wyd�u�aj na si�� ostatniego najazdu � zostaw dok�adnie do attach (ConnectionBuilder dosunie/dope�ni fitting)
                if (horiz.Length > shortTol) segs.Add(horiz);
                System.Diagnostics.Debug.WriteLine($"[PathPlan] 2. Horizontal: ({pUp.X:F3}, {pUp.Y:F3}, {pUp.Z:F3}) -> ({pTail.X:F3}, {pTail.Y:F3}, {pTail.Z:F3}), len={horiz.Length * 304.8:F1}mm");
            }

            System.Diagnostics.Debug.WriteLine($"[PathPlan] Raw segments count: {segs.Count}");
            var normalized = NormalizeManhattan(segs);
            System.Diagnostics.Debug.WriteLine($"[PathPlan] After normalization: {normalized.Count}");
            var final = PostProcessSegments(normalized);
            System.Diagnostics.Debug.WriteLine($"[PathPlan] Final segments count: {final.Count}");

            return final;
        }

        public IList<Line> PlanAtTrayElevationOptimized(XYZ startOnDevice, double targetZ, XYZ attachPoint, XYZ lateralDir, XYZ trayDirXY, bool lateralIsParallelToTray = false)
        {
            var segs = new List<Line>();

            double minLeg = CurrentMinLeg();

            System.Diagnostics.Debug.WriteLine($"[PathPlan] PlanAtTrayElevationOptimized START");
            System.Diagnostics.Debug.WriteLine($"[PathPlan]   startOnDevice: ({startOnDevice.X:F3}, {startOnDevice.Y:F3}, {startOnDevice.Z:F3})");
            System.Diagnostics.Debug.WriteLine($"[PathPlan]   targetZ: {targetZ:F3}, attachPoint: ({attachPoint.X:F3}, {attachPoint.Y:F3}, {attachPoint.Z:F3})");

            // 1) Vertical to targetZ - DON'T extend beyond targetZ (causes gaps)
            var pUp = new XYZ(startOnDevice.X, startOnDevice.Y, targetZ);
            if (!startOnDevice.IsAlmostEqualTo(pUp))
            {
                segs.Add(Line.CreateBound(startOnDevice, pUp));
                System.Diagnostics.Debug.WriteLine($"[PathPlan] 1. Vertical: ({startOnDevice.X:F3}, {startOnDevice.Y:F3}, {startOnDevice.Z:F3}) -> ({pUp.X:F3}, {pUp.Y:F3}, {pUp.Z:F3}), len={Line.CreateBound(startOnDevice, pUp).Length * 304.8:F1}mm");
            }

            // 2) Side-step to clear elbow.
            var lat2D = new XYZ(lateralDir.X, lateralDir.Y, 0.0);
            if (lat2D.IsZeroLength()) lat2D = new XYZ(1, 0, 0);
            lat2D = lat2D.Normalize();

            // Only flip lateral direction for perpendicular approach (not for parallel to tray)
            if (!lateralIsParallelToTray)
            {
                var toTray = new XYZ(attachPoint.X - pUp.X, attachPoint.Y - pUp.Y, 0.0);
                if (toTray.GetLength() > 1e-9)
                {
                    var toTrayXY = toTray.Normalize();
                    var dot = lat2D.DotProduct(toTrayXY);
                    if (dot < 0) lat2D = -lat2D; // flip to face attach point
                }
            }

            var step = System.Math.Max(System.Math.Max(_cfg.MinStraightFt, _cfg.ConnectorOffsetFt), _cfg.MinBendRadiusFt);
            if (step < minLeg) step = minLeg;

            // W scenariuszu "FROM SIDE" (swapped), segment lateralny jest kluczową częścią obejścia (dogleg).
            // MUSI być on na tyle długi, aby kształtka kolana go nie "wchłonęła" podczas tworzenia.
            // Wymuszamy tutaj bardziej robustną, minimalną długość.
            if (lateralIsParallelToTray)
            {
                // Bezpieczna długość to co najmniej promień gięcia plus szerokość korytka.
                // Daje to pewność, że po obu stronach osi gięcia pozostanie wystarczająco dużo "prostego" odcinka.
                double robustLength = _cfg.MinBendRadiusFt + TargetTrayWidthFt;
                if (step < robustLength)
                {
                    System.Diagnostics.Debug.WriteLine($"[PathPlan] SWAPPED logic: Zwiększanie długości segmentu równoległego z {step * 304.8:F1}mm do {robustLength * 304.8:F1}mm w celu uniknięcia jego usunięcia.");
                    step = robustLength;
                }
            }

            var pStep = new XYZ(pUp.X + lat2D.X * step, pUp.Y + lat2D.Y * step, targetZ);
            if (!pStep.IsAlmostEqualTo(pUp))
            {
                segs.Add(Line.CreateBound(pUp, pStep));
                System.Diagnostics.Debug.WriteLine($"[PathPlan] 2. Lateral: ({pUp.X:F3}, {pUp.Y:F3}, {pUp.Z:F3}) -> ({pStep.X:F3}, {pStep.Y:F3}, {pStep.Z:F3}), len={Line.CreateBound(pUp, pStep).Length * 304.8:F1}mm");
            }

            // 3) Align run towards tray
            double lateralDX = System.Math.Abs(pStep.X - pUp.X);
            double lateralDY = System.Math.Abs(pStep.Y - pUp.Y);
            bool lateralInX = lateralDX > lateralDY;

            XYZ pAligned;
            XYZ tail = new XYZ(attachPoint.X, attachPoint.Y, targetZ);

            if (lateralIsParallelToTray)
            {
                // SWAPPED: Lateral is parallel to tray → Aligned is perpendicular
                if (lateralInX)
                {
                    // Lateral in X (parallel) → Aligned in Y (perpendicular)
                    pAligned = new XYZ(pStep.X, attachPoint.Y, targetZ);
                }
                else
                {
                    // Lateral in Y (parallel) → Aligned in X (perpendicular)
                    pAligned = new XYZ(attachPoint.X, pStep.Y, targetZ);
                }
                System.Diagnostics.Debug.WriteLine($"[PathPlan] Using SWAPPED logic (lateral parallel to tray)");
            }
            else
            {
                // NORMAL: Aligned moves in OTHER axis (perpendicular to lateral)
                if (lateralInX)
                {
                    pAligned = new XYZ(pStep.X, attachPoint.Y, targetZ);
                }
                else
                {
                    pAligned = new XYZ(attachPoint.X, pStep.Y, targetZ);
                }
            }

            if (!pAligned.IsAlmostEqualTo(pStep))
            {
                segs.Add(Line.CreateBound(pStep, pAligned));
                System.Diagnostics.Debug.WriteLine($"[PathPlan] 3. Aligned: ({pStep.X:F3}, {pStep.Y:F3}, {pStep.Z:F3}) -> ({pAligned.X:F3}, {pAligned.Y:F3}, {pAligned.Z:F3}), len={Line.CreateBound(pStep, pAligned).Length * 304.8:F1}mm");
            }

            // 4) Final orthogonal jog to attach (completes the path)
            // --- KLUCZOWA POPRAWKA ---
            // Nie dodawaj tego segmentu dla logiki SWAPPED, ponieważ ostatni segment MUSI
            // pozostać prostopadły do trasy, aby umożliwić wstawienie trójnika (TEE).
            // ConnectionBuilder zajmie się dosunięciem ostatniego segmentu do punktu docelowego.
            if (!lateralIsParallelToTray)
            {
                if (!pAligned.IsAlmostEqualTo(tail))
                {
                    segs.Add(Line.CreateBound(pAligned, tail));
                    System.Diagnostics.Debug.WriteLine($"[PathPlan] 4. Final jog: ({pAligned.X:F3}, {pAligned.Y:F3}, {pAligned.Z:F3}) -> ({tail.X:F3}, {tail.Y:F3}, {tail.Z:F3}), len={Line.CreateBound(pAligned, tail).Length * 304.8:F1}mm");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[PathPlan] SWAPPED logic: Pomijanie 'Final jog' w celu zapewnienia prostopadłego podejścia dla trójnika (TEE).");
            }

            System.Diagnostics.Debug.WriteLine($"[PathPlan] Raw segments count: {segs.Count}");
            var normalized = NormalizeManhattan(segs);
            System.Diagnostics.Debug.WriteLine($"[PathPlan] After normalization: {normalized.Count}");
            var final = PostProcessSegments(normalized);
            System.Diagnostics.Debug.WriteLine($"[PathPlan] Final segments count: {final.Count}");
            return final;
        }

        public IList<Line> PlanAtTrayElevationWithSideStep(XYZ startOnDevice, double targetZ, XYZ attachPoint, XYZ preferredLateralDir)
        {
            var segs = new List<Line>();
            double minLeg = CurrentMinLeg();
            double shortTol = CurrentShortTol();

            // Vertical to targetZ - don't extend beyond
            var pUp = new XYZ(startOnDevice.X, startOnDevice.Y, targetZ);
            if (!startOnDevice.IsAlmostEqualTo(pUp))
            {
                segs.Add(Line.CreateBound(startOnDevice, pUp));
            }

            // Lateral segments at targetZ
            var lat = preferredLateralDir; var latXY = new XYZ(lat.X, lat.Y, 0.0); if (latXY.IsZeroLength()) latXY = new XYZ(1, 0, 0); latXY = latXY.Normalize();
            var step = System.Math.Max(System.Math.Max(_cfg.MinStraightFt, _cfg.ConnectorOffsetFt), _cfg.MinBendRadiusFt);
            if (step < minLeg) step = minLeg;
            var pStep = new XYZ(pUp.X + latXY.X * step, pUp.Y + latXY.Y * step, targetZ);
            if (!pStep.IsAlmostEqualTo(pUp)) segs.Add(Line.CreateBound(pUp, pStep));
            var pXY1 = new XYZ(attachPoint.X, pStep.Y, targetZ);
            var pXY2 = new XYZ(attachPoint.X, attachPoint.Y, targetZ);
            if ((pStep - pXY1).GetLength() > shortTol) segs.Add(Line.CreateBound(pStep, pXY1));
            if ((pXY1 - pXY2).GetLength() > shortTol) segs.Add(Line.CreateBound(pXY1, pXY2));
            var normalized = NormalizeManhattan(segs);
            return PostProcessSegments(normalized);
        }

        public IList<Line> PlanAtTrayElevation(XYZ start, double targetZ, XYZ attachPoint)
        {
            var segs = new List<Line>();
            double minLeg = CurrentMinLeg();
            double shortTol = CurrentShortTol();

            // Vertical to targetZ - don't extend beyond
            var pUp = new XYZ(start.X, start.Y, targetZ);
            if (!start.IsAlmostEqualTo(pUp))
            {
                segs.Add(Line.CreateBound(start, pUp));
            }

            // Horizontal segments at targetZ
            var pXY1 = new XYZ(attachPoint.X, pUp.Y, targetZ);
            var pXY2 = new XYZ(attachPoint.X, attachPoint.Y, targetZ);
            if ((pUp - pXY1).GetLength() > shortTol) segs.Add(Line.CreateBound(pUp, pXY1));
            if ((pXY1 - pXY2).GetLength() > shortTol) segs.Add(Line.CreateBound(pXY1, pXY2));
            var normalized = NormalizeManhattan(segs);
            return PostProcessSegments(normalized);
        }

        public IList<Line> PlanFromConnector(Connector srcConnector, XYZ attachPoint)
        {
            double minLeg = CurrentMinLeg();
            double shortTol = CurrentShortTol();

            var start = srcConnector?.Origin ?? attachPoint;
            var rise = System.Math.Max(System.Math.Max(_cfg.MinStraightFt, _cfg.ConnectorOffsetFt), _cfg.MinBendRadiusFt);
            if (rise < minLeg) rise = minLeg;
            var pUp = new XYZ(start.X, start.Y, start.Z + rise);
            var segs = new List<Line> { Line.CreateBound(start, pUp) };
            var pXY1 = new XYZ(attachPoint.X, pUp.Y, pUp.Z);
            var pXY2 = new XYZ(attachPoint.X, attachPoint.Y, pUp.Z);
            if ((pUp - pXY1).GetLength() > shortTol) segs.Add(Line.CreateBound(pUp, pXY1));
            if ((pXY1 - pXY2).GetLength() > shortTol) segs.Add(Line.CreateBound(pXY1, pXY2));
            if (System.Math.Abs(attachPoint.Z - pUp.Z) > shortTol) segs.Add(Line.CreateBound(pXY2, new XYZ(attachPoint.X, attachPoint.Y, attachPoint.Z)));
            var normalized = NormalizeManhattan(segs);
            return PostProcessSegments(normalized);
        }

        public IList<Line> Plan(XYZ start, XYZ end)
        {
            double shortTol = CurrentShortTol();
            var segs = new List<Line>();
            var p1 = new XYZ(end.X, start.Y, start.Z);
            var p2 = new XYZ(end.X, end.Y, start.Z);
            if ((start - p1).GetLength() > shortTol) segs.Add(Line.CreateBound(start, p1));
            if ((p1 - p2).GetLength() > shortTol) segs.Add(Line.CreateBound(p1, p2));
            if (System.Math.Abs(end.Z - start.Z) > shortTol) segs.Add(Line.CreateBound(p2, new XYZ(end.X, end.Y, end.Z)));
            var normalized = NormalizeManhattan(segs);
            return PostProcessSegments(normalized);
        }

        /// <summary>
        /// Normalize to Manhattan axes, enforce min lengths for interior segments, and stitch segment endpoints
        /// so each next segment starts exactly at previous end (no gaps/overlaps).
        /// </summary>
        private IList<Line> NormalizeManhattan(List<Line> segs)
        {
            var normalized = new List<Line>();
            if (segs.Count == 0) return normalized;

            XYZ current = segs[0].GetEndPoint(0);
            double min = CurrentMinLeg();
            double shortTol = CurrentShortTol();

            for (int i = 0; i < segs.Count; i++)
            {
                var dir = segs[i].Direction;
                // snap to principal axis
                XYZ axis;
                double ax = System.Math.Abs(dir.X), ay = System.Math.Abs(dir.Y), az = System.Math.Abs(dir.Z);
                if (ax >= ay && ax >= az) axis = new XYZ(System.Math.Sign(dir.X), 0, 0);
                else if (ay >= ax && ay >= az) axis = new XYZ(0, System.Math.Sign(dir.Y), 0);
                else axis = new XYZ(0, 0, System.Math.Sign(dir.Z));

                var len = segs[i].Length;
                // Nie wymuszaj min na ostatnim segmencie � pozw�l dojecha� dok�adnie do attach
                if (i < segs.Count - 1 && len < min) len = min;

                var next = new XYZ(current.X + axis.X * len, current.Y + axis.Y * len, current.Z + axis.Z * len);
                if (!current.IsAlmostEqualTo(next) && (next - current).GetLength() > shortTol)
                    normalized.Add(Line.CreateBound(current, next));
                current = next;
            }

            // merge colinear neighbours
            return MergeColinear(normalized);
        }

        private static List<Line> MergeColinear(List<Line> segs)
        {
            if (segs.Count < 2) return segs;
            var merged = new List<Line>();
            Line cur = segs[0];
            for (int i = 1; i < segs.Count; i++)
            {
                var nxt = segs[i];
                if (IsColinear(cur, nxt))
                {
                    cur = Line.CreateBound(cur.GetEndPoint(0), nxt.GetEndPoint(1));
                }
                else
                {
                    // filter out too short before pushing
                    if (cur.Length > BaseShortTolFt) merged.Add(cur);
                    cur = nxt;
                }
            }
            if (cur.Length > BaseShortTolFt) merged.Add(cur);
            return merged;
        }

        private static bool IsColinear(Line a, Line b)
        {
            var da = a.Direction.Normalize();
            var db = b.Direction.Normalize();
            return da.IsAlmostEqualTo(db) || da.IsAlmostEqualTo(-db);
        }

        private IList<Line> PostProcessSegments(IList<Line> segs)
        {
            var list = new List<Line>();
            double shortTol = CurrentShortTol();
            foreach (var s in segs)
            {
                if (s.Length > shortTol) list.Add(s);
            }
            // Usuwamy agresywne przycinanie pierwszego/ostatniego segmentu � zostawiamy dojazd do attach.
            return list;
        }
    }
}
