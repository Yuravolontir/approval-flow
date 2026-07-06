using ApprovalFlow.Shared.Constants;
using Dapr.Client;

namespace ApprovalFlow.Workflow.Services;

public interface ISagaIndexStore
{
    Task AddInFlightAsync(string invoiceId);
    Task RemoveInFlightAsync(string invoiceId);
    Task<IReadOnlyList<string>> GetInFlightAsync();
}

public class DaprSagaIndexStore(DaprClient dapr) : ISagaIndexStore
{
    private const int MaxRetries = 8;
    private const string IndexKey = "saga:inflight";

    public async Task AddInFlightAsync(string invoiceId)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var (list, etag) = await dapr.GetStateAndETagAsync<List<string>>(
                DaprComponents.StateStore,
                IndexKey);
            list ??= new List<string>();

            if (list.Contains(invoiceId))
            {
                return;
            }

            list.Add(invoiceId);

            if (await dapr.TrySaveStateAsync(DaprComponents.StateStore, IndexKey, list, etag))
            {
                return;
            }
        }

        throw new InvalidOperationException("Could not add saga to in-flight index under concurrent contention.");
    }

    public async Task RemoveInFlightAsync(string invoiceId)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var (list, etag) = await dapr.GetStateAndETagAsync<List<string>>(
                DaprComponents.StateStore,
                IndexKey);
            list ??= new List<string>();

            if (!list.Contains(invoiceId))
            {
                return;
            }

            list.Remove(invoiceId);

            if (await dapr.TrySaveStateAsync(DaprComponents.StateStore, IndexKey, list, etag))
            {
                return;
            }
        }

        throw new InvalidOperationException("Could not remove saga from in-flight index under concurrent contention.");
    }

    public async Task<IReadOnlyList<string>> GetInFlightAsync()
    {
        var list = await dapr.GetStateAsync<List<string>>(DaprComponents.StateStore, IndexKey);
        return list ?? new List<string>();
    }
}
