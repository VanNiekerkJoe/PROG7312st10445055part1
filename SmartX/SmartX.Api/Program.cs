using SmartX.Api.Endpoints;
using SmartX.Api.Hubs;
using SmartX.Api.Services;
using SmartX.Core.Commands;
using SmartX.Core.Ingestion;
using SmartX.Core.Insights;
using SmartX.Core.Storage;

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
builder.Services.AddSingleton<AttachmentEncryptionService>();

// Part 2: real-time ingestion pipeline and command stream.
builder.Services.AddSingleton<TelemetryIngestQueue>();      // Queue<T> + PriorityQueue
builder.Services.AddSingleton<DeviceStateStore>();          // Dictionary keyed by MAC
builder.Services.AddSingleton(_ => new SortedHistoryLog()); // SortedDictionary by timestamp
builder.Services.AddSingleton<ActiveAlertSet>();            // HashSet of active alerts
builder.Services.AddSingleton<CommandHistory>();            // Stack<T> for undo
builder.Services.AddSingleton<ActionPatternEngine>();       // predictive suggestions
builder.Services.AddSingleton<CommandService>();
builder.Services.AddSingleton<IngestProcessor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestProcessor>());

builder.Services.AddSingleton<TelemetrySeeder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetrySeeder>());


var app = builder.Build();

app.UseCors("Client");

app.MapSensorEndpoints();
app.MapTelemetryEndpoints();
app.MapCommandEndpoints();
app.MapHub<TelemetryHub>("/hubs/telemetry");
app.MapHub<CommandHub>("/hubs/commands");

app.MapGet("/", () => Results.Ok(new
{
    service = "Smart-X Ingestion, Telemetry & Command Stream API",
    status = "running"
}));

app.Run();