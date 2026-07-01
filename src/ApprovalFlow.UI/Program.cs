var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

var gatewayUrl = builder.Configuration["GATEWAY_URL"] ?? "http://gateway:8080";
builder.Services.AddHttpClient("Gateway", client =>
{
    client.BaseAddress = new Uri(gatewayUrl);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "ui" }));

app.Run();
