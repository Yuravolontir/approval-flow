using ApprovalFlow.Shared.Models;
using Dapr.Client;

namespace ApprovalFlow.Workflow.Services;

/// <summary>
/// Thin abstraction over payment-service invocations so the saga is unit-testable.
/// </summary>
public interface IPaymentClient
{
    Task<BudgetReserveResponse> ReserveBudgetAsync(BudgetReserveRequest request);
    Task<PaymentExecuteResponse> ExecutePaymentAsync(PaymentExecuteRequest request);
    Task ReleaseBudgetAsync(string reservationId, string department, decimal amount);
}

public class DaprPaymentClient(DaprClient dapr) : IPaymentClient
{
    public Task<BudgetReserveResponse> ReserveBudgetAsync(BudgetReserveRequest request) =>
        dapr.InvokeMethodAsync<BudgetReserveRequest, BudgetReserveResponse>(
            "payment-service", "budget/reserve", request);

    public Task<PaymentExecuteResponse> ExecutePaymentAsync(PaymentExecuteRequest request) =>
        dapr.InvokeMethodAsync<PaymentExecuteRequest, PaymentExecuteResponse>(
            "payment-service", "payment/execute", request);

    public Task ReleaseBudgetAsync(string reservationId, string department, decimal amount) =>
        dapr.InvokeMethodAsync("payment-service", "budget/release",
            new { reservationId, department, amount });
}
