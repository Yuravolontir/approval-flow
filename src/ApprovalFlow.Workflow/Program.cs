using System.Net.Http.Headers;
using System.Text.Json;
using ApprovalFlow.Shared.Constants;
using ApprovalFlow.Shared.Events;
using ApprovalFlow.Shared.Models;
using ApprovalFlow.Workflow.Services;
using ApprovalFlow.Workflow.Services.Agent;
using Dapr;
using Dapr.Client;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}")
    .Enrich.WithProperty("Service", "workflow-service")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
builder.Services.AddDaprClient();
builder.Services.AddOpenApi();

// Policy config from environment
var policyConfig = new PolicyConfig
{
    AutonomyCeiling = decimal.Parse(builder.Configuration["AUTONOMY_CEILING"] ?? "250"),
    AutonomyConfidence = double.Parse(builder.Configuration["AUTONOMY_CONFIDENCE"] ?? "0.80")
};
builder.Services.AddSingleton(policyConfig);
builder.Services.AddSingleton(new FxRates());
builder.Services.AddSingleton<DeterministicRouter>();

// LLM Client — use Stub by default, OpenRouter when configured
var llmProvider = builder.Configuration["LLM_PROVIDER"] ?? "stub";
if (llmProvider == "openrouter")
{
    var apiKey = builder.Configuration["LLM_API_KEY"] ?? "";
    var baseUrl = builder.Configuration["LLM_BASE_URL"] ?? "https://openrouter.ai/api/v1";
    var model = builder.Configuration["LLM_MODEL"] ?? "anthropic/claude-sonnet-4-20250514";

    builder.Services.AddHttpClient<ILlmClient, OpenRouterLlmClient>((sp, http) =>
    {
        http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        http.DefaultRequestHeaders.Add("HTTP-Referer", "https://approvalflow.local");
        http.DefaultRequestHeaders.Add("X-Title", "ApprovalFlow");
    }).Services.AddSingleton<ILlmClient>(sp =>
    {
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(OpenRouterLlmClient));
        var logger = sp.GetRequiredService<ILogger<OpenRouterLlmClient>>();
        return new OpenRouterLlmClient(http, model, logger);
    });
}
else
{
    builder.Services.AddSingleton<ILlmClient, StubLlmClient>();
}

// Load policy text for agent
var policyText = File.Exists("policy.md") ? File.ReadAllText("policy.md") : "No policy loaded.";

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCloudEvents();
app.MapSubscribeHandler();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "workflow-service" }));

