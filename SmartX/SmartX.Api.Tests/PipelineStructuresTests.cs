using SmartX.Core.Devices;
using SmartX.Core.Storage;
using SmartX.Core.Telemetry;
using Xunit;

namespace SmartX.Api.Tests;

public class PipelineStructuresTests
{
    private static DeploymentNode Device(string id, string mac, SensorCategory category = SensorCategory.Power) => new()
    {
        Id = id,
        Name = id,
        NodeType = DeploymentNodeType.Device,
        Sensor = new SensorProfile { MacAddress = mac, Category = category, LocationDescription = "test" }
    };

    [Fact]
    public void DeviceStateStore_ResolvesByMac_CaseInsensitive()
    {
        var store = new DeviceStateStore();
        store.Sync([Device("D1", "AA:BB:CC:DD:EE:01")]);

        Assert.True(store.TryGetByMac("aa:bb:cc:dd:ee:01", out var state));
        Assert.Equal("D1", state!.DeviceId);
        Assert.True(store.TryGetByDeviceId("D1", out _));
        Assert.False(store.TryGetByMac("00:00:00:00:00:00", out _));
    }

    [Fact]
    public void DeviceStateStore_Sync_KeepsLiveStateAndDropsRemovedDevices()
    {
        var store = new DeviceStateStore();
        store.Sync([Device("D1", "MAC1"), Device("D2", "MAC2")]);
        store.Touch("MAC1", 42, DateTimeOffset.UtcNow);
        store.SetOutput("D1", true);

        store.Sync([Device("D1", "MAC1")]);

        Assert.Equal(1, store.Count);
        Assert.True(store.TryGetByMac("MAC1", out var state));
        Assert.Equal(42, state!.LastValue);
        Assert.True(state.OutputOn);
        Assert.False(store.TryGetByMac("MAC2", out _));
    }

    [Fact]
    public void ActiveAlertSet_SuppressesDuplicates_UntilCleared()
    {
        var alerts = new ActiveAlertSet();

        Assert.True(alerts.TryRaise("D1", AlertKind.Disconnected));
        Assert.False(alerts.TryRaise("D1", AlertKind.Disconnected));
        Assert.False(alerts.TryRaise("D1", AlertKind.Disconnected));
        Assert.Equal(2, alerts.SuppressedCount);
        Assert.Equal(1, alerts.ActiveCount);

        Assert.True(alerts.Clear("D1", AlertKind.Disconnected));
        Assert.True(alerts.TryRaise("D1", AlertKind.Disconnected));
    }

    [Fact]
    public void ActiveAlertSet_RetainOnly_DropsAlertsForRemovedDevices()
    {
        var alerts = new ActiveAlertSet();
        alerts.TryRaise("D1", AlertKind.Anomalous);
        alerts.TryRaise("D2", AlertKind.Disconnected);

        alerts.RetainOnly(new HashSet<string> { "D1" });

        Assert.Equal(1, alerts.ActiveCount);
        Assert.True(alerts.IsActive("D1", AlertKind.Anomalous));
    }

    [Fact]
    public void SortedHistoryLog_ReturnsLatestPoints_OldestFirst_AndRespectsCapacity()
    {
        var log = new SortedHistoryLog(capacityPerDevice: 5);
        var start = DateTimeOffset.UtcNow;

        // Insert out of order on purpose; the SortedDictionary keeps them ordered.
        foreach (var offset in new[] { 3, 1, 5, 2, 4, 7, 6 })
        {
            log.Append("D1", offset, AnomalySeverity.Healthy, start.AddSeconds(offset));
        }

        var latest = log.Latest("D1", 10);
        Assert.Equal(5, latest.Count);
        Assert.Equal(new[] { 3.0, 4.0, 5.0, 6.0, 7.0 }, latest.Select(p => p.Value).ToArray());

        var lastThree = log.Latest("D1", 3);
        Assert.Equal(new[] { 5.0, 6.0, 7.0 }, lastThree.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void SortedHistoryLog_HandlesIdenticalTimestamps()
    {
        var log = new SortedHistoryLog();
        var at = DateTimeOffset.UtcNow;

        log.Append("D1", 1, AnomalySeverity.Healthy, at);
        log.Append("D1", 2, AnomalySeverity.Healthy, at);

        Assert.Equal(2, log.CountFor("D1"));
    }
}
