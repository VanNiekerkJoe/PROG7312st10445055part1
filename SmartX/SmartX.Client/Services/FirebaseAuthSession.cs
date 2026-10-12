using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;

namespace SmartX.Client.Services;

public sealed record FirebaseAuthResult(bool Succeeded, string? Error);

public sealed class FirebaseAuthSession(HttpClient http, IJSRuntime js, string apiBaseAddress)
{
    private const string StorageKey = "smartx.firebase.session";
    private static readonly TimeSpan RefreshLeadTime = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions FirebaseJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private FirebaseSession? _session;
    private FirebaseClientConfiguration? _configuration;
    private bool _restored;

    public event Action? Changed;

    public bool IsAuthenticated => _session is not null && _session.ExpiresAt > DateTimeOffset.UtcNow;
    public string? Email => _session?.Email;

    public async Task RestoreAsync()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        try
        {
            var saved = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(saved))
            {
                _session = JsonSerializer.Deserialize<FirebaseSession>(saved);
                if (_session is not null && !IsAuthenticated)
                {
                    await RefreshTokenAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not restore the Firebase session: {ex.Message}");
            _session = null;
        }

    }

    public async Task<string?> GetIdTokenAsync()
    {
        await RestoreAsync();
        if (_session is null)
        {
            return null;
        }

        if (_session.ExpiresAt <= DateTimeOffset.UtcNow + RefreshLeadTime)
        {
            await RefreshTokenAsync();
        }

        return _session?.IdToken;
    }

    public async Task<FirebaseAuthResult> SignInAsync(string email, string password, bool createAccount)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            return new FirebaseAuthResult(false, "Enter your email address and password.");
        }

        try
        {
            var config = await GetConfigurationAsync();
            var operation = createAccount ? "signUp" : "signInWithPassword";
            using var response = await http.PostAsJsonAsync(
                $"https://identitytoolkit.googleapis.com/v1/accounts:{operation}?key={Uri.EscapeDataString(config.ApiKey)}",
                new { email, password, returnSecureToken = true });

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return new FirebaseAuthResult(false, GetFirebaseError(body));
            }

            var token = JsonSerializer.Deserialize<SignInResponse>(body, FirebaseJsonOptions);
            if (token is null || string.IsNullOrWhiteSpace(token.IdToken) ||
                string.IsNullOrWhiteSpace(token.RefreshToken) ||
                !int.TryParse(token.ExpiresIn, out var seconds))
            {
                return new FirebaseAuthResult(false, "Firebase returned an invalid sign-in response.");
            }

            _session = new FirebaseSession(
                token.IdToken,
                token.RefreshToken,
                token.Email ?? email.Trim(),
                DateTimeOffset.UtcNow.AddSeconds(seconds));
            await SaveSessionAsync();
            Changed?.Invoke();
            return new FirebaseAuthResult(true, null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Firebase sign-in failed: {ex.Message}");
            _session = null;
            return new FirebaseAuthResult(false, "Could not contact Firebase authentication.");
        }
    }

    public async Task SignOutAsync()
    {
        _session = null;
        try
        {
            await js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not clear the Firebase session: {ex.Message}");
        }

        Changed?.Invoke();
    }

    private async Task<FirebaseClientConfiguration> GetConfigurationAsync()
    {
        if (_configuration is not null)
        {
            return _configuration;
        }

        var configUrl = new Uri(new Uri(apiBaseAddress), "/api/auth/config");
        _configuration = await http.GetFromJsonAsync<FirebaseClientConfiguration>(configUrl)
            ?? throw new InvalidOperationException("The API returned no Firebase configuration.");
        return _configuration;
    }

    private async Task RefreshTokenAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (_session is null)
            {
                return;
            }

            if (_session.ExpiresAt > DateTimeOffset.UtcNow + RefreshLeadTime)
            {
                return;
            }

            var config = await GetConfigurationAsync();
            using var response = await http.PostAsync(
                $"https://securetoken.googleapis.com/v1/token?key={Uri.EscapeDataString(config.ApiKey)}",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = _session.RefreshToken
                }));
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                await SignOutAsync();
                throw new InvalidOperationException($"Firebase token refresh failed: {GetFirebaseError(body)}");
            }

            var refreshed = JsonSerializer.Deserialize<RefreshResponse>(body, FirebaseJsonOptions);
            var idToken = refreshed?.IdToken ?? refreshed?.AccessToken;
            if (refreshed is null || string.IsNullOrWhiteSpace(idToken) ||
                string.IsNullOrWhiteSpace(refreshed.RefreshToken) ||
                !int.TryParse(refreshed.ExpiresIn, out var seconds))
            {
                throw new InvalidOperationException("Firebase returned an invalid token refresh response.");
            }

            _session = _session with
            {
                IdToken = idToken,
                RefreshToken = refreshed.RefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds)
            };
            await SaveSessionAsync();
            Changed?.Invoke();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task SaveSessionAsync() =>
        await js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(_session));

    private static string GetFirebaseError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var code = document.RootElement.GetProperty("error").GetProperty("message").GetString();
            return code switch
            {
                "EMAIL_EXISTS" => "An account with this email already exists.",
                "EMAIL_NOT_FOUND" or "INVALID_PASSWORD" or "INVALID_LOGIN_CREDENTIALS" =>
                    "The email or password is incorrect.",
                "WEAK_PASSWORD" => "Use a stronger password (at least 6 characters).",
                "INVALID_EMAIL" => "Enter a valid email address.",
                _ => $"Firebase authentication failed ({code ?? "unknown error"})."
            };
        }
        catch (JsonException)
        {
            return "Firebase authentication failed. Please try again.";
        }
    }

    private sealed record FirebaseClientConfiguration(string ApiKey, string ProjectId);
    private sealed record FirebaseSession(string IdToken, string RefreshToken, string Email, DateTimeOffset ExpiresAt);
    private sealed record SignInResponse(string? IdToken, string? RefreshToken, string? Email, string? ExpiresIn);
    private sealed record RefreshResponse(string? IdToken, string? AccessToken, string? RefreshToken, string? ExpiresIn);
}

public sealed class FirebaseBearerHandler(FirebaseAuthSession session) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await session.GetIdTokenAsync();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
