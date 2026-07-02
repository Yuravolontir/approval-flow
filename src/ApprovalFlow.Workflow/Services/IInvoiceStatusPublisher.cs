using ApprovalFlow.Shared.Models;
using Dapr.Client;
using Serilog;

namespace ApprovalFlow.Workflow.Services;

/// <summary>
/// Pushes a status update to the Invoice Service — the sole owner of status:{id}.
/// The workflow service never writes invoice status to the state store directly.
/// </summary>
public interface IInvoiceStatusPublisher
{
    Task PublishStatusAsync(string invoiceId, InvoiceStatus status, string reason);
}

/// <summary>Single-attempt Dapr service invocation (PUT invoices/{id}/status).</summary>
public class DaprInvoiceStatusPublisher(DaprClient dapr) : IInvoiceStatusPublisher
{
    public Task PublishStatusAsync(string invoiceId, InvoiceStatus status, string reason) =>
        dapr.InvokeMethodAsync(HttpMethod.Put, "invoice-service", $"invoices/{invoiceId}/status",
            new InvoiceStatusResponse
            {
                InvoiceId = invoiceId,
                Status = status.ToString(),
                Reason = reason
            });
}

/// <summary>
/// Retry decorator: 3 attempts with linear backoff; on final failure logs loudly
/// instead of throwing, so a saga never dies because of a transient status update.
/// </summary>
public class RetryingInvoiceStatusPublisher(IInvoiceStatusPublisher inner, TimeSpan? retryDelayBase = null)
    : IInvoiceStatusPublisher
{
    private const int MaxAttempts = 3;
    private readonly TimeSpan _delayBase = retryDelayBase ?? TimeSpan.FromMilliseconds(500);

    public async Task PublishStatusAsync(string invoiceId, InvoiceStatus status, string reason)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await inner.PublishStatusAsync(invoiceId, status, reason);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                Log.Warning(ex, "Status update for {InvoiceId} failed (attempt {Attempt}/{MaxAttempts}), retrying",
                    invoiceId, attempt, MaxAttempts);
                await Task.Delay(_delayBase * attempt);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Status update for {InvoiceId} failed after {MaxAttempts} attempts — status NOT updated",
                    invoiceId, MaxAttempts);
            }
        }
    }
}
