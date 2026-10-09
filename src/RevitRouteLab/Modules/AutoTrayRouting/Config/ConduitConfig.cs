using Autodesk.Revit.DB;

namespace RevitRouteLab.AutoTrayRouting.Config
{
    /// <summary>
    /// Configuration for conduit routing
    /// </summary>
    public class ConduitConfig
    {
        /// <summary>
        /// Conduit diameter in millimeters (default: 25mm)
        /// </summary>
        public double DiameterMM { get; set; } = 25.0;

        /// <summary>
        /// Conduit type to use for routing. If null, uses first available conduit type.
        /// </summary>
        public ElementId ConduitTypeId { get; set; } = null;

        /// <summary>
        /// Service type to assign to created conduits
        /// </summary>
        public string ServiceType { get; set; } = string.Empty;

        /// <summary>
        /// Minimum bend radius for conduit elbows in feet (default: 0.5 ft ≈ 150mm)
        /// </summary>
        public double MinBendRadiusFt { get; set; } = 0.5;

        /// <summary>
        /// Get diameter in Revit internal units (feet)
        /// </summary>
        public double DiameterFt => DiameterMM / 304.8;
    }
}
