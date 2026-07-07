using ApprovalFlow.Shared.Constants;
using ApprovalFlow.Shared.Events;
using ApprovalFlow.Shared.Models;
using ApprovalFlow.Workflow.Services.Agent;
using Dapr.Client;
using Serilog;

namespace ApprovalFlow.Workflow.Services;

public enum InboxHandlingResult
{
    Ok,
    ServiceUnavailable,
    Error
}

public class InvoiceSubmittedProcessor(
    ILlmClient llm,
    DeterministicRouter router,
    SagaOrchestrator saga,
    IInvoiceStatusPublisher statusPublisher,
    IWorkflowStateStore stateStore,
    ISagaIndexStore sagaIndex,
    IInboxStore inbox,
    DaprClient dapr,
    string policyText)
{
    public async Task<InboxHandlingResult> HandleAsync(InvoiceSubmittedEvent evt)
    {
        var begin = await inbox.TryBeginAsync(evt.InvoiceId);
        switch (begin)
        {
            case BeginResult.AlreadyCompleted:
                Log.Information("Duplicate completed delivery of {InvoiceId}; acking without reprocessing", evt.InvoiceId);
                return InboxHandlingResult.Ok;

            case BeginResult.AlreadyInProgress:
                var state = await stateStore.GetWorkflowAsync(evt.InvoiceId);
                if (await IsHandledAsync(evt.InvoiceId, state))
                {
                    await inbox.MarkCompletedAsync(evt.InvoiceId);
                    return InboxHandlingResult.Ok;
                }

                return InboxHandlingResult.ServiceUnavailable;

            case BeginResult.Claimed:
                try
                {
                    await DoWorkAsync(evt);
                    await inbox.MarkCompletedAsync(evt.InvoiceId);
                    return InboxHandlingResult.Ok;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Invoice-submitted processing failed for {InvoiceId}", evt.InvoiceId);
                    return InboxHandlingResult.Error;
                }

            default:
                return InboxHandlingResult.Error;
        }
    }

    private async Task DoWorkAsync(InvoiceSubmittedEvent evt)
    {
        var correlationId = evt.CorrelationId;
        var log = Log.ForContext("CorrelationId", correlationId);

        if (evt.Invoice.Scenario?.Contains("crash-after-begin-before-savestate") == true)
        {
            log.Warning("FAULT INJECTION: crashing after inbox begin, before workflow state save for {InvoiceId}", evt.InvoiceId);
            Environment.Exit(1);
        }

        log.Information("Processing invoice {InvoiceId} from {Vendor}", evt.InvoiceId, evt.Invoice.Vendor);

        // 1. Call AI Agent
        AgentDecision agentDecision;
        try
        {
            agentDecision = await llm.AnalyzeInvoiceAsync(evt.Invoice, policyText);
            log.Information("Agent decision for {InvoiceId}: {Recommendation} (confidence={Confidence:P0})",
                evt.InvoiceId, agentDecision.Recommendation, agentDecision.Confidence);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Agent failed for {InvoiceId}, escalating to human", evt.InvoiceId);
            agentDecision = new AgentDecision
            {
                Recommendation = "escalate",
                Confidence = 0.0,
                Violations = new List<string>(),
                Reasoning = "Agent analysis failed. Escalating to human as safety fallback."
            };
        }

        // 2. Call Deterministic Router
        var routerResult = router.Route(evt.Invoice, agentDecision);
        log.Information("Router decision for {InvoiceId}: {Route} [{Violations}]",
            evt.InvoiceId, routerResult.Route, string.Join(", ", routerResult.Violations));

        // 3. Create workflow state
        var state = new WorkflowState
        {
            InvoiceId = evt.InvoiceId,
            Invoice = evt.Invoice,
            AgentDecision = agentDecision,
            Route = routerResult.Route,
            RouteReason = routerResult.Reason,
            RouterViolations = routerResult.Violations,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // 4. Handle based on route
        switch (routerResult.Route)
        {
            case RouteDecision.AutoApprove:
                state.Status = InvoiceStatus.AutoApproved;
                await stateStore.SaveWorkflowAsync(state);

                if (evt.Invoice.Scenario?.Contains("crash-after-savestate-before-saga") == true)
                {
                    log.Warning("FAULT INJECTION: crashing after workflow state save, before saga for {InvoiceId}", evt.InvoiceId);
                    Environment.Exit(1);
                }

                // Start payment saga
                await saga.ExecuteAsync(evt.InvoiceId, evt.Invoice, state, correlationId);
                break;

            case RouteDecision.HumanReview:
                state.Status = InvoiceStatus.PendingReview;
                state.HitlStatus = HitlStatus.PendingReview;
                await stateStore.SaveWorkflowAsync(state);

                if (evt.Invoice.Scenario?.Contains("crash-after-savestate-before-queue") == true)
                {
                    log.Warning("FAULT INJECTION: crashing after workflow state save, before HITL queue for {InvoiceId}", evt.InvoiceId);
                    Environment.Exit(1);
                }

                // Add to HITL queue
                var queue = await dapr.GetStateAsync<List<string>>(DaprComponents.StateStore, "hitl:queue") ?? new List<string>();
                if (!queue.Contains(evt.InvoiceId))
                {
                    queue.Add(evt.InvoiceId);
                    await dapr.SaveStateAsync(DaprComponents.StateStore, "hitl:queue", queue);
                }

                // Update invoice status
                await statusPublisher.PublishStatusAsync(evt.InvoiceId, InvoiceStatus.PendingReview, routerResult.Reason);
                log.Information("Invoice {InvoiceId} queued for human review", evt.InvoiceId);
                break;

            case RouteDecision.Reject:
                state.Status = InvoiceStatus.Rejected;
                await stateStore.SaveWorkflowAsync(state);
                await statusPublisher.PublishStatusAsync(evt.InvoiceId, InvoiceStatus.Rejected, routerResult.Reason);
                log.Information("Invoice {InvoiceId} rejected: {Reason}", evt.InvoiceId, routerResult.Reason);
                break;

            case RouteDecision.Duplicate:
                state.Status = InvoiceStatus.Duplicate;
                await stateStore.SaveWorkflowAsync(state);
                await statusPublisher.PublishStatusAsync(evt.InvoiceId, InvoiceStatus.Duplicate, routerResult.Reason);
                break;
        }
    }

    private async Task<bool> IsHandledAsync(string invoiceId, WorkflowState? state)
    {
        if (state == null)
        {
            return false;
        }

        if (StartupRecovery.IsTerminal(state))
        {
            return true;
        }

        var inflight = await sagaIndex.GetInFlightAsync();
        if (inflight.Contains(invoiceId))
        {
            return true;
        }

        var queue = await dapr.GetStateAsync<List<string>>(DaprComponents.StateStore, "hitl:queue") ?? new();
        return queue.Contains(invoiceId);
    }
}
