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
builder.Services.AddSingleton<IIdempotencyStore, DaprIdempotencyStore>();
builder.Services.AddSingleton<IInboxStore, DaprInboxStore>();
builder.Services.AddSingleton<ISagaIndexStore, DaprSagaIndexStore>();
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
builder.Services.AddSingleton(sp => new InvoiceSubmittedProcessor(
    sp.GetRequiredService<ILlmClient>(),
    sp.GetRequiredService<DeterministicRouter>(),
    sp.GetRequiredService<SagaOrchestrator>(),
    sp.GetRequiredService<IInvoiceStatusPublisher>(),
    sp.GetRequiredService<IWorkflowStateStore>(),
    sp.GetRequiredService<ISagaIndexStore>(),
    sp.GetRequiredService<IInboxStore>(),
    sp.GetRequiredService<DaprClient>(),
    policyText));

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
    async (InvoiceSubmittedEvent evt, InvoiceSubmittedProcessor processor) =>
{
    var r = await processor.HandleAsync(evt);
    return r switch
    {
        InboxHandlingResult.Ok => Results.Ok(),
        InboxHandlingResult.ServiceUnavailable => Results.StatusCode(503),
        _ => Results.StatusCode(500)
    };
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

await StartupRecovery.RunAsync(app.Services);
app.Run();
