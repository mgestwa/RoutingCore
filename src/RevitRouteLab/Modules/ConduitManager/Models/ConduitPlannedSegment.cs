using Autodesk.Revit.DB;

namespace RevitRouteLab.ConduitManager.Models
{
    public class ConduitPlannedSegment
    {
        public ConduitPlannedSegment(XYZ start, XYZ end, ElementId sourceElementId, int layoutIndex, ElementId levelId)
        {
            Start = start;
            End = end;
            SourceElementId = sourceElementId;
            LayoutIndex = layoutIndex;
            LevelId = levelId;
        }

        public XYZ Start { get; }

        public XYZ End { get; }

        public ElementId SourceElementId { get; }

        public int LayoutIndex { get; }

        public ElementId LevelId { get; }
    }
}
