using ApprovalFlow.Shared.Constants;
using ApprovalFlow.Shared.Models;
using Dapr.Client;

namespace ApprovalFlow.Workflow.Services;

public enum InboxRepairAction
{
    Reprocessed,
    Promoted,
    RepairedSaga,
    RepairedQueue,
    RepairedPublish
}

public sealed class StartupRecoveryRepairContext(
    IInboxStore inbox,
    ISagaIndexStore sagaIndex,
    SagaOrchestrator saga,
    IInvoiceStatusPublisher statusPublisher,
    Func<Task<List<string>>> getQueueAsync,
    Func<List<string>, Task> saveQueueAsync)
{
    public IInboxStore Inbox { get; } = inbox;
    public ISagaIndexStore SagaIndex { get; } = sagaIndex;
    public SagaOrchestrator Saga { get; } = saga;
    public IInvoiceStatusPublisher StatusPublisher { get; } = statusPublisher;
    public Func<Task<List<string>>> GetQueueAsync { get; } = getQueueAsync;
    public Func<List<string>, Task> SaveQueueAsync { get; } = saveQueueAsync;
}

public static class StartupRecovery
{
    private const int MaxReadinessAttempts = 10;
    private static readonly TimeSpan ReadinessDelay = TimeSpan.FromSeconds(3);

    public static async Task RunAsync(IServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("StartupRecovery");
        var inbox = scope.ServiceProvider.GetRequiredService<IInboxStore>();
        var stateStore = scope.ServiceProvider.GetRequiredService<IWorkflowStateStore>();
        var sagaIndex = scope.ServiceProvider.GetRequiredService<ISagaIndexStore>();
        var saga = scope.ServiceProvider.GetRequiredService<SagaOrchestrator>();
        var statusPublisher = scope.ServiceProvider.GetRequiredService<IInvoiceStatusPublisher>();
        var dapr = scope.ServiceProvider.GetRequiredService<DaprClient>();

        var inboxIds = await ReadInboxIndexWhenReadyAsync(inbox, logger);
        if (inboxIds == null)
        {
            return;
        }

        var repairContext = new StartupRecoveryRepairContext(
            inbox,
            sagaIndex,
            saga,
            statusPublisher,
            () => GetHitlQueueAsync(dapr),
            queue => dapr.SaveStateAsync(DaprComponents.StateStore, "hitl:queue", queue));

        var inboxScanned = 0;
        var inboxFailed = 0;

        foreach (var invoiceId in inboxIds)
        {
            inboxScanned++;
            try
            {
                var state = await stateStore.GetWorkflowAsync(invoiceId);
                await RepairInboxEntryAsync(repairContext, invoiceId, state);
            }
            catch (Exception ex)
            {
                inboxFailed++;
                logger.LogError(ex, "Inbox recovery failed for {InvoiceId}; leaving it indexed for next restart.", invoiceId);
            }
        }

        logger.LogInformation(
            "Inbox recovery scan complete: scanned {Scanned}, failed {Failed}.",
            inboxScanned,
            inboxFailed);

        await RunSagaRecoveryPassAsync(sagaIndex, stateStore, saga, logger);
    }

