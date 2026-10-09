using System;
using System.Linq;
using INP_IE.ConduitManager.Models;
using INP_IE.ConduitRouting.Models;

namespace INP_IE.ConduitManager.Services
{
    public static class ConduitPlanAnalyzer
    {
        private const double FeetToMm = 304.8;
        private const double MinimumSegmentLength = 1.0 / 12.0;

        public static void Analyze(ConduitRoutePlan plan, ConduitRoutingReport report)
        {
            if (plan == null || report == null)
            {
                return;
            }

            report.PlannedSegments = plan.Segments.Count;
            report.PlannedLengthMm = plan.Segments
                .Where(segment => segment?.Start != null && segment.End != null)
                .Sum(segment => segment.Start.DistanceTo(segment.End) * FeetToMm);

            report.InvalidPlannedSegments = plan.Segments.Count(segment =>
                segment == null ||
                segment.Start == null ||
                segment.End == null ||
                segment.Start.DistanceTo(segment.End) < MinimumSegmentLength ||
                segment.LevelId == null ||
                segment.LevelId == Autodesk.Revit.DB.ElementId.InvalidElementId);

            if (plan.IsEmpty)
            {
                AddWarningOnce(report, "Plan nie zawiera odcinkow conduitow do utworzenia.");
            }

            if (report.InvalidPlannedSegments > 0)
            {
                AddWarningOnce(report, $"Plan zawiera {report.InvalidPlannedSegments} niepoprawnych odcinkow, ktore moga zostac pominiete podczas tworzenia conduitow.");
            }
        }

        private static void AddWarningOnce(ConduitRoutingReport report, string warning)
        {
            if (!report.Warnings.Contains(warning))
            {
                report.Warnings.Add(warning);
            }
        }
    }
}