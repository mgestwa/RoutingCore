using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace INP_IE.ConduitManager.Models
{
    /// <summary>
    /// Sposób, w jaki trasa zaczyna się albo kończy.
    /// </summary>
    public enum ConduitRouteEndpointKind
    {
        /// <summary>Trasa zaczyna się wprost na korytku — tak jak dotychczas.</summary>
        TrayPoint,

        /// <summary>
        /// Trasa dobiega do urządzenia poza korytkiem: od punktu na korytku
        /// odchodzi zejście prowadzone w powietrzu.
        /// </summary>
        DeviceDrop
    }

    /// <summary>
    /// Koniec trasy conduitu. Zawsze wskazuje korytko, do którego trasa jest
    /// podpięta — dzięki temu przejście po sieci korytek wygląda tak samo jak
    /// dotychczas, a ewentualne zejście do urządzenia jest doklejane jako
    /// osobny odcinek na początku albo na końcu ścieżki.
    /// </summary>
    public sealed class ConduitRouteEndpoint
    {
        private ConduitRouteEndpoint(
            CableTray tray,
            XYZ trayPoint,
            double distanceMm,
            Element device,
            XYZ devicePoint)
        {
            Tray = tray;
            TrayPoint = trayPoint;
            DistanceMm = distanceMm;
            Device = device;
            DevicePoint = devicePoint;
        }

        public static ConduitRouteEndpoint OnTray(CableTray tray, XYZ trayPoint, double distanceMm)
        {
            return new ConduitRouteEndpoint(tray, trayPoint, distanceMm, null, null);
        }

        public static ConduitRouteEndpoint OnDevice(
            CableTray attachTray,
            XYZ trayPoint,
            double distanceMm,
            Element device,
            XYZ devicePoint)
        {
            return new ConduitRouteEndpoint(attachTray, trayPoint, distanceMm, device, devicePoint);
        }

        public CableTray Tray { get; }

        /// <summary>Punkt na korytku, w którym trasa wchodzi w sieć korytek.</summary>
        public XYZ TrayPoint { get; }

        public double DistanceMm { get; }

        /// <summary>Urządzenie na końcu zejścia. Null, gdy trasa kończy się na korytku.</summary>
        public Element Device { get; }

        /// <summary>Punkt podłączenia na urządzeniu. Null, gdy trasa kończy się na korytku.</summary>
        public XYZ DevicePoint { get; }

        public ConduitRouteEndpointKind Kind =>
            Device == null ? ConduitRouteEndpointKind.TrayPoint : ConduitRouteEndpointKind.DeviceDrop;

        public string Describe()
        {
            return Kind == ConduitRouteEndpointKind.TrayPoint
                ? $"korytko {Tray.Id.IntegerValue} ({DistanceMm:F0} mm)"
                : $"korytko {Tray.Id.IntegerValue} + zejście do {Device.Id.IntegerValue} ({DistanceMm:F0} mm)";
        }
    }
}
