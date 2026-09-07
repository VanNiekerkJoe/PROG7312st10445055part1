using SmartX.Core.Batching;
using SmartX.Core.Telemetry;
using Xunit;

namespace SmartX.Api.Tests;

public class SensorReadingTests
{
    [Fact]
    public void AddingTwoReadings_SumsValues()
    {
        var meter1 = new SensorReading("Meter1", 120.5, SensorCategory.Power);
        var meter2 = new SensorReading("Meter2", 80.25, SensorCategory.Power);

        var meter3 = meter1 + meter2;

        Assert.Equal(200.75, meter3.Value, precision: 2);
        Assert.Equal("Meter1+Meter2", meter3.DeviceId);
    }

    [Fact]
    public void SubtractingReadings_ComputesDelta()
    {
        var current = new SensorReading("Sensor1", 25.0, SensorCategory.Environmental);
        var baseline = new SensorReading("Baseline", 22.0, SensorCategory.Environmental);

        var delta = current - baseline;

        Assert.Equal(3.0, delta.Value, precision: 2);
    }

    [Fact]
    public void CombiningDifferentCategories_Throws()
    {
        var power = new SensorReading("Meter1", 100, SensorCategory.Power);
        var env = new SensorReading("Sensor1", 20, SensorCategory.Environmental);

        Assert.Throws<InvalidOperationException>(() => _ = power + env);
    }

    [Fact]
    public void GreaterThanOperator_ComparesByValue()
    {
        var high = new SensorReading("S1", 50, SensorCategory.Power);
        var low = new SensorReading("S2", 10, SensorCategory.Power);

        Assert.True(high > low);
        Assert.False(low > high);
    }
}

public class TelemetryPacketTests
{
    [Fact]
    public void GenericFactory_InfersTypeFromValue()
    {
        var floatPacket = TelemetryPacket.Create("ENV-1", 21.4f, SensorCategory.Environmental);
        var intPacket = TelemetryPacket.Create("PWR-1", 220, SensorCategory.Power);
        var boolPacket = TelemetryPacket.Create("ACT-1", true, SensorCategory.Actuator);

        Assert.IsType<TelemetryPacket<float>>(floatPacket);
        Assert.IsType<TelemetryPacket<int>>(intPacket);
        Assert.IsType<TelemetryPacket<bool>>(boolPacket);
    }
}

public class TelemetryBatcherTests
{
    [Fact]
    public void FlushToPacketList_TransfersAllCompletedWindows()
    {
        var batcher = new TelemetryBatcher<float>("ENV-1", SensorCategory.Environmental, windowSize: 3);

        foreach (var value in new[] { 21.0f, 21.2f, 21.4f, 22.0f, 22.1f, 22.3f })
        {
            batcher.Add(value);
        }

        Assert.Equal(2, batcher.CompletedWindowCount);

        var packets = batcher.FlushToPacketList();

        Assert.Equal(6, packets.Count);
        Assert.Equal(0, batcher.CompletedWindowCount);
    }
}