    public static async Task<InboxRepairAction> RepairInboxEntryAsync(
        StartupRecoveryRepairContext context,
        string invoiceId,
        WorkflowState? state)
    {
        if (state == null)
        {
            await context.Inbox.RemoveAsync(invoiceId);
            return InboxRepairAction.Reprocessed;
        }

        var inflight = await context.SagaIndex.GetInFlightAsync();
        var queue = await context.GetQueueAsync();

        if (IsHandled(invoiceId, state, inflight, queue))
        {
            await context.Inbox.MarkCompletedAsync(invoiceId);
            return InboxRepairAction.Promoted;
        }

        if (state.Status == InvoiceStatus.AutoApproved && state.CurrentSagaStep == SagaStep.NotStarted)
        {
            await context.Saga.ExecuteAsync(invoiceId, state.Invoice, state, $"startup-inbox:{invoiceId}");
            await context.Inbox.MarkCompletedAsync(invoiceId);
            return InboxRepairAction.RepairedSaga;
        }

        if (state.Status == InvoiceStatus.PendingReview)
        {
            if (!queue.Contains(invoiceId))
            {
                queue.Add(invoiceId);
                await context.SaveQueueAsync(queue);
            }

            await context.Inbox.MarkCompletedAsync(invoiceId);
            return InboxRepairAction.RepairedQueue;
        }

        if (state.Status is InvoiceStatus.Rejected or InvoiceStatus.Duplicate)
        {
            await context.StatusPublisher.PublishStatusAsync(invoiceId, state.Status, state.RouteReason ?? "");
            await context.Inbox.MarkCompletedAsync(invoiceId);
            return InboxRepairAction.RepairedPublish;
        }

        await context.Inbox.MarkCompletedAsync(invoiceId);
        return InboxRepairAction.Promoted;
    }

    public static bool IsHandled(
        string invoiceId,
        WorkflowState state,
        IReadOnlyList<string> inflight,
        IReadOnlyList<string> queue)
    {
        if (IsTerminal(state))
        {
            return true;
        }

        if (inflight.Contains(invoiceId))
        {
            return true;
        }

        return queue.Contains(invoiceId);
    }

    public static bool IsTerminal(WorkflowState state) =>
        state.Status is InvoiceStatus.Paid
            or InvoiceStatus.PaymentFailed
            or InvoiceStatus.Rejected
            or InvoiceStatus.Duplicate
        || state.CurrentSagaStep is SagaStep.StatusUpdated
            or SagaStep.Compensated
            or SagaStep.Failed;

    private static async Task<IReadOnlyList<string>?> ReadInboxIndexWhenReadyAsync(
        IInboxStore inbox,
        ILogger logger)
    {
        for (var attempt = 1; attempt <= MaxReadinessAttempts; attempt++)
        {
            try
            {
                return await inbox.GetIndexAsync();
            }
            catch (Exception ex) when (attempt < MaxReadinessAttempts)
            {
                logger.LogWarning(ex,
                    "Startup inbox recovery index read failed on attempt {Attempt}/{MaxAttempts}; retrying.",
                    attempt,
                    MaxReadinessAttempts);
                await Task.Delay(ReadinessDelay);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Startup inbox recovery index did not become ready after {MaxAttempts} attempts; leaving entries for next restart.",
                    MaxReadinessAttempts);
                return null;
            }
        }

        return null;
    }

    private static async Task RunSagaRecoveryPassAsync(
        ISagaIndexStore index,
        IWorkflowStateStore stateStore,
        SagaOrchestrator saga,
        ILogger logger)
    {
        var invoiceIds = await index.GetInFlightAsync();
        var scanned = 0;
        var recovered = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var invoiceId in invoiceIds)
        {
            scanned++;
            try
            {
                var state = await stateStore.GetWorkflowAsync(invoiceId);
                if (state == null || IsTerminal(state))
                {
                    await index.RemoveInFlightAsync(invoiceId);
                    skipped++;
                    continue;
                }

                if (state.CurrentSagaStep is SagaStep.Reserving or SagaStep.BudgetReserved or SagaStep.Compensating)
                {
                    await saga.RecoverAsync(state, $"recovery:{invoiceId}:{Guid.NewGuid():N}");
                    recovered++;
                    continue;
                }

                await index.RemoveInFlightAsync(invoiceId);
                skipped++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(ex, "Saga recovery failed for {InvoiceId}; leaving it indexed for next restart.", invoiceId);
            }
        }

        logger.LogInformation(
            "Saga recovery scan complete: scanned {Scanned}, recovered {Recovered}, skipped {Skipped}, failed {Failed}.",
            scanned,
            recovered,
            skipped,
            failed);
    }

    private static async Task<List<string>> GetHitlQueueAsync(DaprClient dapr) =>
        await dapr.GetStateAsync<List<string>>(DaprComponents.StateStore, "hitl:queue") ?? new List<string>();
}
