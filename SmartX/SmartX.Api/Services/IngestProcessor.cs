using Microsoft.AspNetCore.SignalR;
using SmartX.Api.Hubs;
using SmartX.Core.Ingestion;
using SmartX.Core.Insights;
using SmartX.Core.Storage;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Drains the ingestion queues in the background. Each loop it processes a
/// bounded batch (modelling a resource-constrained gateway): critical packets
/// first, then the FIFO lane. Per packet it resolves the device by MAC
/// (Dictionary), updates the registry baseline, appends to the sorted history,
/// raises/clears alerts through the HashSet, and feeds the pattern engine.
/// It also sweeps for devices that have gone silent.
/// </summary>
public sealed class IngestProcessor(
    TelemetryIngestQueue queue,
    SensorRegistry registry,
    DeviceStateStore devices,
    SortedHistoryLog history,
    ActiveAlertSet alerts,
    ActionPatternEngine patterns,
    IHubContext<TelemetryHub> telemetryHub,
    IHubContext<CommandHub> commandHub,
    ILogger<IngestProcessor> logger) : BackgroundService
{
    /// <summary>Packets handled per 100 ms tick (about 500 packets/second).</summary>
    private const int MaxPerTick = 50;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SyncEvery = TimeSpan.FromSeconds(1);

    private long _processed;
    private long _priorityProcessed;

    public PipelineStats Snapshot() => new(
        queue.StandardDepth,
        queue.PriorityDepth,
        Interlocked.Read(ref _processed),
        Interlocked.Read(ref _priorityProcessed),
        alerts.ActiveCount,
        alerts.SuppressedCount,
        patterns.LearnedPatternCount,
        DateTimeOffset.UtcNow);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastSweep = DateTimeOffset.UtcNow;
        var lastSync = DateTimeOffset.MinValue;
        var lastStats = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;

                if (now - lastSync >= SyncEvery)
                {
                    devices.Sync(registry.DeviceNodes);
                    var liveIds = devices.DeviceIds();
                    history.RetainOnly(liveIds);
                    alerts.RetainOnly(liveIds);
                    patterns.RetainOnly(liveIds);
                    lastSync = now;
                }

                // Latest status per device within this tick, so a burst of
                // packets for one device becomes one broadcast, not hundreds.
                var latest = new Dictionary<string, DeviceStatus>(StringComparer.Ordinal);
                var handled = 0;
                while (handled < MaxPerTick && queue.TryDequeue(out var item))
                {
                    var status = await ProcessAsync(item, stoppingToken);
                    if (status is not null)
                    {
                        latest[status.DeviceId] = status;
                    }

                    handled++;
                }

                foreach (var status in latest.Values)
                {
                    await telemetryHub.Clients.All.SendAsync("DeviceStatusUpdated", status, stoppingToken);
                }

                if (now - lastSweep >= SweepEvery)
                {
                    await SweepStaleDevicesAsync(now, stoppingToken);
                    lastSweep = now;
                }

                if (now - lastStats >= TimeSpan.FromSeconds(1))
                {
                    await commandHub.Clients.All.SendAsync("PipelineStats", Snapshot(), stoppingToken);
                    lastStats = now;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ingest processor tick failed.");
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
        }
    }

    private async Task<DeviceStatus?> ProcessAsync(IngestItem item, CancellationToken ct)
    {
        if (!devices.TryGetByMac(item.MacAddress, out var state))
        {
            devices.Sync(registry.DeviceNodes);
            if (!devices.TryGetByMac(item.MacAddress, out state))
            {
                logger.LogWarning("Dropped packet from unknown MAC {Mac}.", item.MacAddress);
                return null;
            }
        }

        var status = registry.RecordReading(state.DeviceId, (float)item.Value, item.Disconnected);
        if (status is null)
        {
            return null; // device was removed while the packet was queued
        }

        if (!item.Disconnected)
        {
            devices.Touch(state.MacAddress, item.Value, item.ReceivedAt);
        }

        history.Append(status.DeviceId, item.Value, status.Severity, item.ReceivedAt);
        UpdateAlerts(status);

        Interlocked.Increment(ref _processed);

        if (item.IsCritical)
        {
            Interlocked.Increment(ref _priorityProcessed);
            await commandHub.Clients.All.SendAsync(
                "PriorityAlert",
                new PriorityEvent(status.DeviceId, item.Value, status.Severity, item.Reason, DateTimeOffset.UtcNow),
                ct);
        }

        return status;
    }

    private void UpdateAlerts(DeviceStatus status)
    {
        var now = DateTimeOffset.UtcNow;

        if (status.Severity == AnomalySeverity.Disconnected)
        {
            if (alerts.TryRaise(status.DeviceId, AlertKind.Disconnected))
            {
                patterns.ObserveAlert(status.DeviceId, status.Category, status.Severity, now);
            }
        }
        else
        {
            alerts.Clear(status.DeviceId, AlertKind.Disconnected);
        }

        if (status.Severity == AnomalySeverity.Anomalous)
        {
            if (alerts.TryRaise(status.DeviceId, AlertKind.Anomalous))
            {
                patterns.ObserveAlert(status.DeviceId, status.Category, status.Severity, now);
            }
        }
        else
        {
            alerts.Clear(status.DeviceId, AlertKind.Anomalous);
        }
    }

    private async Task SweepStaleDevicesAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var state in devices.Snapshot())
        {
            if (now - state.LastSeen < StaleAfter)
            {
                continue;
            }

            // The set refuses the alert if this device is already flagged, so
            // a device that stays silent is reported once, not every sweep.
            if (!alerts.TryRaise(state.DeviceId, AlertKind.Disconnected))
            {
                continue;
            }

            var status = registry.RecordReading(state.DeviceId, (float)state.LastValue, simulateDisconnect: true);
            if (status is null)
            {
                continue;
            }

            history.Append(status.DeviceId, state.LastValue, status.Severity, now);
            patterns.ObserveAlert(status.DeviceId, status.Category, status.Severity, now);

            await telemetryHub.Clients.All.SendAsync("DeviceStatusUpdated", status, ct);
            await commandHub.Clients.All.SendAsync(
                "PriorityAlert",
                new PriorityEvent(status.DeviceId, state.LastValue, status.Severity, "No packets received for 20 s", now),
                ct);
        }
    }
}
