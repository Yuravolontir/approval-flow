using ApprovalFlow.Shared.Events;
using ApprovalFlow.Shared.Models;
using ApprovalFlow.Workflow.Services;
using ApprovalFlow.Workflow.Services.Agent;

namespace ApprovalFlow.Tests;

internal class FakeLlmClient : ILlmClient
{
    public int Calls { get; private set; }
    public AgentDecision Decision { get; set; } = new()
    {
        Recommendation = "approve",
        Confidence = 1.0,
        Reasoning = "ok"
    };

    public Task<AgentDecision> AnalyzeInvoiceAsync(InvoiceDto invoice, string policyText, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Decision);
    }
}

public class InvoiceSubmittedProcessorTests
{
    [Fact]
    public async Task HandleAsync_AlreadyCompleted_ReturnsOkWithoutWork()
    {
        var inbox = new FakeInboxStore();
        inbox.Statuses["inv-1"] = InboxStatus.Completed;
        var llm = new FakeLlmClient();
        var payments = new FakePaymentClient();
        var publisher = new FakeStatusPublisher();
        var processor = CreateProcessor(inbox, llm: llm, payments: payments, publisher: publisher);

        var result = await processor.HandleAsync(CreateEvent("inv-1"));

        Assert.Equal(InboxHandlingResult.Ok, result);
        Assert.Equal(0, llm.Calls);
        Assert.Empty(payments.ReserveCalls);
        Assert.Empty(payments.ExecuteCalls);
        Assert.Empty(publisher.Published);
        Assert.Empty(inbox.MarkCompletedCalls);
    }

    [Fact]
    public async Task HandleAsync_AlreadyInProgress_Handled_PromotesAndReturnsOk()
    {
        var inbox = new FakeInboxStore();
        inbox.Statuses["inv-1"] = InboxStatus.InProgress;
        var stateStore = new FakeWorkflowStateStore();
        stateStore.Workflows["inv-1"] = new WorkflowState
        {
            InvoiceId = "inv-1",
            Invoice = CreateInvoice(),
            Status = InvoiceStatus.Paid,
            CurrentSagaStep = SagaStep.StatusUpdated
        };
        var processor = CreateProcessor(inbox, stateStore);

        var result = await processor.HandleAsync(CreateEvent("inv-1"));

        Assert.Equal(InboxHandlingResult.Ok, result);
        Assert.Equal(new[] { "inv-1" }, inbox.MarkCompletedCalls);
    }

    [Fact]
    public async Task HandleAsync_AlreadyInProgress_HandledBySagaIndex_PromotesAndReturnsOk()
    {
        var inbox = new FakeInboxStore();
        inbox.Statuses["inv-1"] = InboxStatus.InProgress;
        var stateStore = new FakeWorkflowStateStore();
        stateStore.Workflows["inv-1"] = new WorkflowState
        {
            InvoiceId = "inv-1",
            Invoice = CreateInvoice(),
            Status = InvoiceStatus.AutoApproved,
            CurrentSagaStep = SagaStep.NotStarted
        };
        var index = new InMemorySagaIndexStore();
        await index.AddInFlightAsync("inv-1");
        var processor = CreateProcessor(inbox, stateStore, index);

        var result = await processor.HandleAsync(CreateEvent("inv-1"));

        Assert.Equal(InboxHandlingResult.Ok, result);
        Assert.Equal(new[] { "inv-1" }, inbox.MarkCompletedCalls);
    }

    [Fact]
    public async Task HandleAsync_AlreadyInProgress_NotHandled_Returns503()
    {
        var inbox = new FakeInboxStore();
        inbox.Statuses["inv-1"] = InboxStatus.InProgress;
        var processor = CreateProcessor(inbox);

        var result = await processor.HandleAsync(CreateEvent("inv-1"));

        Assert.Equal(InboxHandlingResult.ServiceUnavailable, result);
        Assert.Empty(inbox.MarkCompletedCalls);
    }

    private static InvoiceSubmittedProcessor CreateProcessor(
        FakeInboxStore inbox,
        FakeWorkflowStateStore? stateStore = null,
        InMemorySagaIndexStore? index = null,
        FakeLlmClient? llm = null,
        FakePaymentClient? payments = null,
        FakeStatusPublisher? publisher = null)
    {
        stateStore ??= new FakeWorkflowStateStore();
        index ??= new InMemorySagaIndexStore();
        llm ??= new FakeLlmClient();
        payments ??= new FakePaymentClient();
        publisher ??= new FakeStatusPublisher();
        var saga = new SagaOrchestrator(payments, stateStore, publisher, new FxRates(), index);
        var router = new DeterministicRouter(new PolicyConfig(), new FxRates());
        return new InvoiceSubmittedProcessor(
            llm,
            router,
            saga,
            publisher,
            stateStore,
            index,
            inbox,
            null!,
            "policy");
    }

    private static InvoiceSubmittedEvent CreateEvent(string invoiceId) => new()
    {
        InvoiceId = invoiceId,
        Invoice = CreateInvoice(),
        CorrelationId = "corr-1"
    };

    private static InvoiceDto CreateInvoice() => new()
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
    };
}
