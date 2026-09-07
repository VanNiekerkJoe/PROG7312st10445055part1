namespace SmartX.Core.Telemetry;

/// <summary>
/// A generic envelope around a single telemetry reading of type T.
///
/// Because T is a genuine CLR generic type parameter constrained to
/// <c>struct</c>, the JIT emits a specialised, value-type-only version of this
/// class per closed generic type (TelemetryPacket&lt;float&gt;,
/// TelemetryPacket&lt;int&gt;, TelemetryPacket&lt;bool&gt;, ...). The value is
/// never stored as <c>object</c>, so passing a float soil-moisture reading, an
/// int wattage reading, or a bool valve state through the same ingestion
/// pipeline never triggers a boxing allocation the way <c>TelemetryPacket</c>
/// wrapping <c>object Value</c> would.
/// </summary>
/// <typeparam name="T">
/// The underlying reading type. Constrained to <c>struct</c> because every
/// Smart-X sensor payload (float, int, bool) is a value type.
/// </typeparam>
public sealed class TelemetryPacket<T> where T : struct
{
    public required string DeviceId { get; init; }
    public required T Value { get; init; }
    public required Core.Telemetry.SensorCategory Category { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Convenience factory so call sites read as
    /// <c>TelemetryPacket.Create(deviceId, 21.4f, SensorCategory.Environmental)</c>
    /// with T inferred, rather than having to spell out the closed generic type.
    /// </summary>
    public static TelemetryPacket<T> Create(string deviceId, T value, SensorCategory category, DateTimeOffset? timestamp = null)
        => new()
        {
            DeviceId = deviceId,
            Value = value,
            Category = category,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow
        };

    public override string ToString() => $"[{Timestamp:HH:mm:ss}] {DeviceId} ({Category}) = {Value}";
}

/// <summary>
/// Non-generic helper so callers can write <c>TelemetryPacket.Create(...)</c>
/// and let the compiler infer T, instead of
/// <c>TelemetryPacket&lt;float&gt;.Create(...)</c> everywhere.
/// </summary>
public static class TelemetryPacket
{
    public static TelemetryPacket<T> Create<T>(string deviceId, T value, SensorCategory category, DateTimeOffset? timestamp = null)
        where T : struct
        => TelemetryPacket<T>.Create(deviceId, value, category, timestamp);
}
