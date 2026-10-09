using System.Collections.Generic;

namespace INP_IE.ConduitManager.Models
{
    public class ConduitCreationResult
    {
        private readonly List<ConduitCreatedSegment> _createdSegments = new List<ConduitCreatedSegment>();

        public IReadOnlyList<ConduitCreatedSegment> CreatedSegments => _createdSegments;

        public void AddCreatedSegment(ConduitCreatedSegment segment)
        {
            _createdSegments.Add(segment);
        }
    }
}
