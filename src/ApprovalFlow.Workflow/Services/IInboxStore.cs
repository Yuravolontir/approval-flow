using ApprovalFlow.Shared.Constants;
using Dapr.Client;

namespace ApprovalFlow.Workflow.Services;

public enum InboxStatus
{
    InProgress,
    Completed
}

public enum BeginResult
{
    Claimed,
    AlreadyInProgress,
    AlreadyCompleted
}

public interface IInboxStore
{
    Task<BeginResult> TryBeginAsync(string invoiceId);
    Task MarkCompletedAsync(string invoiceId);
    Task<InboxStatus?> GetStatusAsync(string invoiceId);
    Task RemoveAsync(string invoiceId);
    Task<IReadOnlyList<string>> GetIndexAsync();
}

public class DaprInboxStore(DaprClient dapr) : IInboxStore
{
    private const int MaxRetries = 8;
    private const string IndexKey = "inbox:index";

    public async Task<BeginResult> TryBeginAsync(string invoiceId)
    {
        await AddIndexAsync(invoiceId);

        var stateKey = GetValueKey(invoiceId);
        var (existing, etag) = await dapr.GetStateAndETagAsync<string>(
            DaprComponents.StateStore,
            stateKey);

        var status = ParseStatus(existing);
        if (status == InboxStatus.Completed)
        {
            await RemoveIndexAsync(invoiceId);
            return BeginResult.AlreadyCompleted;
        }

        if (status == InboxStatus.InProgress)
        {
            return BeginResult.AlreadyInProgress;
        }

        if (await dapr.TrySaveStateAsync(DaprComponents.StateStore, stateKey, InboxStatus.InProgress.ToString(), etag))
        {
            return BeginResult.Claimed;
        }

        var current = await GetStatusAsync(invoiceId);
        if (current == InboxStatus.Completed)
        {
            await RemoveIndexAsync(invoiceId);
            return BeginResult.AlreadyCompleted;
        }

        return BeginResult.AlreadyInProgress;
    }

    public async Task MarkCompletedAsync(string invoiceId)
    {
        var stateKey = GetValueKey(invoiceId);
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var (_, etag) = await dapr.GetStateAndETagAsync<string>(
                DaprComponents.StateStore,
                stateKey);

            if (await dapr.TrySaveStateAsync(DaprComponents.StateStore, stateKey, InboxStatus.Completed.ToString(), etag))
            {
                await RemoveIndexAsync(invoiceId);
                return;
            }
        }

        throw new InvalidOperationException("Could not mark inbox entry completed under concurrent contention.");
    }

    public async Task<InboxStatus?> GetStatusAsync(string invoiceId)
    {
        var existing = await dapr.GetStateAsync<string>(DaprComponents.StateStore, GetValueKey(invoiceId));
        return ParseStatus(existing);
    }

    public async Task RemoveAsync(string invoiceId)
    {
        await dapr.DeleteStateAsync(DaprComponents.StateStore, GetValueKey(invoiceId));
        await RemoveIndexAsync(invoiceId);
    }

    public async Task<IReadOnlyList<string>> GetIndexAsync()
    {
        var list = await dapr.GetStateAsync<List<string>>(DaprComponents.StateStore, IndexKey);
        return list ?? new List<string>();
    }

    private async Task AddIndexAsync(string invoiceId)
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

        throw new InvalidOperationException("Could not add inbox entry to index under concurrent contention.");
    }

    private async Task RemoveIndexAsync(string invoiceId)
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

        throw new InvalidOperationException("Could not remove inbox entry from index under concurrent contention.");
    }

    private static string GetValueKey(string invoiceId) => $"inbox:invoice-submitted:{invoiceId}";

    private static InboxStatus? ParseStatus(string? value) =>
        Enum.TryParse<InboxStatus>(value, out var status) ? status : null;
}
