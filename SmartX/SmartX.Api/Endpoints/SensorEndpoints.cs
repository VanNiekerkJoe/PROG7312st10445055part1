using SmartX.Api.Services;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

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

        group.MapPost("/register", (RegisterDeviceRequest request, SensorRegistry registry) =>
        {
            var result = registry.RegisterDevice(request.ParentSubZoneId, request.MacAddress, request.Category, request.LocationDescription);
            return result.IsValid ? Results.Ok(result) : Results.BadRequest(result);
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
    }
}
