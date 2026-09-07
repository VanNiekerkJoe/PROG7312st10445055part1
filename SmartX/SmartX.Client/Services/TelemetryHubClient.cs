using Microsoft.AspNetCore.SignalR.Client;
using SmartX.Core.Telemetry;

namespace SmartX.Client.Services;

/// <summary>
/// Wraps the SignalR connection to TelemetryHub and exposes plain C# events
/// so Razor components don't need to know anything about SignalR directly.
/// </summary>
public sealed class TelemetryHubClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private bool _started;

    public event Action<DeviceStatus>? StatusUpdated;
    public event Action? ConnectionStateChanged;

    public TelemetryHubClient(string hubUrl)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _connection.On<DeviceStatus>("DeviceStatusUpdated", status => StatusUpdated?.Invoke(status));

        _connection.Reconnecting += _ => { ConnectionStateChanged?.Invoke(); return Task.CompletedTask; };
        _connection.Reconnected += _ => { ConnectionStateChanged?.Invoke(); return Task.CompletedTask; };
        _connection.Closed += _ => { ConnectionStateChanged?.Invoke(); return Task.CompletedTask; };
    }

    public async Task EnsureStartedAsync()
    {
        if (_started) return;
        await _connection.StartAsync();
        _started = true;
        ConnectionStateChanged?.Invoke();
    }

    public HubConnectionState State => _connection.State;

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
