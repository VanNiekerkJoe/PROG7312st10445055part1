namespace SmartX.Core.Telemetry;

/// <summary>
/// The broad category a sensor belongs to. Drives which TelemetryPacket&lt;T&gt;
/// value type is expected (Environmental -> float, Power -> int, Actuator -> bool)
/// but does not itself constrain T at compile time.
/// </summary>
public enum SensorCategory
{
    Environmental,
    Power,
    Actuator
}

/// <summary>
/// Anomaly severity assigned to a reading after validation against a sensor's
/// rolling baseline. Used to drive the Anomaly Constellation dashboard colouring.
/// </summary>
public enum AnomalySeverity
{
    Healthy,
    Elevated,
    Anomalous,
    Disconnected
}