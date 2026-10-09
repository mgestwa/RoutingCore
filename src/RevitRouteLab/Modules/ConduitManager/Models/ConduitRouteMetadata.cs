using Autodesk.Revit.DB;

namespace RevitRouteLab.ConduitManager.Models
{
    public class ConduitRouteMetadata
    {
        public ElementId FromElementId { get; set; } = ElementId.InvalidElementId;

        public ElementId ToElementId { get; set; } = ElementId.InvalidElementId;

        public string FromLabel { get; set; }

        public string FromLabelSource { get; set; }

        public string ToLabel { get; set; }

        public string ToLabelSource { get; set; }

        public string Relation { get; set; }

        public string RelationId { get; set; }

        public string RelationSource => $"Od={FromLabelSource ?? "brak"}, Do={ToLabelSource ?? "brak"}";

        public double RelationLengthMm { get; set; }

        public double FnLengthMm { get; set; }

        public double PeLengthMm { get; set; }

        public bool HasRelation => !string.IsNullOrWhiteSpace(Relation);
    }
}