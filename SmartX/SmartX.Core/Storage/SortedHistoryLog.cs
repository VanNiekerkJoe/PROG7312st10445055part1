using SmartX.Core.Telemetry;

namespace SmartX.Core.Storage;

public sealed record HistoryPoint(DateTimeOffset Timestamp, double Value, AnomalySeverity Severity);

/// <summary>
/// Historical sensor logs kept in a SortedDictionary&lt;TKey, TValue&gt; keyed
/// by timestamp (one per device). The tree keeps entries ordered as they are
/// inserted, so the dashboard can read "the last N points, oldest first"
/// straight off the structure and render a timeline graph without ever
/// sorting at query time. Insert / remove are O(log n). Latest() walks values
/// to skip older points (O(n)); this is bounded by the 300-point per-device
/// capacity. A SortedList would enable indexed reads but make inserts/removes
/// O(n), so the tree is preferred for the current write-and-cap workload.
/// </summary>
public sealed class SortedHistoryLog(int capacityPerDevice = 300)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, SortedDictionary<DateTimeOffset, HistoryPoint>> _byDevice = new(StringComparer.Ordinal);

    public void Append(string deviceId, double value, AnomalySeverity severity, DateTimeOffset? at = null)
    {
        lock (_lock)
        {
            if (!_byDevice.TryGetValue(deviceId, out var log))
            {
                log = new SortedDictionary<DateTimeOffset, HistoryPoint>();
                _byDevice[deviceId] = log;
            }

            var timestamp = at ?? DateTimeOffset.UtcNow;
            while (log.ContainsKey(timestamp))
            {
                timestamp = timestamp.AddTicks(1); // keys must be unique
            }

            log[timestamp] = new HistoryPoint(timestamp, value, severity);

            while (log.Count > capacityPerDevice)
            {
                log.Remove(log.Keys.First());
            }
        }
    }

    /// <summary>The most recent <paramref name="count"/> points, oldest first.</summary>
    public IReadOnlyList<HistoryPoint> Latest(string deviceId, int count)
    {
        lock (_lock)
        {
            if (!_byDevice.TryGetValue(deviceId, out var log))
            {
                return [];
            }

            var skip = Math.Max(0, log.Count - Math.Max(1, count));
            return log.Values.Skip(skip).ToList();
        }
    }

    public int CountFor(string deviceId)
    {
        lock (_lock)
        {
            return _byDevice.TryGetValue(deviceId, out var log) ? log.Count : 0;
        }
    }

    public void RetainOnly(IReadOnlySet<string> deviceIds)
    {
        lock (_lock)
        {
            foreach (var id in _byDevice.Keys.Where(id => !deviceIds.Contains(id)).ToList())
            {
                _byDevice.Remove(id);
            }
        }
    }
}
