using System.Net.Http.Json;
using SmartX.Core.Ingestion;
using SmartX.Core.Telemetry;

// Smart-X device simulator.
//
// Acts like a fleet of ESP32 nodes: it discovers the registered devices from
// the API, then every few seconds each "device" POSTs a reading to
// /api/telemetry/ingest, exactly the way real hardware would. Nothing here
// touches the API's internals.
//
// Usage:  SMARTX_FIREBASE_EMAIL=... SMARTX_FIREBASE_PASSWORD=... dotnet run --project SmartX.DeviceSimulator [apiBaseUrl]
// Keys:   S = spike a random sensor (critical packet)
//         X = silence a random device for 30 s (simulates a disconnect)
//         Q = quit

var apiBase = args.FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("SMARTX_API_URL")
    ?? "https://localhost:5001";

var handler = new HttpClientHandler();
if (apiBase.Contains("localhost", StringComparison.OrdinalIgnoreCase))
{
    // Local dev certificates are often untrusted by a console app; only relax this for localhost.
    handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
}

using var http = new HttpClient(handler) { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(10) };
using var firebaseHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

var firebaseEmail = Environment.GetEnvironmentVariable("SMARTX_FIREBASE_EMAIL");
var firebasePassword = Environment.GetEnvironmentVariable("SMARTX_FIREBASE_PASSWORD");
if (string.IsNullOrWhiteSpace(firebaseEmail) || string.IsNullOrWhiteSpace(firebasePassword))
{
    throw new InvalidOperationException(
        "Set SMARTX_FIREBASE_EMAIL and SMARTX_FIREBASE_PASSWORD to a Firebase operator account before starting the simulator.");
}

var firebaseConfig = await http.GetFromJsonAsync<FirebaseConfiguration>("/api/auth/config")
    ?? throw new InvalidOperationException("The API did not return Firebase client configuration.");
var authToken = await SignInAsync(firebaseHttp, firebaseConfig, firebaseEmail, firebasePassword);
http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken.IdToken);

var random = new Random();
var fleet = new Dictionary<string, SimDevice>(StringComparer.OrdinalIgnoreCase);
var tickInterval = TimeSpan.FromSeconds(2);
var treeRefreshEvery = TimeSpan.FromSeconds(10);
var lastTreeRefresh = DateTimeOffset.MinValue;
var forceSpike = false;
var forceSilence = false;

Console.WriteLine($"Smart-X device simulator -> {apiBase}");
Console.WriteLine("Keys: [S] spike a sensor   [X] silence a device for 30 s   [Q] quit");

while (true)
{
    try
    {
        if (!Console.IsInputRedirected)
        {
            while (Console.KeyAvailable)
            {
                switch (char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar))
                {
                    case 'S': forceSpike = true; break;
                    case 'X': forceSilence = true; break;
                    case 'Q': return;
                }
            }
        }
    }
    catch (InvalidOperationException)
    {
        // No interactive console (e.g. running in a container); keys are simply unavailable.
    }

    var now = DateTimeOffset.UtcNow;
    if (authToken.ExpiresAt <= now.AddMinutes(2))
    {
        authToken = await RefreshAsync(firebaseHttp, firebaseConfig, authToken.RefreshToken);
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken.IdToken);
    }

    if (now - lastTreeRefresh >= treeRefreshEvery)
    {
        await RefreshFleetAsync();
        lastTreeRefresh = now;
    }

    if (fleet.Count > 0)
    {
        var devices = fleet.Values.ToList();

        if (forceSilence)
        {
            var victim = devices[random.Next(devices.Count)];
            victim.SilentUntil = now.AddSeconds(30);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {victim.DeviceId} goes silent for 30 s");
            forceSilence = false;
        }

        SimDevice? spikeTarget = null;
        if (forceSpike)
        {
            var sensors = devices.Where(d => d.Category != SensorCategory.Actuator).ToList();
            if (sensors.Count > 0)
            {
                spikeTarget = sensors[random.Next(sensors.Count)];
            }

            forceSpike = false;
        }

        await Task.WhenAll(devices.Select(d => ReportAsync(d, d == spikeTarget, now)));
    }
    else
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] No registered devices yet (is the API running?)");
    }

    await Task.Delay(tickInterval);
}

