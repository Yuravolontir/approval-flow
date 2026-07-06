using System.Text.Json;
using ApprovalFlow.Shared.Constants;
using ApprovalFlow.Shared.Models;
using Dapr.Client;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}")
    .Enrich.WithProperty("Service", "payment-service")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
builder.Services.AddDaprClient();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IBudgetStore, DaprBudgetStore>();
builder.Services.AddSingleton<BudgetService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Initialize budgets on startup
app.Lifetime.ApplicationStarted.Register(async () =>
{
    // Poll until the Dapr sidecar is ready instead of a blind delay.
    // Seeding is idempotent (skips existing budgets), so retrying the whole loop is safe.
    const int maxAttempts = 30;
    var dapr = app.Services.GetRequiredService<DaprClient>();
    var budgets = new Dictionary<string, decimal>
    {
        ["marketing-2026Q2"] = 1000.0m,
        ["engineering-2026Q2"] = 50000.0m,
        ["sales-2026Q2"] = 20000.0m
    };

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            foreach (var (dept, amount) in budgets)
            {
                var existing = await dapr.GetStateAsync<BudgetState>(DaprComponents.StateStore, $"budget:{dept}");
                if (existing == null)
                {
                    await dapr.SaveStateAsync(DaprComponents.StateStore, $"budget:{dept}", new BudgetState
                    {
                        Department = dept,
                        TotalBudget = amount,
                        Available = amount,
                        Reservations = new Dictionary<string, decimal>()
                    });
                    Log.Information("Initialized budget for {Department}: {Amount:C}", dept, amount);
                }
            }
            Log.Information("Budget initialization complete after {Attempt} attempt(s)", attempt);
            return;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            Log.Warning("Budget init attempt {Attempt}/{MaxAttempts} failed ({Error}), retrying in 1s",
                attempt, maxAttempts, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Budget seeding FAILED after {MaxAttempts} attempts — budgets missing", maxAttempts);
        }
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "payment-service" }));

// POST /budget/reserve — atomically reserve amount from department budget
app.MapPost("/budget/reserve", async (BudgetReserveRequest request, BudgetService budgets) =>
{
    var log = Log.ForContext("CorrelationId", request.InvoiceId);
    log.Information("Reserving {Amount:C} from {Department} for {InvoiceId}",
        request.Amount, request.Department, request.InvoiceId);

    var response = await budgets.ReserveAsync(request);
    return Results.Ok(response);
});

// POST /budget/release — release reservation (compensation)
app.MapPost("/budget/release", async (BudgetReleaseRequest request, BudgetService budgets) =>
{
    var log = Log.ForContext("CorrelationId", request.ReservationId);
    log.Information("Releasing reservation {ReservationId} for {Department}", request.ReservationId, request.Department);

    var result = await budgets.ReleaseAsync(request);
    if (!result.Success && result.Error == $"Department {request.Department} not found")
    {
        return Results.NotFound(new { error = $"Department {request.Department} not found" });
    }

    if (!result.Success)
    {
        return Results.Ok(new { success = false, error = result.Error });
    }

    return Results.Ok(new { success = true, releasedAmount = result.ReleasedAmount, available = result.Available });
});

// POST /payment/execute — simulate payment
app.MapPost("/payment/execute", async (PaymentExecuteRequest request, DaprClient dapr) =>
{
    var log = Log.ForContext("CorrelationId", request.InvoiceId);
    log.Information("Executing payment for {InvoiceId}, amount={Amount:C}", request.InvoiceId, request.Amount);

    // Check for forced failure scenario (Journey D)
    if (!string.IsNullOrEmpty(request.Scenario) && request.Scenario.Contains("payment-failure"))
    {
        log.Warning("Forced payment failure for {InvoiceId} (scenario: {Scenario})", request.InvoiceId, request.Scenario);
        return Results.Ok(new PaymentExecuteResponse
        {
            Success = false,
            Error = "Payment processing failed (simulated failure for testing)."
        });
    }

    // Simulate successful payment
    log.Information("Payment executed successfully for {InvoiceId}", request.InvoiceId);
    return Results.Ok(new PaymentExecuteResponse { Success = true });
});

// POST /payment/reverse — reverse payment (compensation)
app.MapPost("/payment/reverse", (PaymentReverseRequest request) =>
{
    Log.Information("Payment reversed for {InvoiceId}", request.InvoiceId);
    return Results.Ok(new { success = true });
});

// GET /budget/{dept} — query remaining budget
app.MapGet("/budget/{dept}", async (string dept, DaprClient dapr) =>
{
    var budget = await dapr.GetStateAsync<BudgetState>(DaprComponents.StateStore, $"budget:{dept}");
    if (budget == null)
        return Results.NotFound(new { error = $"Department {dept} not found" });

    return Results.Ok(new
    {
        department = budget.Department,
        totalBudget = budget.TotalBudget,
        available = budget.Available,
        reserved = budget.TotalBudget - budget.Available,
        activeReservations = budget.Reservations.Count
    });
});

app.Run();

// Internal models
public class BudgetState
{
    public string Department { get; set; } = string.Empty;
    public decimal TotalBudget { get; set; }
    public decimal Available { get; set; }
    public Dictionary<string, decimal> Reservations { get; set; } = new();
}

public class BudgetReleaseRequest
{
    public string ReservationId { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public class PaymentReverseRequest
{
    public string InvoiceId { get; set; } = string.Empty;
}
