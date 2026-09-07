namespace SmartX.Core.Telemetry;

/// <summary>
/// A single numeric sensor reading, distinct from TelemetryPacket&lt;T&gt;.
/// TelemetryPacket&lt;T&gt; is the generic ingestion envelope for arbitrary
/// value types; SensorReading is specifically the numeric (double-backed)
/// representation used once a reading needs to participate in arithmetic:
/// aggregating two meters, or computing a delta between two points in time.
///
/// Operator overloads below let call sites read the way the assignment brief
/// phrases it: <c>var meter3 = meter1 + meter2;</c> instead of
/// <c>var meter3 = SensorReading.Add(meter1, meter2);</c>
/// </summary>
public readonly struct SensorReading : IEquatable<SensorReading>, IComparable<SensorReading>
{
    public string DeviceId { get; }
    public double Value { get; }
    public SensorCategory Category { get; }
    public DateTimeOffset Timestamp { get; }

    public SensorReading(string deviceId, double value, SensorCategory category, DateTimeOffset? timestamp = null)
    {
        DeviceId = deviceId;
        Value = value;
        Category = category;
        Timestamp = timestamp ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Aggregate load of two readings, e.g. Meter3 = Meter1 + Meter2.
    /// The resulting reading takes the later timestamp and a synthesised
    /// DeviceId so it's traceable back to its two sources.
    /// </summary>
    public static SensorReading operator +(SensorReading a, SensorReading b)
    {
        RequireSameCategory(a, b);
        return new SensorReading(
            deviceId: $"{a.DeviceId}+{b.DeviceId}",
            value: a.Value + b.Value,
            category: a.Category,
            timestamp: a.Timestamp > b.Timestamp ? a.Timestamp : b.Timestamp);
    }

    /// <summary>
    /// Delta between two readings of the same category, e.g. drift between
    /// a sensor's current value and its rolling baseline.
    /// </summary>
    public static SensorReading operator -(SensorReading a, SensorReading b)
    {
        RequireSameCategory(a, b);
        return new SensorReading(
            deviceId: $"{a.DeviceId}-{b.DeviceId}",
            value: a.Value - b.Value,
            category: a.Category,
            timestamp: a.Timestamp > b.Timestamp ? a.Timestamp : b.Timestamp);
    }

    public static bool operator >(SensorReading a, SensorReading b) => a.Value > b.Value;
    public static bool operator <(SensorReading a, SensorReading b) => a.Value < b.Value;
    public static bool operator >=(SensorReading a, SensorReading b) => a.Value >= b.Value;
    public static bool operator <=(SensorReading a, SensorReading b) => a.Value <= b.Value;

    public static bool operator ==(SensorReading a, SensorReading b) => a.Equals(b);
    public static bool operator !=(SensorReading a, SensorReading b) => !a.Equals(b);

    public bool Equals(SensorReading other) =>
        DeviceId == other.DeviceId && Value.Equals(other.Value) && Category == other.Category && Timestamp == other.Timestamp;

    public override bool Equals(object? obj) => obj is SensorReading other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(DeviceId, Value, Category, Timestamp);

    public int CompareTo(SensorReading other) => Value.CompareTo(other.Value);

    public override string ToString() => $"{DeviceId}: {Value:0.##} ({Category})";

    private static void RequireSameCategory(SensorReading a, SensorReading b)
    {
        if (a.Category != b.Category)
        {
            throw new InvalidOperationException(
                $"Cannot combine readings from different categories ({a.Category} vs {b.Category}).");
        }
    }
}
