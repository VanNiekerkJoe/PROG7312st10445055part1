namespace SmartX.Core.Storage;

public enum AlertKind
{
    Disconnected,
    Anomalous
}

/// <summary>
/// HashSet&lt;T&gt; of the alerts that are currently active (one entry per
/// device + alert kind). Because a set only stores unique elements,
/// <see cref="TryRaise"/> naturally refuses a second "disconnected" alert for
/// a sensor that is already flagged, so duplicate alerts are never processed.
/// The number of refused duplicates is counted so the dashboard can show it.
/// </summary>
public sealed class ActiveAlertSet
{
    private readonly object _lock = new();
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private long _suppressed;

    public int ActiveCount
    {
        get { lock (_lock) { return _active.Count; } }
    }

    public long SuppressedCount
    {
        get { lock (_lock) { return _suppressed; } }
    }

    /// <summary>True when the alert is new; false when it was already active (duplicate suppressed).</summary>
    public bool TryRaise(string deviceId, AlertKind kind)
    {
        lock (_lock)
        {
            if (_active.Add(KeyFor(deviceId, kind)))
            {
                return true;
            }

            _suppressed++;
            return false;
        }
    }

    public bool Clear(string deviceId, AlertKind kind)
    {
        lock (_lock)
        {
            return _active.Remove(KeyFor(deviceId, kind));
        }
    }

    public bool IsActive(string deviceId, AlertKind kind)
    {
        lock (_lock)
        {
            return _active.Contains(KeyFor(deviceId, kind));
        }
    }

    public void RetainOnly(IReadOnlySet<string> deviceIds)
    {
        lock (_lock)
        {
            _active.RemoveWhere(key => !deviceIds.Contains(key[..key.LastIndexOf('|')]));
        }
    }

    private static string KeyFor(string deviceId, AlertKind kind) => $"{deviceId}|{kind}";
}