// Subscribe to invoice.submitted via Dapr pub/sub
app.MapPost("/invoice-submitted",
    [Topic(DaprComponents.PubSub, DaprComponents.InvoiceSubmittedTopic)]
    async (InvoiceSubmittedEvent evt, DaprClient dapr, ILlmClient llm, DeterministicRouter router) =>
{
    var correlationId = evt.CorrelationId;
    var log = Log.ForContext("CorrelationId", correlationId);

    log.Information("Processing invoice {InvoiceId} from {Vendor}", evt.InvoiceId, evt.Invoice.Vendor);

    // 1. Call AI Agent
    AgentDecision agentDecision;
    try
    {
        agentDecision = await llm.AnalyzeInvoiceAsync(evt.Invoice, policyText);
        log.Information("Agent decision for {InvoiceId}: {Recommendation} (confidence={Confidence:P0})",
            evt.InvoiceId, agentDecision.Recommendation, agentDecision.Confidence);
    }
    catch (Exception ex)
    {
        log.Error(ex, "Agent failed for {InvoiceId}, escalating to human", evt.InvoiceId);
        agentDecision = new AgentDecision
        {
            Recommendation = "escalate",
            Confidence = 0.0,
            Violations = new List<string>(),
            Reasoning = "Agent analysis failed. Escalating to human as safety fallback."
        };
    }

    // 2. Call Deterministic Router
    var routerResult = router.Route(evt.Invoice, agentDecision);
    log.Information("Router decision for {InvoiceId}: {Route} [{Violations}]",
        evt.InvoiceId, routerResult.Route, string.Join(", ", routerResult.Violations));

    // 3. Create workflow state
    var state = new WorkflowState
    {
        InvoiceId = evt.InvoiceId,
        Invoice = evt.Invoice,
        AgentDecision = agentDecision,
        Route = routerResult.Route,
        RouteReason = routerResult.Reason,
        RouterViolations = routerResult.Violations,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    // 4. Handle based on route
    switch (routerResult.Route)
    {
        case RouteDecision.AutoApprove:
            state.Status = InvoiceStatus.AutoApproved;
            await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{evt.InvoiceId}", state);

            // Start payment saga
            await ExecutePaymentSaga(evt.InvoiceId, evt.Invoice, state, dapr, correlationId, log);
            break;

        case RouteDecision.HumanReview:
            state.Status = InvoiceStatus.PendingReview;
            state.HitlStatus = HitlStatus.PendingReview;
            await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{evt.InvoiceId}", state);

            // Add to HITL queue
            var queue = await dapr.GetStateAsync<List<string>>(DaprComponents.StateStore, "hitl:queue") ?? new List<string>();
            if (!queue.Contains(evt.InvoiceId))
            {
                queue.Add(evt.InvoiceId);
                await dapr.SaveStateAsync(DaprComponents.StateStore, "hitl:queue", queue);
            }

            // Update invoice status
            await UpdateInvoiceStatus(dapr, evt.InvoiceId, InvoiceStatus.PendingReview, routerResult.Reason);
            log.Information("Invoice {InvoiceId} queued for human review", evt.InvoiceId);
            break;

        case RouteDecision.Reject:
            state.Status = InvoiceStatus.Rejected;
            await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{evt.InvoiceId}", state);
            await UpdateInvoiceStatus(dapr, evt.InvoiceId, InvoiceStatus.Rejected, routerResult.Reason);
            log.Information("Invoice {InvoiceId} rejected: {Reason}", evt.InvoiceId, routerResult.Reason);
            break;

        case RouteDecision.Duplicate:
            state.Status = InvoiceStatus.Duplicate;
            await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{evt.InvoiceId}", state);
            await UpdateInvoiceStatus(dapr, evt.InvoiceId, InvoiceStatus.Duplicate, routerResult.Reason);
            break;
    }

    return Results.Ok();
});

// POST /workflow/{id}/decision — HITL approve/reject/request_info
app.MapPost("/workflow/{id}/decision",
    async (string id, HitlDecisionRequest request, DaprClient dapr) =>
{
    var correlationId = Guid.NewGuid().ToString();
    var log = Log.ForContext("CorrelationId", correlationId);

    var state = await dapr.GetStateAsync<WorkflowState>(DaprComponents.StateStore, $"workflow:{id}");
    if (state == null)
        return Results.NotFound(new { error = "Workflow not found", invoiceId = id });

    if (state.HitlStatus != HitlStatus.PendingReview)
        return Results.BadRequest(new { error = "Invoice is not pending review", currentStatus = state.HitlStatus.ToString() });

    state.HitlDecision = request.Action;
    state.UpdatedAt = DateTime.UtcNow;

    switch (request.Action.ToLowerInvariant())
    {
        case "approve":
            state.HitlStatus = HitlStatus.Approved;
            state.Status = InvoiceStatus.Approved;
            await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{id}", state);

            // Start payment saga
            await ExecutePaymentSaga(id, state.Invoice, state, dapr, correlationId, log);
            break;

        case "reject":
            state.HitlStatus = HitlStatus.Rejected;
            state.Status = InvoiceStatus.Rejected;
            await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{id}", state);
            await UpdateInvoiceStatus(dapr, id, InvoiceStatus.Rejected, $"Rejected by human reviewer. {request.Comment ?? ""}");
            log.Information("Invoice {InvoiceId} rejected by human", id);
            break;

        case "request_info":
            state.HitlStatus = HitlStatus.InfoRequested;
            state.Status = InvoiceStatus.PendingReview;
            await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{id}", state);
            await UpdateInvoiceStatus(dapr, id, InvoiceStatus.PendingReview, $"Additional information requested: {request.Comment ?? ""}");
            log.Information("Info requested for {InvoiceId}", id);
            break;

        default:
            return Results.BadRequest(new { error = "Invalid action. Use: approve, reject, request_info" });
    }

    return Results.Ok(new { invoiceId = id, action = request.Action, status = state.Status.ToString() });
});

// GET /workflow/queue — list pending HITL items
app.MapGet("/workflow/queue", async (DaprClient dapr) =>
{
    // Query all workflow states and filter pending ones
    // Since Dapr state store doesn't support listing, we maintain a queue list
    var queueKeys = await dapr.GetStateAsync<List<string>>(DaprComponents.StateStore, "hitl:queue") ?? new List<string>();

    var items = new List<HitlQueueItem>();
    foreach (var invoiceId in queueKeys)
    {
        var state = await dapr.GetStateAsync<WorkflowState>(DaprComponents.StateStore, $"workflow:{invoiceId}");
        if (state?.HitlStatus == HitlStatus.PendingReview)
        {
            items.Add(new HitlQueueItem
            {
                InvoiceId = state.InvoiceId,
                Vendor = state.Invoice.Vendor,
                Total = state.Invoice.Total,
                Currency = state.Invoice.Currency,
                Category = state.Invoice.Category,
                AgentDecision = state.AgentDecision,
                RouterViolations = state.RouterViolations,
                RouteReason = state.RouteReason,
                CreatedAt = state.CreatedAt
            });
        }
    }

    return Results.Ok(items);
});

// GET /dashboard — stats
app.MapGet("/dashboard", async (DaprClient dapr) =>
{
    var stats = await dapr.GetStateAsync<DashboardResponse>(DaprComponents.StateStore, "dashboard:stats")
        ?? new DashboardResponse();
    return Results.Ok(stats);
});

app.Run();

// === Helper functions ===

static async Task ExecutePaymentSaga(string invoiceId, InvoiceDto invoice, WorkflowState state,
    DaprClient dapr, string correlationId, Serilog.ILogger log)
{
    var fxRates = new FxRates();
    var usdAmount = fxRates.ConvertToUsd(invoice.Total, invoice.Currency);

    try
    {
        // Step 1: Reserve budget
        log.Information("Saga step 1: Reserving budget for {InvoiceId} ({Amount:C})", invoiceId, usdAmount);
        var reserveRequest = new BudgetReserveRequest
        {
            Department = invoice.Department,
            Amount = usdAmount,
            InvoiceId = invoiceId,
            IdempotencyKey = $"reserve:{invoiceId}"
        };

        var reserveResponse = await dapr.InvokeMethodAsync<BudgetReserveRequest, BudgetReserveResponse>(
            "payment-service", "budget/reserve", reserveRequest);

        if (!reserveResponse.Success)
        {
            log.Warning("Budget reservation failed for {InvoiceId}: {Error}", invoiceId, reserveResponse.Error);
            state.Status = InvoiceStatus.PaymentFailed;
            state.CurrentSagaStep = SagaStep.Failed;
            await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{invoiceId}", state);
            await UpdateInvoiceStatus(dapr, invoiceId, InvoiceStatus.PaymentFailed, $"Budget reservation failed: {reserveResponse.Error}");
            return;
        }

        state.ReservationId = reserveResponse.ReservationId;
        state.CurrentSagaStep = SagaStep.BudgetReserved;
        await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{invoiceId}", state);

        // Step 2: Execute payment
        log.Information("Saga step 2: Executing payment for {InvoiceId}", invoiceId);
        var payRequest = new PaymentExecuteRequest
        {
            InvoiceId = invoiceId,
            ReservationId = reserveResponse.ReservationId,
            Amount = usdAmount,
            Scenario = invoice.Scenario
        };

        var payResponse = await dapr.InvokeMethodAsync<PaymentExecuteRequest, PaymentExecuteResponse>(
            "payment-service", "payment/execute", payRequest);

        if (!payResponse.Success)
        {
            // COMPENSATE: Release budget reservation
            log.Warning("Payment failed for {InvoiceId}: {Error}. Compensating...", invoiceId, payResponse.Error);
            state.CurrentSagaStep = SagaStep.Compensating;
            await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{invoiceId}", state);

            await dapr.InvokeMethodAsync("payment-service", $"budget/release",
                new { reservationId = reserveResponse.ReservationId, department = invoice.Department, amount = usdAmount });

            state.Status = InvoiceStatus.PaymentFailed;
            state.CurrentSagaStep = SagaStep.Compensated;
            await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{invoiceId}", state);
            await UpdateInvoiceStatus(dapr, invoiceId, InvoiceStatus.PaymentFailed, $"Payment failed, budget released: {payResponse.Error}");
            log.Information("Compensation complete for {InvoiceId}, budget restored", invoiceId);
            return;
        }

        state.CurrentSagaStep = SagaStep.PaymentExecuted;

        // Step 3: Update status to paid
        state.Status = InvoiceStatus.Paid;
        state.CurrentSagaStep = SagaStep.StatusUpdated;
        await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{invoiceId}", state);
        await UpdateInvoiceStatus(dapr, invoiceId, InvoiceStatus.Paid, "Payment completed successfully.");

        // Update dashboard stats
        await UpdateDashboardStats(dapr, state);

        log.Information("Invoice {InvoiceId} paid successfully", invoiceId);
    }
    catch (Exception ex)
    {
        log.Error(ex, "Saga error for {InvoiceId}, attempting compensation", invoiceId);

        // Compensate if we have a reservation
        if (!string.IsNullOrEmpty(state.ReservationId))
        {
            try
            {
                await dapr.InvokeMethodAsync("payment-service", $"budget/release",
                    new { reservationId = state.ReservationId, department = invoice.Department, amount = fxRates.ConvertToUsd(invoice.Total, invoice.Currency) });
                log.Information("Emergency compensation complete for {InvoiceId}", invoiceId);
            }
            catch (Exception compEx)
            {
                log.Error(compEx, "Emergency compensation FAILED for {InvoiceId}", invoiceId);
            }
        }

        state.Status = InvoiceStatus.PaymentFailed;
        state.CurrentSagaStep = SagaStep.Failed;
        await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{invoiceId}", state);
        await UpdateInvoiceStatus(dapr, invoiceId, InvoiceStatus.PaymentFailed, $"Payment processing error: {ex.Message}");
    }
}

static async Task UpdateInvoiceStatus(DaprClient dapr, string invoiceId, InvoiceStatus status, string reason)
{
    var statusUpdate = new InvoiceStatusResponse
    {
        InvoiceId = invoiceId,
        Status = status.ToString(),
        Reason = reason
    };

    try
    {
        await dapr.InvokeMethodAsync("invoice-service", $"invoices/{invoiceId}/status", statusUpdate);
    }
    catch
    {
        // Fallback: write directly to state store
        await dapr.SaveStateAsync(DaprComponents.StateStore, $"status:{invoiceId}", statusUpdate);
    }
}

static async Task UpdateDashboardStats(DaprClient dapr, WorkflowState state)
{
    var fxRates = new FxRates();
    var stats = await dapr.GetStateAsync<DashboardResponse>(DaprComponents.StateStore, "dashboard:stats")
        ?? new DashboardResponse();

    stats.TotalProcessed++;
    var usdAmount = fxRates.ConvertToUsd(state.Invoice.Total, state.Invoice.Currency);

    switch (state.Route)
    {
        case RouteDecision.AutoApprove:
            stats.AutoApproved++;
            stats.TotalAutoApprovedAmount += usdAmount;
            break;
        case RouteDecision.HumanReview:
            stats.HumanReviewed++;
            stats.TotalHumanReviewedAmount += usdAmount;
            break;
        case RouteDecision.Reject:
            stats.Rejected++;
            break;
        case RouteDecision.Duplicate:
            stats.Duplicates++;
            break;
    }

    await dapr.SaveStateAsync(DaprComponents.StateStore, "dashboard:stats", stats);
}
