using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using INP_IE.ConduitManager.Models;
using INP_IE.ConduitManager.Services;
using INP_IE.ConduitRouting.Models;

namespace INP_IE.ConduitRouting.Services
{
    public class ConduitRoutingService
    {
        private readonly Document _doc;

        public ConduitRoutingService(Document doc)
        {
            _doc = doc;
        }

        public ConduitRoutingReport FillSelectedTrayNetwork(IList<Element> elements, ConduitRoutingSettings settings)
        {
            var executionPlan = PrepareFillSelectedTrayNetwork(elements, settings);
            return ExecutePreparedPlan(executionPlan);
        }

        public ConduitRoutingReport RouteBetweenTrayPoints(Element startElement, XYZ startPoint, Element endElement, XYZ endPoint, ConduitRoutingSettings settings)
        {
            var executionPlan = PrepareRouteBetweenTrayPoints(startElement, startPoint, endElement, endPoint, settings);
            return ExecutePreparedPlan(executionPlan);
        }

        public ConduitExecutionPlan PrepareFillSelectedTrayNetwork(IList<Element> elements, ConduitRoutingSettings settings)
        {
            var report = new ConduitRoutingReport
            {
                SelectedCableTrays = elements.Count(TrayElementClassifier.IsCableTray),
                SelectedFittings = elements.Count(TrayElementClassifier.IsCableTrayFitting)
            };

            if (!EnsureConduitType(settings, report))
            {
                return CreateExecutionPlan(new ConduitRoutePlan(), settings, report, "Fill trays with conduits", "Wypelnienie korytek conduitami");
            }

            ApplySingleConduitRepresentation(settings);

            var planner = new ConduitRoutePlanner(_doc);
            var plan = planner.CreatePlanForTrayNetwork(elements, settings, report);
            ConduitPlanAnalyzer.Analyze(plan, report);
            return CreateExecutionPlan(plan, settings, report, "Fill trays with conduits", "Wypelnienie korytek conduitami");
        }

        public ConduitExecutionPlan PrepareRouteBetweenTrayPoints(Element startElement, XYZ startPoint, Element endElement, XYZ endPoint, ConduitRoutingSettings settings)
        {
            var report = new ConduitRoutingReport();
            if (!EnsureConduitType(settings, report))
            {
                return CreateExecutionPlan(new ConduitRoutePlan(), settings, report, "Route conduits between tray points", "Trasa miedzy punktami");
            }

            ApplySingleConduitRepresentation(settings);

            var planner = new ConduitRoutePlanner(_doc);
            var plan = planner.CreatePlanBetweenTrayPoints(startElement, startPoint, endElement, endPoint, settings, report);
            ConduitPlanAnalyzer.Analyze(plan, report);
            return CreateExecutionPlan(plan, settings, report, "Route conduits between tray points", "Trasa miedzy punktami");
        }

        public ConduitExecutionPlan PrepareRouteBetweenTrayPointsViaTray(
            Element startElement,
            XYZ startPoint,
            Element endElement,
            XYZ endPoint,
            Element requiredTrayElement,
            ConduitRoutingSettings settings)
        {
            var report = new ConduitRoutingReport();
            if (!EnsureConduitType(settings, report))
            {
                return CreateExecutionPlan(
                    new ConduitRoutePlan(),
                    settings,
                    report,
                    "Route conduits between tray points via tray",
                    "Trasa między punktami przez wskazane korytko");
            }

            ApplySingleConduitRepresentation(settings);

            var planner = new ConduitRoutePlanner(_doc);
            var plan = planner.CreatePlanBetweenTrayPointsViaTray(
                startElement,
                startPoint,
                endElement,
                endPoint,
                requiredTrayElement,
                settings,
                report);
            ConduitPlanAnalyzer.Analyze(plan, report);
            return CreateExecutionPlan(
                plan,
                settings,
                report,
                "Route conduits between tray points via tray",
                "Trasa między punktami przez wskazane korytko");
        }

