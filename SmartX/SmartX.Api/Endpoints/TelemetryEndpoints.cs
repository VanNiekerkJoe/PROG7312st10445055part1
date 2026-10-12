using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Core.Ingestion;
using SmartX.Core.Storage;

namespace SmartX.Api.Endpoints;

public sealed record SimulateAlertRequest(string DeviceId);

public static class TelemetryEndpoints
{
    public static void MapTelemetryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/telemetry").WithTags("Telemetry");
        group.RequireAuthorization();

        // The front door for devices. Real hardware (or SmartX.DeviceSimulator)
        // POSTs a reading here; the gateway classifies it, drops it into the
        // right lane and returns immediately. Processing is asynchronous.
        group.MapPost("/ingest", (IngestRequest request, SensorRegistry registry, DeviceStateStore devices, TelemetryIngestQueue queue) =>
        {
            if (string.IsNullOrWhiteSpace(request.MacAddress))
            {
                return Results.BadRequest(new IngestResponse(false, false, null, false, "MacAddress is required."));
            }

            if (!devices.TryGetByMac(request.MacAddress, out var state))
            {
                devices.Sync(registry.DeviceNodes);
                if (!devices.TryGetByMac(request.MacAddress, out state))
                {
                    return Results.NotFound(new IngestResponse(false, false, null, false, $"Unknown device '{request.MacAddress}'."));
                }
            }

            var z = registry.PeekZScore(state.DeviceId, (float)request.Value);
            var (urgency, reason) = TelemetryClassifier.Classify(state.Category, request.Value, z, request.Disconnected);

            var item = new IngestItem(state.MacAddress, request.Value, request.Disconnected, DateTimeOffset.UtcNow, urgency, reason);
            if (!queue.Enqueue(item))
            {
                return Results.Json(
                    new IngestResponse(false, false, state.DeviceId, state.OutputOn, "Gateway queue is full; retry shortly."),
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            return Results.Ok(new IngestResponse(true, item.IsCritical, state.DeviceId, state.OutputOn, item.IsCritical ? reason : "Queued"));
        });

        // Demo helper: injects a forced critical packet for one device so the
        // priority lane can be shown on demand. It goes through the same
        // queue as everything else.
        group.MapPost("/simulate-alert", (SimulateAlertRequest request, SensorRegistry registry, DeviceStateStore devices, TelemetryIngestQueue queue) =>
        {
            devices.Sync(registry.DeviceNodes);
            if (!devices.TryGetByDeviceId(request.DeviceId, out var state))
            {
                return Results.NotFound(new IngestResponse(false, false, null, false, $"Device '{request.DeviceId}' was not found."));
            }

            var baseValue = Math.Abs(state.LastValue) < 0.001 ? TelemetryClassifier.BaselineFor(state.Category) : state.LastValue;
            var item = new IngestItem(state.MacAddress, baseValue * 3, false, DateTimeOffset.UtcNow, 3, "Operator-triggered high-priority test alert");
            queue.Enqueue(item);

            return Results.Ok(new IngestResponse(true, true, state.DeviceId, state.OutputOn, item.Reason));
        });

        // Demo helper: floods the standard FIFO lane with ordinary packets so
        // a backlog builds up and a priority alert can visibly jump it.
        group.MapPost("/burst", (int? count, SensorRegistry registry, DeviceStateStore devices, TelemetryIngestQueue queue) =>
        {
            devices.Sync(registry.DeviceNodes);
            var targets = devices.Snapshot().Where(d => d.Category != SmartX.Core.Telemetry.SensorCategory.Actuator).ToList();
            if (targets.Count == 0)
            {
                return Results.BadRequest(new { message = "No sensors are registered yet." });
            }

            var total = Math.Clamp(count ?? 2000, 1, TelemetryIngestQueue.MaxStandardDepth);
            var random = new Random();
            var queued = 0;

            for (var i = 0; i < total; i++)
            {
                var target = targets[random.Next(targets.Count)];
                var baseValue = Math.Abs(target.LastValue) < 0.001 ? TelemetryClassifier.BaselineFor(target.Category) : target.LastValue;
                var noise = (random.NextDouble() - 0.5) * 2 * TelemetryClassifier.NoiseFor(target.Category);

                var item = new IngestItem(target.MacAddress, baseValue + noise, false, DateTimeOffset.UtcNow, 0, string.Empty);
                if (queue.Enqueue(item))
                {
                    queued++;
                }
            }

            return Results.Ok(new { queued });
        });

        group.MapGet("/stats", (IngestProcessor processor) => Results.Ok(processor.Snapshot()));

        // Timeline data for the sparkline, straight out of the SortedDictionary.
        group.MapGet("/history/{deviceId}", (string deviceId, int? count, SortedHistoryLog history) =>
            Results.Ok(history.Latest(deviceId, Math.Clamp(count ?? 60, 1, 300))));
    }
}
