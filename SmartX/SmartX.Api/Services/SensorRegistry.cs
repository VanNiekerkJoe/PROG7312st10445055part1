using System.Collections.Concurrent;
using SmartX.Core.Batching;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// The single in-memory source of truth for Part 1: the deployment tree,
/// per-device rolling baselines, per-device jagged-array batchers, and the
/// latest DeviceStatus snapshot the dashboard renders from. A real system
/// would back this with a database; for this PoE it's deliberately in-memory
/// so the seeder can run without any external dependency.
/// </summary>
public sealed class SensorRegistry
{
    private readonly object _treeLock = new();
    private DeploymentNode _root;

    private readonly ConcurrentDictionary<string, DeploymentNode> _deviceNodesById = new();
    private readonly ConcurrentDictionary<string, string> _zoneNameByDeviceId = new();
    private readonly ConcurrentDictionary<string, SensorBaseline> _baselines = new();
    private readonly ConcurrentDictionary<string, DeviceStatus> _latestStatus = new();
    private readonly ConcurrentDictionary<string, TelemetryBatcher<float>> _batchers = new();
    private readonly ConcurrentDictionary<string, int> _throughputCounters = new();

    // Monotonic counter used to mint device IDs. Deliberately never reset by
    // ClearAllDevices/RemoveDevice, so a freshly cleared registry can never
    // hand out an ID that a still-connected client might be holding onto
    // from before the clear (which is exactly how "ghost" nodes reappear).
    private int _deviceSequence;

    public SensorRegistry()
    {
        _root = SeedData.BuildDefaultTree();
        IndexTree(_root, currentZoneName: null);
    }

    public DeploymentNode Root
    {
        get { lock (_treeLock) { return _root; } }
    }

    public ValidationResult ValidateTree()
    {
        lock (_treeLock)
        {
            return DeploymentTreeValidator.Validate(_root);
        }
    }

    public IReadOnlyCollection<DeploymentNode> DeviceNodes => _deviceNodesById.Values.ToList();

    public IReadOnlyCollection<DeviceStatus> AllStatuses => _latestStatus.Values.ToList();

    /// <summary>
    /// Registers a new simulated/physical device under an existing sub-zone
    /// and re-validates the whole tree (uniqueness of MAC addresses, fully
    /// configured ancestor chain) before it's accepted. On success also seeds
    /// an initial DeviceStatus snapshot (zero reading, Healthy) so the device
    /// shows up immediately in GetAllStatuses/AllStatuses without waiting for
    /// the next seeder tick, and so the caller gets back the *real* device ID
    /// (never the MAC address) to key its own state on.
    /// </summary>
    public DeviceRegistrationOutcome RegisterDevice(string parentSubZoneId, string macAddress, SensorCategory category, string locationDescription)
    {
        lock (_treeLock)
        {
            var parent = FindNode(_root, parentSubZoneId);
            if (parent is null)
            {
                return new DeviceRegistrationOutcome(false, [$"Sub-zone '{parentSubZoneId}' was not found."], null);
            }

            // Already inside the _treeLock, so a plain increment (no
            // Interlocked needed) is safe here.
            var deviceId = $"{parentSubZoneId}-DEV{++_deviceSequence}";
            var device = new DeploymentNode
            {
                Id = deviceId,
                Name = $"{category}-{deviceId}",
                NodeType = DeploymentNodeType.Device,
                Sensor = new SensorProfile
                {
                    MacAddress = macAddress,
                    Category = category,
                    LocationDescription = locationDescription
                }
            };

            parent.Children.Add(device);

            var validation = DeploymentTreeValidator.Validate(_root);
            if (!validation.IsValid)
            {
                parent.Children.Remove(device);
                return new DeviceRegistrationOutcome(false, validation.Errors, null);
            }

            IndexTree(_root, currentZoneName: null);

            var status = new DeviceStatus
            {
                DeviceId = deviceId,
                ZoneName = _zoneNameByDeviceId.GetValueOrDefault(deviceId, "Unknown"),
                Category = category,
                LastValue = 0,
                ZScore = 0,
                Severity = AnomalySeverity.Healthy,
                LastSeen = DateTimeOffset.UtcNow,
                ThroughputLastMinute = 0
            };
            _latestStatus[deviceId] = status;

            return new DeviceRegistrationOutcome(true, [], status);
        }
    }

    public bool AddAttachment(string deviceId, string fileName)
    {
        if (!_deviceNodesById.TryGetValue(deviceId, out var node) || node.Sensor is null)
        {
            return false;
        }

        node.Sensor.Attachments.Add(fileName);
        return true;
    }

    /// <summary>
    /// Removes a single device (by ID) from the deployment tree and purges
    /// every dictionary that keys off it (index, baseline, latest status,
    /// batcher, throughput counter). Without clearing all of these, a
    /// "removed" device would silently reappear the moment anything read
    /// from the surviving dictionary entries or the seeder's next tick.
    /// </summary>
    public bool RemoveDevice(string deviceId)
    {
        lock (_treeLock)
        {
            if (!RemoveDeviceRecursive(_root, deviceId))
            {
                return false;
            }

            _deviceNodesById.TryRemove(deviceId, out _);
            _zoneNameByDeviceId.TryRemove(deviceId, out _);
            _baselines.TryRemove(deviceId, out _);
            _latestStatus.TryRemove(deviceId, out _);
            _batchers.TryRemove(deviceId, out _);
            _throughputCounters.TryRemove(deviceId, out _);

            return true;
        }
    }

