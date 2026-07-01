using System.Threading.RateLimiting;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}")
    .Enrich.WithProperty("Service", "gateway")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
builder.Services.AddOpenApi();

// YARP reverse proxy from config
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Rate limiting: 100 req/min per IP
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("fixed", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 10
            }));
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Correlation ID middleware
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Headers.ContainsKey("X-Correlation-Id"))
    {
        ctx.Request.Headers["X-Correlation-Id"] = Guid.NewGuid().ToString();
    }
    var correlationId = ctx.Request.Headers["X-Correlation-Id"].ToString();
    ctx.Response.Headers["X-Correlation-Id"] = correlationId;

    using (Log.ForContext("CorrelationId", correlationId).BeginTimedOperation(ctx.Request.Path))
    {
        await next();
    }
});

app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "gateway" }));

app.MapReverseProxy();

app.Run();

public static class SerilogExtensions
{
    public static IDisposable BeginTimedOperation(this Serilog.ILogger logger, string operation)
    {
        logger.Information("→ {Operation}", operation);
        return new NoopDisposable();
    }

    private class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
