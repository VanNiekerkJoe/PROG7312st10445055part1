using SmartX.Core.Ingestion;
using Xunit;

namespace SmartX.Api.Tests;

public class IngestQueueTests
{
    private static IngestItem Item(string mac, int urgency = 0) =>
        new(mac, 1.0, false, DateTimeOffset.UtcNow, urgency, urgency > 0 ? "test" : string.Empty);

    [Fact]
    public void StandardLane_IsFirstInFirstOut()
    {
        var queue = new TelemetryIngestQueue();
        queue.Enqueue(Item("A"));
        queue.Enqueue(Item("B"));
        queue.Enqueue(Item("C"));

        Assert.True(queue.TryDequeue(out var first));
        Assert.True(queue.TryDequeue(out var second));
        Assert.True(queue.TryDequeue(out var third));
        Assert.Equal(new[] { "A", "B", "C" }, new[] { first!.MacAddress, second!.MacAddress, third!.MacAddress });
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void CriticalPacket_BypassesStandardBacklog()
    {
        var queue = new TelemetryIngestQueue();
        for (var i = 0; i < 100; i++)
        {
            queue.Enqueue(Item($"STD{i}"));
        }

        queue.Enqueue(Item("CRITICAL", urgency: 3));

        Assert.Equal(1, queue.PriorityDepth);
        Assert.Equal(100, queue.StandardDepth);
        Assert.True(queue.TryDequeue(out var next));
        Assert.Equal("CRITICAL", next!.MacAddress);
    }

    [Fact]
    public void PriorityLane_OrdersByUrgency_ThenArrival()
    {
        var queue = new TelemetryIngestQueue();
        queue.Enqueue(Item("LOW1", urgency: 1));
        queue.Enqueue(Item("HIGH", urgency: 3));
        queue.Enqueue(Item("LOW2", urgency: 1));

        var order = new List<string>();
        while (queue.TryDequeue(out var item))
        {
            order.Add(item.MacAddress);
        }

        Assert.Equal(new[] { "HIGH", "LOW1", "LOW2" }, order.ToArray());
    }

    [Fact]
    public void StandardLane_AppliesBackPressureWhenFull()
    {
        var queue = new TelemetryIngestQueue();
        for (var i = 0; i < TelemetryIngestQueue.MaxStandardDepth; i++)
        {
            Assert.True(queue.Enqueue(Item("X")));
        }

        Assert.False(queue.Enqueue(Item("OVERFLOW")));
        Assert.True(queue.Enqueue(Item("CRITICAL", urgency: 3)));
    }
}
