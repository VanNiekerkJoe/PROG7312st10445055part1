using SmartX.Core.Commands;
using SmartX.Core.Ingestion;
using SmartX.Core.Telemetry;

namespace SmartX.Client.Services;

public sealed record FeedItem(DateTimeOffset At, string Text, bool IsPriority, AnomalySeverity? Severity);

/// <summary>
/// App-lifetime store for everything the Command Stream page shows. It is
/// registered as a singleton and subscribes to the SignalR hubs once, so
/// navigating between pages (which disposes the page component) never loses
/// device state, the command history, the live feed, or the operator's
/// search/filter selections, and never opens a second hub connection.
///
/// Change notifications are coalesced to at most one every 250 ms so a burst
/// of thousands of packets cannot make the UI thread thrash.
/// </summary>
public sealed class LiveStateService : IDisposable
{
    private const int MaxFeedItems = 60;

    private readonly TelemetryApiClient _telemetryApi;
    private readonly TelemetryHubClient _telemetryHub;
    private readonly CommandApiClient _commandApi;
    private readonly CommandHubClient _commandHub;

    private readonly Dictionary<string, DeviceStatus> _statuses = new();
    private readonly List<DeviceCommand> _commands = [];
    private readonly List<FeedItem> _feed = [];
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly Timer _flushTimer;

    private readonly List<double> _standardSeries = [];
    private readonly List<double> _prioritySeries = [];
    private readonly List<double> _rateSeries = [];
    private PipelineStats? _previousStats;

    private bool _started;
    private bool _dirty;

