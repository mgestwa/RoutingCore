using System;
using System.Collections.Generic;

namespace INP_IE.ConduitManager.Services
{
    /// <summary>
    /// Pure allocation math kept independent from the Revit API so it can be
    /// validated without opening a model.
    /// </summary>
    public static class ConduitLaneMath
    {
        public const double MinimumSupportClearanceMm = 1.0;

        /// <summary>
        /// Deterministic center-out lane order: 0, +1, -1, +2, -2, ...
        /// </summary>
        public static IEnumerable<int> BuildCenterOutLaneSequence(int maximumStep)
        {
            yield return 0;

            for (var step = 1; step <= Math.Max(0, maximumStep); step++)
            {
                yield return step;
                yield return -step;
            }
        }

        public static double GetPitchMm(double diameterMm, double spacingMm)
        {
            return Math.Max(0, diameterMm) + Math.Max(0, spacingMm);
        }
        public static double GetCrossSectionClearanceMm(double marginMm)
        {
            return Math.Max(MinimumSupportClearanceMm, Math.Max(0, marginMm));
        }

        public static double GetRequiredTrayHeightMm(double conduitDiameterMm, double marginMm)
        {
            return Math.Max(0, conduitDiameterMm) +
                   2.0 * GetCrossSectionClearanceMm(marginMm);
        }

        public static bool FitsInsideTrayHeight(
            double trayHeightMm,
            double conduitDiameterMm,
            double marginMm)
        {
            return trayHeightMm > 0 &&
                   GetRequiredTrayHeightMm(conduitDiameterMm, marginMm) <= trayHeightMm + 1e-6;
        }

        public static double GetRequiredAxisDistanceMm(
            double firstDiameterMm,
            double secondDiameterMm,
            double spacingMm)
        {
            return Math.Max(0, firstDiameterMm) / 2.0 +
                   Math.Max(0, secondDiameterMm) / 2.0 +
                   Math.Max(0, spacingMm);
        }

        public static bool FitsInsideTrayWidth(
            double trayWidthMm,
            double conduitDiameterMm,
            double marginMm,
            double axisOffsetMm)
        {
            if (trayWidthMm <= 0 || conduitDiameterMm <= 0)
            {
                return false;
            }

            var requiredHalfWidth =
                Math.Abs(axisOffsetMm) +
                conduitDiameterMm / 2.0 +
                Math.Max(0, marginMm);
            return requiredHalfWidth <= trayWidthMm / 2.0 + 1e-6;
        }

        /// <summary>
        /// Highest lane index whose axis still fits inside the tray for the given
        /// pitch. Returns -1 when not even the center lane fits.
        /// </summary>
        public static int GetMaximumLaneStep(
            double trayWidthMm,
            double conduitDiameterMm,
            double marginMm,
            double pitchMm)
        {
            if (!FitsInsideTrayWidth(trayWidthMm, conduitDiameterMm, marginMm, 0))
            {
                return -1;
            }

            if (pitchMm <= 0)
            {
                return 0;
            }

            var usableHalfWidth =
                trayWidthMm / 2.0 - conduitDiameterMm / 2.0 - Math.Max(0, marginMm);
            if (usableHalfWidth < 0)
            {
                return 0;
            }

            return (int)Math.Floor((usableHalfWidth + 1e-6) / pitchMm);
        }

        /// <summary>
        /// How close the given lane axis sits to the tray edge, expressed as a
        /// fraction of the tray half-width (0 = centered, 1 = touching the edge).
        /// </summary>
        public static double GetTrayFillRatio(
            double trayWidthMm,
            double conduitDiameterMm,
            double marginMm,
            double axisOffsetMm)
        {
            if (trayWidthMm <= 0)
            {
                return 0;
            }

            var requiredHalfWidth =
                Math.Abs(axisOffsetMm) +
                conduitDiameterMm / 2.0 +
                Math.Max(0, marginMm);
            return requiredHalfWidth / (trayWidthMm / 2.0);
        }
    }
}
