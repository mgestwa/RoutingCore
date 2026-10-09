using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using INP_IE.ConduitManager.Models;
using INP_IE.ConduitRouting.Models;

namespace INP_IE.ConduitManager.Services
{
    public class ConduitMetadataWriter
    {
        private const double FeetToMm = 304.8;

        public void Write(
            IReadOnlyList<ConduitCreatedSegment> createdSegments,
            ConduitRouteMetadata metadata,
            ConduitRoutingReport report)
        {
            if (createdSegments == null || createdSegments.Count == 0 || metadata == null)
            {
                return;
            }

            EnsureRelationId(metadata);

            var relationLengthMm = CalculateRouteLengthMm(createdSegments);
            metadata.RelationLengthMm = relationLengthMm;
            metadata.FnLengthMm = relationLengthMm;
            metadata.PeLengthMm = relationLengthMm;
            report.Relation = metadata.Relation;
            report.RelationId = metadata.RelationId;
            report.RelationSource = metadata.RelationSource;
            report.RelationLengthMm = metadata.RelationLengthMm;
            report.FnLengthMm = metadata.FnLengthMm;
            report.PeLengthMm = metadata.PeLengthMm;

            foreach (var createdSegment in createdSegments)
            {
                var conduit = createdSegment.Conduit;
                TrySetParameter(conduit, "INP_RelacjaId", metadata.RelationId, report);
                TrySetParameter(conduit, "INP_Relacja", metadata.Relation, report);
                TrySetParameter(conduit, "INP_DlugoscRelacji", metadata.RelationLengthMm, report);
                TrySetParameter(conduit, "INP_DlugoscFN", metadata.FnLengthMm, report);
                TrySetParameter(conduit, "INP_DlugoscPE", metadata.PeLengthMm, report);
                TrySetParameter(conduit, "INP_RelacjaOd", metadata.FromLabel, report, false);
                TrySetParameter(conduit, "INP_RelacjaDo", metadata.ToLabel, report, false);
                TrySetParameter(conduit, "INP_RelacjaOdElementId", GetElementIdValue(metadata.FromElementId), report, false);
                TrySetParameter(conduit, "INP_RelacjaDoElementId", GetElementIdValue(metadata.ToElementId), report, false);
            }
        }

        private static void EnsureRelationId(ConduitRouteMetadata metadata)
        {
            if (string.IsNullOrWhiteSpace(metadata.RelationId))
            {
                metadata.RelationId = Guid.NewGuid().ToString("N");
            }
        }

        private static double CalculateRouteLengthMm(IEnumerable<ConduitCreatedSegment> createdSegments)
        {
            return createdSegments
                .Where(segment => segment?.PlannedSegment?.Start != null && segment.PlannedSegment.End != null)
                .Sum(segment => segment.PlannedSegment.Start.DistanceTo(segment.PlannedSegment.End) * FeetToMm);
        }

        private static string GetElementIdValue(ElementId elementId)
        {
            return elementId == null || elementId == ElementId.InvalidElementId
                ? string.Empty
                : elementId.IntegerValue.ToString(CultureInfo.InvariantCulture);
        }

        private static void TrySetParameter(Element element, string parameterName, string value, ConduitRoutingReport report, bool warnIfMissing = true)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var parameter = element.LookupParameter(parameterName);
            if (parameter == null)
            {
                if (warnIfMissing)
                {
                    AddWarningOnce(report, $"Element {element.Id.IntegerValue}: brak parametru {parameterName}.");
                }

                return;
            }

            if (parameter.IsReadOnly)
            {
                AddWarningOnce(report, $"Element {element.Id.IntegerValue}: parametr {parameterName} jest tylko do odczytu.");
                return;
            }

            try
            {
                if (parameter.StorageType == StorageType.String)
                {
                    parameter.Set(value);
                }
                else
                {
                    parameter.SetValueString(value);
                }
            }
            catch (Exception ex)
            {
                AddWarningOnce(report, $"Element {element.Id.IntegerValue}: nie zapisano {parameterName}: {ex.Message}");
            }
        }

        private static void TrySetParameter(Element element, string parameterName, double valueMm, ConduitRoutingReport report)
        {
            var parameter = element.LookupParameter(parameterName);
            if (parameter == null)
            {
                AddWarningOnce(report, $"Element {element.Id.IntegerValue}: brak parametru {parameterName}.");
                return;
            }

            if (parameter.IsReadOnly)
            {
                AddWarningOnce(report, $"Element {element.Id.IntegerValue}: parametr {parameterName} jest tylko do odczytu.");
                return;
            }

            try
            {
                if (parameter.StorageType == StorageType.Double)
                {
                    parameter.Set(IsLengthParameter(parameter) ? valueMm / FeetToMm : valueMm);
                }
                else if (parameter.StorageType == StorageType.Integer)
                {
                    parameter.Set((int)Math.Round(valueMm));
                }
                else if (parameter.StorageType == StorageType.String)
                {
                    parameter.Set(valueMm.ToString("F1", CultureInfo.InvariantCulture));
                }
                else
                {
                    parameter.SetValueString(valueMm.ToString("F1", CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                AddWarningOnce(report, $"Element {element.Id.IntegerValue}: nie zapisano {parameterName}: {ex.Message}");
            }
        }

        private static bool IsLengthParameter(Parameter parameter)
        {
            var definition = parameter?.Definition;
            if (definition == null)
            {
                return false;
            }

            var dataType = definition.GetType().GetMethod("GetDataType", Type.EmptyTypes)?.Invoke(definition, null);
            var dataTypeId = dataType?.GetType().GetProperty("TypeId")?.GetValue(dataType)?.ToString() ?? dataType?.ToString();
            if (!string.IsNullOrWhiteSpace(dataTypeId))
            {
                return dataTypeId.IndexOf("length", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            var parameterType = definition.GetType().GetProperty("ParameterType")?.GetValue(definition)?.ToString();
            return string.Equals(parameterType, "Length", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddWarningOnce(ConduitRoutingReport report, string warning)
        {
            if (!report.Warnings.Contains(warning))
            {
                report.Warnings.Add(warning);
            }
        }
    }
}