    public LiveStateService(
        TelemetryApiClient telemetryApi,
        TelemetryHubClient telemetryHub,
        CommandApiClient commandApi,
        CommandHubClient commandHub)
    {
        _telemetryApi = telemetryApi;
        _telemetryHub = telemetryHub;
        _commandApi = commandApi;
        _commandHub = commandHub;

        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    /// <summary>Raised (throttled) whenever any live state changed.</summary>
    public event Action? Changed;

    // ----- UI selections that must survive navigation -----
    public string SearchTerm { get; set; } = "";
    public string CategoryFilter { get; set; } = "";
    public string SeverityFilter { get; set; } = "";
    public string? HighlightZone { get; set; }
    public HashSet<AnomalySeverity> HiddenSeverities { get; } = [];
    public string? SelectedDeviceId { get; set; }

    public void ToggleHiddenSeverity(AnomalySeverity severity)
    {
        if (HiddenSeverities.Remove(severity))
        {
            return;
        }

        if (HiddenSeverities.Count < Enum.GetValues<AnomalySeverity>().Length - 1)
        {
            HiddenSeverities.Add(severity);
        }
    }

    public void ResetConstellationFilters()
    {
        HighlightZone = null;
        HiddenSeverities.Clear();
    }

    // ----- live data -----
    public IReadOnlyCollection<DeviceStatus> Statuses => _statuses.Values;
    public IReadOnlyList<DeviceCommand> Commands => _commands;
    public IReadOnlyList<FeedItem> Feed => _feed;
    public PipelineStats? Stats { get; private set; }
    public double ProcessedPerSecond { get; private set; }
    public IReadOnlyList<double> StandardSeries => _standardSeries;
    public IReadOnlyList<double> PrioritySeries => _prioritySeries;
    public IReadOnlyList<double> RateSeries => _rateSeries;
    public PriorityEvent? LastPriority { get; private set; }
    public DateTimeOffset LastPriorityReceivedAt { get; private set; } = DateTimeOffset.MinValue;
    public int LastPriorityBacklog { get; private set; }
    public int UndoDepth { get; private set; }

    public bool IsConnected =>
        _telemetryHub.State == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected
        && _commandHub.State == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected;

    public DeviceStatus? Find(string? deviceId) =>
        deviceId is not null && _statuses.TryGetValue(deviceId, out var status) ? status : null;

    /// <summary>Output state implied by the most recent command sent to the device, or null if none.</summary>
    public bool? OutputStateFor(string deviceId)
    {
        foreach (var command in _commands)
        {
            if (command.DeviceId == deviceId)
            {
                return command.Type.TurnsOutputOn();
            }
        }

        return null;
    }

    /// <summary>Idempotent: safe to call from the layout and from any page; retries if the API wasn't up yet.</summary>
    public async Task StartAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (_started)
            {
                return;
            }

            await ReloadAsync();

            _telemetryHub.StatusUpdated += OnStatusUpdated;
            _telemetryHub.DeviceRemoved += OnDeviceRemoved;
            _telemetryHub.AllDevicesCleared += OnAllDevicesCleared;
            _telemetryHub.Reconnected += OnReconnected;
            _telemetryHub.ConnectionStateChanged += MarkDirty;

            _commandHub.CommandIssued += OnCommandIssued;
            _commandHub.CommandUndone += OnCommandUndone;
            _commandHub.PriorityAlert += OnPriorityAlert;
            _commandHub.StatsUpdated += OnStatsUpdated;
            _commandHub.Reconnected += OnReconnected;
            _commandHub.ConnectionStateChanged += MarkDirty;

            await _telemetryHub.EnsureStartedAsync();
            await _commandHub.EnsureStartedAsync();

            _started = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LiveStateService start failed (will retry on next call): {ex.Message}");
            UnsubscribeAll();
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task ReloadAsync()
    {
        var statuses = await _telemetryApi.GetAllStatusesAsync();
        _statuses.Clear();
        foreach (var status in statuses)
        {
            _statuses[status.DeviceId] = status;
        }

        var log = await _commandApi.GetCommandLogAsync();
        if (log is not null)
        {
            _commands.Clear();
            _commands.AddRange(log.Commands);
            UndoDepth = log.UndoDepth;
        }

        MarkDirty();
    }

    private void OnStatusUpdated(DeviceStatus status)
    {
        _statuses[status.DeviceId] = status;
        AddFeed(new FeedItem(
            status.LastSeen,
            $"{status.DeviceId}  {status.LastValue:0.##}  z={status.ZScore:0.0}",
            false,
            status.Severity));
        MarkDirty();
    }

    private void OnDeviceRemoved(string deviceId)
    {
        _statuses.Remove(deviceId);
        if (SelectedDeviceId == deviceId)
        {
            SelectedDeviceId = null;
        }

        MarkDirty();
    }

    private void OnAllDevicesCleared()
    {
        _statuses.Clear();
        SelectedDeviceId = null;
        MarkDirty();
    }

    private void OnCommandIssued(DeviceCommand command)
    {
        _commands.Insert(0, command);
        UndoDepth++;
        MarkDirty();
    }

    private void OnCommandUndone(DeviceCommand command)
    {
        _commands.Insert(0, command);
        UndoDepth = Math.Max(0, UndoDepth - 1);
        MarkDirty();
    }

    private void OnPriorityAlert(PriorityEvent alert)
    {
        LastPriority = alert;
        LastPriorityReceivedAt = DateTimeOffset.UtcNow;
        LastPriorityBacklog = Stats?.StandardDepth ?? 0;

        AddFeed(new FeedItem(
            alert.At,
            $"PRIORITY  {alert.DeviceId}  {alert.Value:0.##}  {alert.Reason}",
            true,
            alert.Severity));
        MarkDirty();
    }

    private void OnStatsUpdated(PipelineStats stats)
    {
        if (_previousStats is not null)
        {
            var seconds = (stats.At - _previousStats.At).TotalSeconds;
            if (seconds > 0)
            {
                ProcessedPerSecond = Math.Max(0, (stats.ProcessedTotal - _previousStats.ProcessedTotal) / seconds);
            }
        }

        _previousStats = stats;
        Stats = stats;

        Push(_standardSeries, stats.StandardDepth);
        Push(_prioritySeries, stats.PriorityDepth);
        Push(_rateSeries, ProcessedPerSecond);
        MarkDirty();
    }

    private static void Push(List<double> series, double value)
    {
        series.Add(value);
        if (series.Count > 40)
        {
            series.RemoveAt(0);
        }
    }

    // A reconnect can miss events (e.g. a clear or a remove); re-sync everything.
    private void OnReconnected()
    {
        _ = ReloadAsync();
    }

    private void AddFeed(FeedItem item)
    {
        _feed.Insert(0, item);
        if (_feed.Count > MaxFeedItems)
        {
            _feed.RemoveAt(_feed.Count - 1);
        }
    }

    private void MarkDirty() => _dirty = true;

    private void Flush()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        Changed?.Invoke();
    }

    private void UnsubscribeAll()
    {
        _telemetryHub.StatusUpdated -= OnStatusUpdated;
        _telemetryHub.DeviceRemoved -= OnDeviceRemoved;
        _telemetryHub.AllDevicesCleared -= OnAllDevicesCleared;
        _telemetryHub.Reconnected -= OnReconnected;
        _telemetryHub.ConnectionStateChanged -= MarkDirty;

        _commandHub.CommandIssued -= OnCommandIssued;
        _commandHub.CommandUndone -= OnCommandUndone;
        _commandHub.PriorityAlert -= OnPriorityAlert;
        _commandHub.StatsUpdated -= OnStatsUpdated;
        _commandHub.Reconnected -= OnReconnected;
        _commandHub.ConnectionStateChanged -= MarkDirty;
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        UnsubscribeAll();
    }
}
