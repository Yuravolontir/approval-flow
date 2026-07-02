using ApprovalFlow.Shared.Models;
using ApprovalFlow.Workflow.Services;

namespace ApprovalFlow.Tests;

// === Hand-rolled fakes (project convention: no mocking library) ===

internal class FakePaymentClient : IPaymentClient
{
    public List<BudgetReserveRequest> ReserveCalls { get; } = new();
    public List<PaymentExecuteRequest> ExecuteCalls { get; } = new();
    public List<(string ReservationId, string Department, decimal Amount)> ReleaseCalls { get; } = new();

    public BudgetReserveResponse ReserveResponse { get; set; } =
        new() { Success = true, ReservationId = "res-1" };
    public PaymentExecuteResponse ExecuteResponse { get; set; } = new() { Success = true };
    public Exception? ReserveThrows { get; set; }
    public Exception? ExecuteThrows { get; set; }

    public Task<BudgetReserveResponse> ReserveBudgetAsync(BudgetReserveRequest request)
    {
        ReserveCalls.Add(request);
        if (ReserveThrows != null) throw ReserveThrows;
        return Task.FromResult(ReserveResponse);
    }

    public Task<PaymentExecuteResponse> ExecutePaymentAsync(PaymentExecuteRequest request)
    {
        ExecuteCalls.Add(request);
        if (ExecuteThrows != null) throw ExecuteThrows;
        return Task.FromResult(ExecuteResponse);
    }

    public Task ReleaseBudgetAsync(string reservationId, string department, decimal amount)
    {
        ReleaseCalls.Add((reservationId, department, amount));
        return Task.CompletedTask;
    }
}

internal class FakeWorkflowStateStore : IWorkflowStateStore
{
    public List<SagaStep> SavedSteps { get; } = new();
    public DashboardResponse? Stats { get; set; }
    public bool StatsSaved { get; private set; }

    public Task SaveWorkflowAsync(WorkflowState state)
    {
        SavedSteps.Add(state.CurrentSagaStep);
        return Task.CompletedTask;
    }

    public Task<DashboardResponse?> GetDashboardStatsAsync() => Task.FromResult(Stats);

    public Task SaveDashboardStatsAsync(DashboardResponse stats)
    {
        Stats = stats;
        StatsSaved = true;
        return Task.CompletedTask;
    }
}

internal class FakeStatusPublisher : IInvoiceStatusPublisher
{
    public List<(string InvoiceId, InvoiceStatus Status, string Reason)> Published { get; } = new();

    public Task PublishStatusAsync(string invoiceId, InvoiceStatus status, string reason)
    {
        Published.Add((invoiceId, status, reason));
        return Task.CompletedTask;
    }
}

public class SagaOrchestratorTests
{
    private readonly FakePaymentClient _payments = new();
    private readonly FakeWorkflowStateStore _stateStore = new();
    private readonly FakeStatusPublisher _publisher = new();

    private SagaOrchestrator CreateSaga() =>
        new(_payments, _stateStore, _publisher, new FxRates());

    private static (InvoiceDto Invoice, WorkflowState State) CreateInvoice(decimal total = 100m)
    {
        var invoice = new InvoiceDto
        {
            Vendor = "Acme",
            InvoiceNumber = "INV-1",
            Department = "engineering-2026Q2",
            Total = total,
            Currency = "USD"
        };
        var state = new WorkflowState
        {
            InvoiceId = "inv-1",
            Invoice = invoice,
            Route = RouteDecision.AutoApprove,
            Status = InvoiceStatus.AutoApproved
        };
        return (invoice, state);
    }

    [Fact]
    public async Task HappyPath_PaysAndUpdatesDashboard()
    {
        var (invoice, state) = CreateInvoice();

        await CreateSaga().ExecuteAsync("inv-1", invoice, state, "corr-1");

        Assert.Equal(InvoiceStatus.Paid, state.Status);
        Assert.Equal(SagaStep.StatusUpdated, state.CurrentSagaStep);
        Assert.Equal("res-1", state.ReservationId);
        Assert.Empty(_payments.ReleaseCalls);
        Assert.Single(_payments.ExecuteCalls);
        Assert.Equal("reserve:inv-1", _payments.ReserveCalls.Single().IdempotencyKey);
        var published = Assert.Single(_publisher.Published);
        Assert.Equal(InvoiceStatus.Paid, published.Status);
        Assert.True(_stateStore.StatsSaved);
        Assert.Equal(1, _stateStore.Stats!.TotalProcessed);
        Assert.Equal(1, _stateStore.Stats.AutoApproved);
        Assert.Equal(100m, _stateStore.Stats.TotalAutoApprovedAmount);
    }

