using ApprovalFlow.Shared.Models;
using ApprovalFlow.Workflow.Services;

namespace ApprovalFlow.Tests;

public class StartupRecoveryTests
{
    [Fact]
    public async Task InboxEntry_NullState_IsReprocessed()
    {
        var inbox = new FakeInboxStore();
        inbox.Statuses["inv-1"] = InboxStatus.InProgress;
        var context = CreateContext(inbox);

        var action = await StartupRecovery.RepairInboxEntryAsync(context, "inv-1", null);

        Assert.Equal(InboxRepairAction.Reprocessed, action);
        Assert.Equal(new[] { "inv-1" }, inbox.RemoveCalls);
        Assert.Empty(inbox.MarkCompletedCalls);
    }

    [Fact]
    public async Task InboxEntry_Handled_IsPromoted()
    {
        var inbox = new FakeInboxStore();
        inbox.Statuses["inv-1"] = InboxStatus.InProgress;
        var index = new InMemorySagaIndexStore();
        await index.AddInFlightAsync("inv-1");
        var context = CreateContext(inbox, index: index);
        var state = CreateState(InvoiceStatus.AutoApproved, SagaStep.NotStarted);

        var action = await StartupRecovery.RepairInboxEntryAsync(context, "inv-1", state);

        Assert.Equal(InboxRepairAction.Promoted, action);
        Assert.Equal(new[] { "inv-1" }, inbox.MarkCompletedCalls);
    }

    [Fact]
    public async Task InboxEntry_AutoApprovedNotStarted_RedrivesSaga()
    {
        var inbox = new FakeInboxStore();
        inbox.Statuses["inv-1"] = InboxStatus.InProgress;
        var payments = new FakePaymentClient();
        var context = CreateContext(inbox, payments: payments);
        var state = CreateState(InvoiceStatus.AutoApproved, SagaStep.NotStarted);

        var action = await StartupRecovery.RepairInboxEntryAsync(context, "inv-1", state);

        Assert.Equal(InboxRepairAction.RepairedSaga, action);
        Assert.Single(payments.ReserveCalls);
        Assert.Single(payments.ExecuteCalls);
        Assert.Equal(new[] { "inv-1" }, inbox.MarkCompletedCalls);
    }

    [Fact]
    public async Task InboxEntry_PendingReviewMissingQueue_ReAddsToQueue()
    {
        var inbox = new FakeInboxStore();
        inbox.Statuses["inv-1"] = InboxStatus.InProgress;
        var queue = new List<string>();
        var saveQueueCalls = 0;
        var context = CreateContext(
            inbox,
            queue,
            savedQueue =>
            {
                saveQueueCalls++;
                queue = savedQueue;
                return Task.CompletedTask;
            });
        var state = CreateState(InvoiceStatus.PendingReview, SagaStep.NotStarted);
        state.HitlStatus = HitlStatus.PendingReview;
        state.Route = RouteDecision.HumanReview;

        var action = await StartupRecovery.RepairInboxEntryAsync(context, "inv-1", state);

        Assert.Equal(InboxRepairAction.RepairedQueue, action);
        Assert.Contains("inv-1", queue);
        Assert.Equal(1, saveQueueCalls);
        Assert.Equal(new[] { "inv-1" }, inbox.MarkCompletedCalls);
    }

    private static StartupRecoveryRepairContext CreateContext(
        FakeInboxStore inbox,
        List<string>? queue = null,
        Func<List<string>, Task>? saveQueueAsync = null,
        InMemorySagaIndexStore? index = null,
        FakePaymentClient? payments = null,
        FakeWorkflowStateStore? stateStore = null,
        FakeStatusPublisher? publisher = null)
    {
        var queueState = queue ?? new List<string>();
        index ??= new InMemorySagaIndexStore();
        payments ??= new FakePaymentClient();
        stateStore ??= new FakeWorkflowStateStore();
        publisher ??= new FakeStatusPublisher();
        var saga = new SagaOrchestrator(payments, stateStore, publisher, new FxRates(), index);

        return new StartupRecoveryRepairContext(
            inbox,
            index,
            saga,
            publisher,
            () => Task.FromResult(queueState),
            saveQueueAsync ?? (_ => Task.CompletedTask));
    }

    private static WorkflowState CreateState(InvoiceStatus status, SagaStep step) => new()
    {
        InvoiceId = "inv-1",
        Invoice = new InvoiceDto
        {
            Vendor = "Acme",
            VendorKnown = true,
            InvoiceNumber = "INV-1",
            Department = "engineering-2026Q2",
            Total = 100m,
            Currency = "USD",
            ReceiptPresent = true,
            Category = "saas",
            LineItems = new List<LineItemDto>
            {
                new()
                {
                    Description = "Subscription",
                    Quantity = 1,
                    UnitPrice = 100m
                }
            }
        },
        Route = RouteDecision.AutoApprove,
        Status = status,
        CurrentSagaStep = step
    };
}
