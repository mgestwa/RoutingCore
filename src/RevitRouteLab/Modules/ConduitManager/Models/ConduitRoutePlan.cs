using System.Collections.Generic;
using System.Linq;

namespace RevitRouteLab.ConduitManager.Models
{
    public class ConduitRoutePlan
    {
        private readonly List<ConduitPlannedSegment> _segments = new List<ConduitPlannedSegment>();

        public IReadOnlyList<ConduitPlannedSegment> Segments => _segments;

        public bool IsEmpty => _segments.Count == 0;

        public void AddSegment(ConduitPlannedSegment segment)
        {
            _segments.Add(segment);
        }

        public void AddSegments(IEnumerable<ConduitPlannedSegment> segments)
        {
            _segments.AddRange(segments.Where(segment => segment != null));
        }
    }
}
