namespace SmartX.Core.Telemetry;

/// <summary>
/// Current live state of one device, as broadcast to the constellation view.
/// This is a plain snapshot DTO (not the ingestion envelope); it's what the
/// dashboard actually renders one node from.
/// </summary>
public sealed record DeviceStatus
{
    public required string DeviceId { get; init; }
    public required string ZoneName { get; init; }
    public required SensorCategory Category { get; init; }
    public required double LastValue { get; init; }
    public required double ZScore { get; init; }
    public required AnomalySeverity Severity { get; init; }
    public required DateTimeOffset LastSeen { get; init; }
    public required int ThroughputLastMinute { get; init; }
}
