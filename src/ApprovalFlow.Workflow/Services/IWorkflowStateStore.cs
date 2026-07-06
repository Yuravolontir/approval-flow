using ApprovalFlow.Shared.Constants;
using ApprovalFlow.Shared.Models;
using Dapr.Client;

namespace ApprovalFlow.Workflow.Services;

/// <summary>
/// Thin abstraction over the Dapr state store operations used by the saga.
/// </summary>
public interface IWorkflowStateStore
{
    Task<WorkflowState?> GetWorkflowAsync(string invoiceId);
    Task SaveWorkflowAsync(WorkflowState state);
    Task<DashboardResponse?> GetDashboardStatsAsync();
    Task SaveDashboardStatsAsync(DashboardResponse stats);
}

public class DaprWorkflowStateStore(DaprClient dapr) : IWorkflowStateStore
{
    public Task<WorkflowState?> GetWorkflowAsync(string invoiceId) =>
        dapr.GetStateAsync<WorkflowState?>(DaprComponents.StateStore, $"workflow:{invoiceId}");

    public Task SaveWorkflowAsync(WorkflowState state) =>
        dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{state.InvoiceId}", state);

    public Task<DashboardResponse?> GetDashboardStatsAsync() =>
        dapr.GetStateAsync<DashboardResponse?>(DaprComponents.StateStore, "dashboard:stats");

    public Task SaveDashboardStatsAsync(DashboardResponse stats) =>
        dapr.SaveStateAsync(DaprComponents.StateStore, "dashboard:stats", stats);
}
