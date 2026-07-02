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

// Saga dependencies — thin Dapr wrappers behind interfaces (unit-testable orchestrator)
builder.Services.AddSingleton<IPaymentClient, DaprPaymentClient>();
builder.Services.AddSingleton<IWorkflowStateStore, DaprWorkflowStateStore>();
builder.Services.AddSingleton<IInvoiceStatusPublisher>(sp =>
    new RetryingInvoiceStatusPublisher(new DaprInvoiceStatusPublisher(sp.GetRequiredService<DaprClient>())));
builder.Services.AddSingleton<SagaOrchestrator>();

// LLM Client — use Stub by default, OpenRouter when configured
var llmProvider = builder.Configuration["LLM_PROVIDER"] ?? "stub";
if (llmProvider == "openrouter")
{
    var apiKey = builder.Configuration["LLM_API_KEY"] ?? "";
    var baseUrl = builder.Configuration["LLM_BASE_URL"] ?? "https://openrouter.ai/api/v1";
    var model = builder.Configuration["LLM_MODEL"] ?? "anthropic/claude-sonnet-4";

    // NAMED client (not typed): the singleton below resolves it by this same name,
    // so BaseAddress/headers are actually applied to the client it receives.
    builder.Services.AddHttpClient(nameof(OpenRouterLlmClient), http =>
    {
        http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        http.DefaultRequestHeaders.Add("HTTP-Referer", "https://approvalflow.local");
        http.DefaultRequestHeaders.Add("X-Title", "ApprovalFlow");
    });
    builder.Services.AddSingleton<ILlmClient>(sp =>
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
    async (InvoiceSubmittedEvent evt, DaprClient dapr, ILlmClient llm, DeterministicRouter router,
        SagaOrchestrator saga, IInvoiceStatusPublisher statusPublisher) =>
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
            await saga.ExecuteAsync(evt.InvoiceId, evt.Invoice, state, correlationId);
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
            await statusPublisher.PublishStatusAsync(evt.InvoiceId, InvoiceStatus.PendingReview, routerResult.Reason);
            log.Information("Invoice {InvoiceId} queued for human review", evt.InvoiceId);
            break;

        case RouteDecision.Reject:
            state.Status = InvoiceStatus.Rejected;
            await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{evt.InvoiceId}", state);
            await statusPublisher.PublishStatusAsync(evt.InvoiceId, InvoiceStatus.Rejected, routerResult.Reason);
            log.Information("Invoice {InvoiceId} rejected: {Reason}", evt.InvoiceId, routerResult.Reason);
            break;

        case RouteDecision.Duplicate:
            state.Status = InvoiceStatus.Duplicate;
            await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{evt.InvoiceId}", state);
            await statusPublisher.PublishStatusAsync(evt.InvoiceId, InvoiceStatus.Duplicate, routerResult.Reason);
            break;
    }

    return Results.Ok();
});

// POST /workflow/{id}/decision — HITL approve/reject/request_info
app.MapPost("/workflow/{id}/decision",
    async (string id, HitlDecisionRequest request, DaprClient dapr,
        SagaOrchestrator saga, IInvoiceStatusPublisher statusPublisher) =>
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
            await saga.ExecuteAsync(id, state.Invoice, state, correlationId);
            break;

        case "reject":
            state.HitlStatus = HitlStatus.Rejected;
            state.Status = InvoiceStatus.Rejected;
            await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{id}", state);
            await statusPublisher.PublishStatusAsync(id, InvoiceStatus.Rejected, $"Rejected by human reviewer. {request.Comment ?? ""}");
            log.Information("Invoice {InvoiceId} rejected by human", id);
            break;

        case "request_info":
            state.HitlStatus = HitlStatus.InfoRequested;
            state.Status = InvoiceStatus.PendingReview;
            await dapr.SaveStateAsync(DaprComponents.StateStore, $"workflow:{id}", state);
            await statusPublisher.PublishStatusAsync(id, InvoiceStatus.PendingReview, $"Additional information requested: {request.Comment ?? ""}");
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