    /// <summary>
    /// Removes every simulated/registered Device node from the tree (keeping
    /// the Facility/Zone/Sub-Zone skeleton intact so the registration form's
    /// sub-zone list still works) and purges every device-keyed dictionary.
    /// This is the server-side half of "clear all"; without it the client
    /// could only ever hide devices locally; the API would keep reporting
    /// them, and the very next SignalR tick (or page reload/reconnect) would
    /// bring every "cleared" node straight back.
    /// </summary>
    public void ClearAllDevices()
    {
        lock (_treeLock)
        {
            StripDeviceChildren(_root);

            _deviceNodesById.Clear();
            _zoneNameByDeviceId.Clear();
            _baselines.Clear();
            _latestStatus.Clear();
            _batchers.Clear();
            _throughputCounters.Clear();
        }
    }

    private static bool RemoveDeviceRecursive(DeploymentNode node, string deviceId)
    {
        var match = node.Children.FirstOrDefault(c => c.NodeType == DeploymentNodeType.Device && c.Id == deviceId);
        if (match is not null)
        {
            node.Children.Remove(match);
            return true;
        }

        foreach (var child in node.Children)
        {
            if (RemoveDeviceRecursive(child, deviceId))
            {
                return true;
            }
        }

        return false;
    }

    private static void StripDeviceChildren(DeploymentNode node)
    {
        node.Children.RemoveAll(c => c.NodeType == DeploymentNodeType.Device);
        foreach (var child in node.Children)
        {
            StripDeviceChildren(child);
        }
    }

    /// <summary>
    /// Records one raw reading for a device: pushes it through that device's
    /// jagged-array batcher, updates its rolling baseline, and produces the
    /// DeviceStatus snapshot the constellation renders next.
    /// </summary>
    public DeviceStatus? RecordReading(string deviceId, float value, bool simulateDisconnect = false)
    {
        if (!_deviceNodesById.TryGetValue(deviceId, out var node) || node.Sensor is null)
        {
            return null;
        }

        var batcher = _batchers.GetOrAdd(deviceId, _ => new TelemetryBatcher<float>(deviceId, node.Sensor.Category, windowSize: 5));
        batcher.Add(value);
        if (batcher.CompletedWindowCount > 0)
        {
            _ = batcher.FlushToPacketList(); // downstream storage/export hook point
        }

        var baseline = _baselines.GetOrAdd(deviceId, _ => new SensorBaseline());
        var zScore = simulateDisconnect ? 0 : baseline.Observe(value);

        _throughputCounters.AddOrUpdate(deviceId, 1, (_, count) => count + 1);

        var severity = simulateDisconnect
            ? AnomalySeverity.Disconnected
            : SensorBaseline.Classify(zScore);

        var status = new DeviceStatus
        {
            DeviceId = deviceId,
            ZoneName = _zoneNameByDeviceId.GetValueOrDefault(deviceId, "Unknown"),
            Category = node.Sensor.Category,
            LastValue = value,
            ZScore = Math.Round(zScore, 2),
            Severity = severity,
            LastSeen = DateTimeOffset.UtcNow,
            ThroughputLastMinute = _throughputCounters.GetValueOrDefault(deviceId, 0)
        };

        _latestStatus[deviceId] = status;
        return status;
    }

    public void ResetThroughputWindow() => _throughputCounters.Clear();

    private static DeploymentNode? FindNode(DeploymentNode node, string id)
    {
        if (node.Id == id) return node;
        foreach (var child in node.Children)
        {
            var found = FindNode(child, id);
            if (found is not null) return found;
        }
        return null;
    }

    private void IndexTree(DeploymentNode node, string? currentZoneName)
    {
        var zoneName = node.NodeType == DeploymentNodeType.Zone ? node.Name : currentZoneName;

        if (node.NodeType == DeploymentNodeType.Device)
        {
            _deviceNodesById[node.Id] = node;
            _zoneNameByDeviceId[node.Id] = zoneName ?? "Unknown";
        }

        foreach (var child in node.Children)
        {
            IndexTree(child, zoneName);
        }
    }
}

/// <summary>
/// Result of a device registration attempt. Carries the freshly minted
/// DeviceStatus on success so callers (the API endpoint, and in turn the
/// client) never have to guess or reconstruct the server-assigned device ID
/// themselves - guessing wrong (e.g. keying local state on the MAC address
/// instead of the real "{subZone}-DEV{n}" ID) is what let stale/duplicate
/// nodes creep into the dashboard in the first place.
/// </summary>
public sealed record DeviceRegistrationOutcome(bool IsValid, IReadOnlyList<string> Errors, DeviceStatus? Device);
