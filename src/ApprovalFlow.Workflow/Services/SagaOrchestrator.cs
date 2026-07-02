using ApprovalFlow.Shared.Models;
using Serilog;

namespace ApprovalFlow.Workflow.Services;

/// <summary>
/// Payment saga: reserve budget → execute payment → mark paid.
/// On execute failure: compensate by releasing the reservation.
/// On unexpected exception: emergency compensation if a reservation exists.
/// Behavior is a 1:1 extraction of the former static ExecutePaymentSaga in Program.cs.
/// </summary>
public class SagaOrchestrator(
    IPaymentClient payments,
    IWorkflowStateStore stateStore,
    IInvoiceStatusPublisher statusPublisher,
    FxRates fxRates)
{
    public async Task ExecuteAsync(string invoiceId, InvoiceDto invoice, WorkflowState state, string correlationId)
    {
        var log = Log.ForContext("CorrelationId", correlationId);
        var usdAmount = fxRates.ConvertToUsd(invoice.Total, invoice.Currency);

        try
        {
            // Step 1: Reserve budget
            log.Information("Saga step 1: Reserving budget for {InvoiceId} ({Amount:C})", invoiceId, usdAmount);
            var reserveResponse = await payments.ReserveBudgetAsync(new BudgetReserveRequest
            {
                Department = invoice.Department,
                Amount = usdAmount,
                InvoiceId = invoiceId,
                IdempotencyKey = $"reserve:{invoiceId}"
            });

            if (!reserveResponse.Success)
            {
                log.Warning("Budget reservation failed for {InvoiceId}: {Error}", invoiceId, reserveResponse.Error);
                state.Status = InvoiceStatus.PaymentFailed;
                state.CurrentSagaStep = SagaStep.Failed;
                await stateStore.SaveWorkflowAsync(state);
                await statusPublisher.PublishStatusAsync(invoiceId, InvoiceStatus.PaymentFailed,
                    $"Budget reservation failed: {reserveResponse.Error}");
                return;
            }

            state.ReservationId = reserveResponse.ReservationId;
            state.CurrentSagaStep = SagaStep.BudgetReserved;
            await stateStore.SaveWorkflowAsync(state);

            // Step 2: Execute payment
            log.Information("Saga step 2: Executing payment for {InvoiceId}", invoiceId);
            var payResponse = await payments.ExecutePaymentAsync(new PaymentExecuteRequest
            {
                InvoiceId = invoiceId,
                ReservationId = reserveResponse.ReservationId,
                Amount = usdAmount,
                Scenario = invoice.Scenario
            });

            if (!payResponse.Success)
            {
                // COMPENSATE: Release budget reservation
                log.Warning("Payment failed for {InvoiceId}: {Error}. Compensating...", invoiceId, payResponse.Error);
                state.CurrentSagaStep = SagaStep.Compensating;
                await stateStore.SaveWorkflowAsync(state);

                await payments.ReleaseBudgetAsync(reserveResponse.ReservationId, invoice.Department, usdAmount);

                state.Status = InvoiceStatus.PaymentFailed;
                state.CurrentSagaStep = SagaStep.Compensated;
                await stateStore.SaveWorkflowAsync(state);
                await statusPublisher.PublishStatusAsync(invoiceId, InvoiceStatus.PaymentFailed,
                    $"Payment failed, budget released: {payResponse.Error}");
                log.Information("Compensation complete for {InvoiceId}, budget restored", invoiceId);
                return;
            }

            state.CurrentSagaStep = SagaStep.PaymentExecuted;

            // Step 3: Update status to paid
            state.Status = InvoiceStatus.Paid;
            state.CurrentSagaStep = SagaStep.StatusUpdated;
            await stateStore.SaveWorkflowAsync(state);
            await statusPublisher.PublishStatusAsync(invoiceId, InvoiceStatus.Paid, "Payment completed successfully.");

            // Update dashboard stats
            await UpdateDashboardStatsAsync(state);

            log.Information("Invoice {InvoiceId} paid successfully", invoiceId);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Saga error for {InvoiceId}, attempting compensation", invoiceId);

            // Compensate if we have a reservation
            if (!string.IsNullOrEmpty(state.ReservationId))
            {
                try
                {
                    await payments.ReleaseBudgetAsync(state.ReservationId, invoice.Department, usdAmount);
                    log.Information("Emergency compensation complete for {InvoiceId}", invoiceId);
                }
                catch (Exception compEx)
                {
                    log.Error(compEx, "Emergency compensation FAILED for {InvoiceId}", invoiceId);
                }
            }

            state.Status = InvoiceStatus.PaymentFailed;
            state.CurrentSagaStep = SagaStep.Failed;
            await stateStore.SaveWorkflowAsync(state);
            await statusPublisher.PublishStatusAsync(invoiceId, InvoiceStatus.PaymentFailed,
                $"Payment processing error: {ex.Message}");
        }
    }

    private async Task UpdateDashboardStatsAsync(WorkflowState state)
    {
        var stats = await stateStore.GetDashboardStatsAsync() ?? new DashboardResponse();

        stats.TotalProcessed++;
        var usdAmount = fxRates.ConvertToUsd(state.Invoice.Total, state.Invoice.Currency);

        switch (state.Route)
        {
            case RouteDecision.AutoApprove:
                stats.AutoApproved++;
                stats.TotalAutoApprovedAmount += usdAmount;
                break;
            case RouteDecision.HumanReview:
                stats.HumanReviewed++;
                stats.TotalHumanReviewedAmount += usdAmount;
                break;
            case RouteDecision.Reject:
                stats.Rejected++;
                break;
            case RouteDecision.Duplicate:
                stats.Duplicates++;
                break;
        }

        await stateStore.SaveDashboardStatsAsync(stats);
    }
}
