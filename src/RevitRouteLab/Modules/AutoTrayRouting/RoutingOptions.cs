using RevitRouteLab.AutoTrayRouting.Config;

namespace RevitRouteLab.AutoTrayRouting
{
    /// <summary>
    /// Options for what to create during auto-routing
    /// </summary>
    public class RoutingOptions
    {
        /// <summary>
        /// Create cable tray along the route
        /// </summary>
        public bool CreateCableTray { get; set; } = true;

        /// <summary>
        /// Create conduit along the route
        /// </summary>
        public bool CreateConduit { get; set; } = false;

        /// <summary>
        /// Configuration for conduit creation (only used if CreateConduit = true)
        /// </summary>
        public ConduitConfig ConduitConfig { get; set; } = new ConduitConfig();
    }
}
