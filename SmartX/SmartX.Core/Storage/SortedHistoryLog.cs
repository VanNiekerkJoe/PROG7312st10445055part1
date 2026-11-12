using SmartX.Core.Telemetry;

namespace SmartX.Core.Storage;

public sealed record HistoryPoint(DateTimeOffset Timestamp, double Value, AnomalySeverity Severity);

/// <summary>
/// Historical sensor logs. Part 3 upgrade: each device's history is now an
/// <see cref="AvlTree{TKey,TValue}"/> keyed by timestamp and arrival sequence instead of a SortedDictionary
/// (a red-black tree we could not look inside). The self-balancing tree keeps entries
/// ordered as they arrive, so:
///   - Append is O(log n), and so is evicting the oldest point once the cap is reached;
///   - Latest(n) walks the tree from the newest end, so it never sorts;
///   - Range(from, to) answers "all readings between 14:00 and 16:00 for this node"
///     in O(log n + k), touching only the nodes on the path to the window plus the
///     k matches, instead of scanning the whole history.
/// </summary>
public sealed class SortedHistoryLog(int capacityPerDevice = 5000)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, AvlTree<HistoryKey, HistoryPoint>> _byDevice = new(StringComparer.Ordinal);
    private long _sequence;

    private readonly record struct HistoryKey(DateTimeOffset Timestamp, long Sequence) : IComparable<HistoryKey>
    {
        public int CompareTo(HistoryKey other)
        {
            var timestampOrder = Timestamp.CompareTo(other.Timestamp);
            return timestampOrder != 0 ? timestampOrder : Sequence.CompareTo(other.Sequence);
        }
    }

    public void Append(string deviceId, double value, AnomalySeverity severity, DateTimeOffset? at = null)
    {
        lock (_lock)
        {
            if (!_byDevice.TryGetValue(deviceId, out var log))
            {
                log = new AvlTree<HistoryKey, HistoryPoint>();
                _byDevice[deviceId] = log;
            }

            var timestamp = at ?? DateTimeOffset.UtcNow;
            log.Set(new HistoryKey(timestamp, ++_sequence), new HistoryPoint(timestamp, value, severity));

            while (log.Count > capacityPerDevice && log.TryGetMin(out var oldest, out _))
            {
                log.Remove(oldest);
            }
        }
    }

    /// <summary>The most recent <paramref name="count"/> points, oldest first.</summary>
    public IReadOnlyList<HistoryPoint> Latest(string deviceId, int count)
    {
        lock (_lock)
        {
            return _byDevice.TryGetValue(deviceId, out var log)
                ? log.Last(Math.Max(1, count)).Select(pair => pair.Value).ToList()
                : [];
        }
    }

    /// <summary>Every point with from &lt;= timestamp &lt;= to, oldest first, plus the cost of the query.</summary>
    public (IReadOnlyList<HistoryPoint> Points, RangeStats Stats) Range(string deviceId, DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            if (!_byDevice.TryGetValue(deviceId, out var log))
            {
                return ([], new RangeStats(0, 0, 0, 0));
            }

            var (items, stats) = log.Range(
                new HistoryKey(from, long.MinValue),
                new HistoryKey(to, long.MaxValue));
            return (items.Select(pair => pair.Value).ToList(), stats);
        }
    }

    public int CountFor(string deviceId)
    {
        lock (_lock)
        {
            return _byDevice.TryGetValue(deviceId, out var log) ? log.Count : 0;
        }
    }

    public int HeightFor(string deviceId)
    {
        lock (_lock)
        {
            return _byDevice.TryGetValue(deviceId, out var log) ? log.Height : 0;
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
