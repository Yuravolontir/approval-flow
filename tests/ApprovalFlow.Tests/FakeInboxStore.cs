using ApprovalFlow.Workflow.Services;

namespace ApprovalFlow.Tests;

internal class FakeInboxStore : IInboxStore
{
    public Dictionary<string, InboxStatus> Statuses { get; } = new();
    public List<string> Index { get; } = new();
    public List<string> Calls { get; } = new();
    public List<string> MarkCompletedCalls { get; } = new();
    public List<string> RemoveCalls { get; } = new();

    public Task<BeginResult> TryBeginAsync(string invoiceId)
    {
        Calls.Add($"begin:{invoiceId}");
        if (!Index.Contains(invoiceId))
        {
            Index.Add(invoiceId);
        }

        if (Statuses.TryGetValue(invoiceId, out var status))
        {
            if (status == InboxStatus.Completed)
            {
                Index.Remove(invoiceId);
            }

            return Task.FromResult(status == InboxStatus.Completed
                ? BeginResult.AlreadyCompleted
                : BeginResult.AlreadyInProgress);
        }

        Statuses[invoiceId] = InboxStatus.InProgress;
        return Task.FromResult(BeginResult.Claimed);
    }

    public Task MarkCompletedAsync(string invoiceId)
    {
        Calls.Add($"completed:{invoiceId}");
        MarkCompletedCalls.Add(invoiceId);
        Statuses[invoiceId] = InboxStatus.Completed;
        Index.Remove(invoiceId);
        return Task.CompletedTask;
    }

    public Task<InboxStatus?> GetStatusAsync(string invoiceId)
    {
        Calls.Add($"status:{invoiceId}");
        Statuses.TryGetValue(invoiceId, out var status);
        return Task.FromResult<InboxStatus?>(status);
    }

    public Task RemoveAsync(string invoiceId)
    {
        Calls.Add($"remove:{invoiceId}");
        RemoveCalls.Add(invoiceId);
        Statuses.Remove(invoiceId);
        Index.Remove(invoiceId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetIndexAsync()
    {
        Calls.Add("index");
        return Task.FromResult<IReadOnlyList<string>>(Index.ToList());
    }
}
