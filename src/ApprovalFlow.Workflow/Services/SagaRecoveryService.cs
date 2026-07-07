using ApprovalFlow.Shared.Models;

namespace ApprovalFlow.Workflow.Services;

public class SagaRecoveryService(IServiceProvider sp, ILogger<SagaRecoveryService> logger) : BackgroundService
{
    private const int MaxReadinessAttempts = 10;
    private static readonly TimeSpan ReadinessDelay = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = sp.CreateScope();
        var index = scope.ServiceProvider.GetRequiredService<ISagaIndexStore>();
        var stateStore = scope.ServiceProvider.GetRequiredService<IWorkflowStateStore>();
        var saga = scope.ServiceProvider.GetRequiredService<SagaOrchestrator>();

        IReadOnlyList<string>? invoiceIds = null;
        for (var attempt = 1; attempt <= MaxReadinessAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                invoiceIds = await index.GetInFlightAsync();
                break;
            }
            catch (Exception ex) when (attempt < MaxReadinessAttempts)
            {
                logger.LogWarning(ex,
                    "Saga recovery index read failed on attempt {Attempt}/{MaxAttempts}; retrying.",
                    attempt,
                    MaxReadinessAttempts);
                await Task.Delay(ReadinessDelay, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Saga recovery index did not become ready after {MaxAttempts} attempts; leaving entries for next restart.",
                    MaxReadinessAttempts);
                return;
            }
        }

        if (invoiceIds == null || stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var scanned = 0;
        var recovered = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var invoiceId in invoiceIds)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

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

    private static bool IsTerminal(WorkflowState state) =>
        state.Status is InvoiceStatus.Paid
            or InvoiceStatus.PaymentFailed
            or InvoiceStatus.Rejected
            or InvoiceStatus.Duplicate
        || state.CurrentSagaStep is SagaStep.StatusUpdated
            or SagaStep.Compensated
            or SagaStep.Failed;
}