async Task RefreshFleetAsync()
{
    try
    {
        var root = await http.GetFromJsonAsync<TreeNode>("/api/sensors/tree");
        if (root is null)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in Flatten(root))
        {
            if (string.IsNullOrEmpty(node.MacAddress) || !Enum.TryParse<SensorCategory>(node.Category, out var category))
            {
                continue;
            }

            seen.Add(node.MacAddress);
            if (!fleet.ContainsKey(node.MacAddress))
            {
                fleet[node.MacAddress] = new SimDevice(node.MacAddress, node.Id, category);
            }
        }

        foreach (var gone in fleet.Keys.Where(mac => !seen.Contains(mac)).ToList())
        {
            fleet.Remove(gone);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Could not reach the API: {ex.Message}");
    }
}

async Task ReportAsync(SimDevice device, bool spike, DateTimeOffset now)
{
    if (now < device.SilentUntil)
    {
        return; // simulated outage: the device sends nothing at all
    }

    // Small chance each tick that a device drops off the network for a while.
    if (random.NextDouble() < 0.003)
    {
        device.SilentUntil = now.AddSeconds(30);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {device.DeviceId} lost power (silent 30 s)");
        return;
    }

    double value;
    if (device.Category == SensorCategory.Actuator)
    {
        value = device.OutputOn ? 1 : 0; // actuators report the state the gateway last told them
    }
    else if (spike || random.NextDouble() < 0.02)
    {
        value = device.Value * (1.8 + random.NextDouble());
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {device.DeviceId} spike -> {value:0.0}");
    }
    else
    {
        var noise = (random.NextDouble() - 0.5) * 2 * NoiseFor(device.Category);
        device.Value += noise;
        value = device.Value;
    }

    try
    {
        using var response = await http.PostAsJsonAsync("/api/telemetry/ingest", new IngestRequest(device.MacAddress, value));
        var ack = await response.Content.ReadFromJsonAsync<IngestResponse>();
        if (ack is not null)
        {
            device.OutputOn = ack.OutputOn;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {device.DeviceId} failed to report: {ex.Message}");
    }
}

async Task<FirebaseToken> SignInAsync(HttpClient authHttp, FirebaseConfiguration config, string email, string password)
{
    using var response = await authHttp.PostAsJsonAsync(
        $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={Uri.EscapeDataString(config.ApiKey)}",
        new { email, password, returnSecureToken = true });
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<FirebaseSignInResponse>()
        ?? throw new InvalidOperationException("Firebase returned an empty sign-in response.");
    if (!int.TryParse(result.ExpiresIn, out var seconds))
    {
        throw new InvalidOperationException("Firebase returned an invalid ID token lifetime.");
    }

    return new FirebaseToken(result.IdToken, result.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(seconds));
}

async Task<FirebaseToken> RefreshAsync(HttpClient authHttp, FirebaseConfiguration config, string refreshToken)
{
    using var response = await authHttp.PostAsync(
        $"https://securetoken.googleapis.com/v1/token?key={Uri.EscapeDataString(config.ApiKey)}",
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        }));
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<FirebaseRefreshResponse>()
        ?? throw new InvalidOperationException("Firebase returned an empty token refresh response.");
    var idToken = result.IdToken ?? result.AccessToken;
    if (string.IsNullOrWhiteSpace(idToken) || !int.TryParse(result.ExpiresIn, out var seconds))
    {
        throw new InvalidOperationException("Firebase returned an invalid refreshed token.");
    }

    return new FirebaseToken(idToken, result.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(seconds));
}

static IEnumerable<TreeNode> Flatten(TreeNode node)
{
    yield return node;
    foreach (var child in node.Children)
    {
        foreach (var descendant in Flatten(child))
        {
            yield return descendant;
        }
    }
}

static double NoiseFor(SensorCategory category) => category switch
{
    SensorCategory.Environmental => 1.2,
    SensorCategory.Power => 15,
    _ => 1
};

internal sealed record TreeNode(string Id, string Name, string NodeType, string? MacAddress, string? Category, List<TreeNode> Children);
internal sealed record FirebaseConfiguration(string ApiKey, string ProjectId);
internal sealed record FirebaseToken(string IdToken, string RefreshToken, DateTimeOffset ExpiresAt);
internal sealed record FirebaseSignInResponse(string IdToken, string RefreshToken, string ExpiresIn);
internal sealed record FirebaseRefreshResponse(string? IdToken, string? AccessToken, string RefreshToken, string ExpiresIn);

internal sealed class SimDevice(string macAddress, string deviceId, SensorCategory category)
{
    public string MacAddress { get; } = macAddress;
    public string DeviceId { get; } = deviceId;
    public SensorCategory Category { get; } = category;
    public double Value { get; set; } = category switch
    {
        SensorCategory.Environmental => 22,
        SensorCategory.Power => 220,
        _ => 1
    };
    public bool OutputOn { get; set; }
    public DateTimeOffset SilentUntil { get; set; } = DateTimeOffset.MinValue;
}
