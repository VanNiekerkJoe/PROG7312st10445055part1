using Microsoft.AspNetCore.SignalR.Client;
using SmartX.Core.Commands;
using SmartX.Core.Ingestion;

namespace SmartX.Client.Services;

/// <summary>Wraps the SignalR connection to CommandHub and exposes plain C# events.</summary>
public sealed class CommandHubClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private bool _started;

    public event Action<DeviceCommand>? CommandIssued;
    public event Action<DeviceCommand>? CommandUndone;
    public event Action<PriorityEvent>? PriorityAlert;
    public event Action<PipelineStats>? StatsUpdated;
    public event Action? ConnectionStateChanged;
    public event Action? Reconnected;

    public CommandHubClient(string hubUrl)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new SignalRReconnectPolicy())
            .Build();

        _connection.On<DeviceCommand>("CommandIssued", command => CommandIssued?.Invoke(command));
        _connection.On<DeviceCommand>("CommandUndone", command => CommandUndone?.Invoke(command));
        _connection.On<PriorityEvent>("PriorityAlert", alert => PriorityAlert?.Invoke(alert));
        _connection.On<PipelineStats>("PipelineStats", stats => StatsUpdated?.Invoke(stats));

        _connection.Reconnecting += _ => { ConnectionStateChanged?.Invoke(); return Task.CompletedTask; };
        _connection.Reconnected += _ =>
        {
            ConnectionStateChanged?.Invoke();
            Reconnected?.Invoke();
            return Task.CompletedTask;
        };
        _connection.Closed += _ => { ConnectionStateChanged?.Invoke(); return Task.CompletedTask; };
    }

    public HubConnectionState State => _connection.State;

    public async Task EnsureStartedAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (_started)
            {
                return;
            }

            await _connection.StartAsync();
            _started = true;
            ConnectionStateChanged?.Invoke();
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
