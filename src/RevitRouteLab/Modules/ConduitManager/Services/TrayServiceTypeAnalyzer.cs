using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using INP_IE.ConduitManager.Models;
using INP_IE.ConduitRouting.Models;

namespace INP_IE.ConduitManager.Services
{
    /// <summary>
    /// Informacyjnie sprawdza Service Type na korytkach, po których faktycznie
    /// prowadzi wybrana ścieżka. Nie filtruje ani nie zmienia kosztu trasy.
    /// </summary>
    public static class TrayServiceTypeAnalyzer
    {
        private const string MissingValueLabel = "[brak]";

        public static void AnalyzeRoute(
            IEnumerable<TrayGraphEdge> routeEdges,
            ConduitRoutingReport report)
        {
            if (report == null)
            {
                return;
            }

            report.TrayServiceTypeSummary = string.Empty;
            report.IsTrayServiceTypeConsistent = null;

            var trays = new List<CableTray>();
            var seen = new HashSet<int>();
            foreach (var edge in routeEdges ?? Enumerable.Empty<TrayGraphEdge>())
            {
                if (edge?.Element is CableTray tray &&
                    seen.Add(tray.Id.IntegerValue))
                {
                    trays.Add(tray);
                }
            }

            if (trays.Count == 0)
            {
                return;
            }

            var sequence = new List<string>();
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missingCount = 0;
            foreach (var tray in trays)
            {
                var value = ReadServiceType(tray);
                if (string.IsNullOrWhiteSpace(value))
                {
                    value = MissingValueLabel;
                    missingCount++;
                }

                distinct.Add(value);
                if (sequence.Count == 0 ||
                    !string.Equals(
                        sequence[sequence.Count - 1],
                        value,
                        StringComparison.OrdinalIgnoreCase))
                {
                    sequence.Add(value);
                }
            }

            if (distinct.Count == 1 && missingCount == trays.Count)
            {
                report.TrayServiceTypeSummary =
                    "brak danych — wszystkie korytka bez wartości";
                return;
            }

            if (distinct.Count == 1)
            {
                report.IsTrayServiceTypeConsistent = true;
                report.TrayServiceTypeSummary = "stały — " + sequence[0];
                return;
            }

            report.IsTrayServiceTypeConsistent = false;
            var displayed = string.Join(" → ", sequence.Take(6));
            if (sequence.Count > 6)
            {
                displayed += " → …";
            }

            report.TrayServiceTypeSummary = "niejednolity — " + displayed;
            if (missingCount > 0)
            {
                report.TrayServiceTypeSummary +=
                    $" (brak wartości: {missingCount})";
            }
        }

        private static string ReadServiceType(Element tray)
        {
            try
            {
                var parameter = tray?.get_Parameter(
                    BuiltInParameter.RBS_CTC_SERVICE_TYPE);
                return (parameter?.AsString() ??
                        parameter?.AsValueString() ??
                        string.Empty).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
