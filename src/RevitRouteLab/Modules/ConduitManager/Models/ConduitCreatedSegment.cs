using Autodesk.Revit.DB.Electrical;

namespace RevitRouteLab.ConduitManager.Models
{
    public class ConduitCreatedSegment
    {
        public ConduitCreatedSegment(ConduitPlannedSegment plannedSegment, Conduit conduit)
        {
            PlannedSegment = plannedSegment;
            Conduit = conduit;
        }

        public ConduitPlannedSegment PlannedSegment { get; }

        public Conduit Conduit { get; }
    }
}