    [Fact]
    public async Task ReserveFails_MarksFailed_NoExecute_NoRelease()
    {
        var (invoice, state) = CreateInvoice();
        _payments.ReserveResponse = new BudgetReserveResponse { Success = false, Error = "Insufficient budget" };

        await CreateSaga().ExecuteAsync("inv-1", invoice, state, "corr-1");

        Assert.Equal(InvoiceStatus.PaymentFailed, state.Status);
        Assert.Equal(SagaStep.Failed, state.CurrentSagaStep);
        Assert.Empty(_payments.ExecuteCalls);
        Assert.Empty(_payments.ReleaseCalls);
        var published = Assert.Single(_publisher.Published);
        Assert.Equal(InvoiceStatus.PaymentFailed, published.Status);
        Assert.Contains("Insufficient budget", published.Reason);
        Assert.False(_stateStore.StatsSaved);
    }

    [Fact]
    public async Task ExecuteFails_CompensatesByReleasingBudget()
    {
        var (invoice, state) = CreateInvoice();
        _payments.ExecuteResponse = new PaymentExecuteResponse { Success = false, Error = "Gateway timeout" };

        await CreateSaga().ExecuteAsync("inv-1", invoice, state, "corr-1");

        var release = Assert.Single(_payments.ReleaseCalls);
        Assert.Equal("res-1", release.ReservationId);
        Assert.Equal("engineering-2026Q2", release.Department);
        Assert.Equal(100m, release.Amount);
        Assert.Equal(InvoiceStatus.PaymentFailed, state.Status);
        Assert.Equal(SagaStep.Compensated, state.CurrentSagaStep);
        // Saga passed through Compensating before Compensated
        Assert.Contains(SagaStep.Compensating, _stateStore.SavedSteps);
        Assert.Contains(SagaStep.Compensated, _stateStore.SavedSteps);
        var published = Assert.Single(_publisher.Published);
        Assert.Contains("budget released", published.Reason);
    }

    [Fact]
    public async Task ExecuteThrows_EmergencyCompensationReleasesBudget()
    {
        var (invoice, state) = CreateInvoice();
        _payments.ExecuteThrows = new InvalidOperationException("sidecar down");

        await CreateSaga().ExecuteAsync("inv-1", invoice, state, "corr-1");

        var release = Assert.Single(_payments.ReleaseCalls);
        Assert.Equal("res-1", release.ReservationId);
        Assert.Equal(InvoiceStatus.PaymentFailed, state.Status);
        Assert.Equal(SagaStep.Failed, state.CurrentSagaStep);
        var published = Assert.Single(_publisher.Published);
        Assert.Contains("sidecar down", published.Reason);
    }

    [Fact]
    public async Task ReserveThrows_NoReservation_NoCompensation()
    {
        var (invoice, state) = CreateInvoice();
        _payments.ReserveThrows = new InvalidOperationException("network error");

        await CreateSaga().ExecuteAsync("inv-1", invoice, state, "corr-1");

        Assert.Empty(_payments.ReleaseCalls);
        Assert.Empty(_payments.ExecuteCalls);
        Assert.Equal(InvoiceStatus.PaymentFailed, state.Status);
        Assert.Equal(SagaStep.Failed, state.CurrentSagaStep);
    }

    [Fact]
    public async Task ConvertsToUsd_BeforeReservingBudget()
    {
        var (invoice, state) = CreateInvoice(total: 100m);
        invoice.Currency = "EUR";
        var expectedUsd = new FxRates().ConvertToUsd(100m, "EUR");

        await CreateSaga().ExecuteAsync("inv-1", invoice, state, "corr-1");

        Assert.Equal(expectedUsd, _payments.ReserveCalls.Single().Amount);
        Assert.Equal(expectedUsd, _payments.ExecuteCalls.Single().Amount);
    }
}

public class RetryingInvoiceStatusPublisherTests
{
    private class FlakyPublisher(int failures) : IInvoiceStatusPublisher
    {
        public int Attempts { get; private set; }

        public Task PublishStatusAsync(string invoiceId, InvoiceStatus status, string reason)
        {
            Attempts++;
            if (Attempts <= failures) throw new InvalidOperationException($"transient #{Attempts}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Succeeds_FirstAttempt_NoRetry()
    {
        var inner = new FlakyPublisher(failures: 0);
        var publisher = new RetryingInvoiceStatusPublisher(inner, TimeSpan.Zero);

        await publisher.PublishStatusAsync("inv-1", InvoiceStatus.Paid, "ok");

        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task FailsTwice_ThenSucceeds_ThreeAttempts()
    {
        var inner = new FlakyPublisher(failures: 2);
        var publisher = new RetryingInvoiceStatusPublisher(inner, TimeSpan.Zero);

        await publisher.PublishStatusAsync("inv-1", InvoiceStatus.Paid, "ok");

        Assert.Equal(3, inner.Attempts);
    }

    [Fact]
    public async Task AlwaysFails_ThreeAttempts_DoesNotThrow()
    {
        var inner = new FlakyPublisher(failures: int.MaxValue);
        var publisher = new RetryingInvoiceStatusPublisher(inner, TimeSpan.Zero);

        // Must not throw — a lost status update is logged, never kills the saga
        await publisher.PublishStatusAsync("inv-1", InvoiceStatus.Paid, "ok");

        Assert.Equal(3, inner.Attempts);
    }
}
