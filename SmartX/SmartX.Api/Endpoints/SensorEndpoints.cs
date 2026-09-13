using SmartX.Api.Services;
using SmartX.Api.Hubs;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace SmartX.Api.Endpoints;

public sealed record RegisterDeviceRequest(string ParentSubZoneId, string MacAddress, SensorCategory Category, string LocationDescription);

public sealed record DeploymentNodeDto(string Id, string Name, string NodeType, string? MacAddress, string? Category, List<string> Attachments, List<DeploymentNodeDto> Children)
{
    public static DeploymentNodeDto From(DeploymentNode node) => new(
        node.Id,
        node.Name,
        node.NodeType.ToString(),
        node.Sensor?.MacAddress,
        node.Sensor?.Category.ToString(),
        node.Sensor?.Attachments ?? [],
        node.Children.Select(From).ToList());
}

public static class SensorEndpoints
{
    public static void MapSensorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/sensors").WithTags("Sensors");

        group.MapGet("/", (SensorRegistry registry) =>
            Results.Ok(registry.AllStatuses));

        group.MapGet("/tree", (SensorRegistry registry) =>
            Results.Ok(DeploymentNodeDto.From(registry.Root)));

        group.MapGet("/validate", (SensorRegistry registry) =>
        {
            var result = registry.ValidateTree();
            return Results.Ok(result);
        });

        group.MapPost("/register", async (RegisterDeviceRequest request, SensorRegistry registry, IHubContext<TelemetryHub> hub) =>
        {
            var outcome = registry.RegisterDevice(request.ParentSubZoneId, request.MacAddress, request.Category, request.LocationDescription);

            if (!outcome.IsValid)
            {
                return Results.BadRequest(outcome);
            }

            // Broadcast immediately so every other connected dashboard picks
            // up the new node too, not just the one that registered it.
            if (outcome.Device is not null)
            {
                await hub.Clients.All.SendAsync("DeviceStatusUpdated", outcome.Device);
            }

            return Results.Ok(outcome);
        });

        // Removes a single simulated/registered device from the tree and
        // notifies every connected dashboard so it disappears everywhere,
        // not just for the caller.
        group.MapDelete("/{deviceId}", async (string deviceId, SensorRegistry registry, IHubContext<TelemetryHub> hub) =>
        {
            var removed = registry.RemoveDevice(deviceId);
            if (!removed)
            {
                return Results.NotFound($"Device '{deviceId}' was not found.");
            }

            await hub.Clients.All.SendAsync("DeviceRemoved", deviceId);
            return Results.Ok(new { deviceId, removed = true });
        });

        // Clears every simulated/registered device from the registry (tree,
        // baselines, latest status, batchers, throughput counters) and
        // notifies every connected dashboard to drop its local state too.
        // This is the fix for "clear all" only ever working client-side:
        // previously nothing on the server ever forgot a device, so a page
        // reload or the next seeder tick would bring every "cleared" node
        // straight back.
        group.MapPost("/clear", async (SensorRegistry registry, IHubContext<TelemetryHub> hub) =>
        {
            registry.ClearAllDevices();
            await hub.Clients.All.SendAsync("AllDevicesCleared");
            return Results.Ok(new { cleared = true });
        });

        group.MapPost("/{deviceId}/attachments", async (string deviceId, HttpRequest request, SensorRegistry registry) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest("Expected multipart/form-data.");
            }

            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest("No file was uploaded.");
            }

            var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads", deviceId);
            Directory.CreateDirectory(uploadsDir);
            var targetPath = Path.Combine(uploadsDir, Path.GetFileName(file.FileName));

            await using (var stream = File.Create(targetPath))
            {
                await file.CopyToAsync(stream);
            }

            var added = registry.AddAttachment(deviceId, file.FileName);
            return added ? Results.Ok(new { file.FileName }) : Results.NotFound($"Device '{deviceId}' was not found.");
        }).DisableAntiforgery();

        // Returns the attachment file names actually recorded against this
        // device (the server-side source of truth), so the dashboard can
        // show previously uploaded files for a device instead of only
        // whatever was uploaded in the current browser session.
        group.MapGet("/{deviceId}/attachments", (string deviceId, SensorRegistry registry) =>
        {
            var attachments = registry.GetAttachments(deviceId);
            return attachments is null
                ? Results.NotFound($"Device '{deviceId}' was not found.")
                : Results.Ok(attachments);
        });

        // Serves a single attachment for viewing/downloading. Only file names
        // already recorded against this device are served (checked via the
        // registry, not just "does this path exist on disk") so one device's
        // route can't be used to read another device's uploaded files, and
        // Path.GetFileName strips any directory traversal segments from the
        // requested name before it ever touches the filesystem.
        group.MapGet("/{deviceId}/attachments/{fileName}", (string deviceId, string fileName, SensorRegistry registry) =>
        {
            var attachments = registry.GetAttachments(deviceId);
            if (attachments is null)
            {
                return Results.NotFound($"Device '{deviceId}' was not found.");
            }

            var safeFileName = Path.GetFileName(fileName);
            if (!attachments.Contains(safeFileName))
            {
                return Results.NotFound($"No attachment '{safeFileName}' recorded for device '{deviceId}'.");
            }

            var filePath = Path.Combine(AppContext.BaseDirectory, "uploads", deviceId, safeFileName);
            if (!File.Exists(filePath))
            {
                return Results.NotFound("File was recorded but is missing from disk.");
            }

            var contentType = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider()
                .TryGetContentType(safeFileName, out var type)
                ? type
                : "application/octet-stream";

            // enableRangeProcessing lets the browser preview large files
            // (e.g. video/audio scrubbing, PDF viewers) instead of only
            // supporting a single full-file download.
            return Results.File(filePath, contentType, safeFileName, enableRangeProcessing: true);
        });

        // DEV-ONLY: Stop the telemetry seeder - ADD [FromServices] ATTRIBUTE
        group.MapPost("/seeder/stop", async ([FromServices] TelemetrySeeder seeder) =>
        {
            await seeder.StopSeedingAsync();
            return Results.Ok(new { message = "Seeder stopped" });
        });

        // DEV-ONLY: Start the telemetry seeder - ADD [FromServices] ATTRIBUTE
        group.MapPost("/seeder/start", async ([FromServices] TelemetrySeeder seeder) =>
        {
            await seeder.StartSeedingAsync();
            return Results.Ok(new { message = "Seeder started" });
        });

        // DEV-ONLY: Get seeder status - ADD [FromServices] ATTRIBUTE
        group.MapGet("/seeder/status", ([FromServices] TelemetrySeeder seeder) =>
        {
            return Results.Ok(new { isRunning = seeder.IsRunning });
        });
    }
}