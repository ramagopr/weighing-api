var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Weighing API is running");

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/weight", () => Results.Ok(new
{
    deviceId = "SCALE-001",
    weightKg = 1250.5,
    timestamp = DateTime.UtcNow
}));

app.Run();
