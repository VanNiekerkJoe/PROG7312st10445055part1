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
    /// configured ancestor chain) before it's accepted.
    /// </summary>
    public ValidationResult RegisterDevice(string parentSubZoneId, string macAddress, SensorCategory category, string locationDescription)
    {
        lock (_treeLock)
        {
            var parent = FindNode(_root, parentSubZoneId);
            if (parent is null)
            {
                return new ValidationResult(false, [$"Sub-zone '{parentSubZoneId}' was not found."]);
            }

            var deviceId = $"{parentSubZoneId}-DEV{parent.Children.Count + 1}";
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

            var result = DeploymentTreeValidator.Validate(_root);
            if (!result.IsValid)
            {
                parent.Children.Remove(device);
                return result;
            }

            IndexTree(_root, currentZoneName: null);
            return result;
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
