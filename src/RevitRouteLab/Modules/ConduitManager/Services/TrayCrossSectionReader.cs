using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace INP_IE.ConduitManager.Services
{
    /// <summary>
    /// Reads the usable cross-section width of a cable tray or a tray fitting.
    /// Fittings frequently expose a zero width parameter, so the reader falls
    /// back to the width of a connected straight tray, mirroring the behaviour
    /// used when the raw path segments are generated.
    /// </summary>
    public static class TrayCrossSectionReader
    {
        private const double FeetToMm = 304.8;

        public static double GetWidthFeet(Document document, ElementId elementId)
        {
            if (document == null || elementId == null || elementId == ElementId.InvalidElementId)
            {
                return 0;
            }

            return GetWidthFeet(document, document.GetElement(elementId));
        }

        public static double GetWidthFeet(Document document, Element element)
        {
            if (element == null)
            {
                return 0;
            }

            var width = GetDoubleParameter(element, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            if (width > 0)
            {
                return width;
            }

            var connectedTray = FindConnectedTray(element);
            if (connectedTray != null)
            {
                width = GetDoubleParameter(connectedTray, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            }

            return Math.Max(0, width);
        }

        public static double GetWidthMm(Document document, ElementId elementId)
        {
            return GetWidthFeet(document, elementId) * FeetToMm;
        }
        public static double GetHeightFeet(Document document, ElementId elementId)
        {
            if (document == null || elementId == null || elementId == ElementId.InvalidElementId)
            {
                return 0;
            }

            return GetHeightFeet(document, document.GetElement(elementId));
        }

        public static double GetHeightFeet(Document document, Element element)
        {
            if (element == null)
            {
                return 0;
            }

            var height = GetDoubleParameter(element, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
            if (height > 0)
            {
                return height;
            }

            var connectedTray = FindConnectedTray(element);
            if (connectedTray != null)
            {
                height = GetDoubleParameter(connectedTray, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
            }

            return Math.Max(0, height);
        }

        public static double GetHeightMm(Document document, ElementId elementId)
        {
            return GetHeightFeet(document, elementId) * FeetToMm;
        }

        private static CableTray FindConnectedTray(Element element)
        {
            foreach (var connector in MepConnectorReader.GetConnectors(element))
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
    }
}
