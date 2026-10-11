using SmartX.Core.Telemetry;

namespace SmartX.Core.Ingestion;

/// <summary>What a device (or the device simulator) POSTs to /api/telemetry/ingest.</summary>
public sealed record IngestRequest(string MacAddress, double Value, bool Disconnected = false);

/// <summary>
/// Acknowledgement returned to the device. <c>OutputOn</c> is the output state
/// the gateway wants the device to be in, so actuators pick up manual
/// override commands the same way a real device would on its next check-in.
/// </summary>
public sealed record IngestResponse(bool Accepted, bool Priority, string? DeviceId, bool OutputOn, string Message);

/// <summary>A packet waiting in (or just removed from) the ingestion queues.</summary>
public sealed record IngestItem(
    string MacAddress,
    double Value,
    bool Disconnected,
    DateTimeOffset ReceivedAt,
    int Urgency,
    string Reason)
{
    public bool IsCritical => Urgency > 0;
}

public sealed record PipelineStats(
    int StandardDepth,
    int PriorityDepth,
    long ProcessedTotal,
    long PriorityProcessed,
    int ActiveAlerts,
    long DuplicateAlertsSuppressed,
    int LearnedPatterns,
    DateTimeOffset At);

/// <summary>Broadcast when a critical packet was processed ahead of the standard queue.</summary>
public sealed record PriorityEvent(string DeviceId, double Value, AnomalySeverity Severity, string Reason, DateTimeOffset At);