        public ConduitExecutionPlan PrepareCollisionAwareRouteBetweenTrayPoints(
            Element startElement,
            XYZ startPoint,
            Element endElement,
            XYZ endPoint,
            ConduitRoutingSettings settings)
        {
            var report = new ConduitRoutingReport();
            if (!TryStartCollisionAwareRoute(settings, report, out var planner, out var earlyExit))
            {
                return earlyExit;
            }

            var rawPath = planner.BuildRawRouteBetweenTrayPoints(
                startElement, startPoint, endElement, endPoint, settings, report);
            return FinishCollisionAwareRoute(rawPath, settings, report);
        }

        public ConduitExecutionPlan PrepareCollisionAwareRouteBetweenTrayPointsViaTray(
            Element startElement,
            XYZ startPoint,
            Element endElement,
            XYZ endPoint,
            Element requiredTrayElement,
            ConduitRoutingSettings settings)
        {
            var report = new ConduitRoutingReport();
            if (!TryStartCollisionAwareRoute(settings, report, out var planner, out var earlyExit))
            {
                return earlyExit;
            }

            var rawPath = planner.BuildRawRouteBetweenTrayPointsViaTray(
                startElement,
                startPoint,
                endElement,
                endPoint,
                requiredTrayElement,
                settings,
                report);
            return FinishCollisionAwareRoute(rawPath, settings, report);
        }

        /// <summary>
        /// Wariant przyjmujący gotowe końce trasy — pozwala doprowadzić conduit
        /// aż do urządzenia, przy którym nie ma trasy kablowej
        /// </summary>
        public ConduitExecutionPlan PrepareCollisionAwareRoute(
            ConduitAutomaticRouteEndpoints endpoints,
            ConduitRoutingSettings settings)
        {
            var report = new ConduitRoutingReport();
            if (!TryStartCollisionAwareRoute(settings, report, out var planner, out var earlyExit))
            {
                return earlyExit;
            }

            var rawPath = planner.BuildRawRoute(endpoints.Start, endpoints.End, settings, report);
            return FinishCollisionAwareRoute(rawPath, settings, report);
        }

        private bool TryStartCollisionAwareRoute(
            ConduitRoutingSettings settings,
            ConduitRoutingReport report,
            out ConduitRoutePlanner planner,
            out ConduitExecutionPlan earlyExit)
        {
            planner = null;
            earlyExit = null;
            if (!EnsureConduitType(settings, report))
            {
                earlyExit = CreateExecutionPlan(new ConduitRoutePlan(), settings, report, "Route conduits between tray points", "Trasa miedzy punktami");
                return false;
            }

            ApplySingleConduitLayout(settings);
            planner = new ConduitRoutePlanner(_doc);
            return true;
        }

        private ConduitExecutionPlan FinishCollisionAwareRoute(
            List<ConduitRunSegment> rawPath,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report)
        {
            if (rawPath.Count == 0)
            {
                return CreateExecutionPlan(new ConduitRoutePlan(), settings, report, "Route conduits between tray points", "Trasa miedzy punktami");
            }

            var allocationContext = new ConduitAllocationContext();
            var allocator = new ConduitPositionAllocator(_doc);
            allocator.SeedExistingConduits(allocationContext);

            var allocation = allocator.AllocateLane(rawPath, allocationContext, settings);
            if (!allocation.Success)
            {
                if (!string.IsNullOrWhiteSpace(allocation.SkipReason))
                {
                    report.Warnings.Add(allocation.SkipReason);
                }

                return CreateExecutionPlan(new ConduitRoutePlan(), settings, report, "Route conduits between tray points", "Trasa miedzy punktami");
            }

            if (allocation.LaneIndex != 0)
            {
                report.Warnings.Add(
                    allocation.CenterBlockedByModel
                        ? $"Tor środkowy jest zajęty przez istniejący conduit. Przydzielono najbliższy wolny tor {allocation.LaneIndex}."
                        : $"Przydzielono najbliższy wolny tor {allocation.LaneIndex}.");
            }

            var plan = ConduitRoutePlanner.CreatePlan(allocation.Segments);
            ConduitPlanAnalyzer.Analyze(plan, report);
            return CreateExecutionPlan(plan, settings, report, "Route conduits between tray points", "Trasa miedzy punktami");
        }

