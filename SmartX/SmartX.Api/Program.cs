using SmartX.Api.Endpoints;
using SmartX.Api.Hubs;
using SmartX.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var clientOrigin = builder.Configuration["Cors:ClientOrigin"] ?? "https://localhost:5173";

builder.Services.AddCors(options =>
{
    options.AddPolicy("Client", policy => policy
        .WithOrigins(clientOrigin)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();

// Single shared in-memory registry for the whole app lifetime.
builder.Services.AddSingleton<SensorRegistry>();
builder.Services.AddHostedService<TelemetrySeeder>();

var app = builder.Build();

app.UseCors("Client");

app.MapSensorEndpoints();
app.MapHub<TelemetryHub>("/hubs/telemetry");

app.MapGet("/", () => Results.Ok(new
{
    service = "Smart-X Sensor Data Ingestion & Telemetry API",
    status = "running"
}));

app.Run();
