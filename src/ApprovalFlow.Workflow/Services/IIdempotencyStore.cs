using ApprovalFlow.Shared.Constants;
using Dapr.Client;

namespace ApprovalFlow.Workflow.Services;

public interface IIdempotencyStore
{
    Task<bool> TryClaimAsync(string key);
}

public class DaprIdempotencyStore(DaprClient dapr) : IIdempotencyStore
{
    public async Task<bool> TryClaimAsync(string key)
    {
        var stateKey = $"processed:{key}";
        var (existing, etag) = await dapr.GetStateAndETagAsync<string>(
            DaprComponents.StateStore,
            stateKey);

        if (!string.IsNullOrEmpty(existing))
        {
            return false;
        }

        return await dapr.TrySaveStateAsync(DaprComponents.StateStore, stateKey, "1", etag);
    }
}
