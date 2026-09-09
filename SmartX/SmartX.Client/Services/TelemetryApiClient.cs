using System.Net.Http.Json;
using SmartX.Core.Telemetry;

namespace SmartX.Client.Services;

public sealed record RegisterDeviceRequest(string ParentSubZoneId, string MacAddress, SensorCategory Category, string LocationDescription);

/// <summary>Thin wrapper around the SmartX.Api REST endpoints.</summary>
public sealed class TelemetryApiClient(string baseAddress)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseAddress) };

    public async Task<List<DeviceStatus>> GetAllStatusesAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<DeviceStatus>>("/api/sensors", ct) ?? [];

    public async Task<TreeNodeDto?> GetTreeAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<TreeNodeDto>("/api/sensors/tree", ct);
                        
    public async Task<ValidationResultDto?> ValidateTreeAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<ValidationResultDto>("/api/sensors/validate", ct);

    public async Task<ValidationResultDto?> RegisterDeviceAsync(
        string parentSubZoneId, string macAddress, SensorCategory category, string locationDescription, CancellationToken ct = default)
    {
        var request = new RegisterDeviceRequest(parentSubZoneId, macAddress, category, locationDescription);
        var response = await _http.PostAsJsonAsync("/api/sensors/register", request, ct);
        return await response.Content.ReadFromJsonAsync<ValidationResultDto>(cancellationToken: ct);
    }

    public async Task<bool> UploadAttachmentAsync(string deviceId, Stream fileStream, string fileName, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        using var streamContent = new StreamContent(fileStream);
        content.Add(streamContent, "file", fileName);
        var response = await _http.PostAsync($"/api/sensors/{deviceId}/attachments", content, ct);
        return response.IsSuccessStatusCode;
    }
}