using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using INP_IE.ConduitManager.Models;
using INP_IE.ConduitRouting.Models;

namespace INP_IE.ConduitManager.Services
{
    public class ConduitElementFactory
    {
        private const double MinimumSegmentLength = 1.0 / 12.0;

        private readonly Document _doc;

        public ConduitElementFactory(Document doc)
        {
            _doc = doc;
        }

        public ConduitCreationResult CreateConduits(
            ConduitRoutePlan plan,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report)
        {
            var result = new ConduitCreationResult();
            var diameter = RevitUnitConverter.ToFeet(settings.DiameterMm);

            foreach (var segment in plan.Segments)
            {
                if (segment.Start.DistanceTo(segment.End) < MinimumSegmentLength)
                {
                    continue;
                }

                try
                {
                    var conduit = Conduit.Create(_doc, settings.ConduitTypeId, segment.Start, segment.End, segment.LevelId);
                    var diameterParameter = conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                    if (diameterParameter != null && !diameterParameter.IsReadOnly)
                    {
                        diameterParameter.Set(diameter);
                    }

                    result.AddCreatedSegment(new ConduitCreatedSegment(segment, conduit));
                    report.CreatedConduits++;
                    report.CreatedElementIds.Add(conduit.Id);
                }
                catch (Exception ex)
                {
                    report.Warnings.Add($"Nie utworzono conduita dla elementu {segment.SourceElementId.IntegerValue}: {ex.Message}");
                }
            }

            return result;
        }
    }
}
