using SmartX.Api.Services;
using SmartX.Core.Telemetry;
using Xunit;

namespace SmartX.Api.Tests;

/// <summary>
/// Covers the "clear all simulated nodes" fix: registering, removing, and
/// clearing devices must actually forget them server-side (tree + every
/// index), not just leave them recoverable via GET /api/sensors.
/// </summary>
public class SensorRegistryTests
{
    private static string FirstSubZoneId(SensorRegistry registry)
    {
        var subZone = FindFirstSubZone(registry.Root)
            ?? throw new InvalidOperationException("Seed data has no sub-zones.");
        return subZone.Id;

        static SmartX.Core.Devices.DeploymentNode? FindFirstSubZone(SmartX.Core.Devices.DeploymentNode node)
        {
            if (node.NodeType == SmartX.Core.Devices.DeploymentNodeType.SubZone) return node;
            foreach (var child in node.Children)
            {
                var found = FindFirstSubZone(child);
                if (found is not null) return found;
            }
            return null;
        }
    }

    [Fact]
    public void RegisterDevice_ReturnsRealDeviceId_NotTheMacAddress()
    {
        var registry = new SensorRegistry();
        var subZoneId = FirstSubZoneId(registry);

        var outcome = registry.RegisterDevice(subZoneId, "02:AA:BB:CC:DD:EE", SensorCategory.Environmental, "Test bench");

        Assert.True(outcome.IsValid);
        Assert.NotNull(outcome.Device);
        Assert.NotEqual("02:AA:BB:CC:DD:EE", outcome.Device!.DeviceId);
        Assert.StartsWith(subZoneId, outcome.Device.DeviceId);
        Assert.Contains(registry.AllStatuses, s => s.DeviceId == outcome.Device.DeviceId);
    }

    [Fact]
    public void RemoveDevice_ForgetsItEverywhere()
    {
        var registry = new SensorRegistry();
        var subZoneId = FirstSubZoneId(registry);
        var outcome = registry.RegisterDevice(subZoneId, "02:AA:BB:CC:DD:01", SensorCategory.Power, "Test bench");
        var deviceId = outcome.Device!.DeviceId;

        var removed = registry.RemoveDevice(deviceId);

        Assert.True(removed);
        Assert.DoesNotContain(registry.AllStatuses, s => s.DeviceId == deviceId);
        Assert.DoesNotContain(registry.DeviceNodes, n => n.Id == deviceId);
        // Recording a reading for a device that no longer exists must be a no-op,
        // not silently resurrect it in the status/index dictionaries.
        Assert.Null(registry.RecordReading(deviceId, 42f));
    }

    [Fact]
    public void RemoveDevice_UnknownId_ReturnsFalse()
    {
        var registry = new SensorRegistry();

        Assert.False(registry.RemoveDevice("does-not-exist"));
    }

    [Fact]
    public void ClearAllDevices_RemovesEveryDevice_ButKeepsSubZonesForReRegistration()
    {
        var registry = new SensorRegistry();
        var seededDeviceCount = registry.DeviceNodes.Count;
        Assert.True(seededDeviceCount > 0, "Seed data should include devices before clearing.");
        var subZoneId = FirstSubZoneId(registry);

        registry.ClearAllDevices();

        Assert.Empty(registry.DeviceNodes);
        Assert.Empty(registry.AllStatuses);

        // The sub-zone skeleton must survive the clear, otherwise the
        // registration form has nothing to register a new device under.
        var reRegistered = registry.RegisterDevice(subZoneId, "02:AA:BB:CC:DD:02", SensorCategory.Actuator, "Post-clear bench");
        Assert.True(reRegistered.IsValid);
    }

    [Fact]
    public void ClearAllDevices_ThenGetAllStatuses_DoesNotResurrectClearedDevices()
    {
        var registry = new SensorRegistry();
        var deviceIdsBeforeClear = registry.DeviceNodes.Select(n => n.Id).ToList();

        registry.ClearAllDevices();

        // Simulate what a page reload does: re-read the "current" state from
        // the same source the API's GET endpoint uses. None of the pre-clear
        // devices should still be reachable through it.
        var statusesAfterClear = registry.AllStatuses;
        foreach (var deviceId in deviceIdsBeforeClear)
        {
            Assert.DoesNotContain(statusesAfterClear, s => s.DeviceId == deviceId);
        }
    }
}
