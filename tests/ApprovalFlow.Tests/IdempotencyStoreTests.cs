using ApprovalFlow.Workflow.Services;

namespace ApprovalFlow.Tests;

internal class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string Value, int Version)> _claims = new();
    private ReadBarrier? _readBarrier;

    public void SynchronizeNextReads(int participants)
    {
        lock (_lock)
        {
            _readBarrier = new ReadBarrier(participants);
        }
    }

    public async Task<bool> TryClaimAsync(string key)
    {
        ReadBarrier? barrier;
        string? existing;
        string? etag;

        lock (_lock)
        {
            barrier = _readBarrier;
            if (_claims.TryGetValue(key, out var entry))
            {
                existing = entry.Value;
                etag = entry.Version.ToString();
            }
            else
            {
                existing = null;
                etag = null;
            }

            barrier?.Arrive();
        }

        if (barrier != null)
        {
            await barrier.WaitAsync();
        }

        if (!string.IsNullOrEmpty(existing))
        {
            return false;
        }

        lock (_lock)
        {
            if (_claims.TryGetValue(key, out var entry))
            {
                if (etag != entry.Version.ToString())
                {
                    return false;
                }

                _claims[key] = ("1", entry.Version + 1);
                return true;
            }

            if (etag != null)
            {
                return false;
            }

            _claims[key] = ("1", 1);
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

public class IdempotencyStoreTests
{
    private readonly InMemoryIdempotencyStore _store = new();

    [Fact]
    public async Task TryClaim_FirstCallForKey_ReturnsTrue()
    {
        var result = await _store.TryClaimAsync("invoice-submitted:inv-1");

        Assert.True(result);
    }

    [Fact]
    public async Task TryClaim_SecondCallSameKey_ReturnsFalse()
    {
        var first = await _store.TryClaimAsync("invoice-submitted:inv-1");
        var second = await _store.TryClaimAsync("invoice-submitted:inv-1");

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task TryClaim_TwoConcurrentClaimsSameKey_ExactlyOneWins()
    {
        _store.SynchronizeNextReads(participants: 2);

        var results = await Task.WhenAll(
            _store.TryClaimAsync("invoice-submitted:inv-1"),
            _store.TryClaimAsync("invoice-submitted:inv-1"));

        Assert.Equal(1, results.Count(result => result));
        Assert.Equal(1, results.Count(result => !result));
    }

    [Fact]
    public async Task TryClaim_DifferentKeys_BothReturnTrue()
    {
        var first = await _store.TryClaimAsync("invoice-submitted:inv-1");
        var second = await _store.TryClaimAsync("invoice-submitted:inv-2");

        Assert.True(first);
        Assert.True(second);
    }
}
