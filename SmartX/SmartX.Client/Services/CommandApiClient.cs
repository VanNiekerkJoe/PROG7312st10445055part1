using System.Net.Http.Json;
using SmartX.Core.Commands;
using SmartX.Core.Ingestion;
using SmartX.Core.Insights;
using SmartX.Core.Storage;

namespace SmartX.Client.Services;

public sealed record SimulateAlertRequest(string DeviceId);
public sealed record SearchLogRequest(string Term);

/// <summary>Thin wrapper around the Part 2 REST endpoints (commands, insights, telemetry helpers).</summary>
public sealed class CommandApiClient
{
    private readonly HttpClient _http;

    public CommandApiClient(string baseAddress, FirebaseAuthSession auth)
    {
        _http = new HttpClient(new FirebaseBearerHandler(auth))
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<CommandLogResponse?> GetCommandLogAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<CommandLogResponse>("/api/commands", ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting commands: {ex.Message}");
            return null;
        }
    }

    public async Task<CommandResult> IssueCommandAsync(
        string deviceId,
        CommandType type,
        bool fromSuggestion = false,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(
                "/api/commands",
                new IssueCommandRequest(deviceId, type, fromSuggestion),
                ct);
            return await response.Content.ReadFromJsonAsync<CommandResult>(cancellationToken: ct)
                   ?? new CommandResult(false, "Empty response from the API.", null);
        }
        catch (Exception ex)
        {
            return new CommandResult(false, $"Could not reach the API: {ex.Message}", null);
        }
    }

    public async Task<CommandResult> UndoLastCommandAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync("/api/commands/undo", null, ct);
            return await response.Content.ReadFromJsonAsync<CommandResult>(cancellationToken: ct)
                   ?? new CommandResult(false, "Empty response from the API.", null);
        }
        catch (Exception ex)
        {
            return new CommandResult(false, $"Could not reach the API: {ex.Message}", null);
        }
    }

    public async Task<List<SuggestedAction>> GetInsightsAsync(string? search, CancellationToken ct = default)
    {
        try
        {
            var url = string.IsNullOrWhiteSpace(search)
                ? "/api/insights"
                : $"/api/insights?search={Uri.EscapeDataString(search)}";
            return await _http.GetFromJsonAsync<List<SuggestedAction>>(url, ct) ?? [];
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting insights: {ex.Message}");
            return [];
        }
    }

    public async Task LogSearchAsync(string term, CancellationToken ct = default)
    {
        try
        {
            await _http.PostAsJsonAsync("/api/insights/search", new SearchLogRequest(term), ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error logging search: {ex.Message}");
        }
    }

    public async Task TrainDemoPatternsAsync(CancellationToken ct = default)
    {
        try
        {
            await _http.PostAsync("/api/insights/train-demo", null, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error training demo patterns: {ex.Message}");
        }
    }

    public async Task<List<HistoryPoint>> GetHistoryAsync(string deviceId, int count = 60, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<HistoryPoint>>(
                $"/api/telemetry/history/{Uri.EscapeDataString(deviceId)}?count={count}", ct) ?? [];
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting history: {ex.Message}");
            return [];
        }
    }

    public async Task<IngestResponse?> SimulateAlertAsync(string deviceId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/telemetry/simulate-alert", new SimulateAlertRequest(deviceId), ct);
            return await response.Content.ReadFromJsonAsync<IngestResponse>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error simulating alert: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> BurstAsync(int count, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync($"/api/telemetry/burst?count={count}", null, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending burst: {ex.Message}");
            return false;
        }
    }
}
