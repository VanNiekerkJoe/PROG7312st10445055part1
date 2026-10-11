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
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private bool _started;

    public event Action<DeviceStatus>? StatusUpdated;
    public event Action<string>? DeviceRemoved;
    public event Action? AllDevicesCleared;
    public event Action? ConnectionStateChanged;
    public event Action? Reconnected;

    public TelemetryHubClient(string hubUrl)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new SignalRReconnectPolicy())
            .Build();

        _connection.On<DeviceStatus>("DeviceStatusUpdated", status => StatusUpdated?.Invoke(status));
        _connection.On<string>("DeviceRemoved", deviceId => DeviceRemoved?.Invoke(deviceId));
        _connection.On("AllDevicesCleared", () => AllDevicesCleared?.Invoke());

        // Reconnects (e.g. after the dev server restarts, or a brief network
        // blip) can miss events that fired while disconnected. Re-syncing the
        // full device list on reconnect is what stops a client that missed a
        // "clear" or "remove" broadcast from being stuck showing ghost nodes
        // forever.
        _connection.Reconnecting += _ => { ConnectionStateChanged?.Invoke(); return Task.CompletedTask; };
        _connection.Reconnected += _ =>
        {
            ConnectionStateChanged?.Invoke();
            Reconnected?.Invoke();
            return Task.CompletedTask;
        };
        _connection.Closed += _ => { ConnectionStateChanged?.Invoke(); return Task.CompletedTask; };
    }

    public async Task EnsureStartedAsync()
    {
        // Several consumers (the page and the app-wide LiveStateService) can
        // call this at the same time; serialise them so StartAsync runs once.
        await _startLock.WaitAsync();
        try
        {
            if (_started) return;
            await _connection.StartAsync();
            _started = true;
            ConnectionStateChanged?.Invoke();
        }
        finally
        {
            _startLock.Release();
        }
    }

    public HubConnectionState State => _connection.State;

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
