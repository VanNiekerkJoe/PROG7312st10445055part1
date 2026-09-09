using System.Net.Http.Json;
using SmartX.Core.Telemetry;

namespace SmartX.Client.Services;

public sealed record RegisterDeviceRequest(string ParentSubZoneId, string MacAddress, SensorCategory Category, string LocationDescription);

public sealed record SeederStatusResponse(bool IsRunning);

public sealed record DeviceRegistrationOutcomeDto(bool IsValid, List<string> Errors, DeviceStatus? Device);

/// <summary>Thin wrapper around the SmartX.Api REST endpoints.</summary>
public sealed class TelemetryApiClient
{
    private readonly HttpClient _http;

    public TelemetryApiClient(string baseAddress)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseAddress) };
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<List<DeviceStatus>> GetAllStatusesAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<DeviceStatus>>("/api/sensors", ct) ?? [];
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting statuses: {ex.Message}");
            return [];
        }
    }

    public async Task<TreeNodeDto?> GetTreeAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TreeNodeDto>("/api/sensors/tree", ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting tree: {ex.Message}");
            return null;
        }
    }

    public async Task<ValidationResultDto?> ValidateTreeAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<ValidationResultDto>("/api/sensors/validate", ct);

    public async Task<DeviceRegistrationOutcomeDto?> RegisterDeviceAsync(
        string parentSubZoneId, string macAddress, SensorCategory category, string locationDescription, CancellationToken ct = default)
    {
        var request = new RegisterDeviceRequest(parentSubZoneId, macAddress, category, locationDescription);
        var response = await _http.PostAsJsonAsync("/api/sensors/register", request, ct);
        return await response.Content.ReadFromJsonAsync<DeviceRegistrationOutcomeDto>(cancellationToken: ct);
    }

    /// <summary>Removes a single device from the server-side registry (tree + all indexes).</summary>
    public async Task<bool> RemoveDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.DeleteAsync($"/api/sensors/{deviceId}", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error removing device: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clears every simulated/registered device from the server-side registry.
    /// This is the authoritative "clear all" - it must be used instead of (or
    /// alongside) any local/client-only clearing, otherwise the API keeps
    /// reporting the "cleared" devices and they reappear on the next reload
    /// or SignalR broadcast.
    /// </summary>
    public async Task<bool> ClearAllDevicesAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync("/api/sensors/clear", null, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error clearing devices: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UploadAttachmentAsync(string deviceId, Stream fileStream, string fileName, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        using var streamContent = new StreamContent(fileStream);
        content.Add(streamContent, "file", fileName);
        var response = await _http.PostAsync($"/api/sensors/{deviceId}/attachments", content, ct);
        return response.IsSuccessStatusCode;
    }

    // DEV-ONLY: Control the telemetry seeder
    public async Task<bool> StopSeederAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync("/api/sensors/seeder/stop", null, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StartSeederAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync("/api/sensors/seeder/start", null, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<SeederStatusResponse?> GetSeederStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<SeederStatusResponse>("/api/sensors/seeder/status", ct);
        }
        catch
        {
            return new SeederStatusResponse(true);
        }
    }
}