using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApprovalFlow.Shared.Constants;
using ApprovalFlow.Shared.Events;
using ApprovalFlow.Shared.Models;
using Dapr.Client;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}")
    .Enrich.WithProperty("Service", "invoice-service")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
builder.Services.AddDaprClient();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "invoice-service" }));

// POST /invoices — submit a new invoice
app.MapPost("/invoices", async (InvoiceDto invoice, DaprClient dapr, HttpContext ctx) =>
{
    var correlationId = ctx.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
    using var _ = Log.ForContext("CorrelationId", correlationId).BeginTimedOperation("SubmitInvoice");

    // Generate tracking ID if not set
    if (string.IsNullOrEmpty(invoice.Id))
        invoice.Id = $"INV-{Guid.NewGuid().ToString("N")[..8].ToUpper()}";

    Log.ForContext("CorrelationId", correlationId)
       .Information("Received invoice {InvoiceId} from {Vendor}, total={Total} {Currency}",
           invoice.Id, invoice.Vendor, invoice.Total, invoice.Currency);

    // Dedup check: hash(vendor + invoiceNumber + total)
    var dedupKey = ComputeDedupKey(invoice.Vendor, invoice.InvoiceNumber, invoice.Total);
    var existingId = await dapr.GetStateAsync<string>(DaprComponents.StateStore, $"dedup:{dedupKey}");

    if (!string.IsNullOrEmpty(existingId))
    {
        Log.ForContext("CorrelationId", correlationId)
           .Warning("Duplicate detected: {InvoiceId} duplicates {OriginalId}", invoice.Id, existingId);

        return Results.Ok(new SubmitInvoiceResponse
        {
            TrackingId = invoice.Id,
            Status = "duplicate",
            OriginalId = existingId
        });
    }

    // Save invoice to state store
    await dapr.SaveStateAsync(DaprComponents.StateStore, $"invoice:{invoice.Id}", invoice);

    // Save dedup key
    await dapr.SaveStateAsync(DaprComponents.StateStore, $"dedup:{dedupKey}", invoice.Id);

    // Save initial status
    var statusEntry = new InvoiceStatusResponse
    {
        InvoiceId = invoice.Id,
        Status = InvoiceStatus.Received.ToString(),
        Reason = "Invoice received, processing started."
    };
    await dapr.SaveStateAsync(DaprComponents.StateStore, $"status:{invoice.Id}", statusEntry);

    // Publish event
    var evt = new InvoiceSubmittedEvent
    {
        InvoiceId = invoice.Id,
        Invoice = invoice,
        CorrelationId = correlationId
    };
    await dapr.PublishEventAsync(DaprComponents.PubSub, DaprComponents.InvoiceSubmittedTopic, evt);

    Log.ForContext("CorrelationId", correlationId)
       .Information("Invoice {InvoiceId} published to workflow", invoice.Id);

    return Results.Accepted($"/invoices/{invoice.Id}/status", new SubmitInvoiceResponse
    {
        TrackingId = invoice.Id,
        Status = "received"
    });
});

// GET /invoices/{id}/status — check invoice status
app.MapGet("/invoices/{id}/status", async (string id, DaprClient dapr, HttpContext ctx) =>
{
    var correlationId = ctx.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();

    var status = await dapr.GetStateAsync<InvoiceStatusResponse>(DaprComponents.StateStore, $"status:{id}");
    if (status == null)
    {
        return Results.NotFound(new { error = "Invoice not found", invoiceId = id });
    }

    return Results.Ok(status);
});

// PUT /invoices/{id}/status — internal: update invoice status (called by Workflow Service)
app.MapPut("/invoices/{id}/status", async (string id, InvoiceStatusResponse statusUpdate, DaprClient dapr) =>
{
    await dapr.SaveStateAsync(DaprComponents.StateStore, $"status:{id}", statusUpdate);
    return Results.Ok();
});

app.Run();

static string ComputeDedupKey(string vendor, string invoiceNumber, decimal total)
{
    var input = $"{vendor.ToLowerInvariant()}|{invoiceNumber.ToLowerInvariant()}|{total:F2}";
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(hash)[..16];
}

public static class SerilogExtensions
{
    public static IDisposable BeginTimedOperation(this Serilog.ILogger logger, string operationName)
    {
        logger.Information("Starting {Operation}", operationName);
        return new NoopDisposable();
    }

    private class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
