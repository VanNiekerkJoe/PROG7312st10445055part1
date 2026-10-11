using System.Diagnostics.CodeAnalysis;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Storage;

/// <summary>Live state of one registered device, as the ingestion pipeline sees it.</summary>
public sealed record DeviceState(
    string DeviceId,
    string MacAddress,
    SensorCategory Category,
    DateTimeOffset LastSeen,
    double LastValue,
    bool OutputOn);

/// <summary>
/// Dictionary&lt;TKey, TValue&gt; of every registered device, keyed by MAC
/// address. When a telemetry packet arrives the pipeline resolves the sender
/// with a single hash lookup, O(1) on average, instead of scanning the
/// deployment tree. A second dictionary maps device ID back to MAC so command
/// handling is O(1) as well.
/// </summary>
public sealed class DeviceStateStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DeviceState> _byMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _macByDeviceId = new(StringComparer.Ordinal);

    public int Count
    {
        get { lock (_lock) { return _byMac.Count; } }
    }

    public bool TryGetByMac(string macAddress, [NotNullWhen(true)] out DeviceState? state)
    {
        lock (_lock)
        {
            return _byMac.TryGetValue(macAddress, out state);
        }
    }

    public bool TryGetByDeviceId(string deviceId, [NotNullWhen(true)] out DeviceState? state)
    {
        lock (_lock)
        {
            if (_macByDeviceId.TryGetValue(deviceId, out var mac))
            {
                return _byMac.TryGetValue(mac, out state);
            }

            state = null;
            return false;
        }
    }

    /// <summary>
    /// Brings the store in line with the deployment tree: new devices are
    /// added, devices that no longer exist are dropped, and existing devices
    /// keep their live state (last value, last seen, output).
    /// </summary>
    public void Sync(IEnumerable<DeploymentNode> deviceNodes)
    {
        lock (_lock)
        {
            var liveIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var node in deviceNodes)
            {
                if (node.Sensor is null)
                {
                    continue;
                }

                liveIds.Add(node.Id);

                if (_macByDeviceId.TryGetValue(node.Id, out var knownMac)
                    && string.Equals(knownMac, node.Sensor.MacAddress, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _macByDeviceId[node.Id] = node.Sensor.MacAddress;
                _byMac[node.Sensor.MacAddress] = new DeviceState(
                    node.Id, node.Sensor.MacAddress, node.Sensor.Category, DateTimeOffset.UtcNow, 0, false);
            }

            foreach (var staleId in _macByDeviceId.Keys.Where(id => !liveIds.Contains(id)).ToList())
            {
                var mac = _macByDeviceId[staleId];
                _macByDeviceId.Remove(staleId);
                _byMac.Remove(mac);
            }
        }
    }

    public void Touch(string macAddress, double value, DateTimeOffset seenAt)
    {
        lock (_lock)
        {
            if (_byMac.TryGetValue(macAddress, out var state))
            {
                _byMac[macAddress] = state with { LastSeen = seenAt, LastValue = value };
            }
        }
    }

    public bool SetOutput(string deviceId, bool outputOn)
    {
        lock (_lock)
        {
            if (_macByDeviceId.TryGetValue(deviceId, out var mac) && _byMac.TryGetValue(mac, out var state))
            {
                _byMac[mac] = state with { OutputOn = outputOn };
                return true;
            }

            return false;
        }
    }

    public IReadOnlyList<DeviceState> Snapshot()
    {
        lock (_lock)
        {
            return _byMac.Values.ToList();
        }
    }

    public IReadOnlySet<string> DeviceIds()
    {
        lock (_lock)
        {
            return new HashSet<string>(_macByDeviceId.Keys, StringComparer.Ordinal);
        }
    }
}
