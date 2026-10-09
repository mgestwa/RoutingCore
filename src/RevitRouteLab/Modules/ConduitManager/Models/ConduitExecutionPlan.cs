using INP_IE.ConduitRouting.Models;

namespace INP_IE.ConduitManager.Models
{
    public class ConduitExecutionPlan
    {
        public ConduitExecutionPlan(
            ConduitRoutePlan routePlan,
            ConduitRoutingSettings settings,
            ConduitRoutingReport report,
            string transactionName,
            string operationName)
        {
            RoutePlan = routePlan ?? new ConduitRoutePlan();
            Settings = settings;
            Report = report ?? new ConduitRoutingReport();
            TransactionName = transactionName;
            OperationName = operationName;
        }

        public ConduitRoutePlan RoutePlan { get; }

        public ConduitRoutingSettings Settings { get; }

        public ConduitRoutingReport Report { get; }

        public string TransactionName { get; }

        public string OperationName { get; }

        public string EndpointSummary { get; set; } = string.Empty;

        public ConduitRouteMetadata Metadata { get; set; }

        public bool CanExecute => Settings != null && RoutePlan != null && !RoutePlan.IsEmpty;
    }
}