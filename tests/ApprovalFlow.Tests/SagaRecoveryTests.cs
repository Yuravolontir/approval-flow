using ApprovalFlow.Shared.Models;
using ApprovalFlow.Workflow.Services;

namespace ApprovalFlow.Tests;

internal class InMemorySagaIndexStore : ISagaIndexStore
{
    private const int MaxRetries = 8;
    private readonly Lock _lock = new();
    private (List<string> List, int Version) _state = (new List<string>(), 0);
    private ReadBarrier? _readBarrier;

    public void SynchronizeNextReads(int participants)
    {
        lock (_lock)
        {
            _readBarrier = new ReadBarrier(participants);
        }
    }

    public async Task AddInFlightAsync(string invoiceId)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var (list, etag) = await ReadAsync();

            if (list.Contains(invoiceId))
            {
                return;
            }

            list.Add(invoiceId);

            if (TrySave(list, etag))
            {
                return;
            }
        }

        throw new InvalidOperationException("CAS retry limit reached.");
    }

    public async Task RemoveInFlightAsync(string invoiceId)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var (list, etag) = await ReadAsync();

            if (!list.Contains(invoiceId))
            {
                return;
            }

            list.Remove(invoiceId);

            if (TrySave(list, etag))
            {
                return;
            }
        }

        throw new InvalidOperationException("CAS retry limit reached.");
    }

    public Task<IReadOnlyList<string>> GetInFlightAsync()
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<string>>(_state.List.ToList());
        }
    }

    private async Task<(List<string> List, string ETag)> ReadAsync()
    {
        ReadBarrier? barrier;
        List<string> list;
        string etag;

        lock (_lock)
        {
            barrier = _readBarrier;
            list = _state.List.ToList();
            etag = _state.Version.ToString();
            barrier?.Arrive();
        }

        if (barrier != null)
        {
            await barrier.WaitAsync();
        }

        return (list, etag);
    }

    private bool TrySave(List<string> list, string etag)
    {
        lock (_lock)
        {
            if (etag != _state.Version.ToString())
            {
                return false;
            }

            _state = (list.ToList(), _state.Version + 1);
            return true;
        }
    }

    private sealed class ReadBarrier(int participants)
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public void Arrive()
        {
            _arrived++;
            if (_arrived == participants)
            {
                _ready.SetResult();
            }
        }

        public Task WaitAsync() => _ready.Task;
    }
}

public class SagaRecoveryTests
{
    private readonly FakePaymentClient _payments = new();
    private readonly FakeWorkflowStateStore _stateStore = new();
    private readonly FakeStatusPublisher _publisher = new();
    private readonly InMemorySagaIndexStore _index = new();

    private SagaOrchestrator CreateSaga() =>
        new(_payments, _stateStore, _publisher, new FxRates(), _index);

    private static WorkflowState CreateState(SagaStep step, InvoiceStatus status = InvoiceStatus.AutoApproved)
    {
        var invoice = new InvoiceDto
        {
            Vendor = "Acme",
            InvoiceNumber = "INV-1",
            Department = "engineering-2026Q2",
            Total = 100m,
            Currency = "USD"
        };

        return new WorkflowState
        {
            InvoiceId = "inv-1",
            Invoice = invoice,
            Route = RouteDecision.AutoApprove,
            Status = status,
            CurrentSagaStep = step,
            ReservationId = "res-1"
        };
    }

    [Fact]
    public async Task Recover_BudgetReservedSaga_ResumesToPaid()
    {
        var state = CreateState(SagaStep.BudgetReserved);
        await _index.AddInFlightAsync("inv-1");

        await CreateSaga().RecoverAsync(state, "corr-1");

        Assert.Equal(InvoiceStatus.Paid, state.Status);
        Assert.Equal(SagaStep.StatusUpdated, state.CurrentSagaStep);
        Assert.Single(_payments.ReserveCalls);
        Assert.Single(_payments.ExecuteCalls);
        Assert.Empty(_payments.ReleaseCalls);
        Assert.DoesNotContain("inv-1", await _index.GetInFlightAsync());
    }

    [Fact]
    public async Task Recover_ReservingSaga_RetriesReserveAndPays()
    {
        var state = CreateState(SagaStep.Reserving);
        state.ReservationId = null;
        await _index.AddInFlightAsync("inv-1");

        await CreateSaga().RecoverAsync(state, "corr-1");

        Assert.Equal(InvoiceStatus.Paid, state.Status);
        Assert.Equal(SagaStep.StatusUpdated, state.CurrentSagaStep);
        Assert.Equal("res-1", state.ReservationId);
        Assert.Single(_payments.ReserveCalls);
        Assert.Single(_payments.ExecuteCalls);
        Assert.Empty(_payments.ReleaseCalls);
        Assert.DoesNotContain("inv-1", await _index.GetInFlightAsync());
    }

    [Fact]
    public async Task Recover_CompensatingSaga_ReleasesAndMarksPaymentFailed()
    {
        var state = CreateState(SagaStep.Compensating);
        await _index.AddInFlightAsync("inv-1");

        await CreateSaga().RecoverAsync(state, "corr-1");

        Assert.Empty(_payments.ReserveCalls);
        Assert.Empty(_payments.ExecuteCalls);
        var release = Assert.Single(_payments.ReleaseCalls);
        Assert.Equal("res-1", release.ReservationId);
        Assert.Equal("engineering-2026Q2", release.Department);
        Assert.Equal(100m, release.Amount);
        Assert.Equal(InvoiceStatus.PaymentFailed, state.Status);
        Assert.Equal(SagaStep.Compensated, state.CurrentSagaStep);
        Assert.DoesNotContain("inv-1", await _index.GetInFlightAsync());
    }

    [Fact]
    public async Task Recover_TerminalSaga_IsNoOp()
    {
        var state = CreateState(SagaStep.StatusUpdated, InvoiceStatus.Paid);
        await _index.AddInFlightAsync("inv-1");

        await CreateSaga().RecoverAsync(state, "corr-1");

        Assert.Empty(_payments.ReserveCalls);
        Assert.Empty(_payments.ExecuteCalls);
        Assert.Empty(_payments.ReleaseCalls);
        Assert.Equal(InvoiceStatus.Paid, state.Status);
        Assert.Equal(SagaStep.StatusUpdated, state.CurrentSagaStep);
        Assert.DoesNotContain("inv-1", await _index.GetInFlightAsync());
    }

    [Fact]
    public async Task Index_AddIsIdempotent_And_RemoveMissingIsNoOp()
    {
        await _index.AddInFlightAsync("inv-1");
        await _index.AddInFlightAsync("inv-1");
        await _index.RemoveInFlightAsync("missing");

        var ids = await _index.GetInFlightAsync();
        Assert.Equal(new[] { "inv-1" }, ids);

        await _index.RemoveInFlightAsync("inv-1");
        Assert.Empty(await _index.GetInFlightAsync());
    }

    [Fact]
    public async Task Index_TwoConcurrentAdds_NeitherIsLost()
    {
        _index.SynchronizeNextReads(participants: 2);

        await Task.WhenAll(
            _index.AddInFlightAsync("inv-1"),
            _index.AddInFlightAsync("inv-2"));

        var ids = await _index.GetInFlightAsync();
        Assert.Contains("inv-1", ids);
        Assert.Contains("inv-2", ids);
        Assert.Equal(2, ids.Count);
    }
}
