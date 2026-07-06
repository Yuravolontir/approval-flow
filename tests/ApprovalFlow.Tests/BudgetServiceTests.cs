using ApprovalFlow.Shared.Models;

namespace ApprovalFlow.Tests;

internal class InMemoryBudgetStore : IBudgetStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (BudgetState State, int Version)> _budgets = new();
    private readonly Dictionary<string, string> _idempotency = new();
    private ReadBarrier? _readBarrier;

    public void SeedBudget(string department, decimal amount)
    {
        lock (_lock)
        {
            _budgets[department] = (new BudgetState
            {
                Department = department,
                TotalBudget = amount,
                Available = amount,
                Reservations = new Dictionary<string, decimal>()
            }, 0);
        }
    }

    public void SynchronizeNextBudgetReads(int participants)
    {
        lock (_lock)
        {
            _readBarrier = new ReadBarrier(participants);
        }
    }

    public async Task<(BudgetState? State, string? ETag)> GetAsync(string department)
    {
        ReadBarrier? barrier;
        BudgetState? state;
        string? etag;

        lock (_lock)
        {
            barrier = _readBarrier;
            if (_budgets.TryGetValue(department, out var entry))
            {
                state = Clone(entry.State);
                etag = entry.Version.ToString();
            }
            else
            {
                state = null;
                etag = null;
            }

            barrier?.Arrive();
        }

        if (barrier != null)
        {
            await barrier.WaitAsync();
        }

        return (state, etag);
    }

    public Task<bool> TrySaveAsync(string department, BudgetState state, string? etag)
    {
        lock (_lock)
        {
            if (!_budgets.TryGetValue(department, out var entry))
            {
                return Task.FromResult(false);
            }

            if (etag != entry.Version.ToString())
            {
                return Task.FromResult(false);
            }

            _budgets[department] = (Clone(state), entry.Version + 1);
            return Task.FromResult(true);
        }
    }

    public Task<string?> GetReservationIdAsync(string idempotencyKey)
    {
        lock (_lock)
        {
            _idempotency.TryGetValue(idempotencyKey, out var reservationId);
            return Task.FromResult<string?>(reservationId);
        }
    }

    public Task SaveReservationIdAsync(string idempotencyKey, string reservationId)
    {
        lock (_lock)
        {
            _idempotency[idempotencyKey] = reservationId;
            return Task.CompletedTask;
        }
    }

    private static BudgetState Clone(BudgetState state) =>
        new()
        {
            Department = state.Department,
            TotalBudget = state.TotalBudget,
            Available = state.Available,
            Reservations = new Dictionary<string, decimal>(state.Reservations)
        };

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

public class BudgetServiceTests
{
    private readonly InMemoryBudgetStore _store = new();

    private BudgetService CreateService() => new(_store);

    [Fact]
    public async Task Reserve_TwoConcurrentReservesForLastDollars_OnlyOneSucceeds()
    {
        _store.SeedBudget("marketing-2026Q2", 1000m);
        _store.SynchronizeNextBudgetReads(participants: 2);
        var service = CreateService();
        var first = new BudgetReserveRequest
        {
            Department = "marketing-2026Q2",
            Amount = 600m,
            InvoiceId = "inv-1",
            IdempotencyKey = "reserve:inv-1"
        };
        var second = new BudgetReserveRequest
        {
            Department = "marketing-2026Q2",
            Amount = 600m,
            InvoiceId = "inv-2",
            IdempotencyKey = "reserve:inv-2"
        };

        var results = await Task.WhenAll(service.ReserveAsync(first), service.ReserveAsync(second));

        Assert.Equal(1, results.Count(result => result.Success));
        Assert.Equal(1, results.Count(result => !result.Success));
        Assert.Contains(results, result => result.Error?.StartsWith("Insufficient budget") == true);
        var (budget, _) = await _store.GetAsync("marketing-2026Q2");
        Assert.NotNull(budget);
        Assert.Equal(400m, budget.Available);
        Assert.True(budget.Available >= 0m);
    }

    [Fact]
    public async Task Reserve_SameIdempotencyKeyTwice_ReservesOnce()
    {
        _store.SeedBudget("marketing-2026Q2", 1000m);
        var service = CreateService();
        var request = new BudgetReserveRequest
        {
            Department = "marketing-2026Q2",
            Amount = 600m,
            InvoiceId = "inv-1",
            IdempotencyKey = "reserve:inv-1"
        };

        var first = await service.ReserveAsync(request);
        var second = await service.ReserveAsync(request);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.ReservationId, second.ReservationId);
        var (budget, _) = await _store.GetAsync("marketing-2026Q2");
        Assert.NotNull(budget);
        Assert.Equal(400m, budget.Available);
        Assert.Single(budget.Reservations);
    }

    [Fact]
    public async Task Reserve_InsufficientBudget_Fails()
    {
        _store.SeedBudget("marketing-2026Q2", 400m);
        var service = CreateService();
        var request = new BudgetReserveRequest
        {
            Department = "marketing-2026Q2",
            Amount = 600m,
            InvoiceId = "inv-1",
            IdempotencyKey = "reserve:inv-1"
        };

        var result = await service.ReserveAsync(request);

        Assert.False(result.Success);
        Assert.StartsWith("Insufficient budget", result.Error);
        var (budget, _) = await _store.GetAsync("marketing-2026Q2");
        Assert.NotNull(budget);
        Assert.Equal(400m, budget.Available);
    }

    [Fact]
    public async Task Release_ReturnsAmountToAvailable()
    {
        _store.SeedBudget("marketing-2026Q2", 1000m);
        var service = CreateService();
        var reserve = await service.ReserveAsync(new BudgetReserveRequest
        {
            Department = "marketing-2026Q2",
            Amount = 600m,
            InvoiceId = "inv-1",
            IdempotencyKey = "reserve:inv-1"
        });

        var release = await service.ReleaseAsync(new BudgetReleaseRequest
        {
            Department = "marketing-2026Q2",
            ReservationId = reserve.ReservationId,
            Amount = 600m
        });

        Assert.True(release.Success);
        Assert.Equal(600m, release.ReleasedAmount);
        Assert.Equal(1000m, release.Available);
        var (budget, _) = await _store.GetAsync("marketing-2026Q2");
        Assert.NotNull(budget);
        Assert.Equal(1000m, budget.Available);
        Assert.Empty(budget.Reservations);
    }

    [Fact]
    public async Task Release_CalledTwice_IsIdempotent()
    {
        _store.SeedBudget("marketing-2026Q2", 1000m);
        var service = CreateService();
        var reserve = await service.ReserveAsync(new BudgetReserveRequest
        {
            Department = "marketing-2026Q2",
            Amount = 600m,
            InvoiceId = "inv-1",
            IdempotencyKey = "reserve:inv-1"
        });
        var request = new BudgetReleaseRequest
        {
            Department = "marketing-2026Q2",
            ReservationId = reserve.ReservationId,
            Amount = 600m
        };

        var first = await service.ReleaseAsync(request);
        var second = await service.ReleaseAsync(request);

        Assert.True(first.Success);
        Assert.Equal(600m, first.ReleasedAmount);
        Assert.True(second.Success);
        Assert.Equal(0m, second.ReleasedAmount);
        Assert.Equal(1000m, second.Available);
        var (budget, _) = await _store.GetAsync("marketing-2026Q2");
        Assert.NotNull(budget);
        Assert.Equal(1000m, budget.Available);
    }
}
