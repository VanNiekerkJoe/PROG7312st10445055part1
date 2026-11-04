using System.Net.Http.Json;
using SmartX.Core.Mesh;

namespace SmartX.Client.Services;

/// <summary>Thin wrapper around the Part 3 REST endpoints (mesh topology, routing, traversal, historical range queries).</summary>
public sealed class MeshApiClient
{
    private readonly HttpClient _http;

    public MeshApiClient(string baseAddress, FirebaseAuthSession auth)
    {
        _http = new HttpClient(new FirebaseBearerHandler(auth))
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public Task<MeshTopologyResponse?> GetTopologyAsync(CancellationToken ct = default) =>
        GetAsync<MeshTopologyResponse>("/api/mesh", ct);

    public async Task<List<string>> GetSubnetsAsync(CancellationToken ct = default) =>
        await GetAsync<List<string>>("/api/mesh/subnets", ct) ?? [];

    /// <summary>Returns the body even on 404 so the page can show the API's explanation.</summary>
    public Task<MeshRouteResponse?> GetRouteAsync(string idOrMac, CancellationToken ct = default) =>
        GetAsync<MeshRouteResponse>($"/api/mesh/route/{Uri.EscapeDataString(idOrMac)}", ct);

    public Task<MeshSubnetResponse?> GetReachableAsync(string? subnet, bool dfs, CancellationToken ct = default)
    {
        var url = $"/api/mesh/reachable?algo={(dfs ? "dfs" : "bfs")}";
        if (!string.IsNullOrWhiteSpace(subnet))
        {
            url += $"&subnet={Uri.EscapeDataString(subnet)}";
        }

        return GetAsync<MeshSubnetResponse>(url, ct);
    }

    public Task<bool> FailAsync(string idOrMac, CancellationToken ct = default) =>
        PostAsync($"/api/mesh/nodes/{Uri.EscapeDataString(idOrMac)}/fail", ct);

    public Task<bool> RestoreAsync(string idOrMac, CancellationToken ct = default) =>
        PostAsync($"/api/mesh/nodes/{Uri.EscapeDataString(idOrMac)}/restore", ct);

    public Task<bool> RestoreAllAsync(CancellationToken ct = default) =>
        PostAsync("/api/mesh/restore-all", ct);

    public Task<HistoryRangeResponse?> GetHistoryRangeAsync(string idOrMac, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        GetAsync<HistoryRangeResponse>(
            $"/api/mesh/history/{Uri.EscapeDataString(idOrMac)}?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}&limit=500", ct);

    public Task<bool> BackfillAsync(string idOrMac, CancellationToken ct = default) =>
        PostAsync($"/api/mesh/history/{Uri.EscapeDataString(idOrMac)}/backfill?count=1500&hours=6", ct);

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Mesh API error ({url}): {ex.Message}");
            return null;
        }
    }

    private async Task<bool> PostAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsync(url, null, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Mesh API error ({url}): {ex.Message}");
            return false;
        }
    }
}