        public ConduitRoutingReport ExecutePreparedPlan(ConduitExecutionPlan executionPlan)
        {
            if (executionPlan == null)
            {
                var report = new ConduitRoutingReport();
                report.Warnings.Add("Brak przygotowanego planu wykonania conduitow.");
                return report;
            }

            ExecutePlan(executionPlan.RoutePlan, executionPlan.Settings, executionPlan.Report, executionPlan.TransactionName, executionPlan.Metadata);
            return executionPlan.Report;
        }

        private static ConduitExecutionPlan CreateExecutionPlan(
            ConduitRoutePlan plan,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report,
            string transactionName,
            string operationName)
        {
            return new ConduitExecutionPlan(plan, settings, report, transactionName, operationName);
        }

        private static void ApplySingleConduitRepresentation(ConduitRoutingSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            settings.LayoutMode = ConduitLayoutMode.Single;
            settings.FixedCount = 1;
            settings.SpacingMm = 0;
            settings.MarginMm = 0;
        }

        private static void ApplySingleConduitLayout(ConduitRoutingSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            settings.LayoutMode = ConduitLayoutMode.Single;
            settings.FixedCount = 1;
        }

        private bool EnsureConduitType(ConduitRoutingSettings settings, ConduitRoutingReport report)
        {
            if (settings == null)
            {
                report.Warnings.Add("Brak ustawien generowania conduitow.");
                return false;
            }

            settings.ConduitTypeId = settings.ConduitTypeId == ElementId.InvalidElementId
                ? GetDefaultConduitTypeId()
                : settings.ConduitTypeId;

            if (settings.ConduitTypeId != ElementId.InvalidElementId)
            {
                return true;
            }

            report.Warnings.Add("Nie znaleziono typu Conduit w projekcie.");
            return false;
        }

        private void ExecutePlan(ConduitRoutePlan plan, ConduitRoutingSettings settings, ConduitRoutingReport report, string transactionName, ConduitRouteMetadata metadata)
        {
            if (plan == null || plan.IsEmpty)
            {
                return;
            }

            using (var transaction = new Transaction(_doc, transactionName))
            {
                var transactionStarted = false;
                try
                {
                    transaction.Start();
                    transactionStarted = true;

                    var factory = new ConduitElementFactory(_doc);
                    var creationResult = factory.CreateConduits(plan, settings, report);
                    _doc.Regenerate();

                    var metadataWriter = new ConduitMetadataWriter();
                    metadataWriter.Write(creationResult.CreatedSegments, metadata, report);

                    var connectorService = new ConduitConnectorService(_doc);
                    connectorService.Connect(creationResult.CreatedSegments, settings, report);

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    report.TransactionRolledBack = true;
                    report.CreatedConduits = 0;
                    report.CreatedConnections = 0;
                    report.CreatedElementIds.Clear();
                    report.Warnings.Add($"Przerwano transakcje generowania conduitow: {ex.Message}");

                    if (transactionStarted && transaction.GetStatus() == TransactionStatus.Started)
                    {
                        try
                        {
                            transaction.RollBack();
                        }
                        catch (Exception rollbackEx)
                        {
                            report.Warnings.Add($"Nie udalo sie wykonac rollback transakcji: {rollbackEx.Message}");
                        }
                    }
                }
            }
        }

        private ElementId GetDefaultConduitTypeId()
        {
            return new FilteredElementCollector(_doc)
                .OfClass(typeof(ConduitType))
                .FirstElementId();
        }
    }
}
