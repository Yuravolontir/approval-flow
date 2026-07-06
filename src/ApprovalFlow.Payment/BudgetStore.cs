using ApprovalFlow.Shared.Constants;
using Dapr.Client;

public interface IBudgetStore
{
    Task<(BudgetState? State, string? ETag)> GetAsync(string department);
    Task<bool> TrySaveAsync(string department, BudgetState state, string? etag);
    Task<string?> GetReservationIdAsync(string idempotencyKey);
    Task SaveReservationIdAsync(string idempotencyKey, string reservationId);
}

public class DaprBudgetStore(DaprClient dapr) : IBudgetStore
{
    public async Task<(BudgetState? State, string? ETag)> GetAsync(string department)
    {
        var (state, etag) = await dapr.GetStateAndETagAsync<BudgetState>(
            DaprComponents.StateStore,
            $"budget:{department}");

        return (state, etag);
    }

    public Task<bool> TrySaveAsync(string department, BudgetState state, string? etag) =>
        dapr.TrySaveStateAsync(DaprComponents.StateStore, $"budget:{department}", state, etag);

    public async Task<string?> GetReservationIdAsync(string idempotencyKey) =>
        await dapr.GetStateAsync<string>(DaprComponents.StateStore, $"idempotency:{idempotencyKey}");

    public Task SaveReservationIdAsync(string idempotencyKey, string reservationId) =>
        dapr.SaveStateAsync(DaprComponents.StateStore, $"idempotency:{idempotencyKey}", reservationId);
}
