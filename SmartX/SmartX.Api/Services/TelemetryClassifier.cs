using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Decides which ingestion lane a packet goes into. Urgency 0 = standard
/// FIFO lane; 1-3 = priority lane (3 is the most urgent). Called at ingest
/// time, before queuing, so a critical packet never waits behind a backlog.
/// </summary>
public static class TelemetryClassifier
{
    public const double CriticalZScore = 3.0;

    public static (int Urgency, string Reason) Classify(SensorCategory category, double value, double zScore, bool disconnected)
    {
        if (Math.Abs(zScore) >= CriticalZScore)
        {
            return (3, $"Statistical anomaly (z = {zScore:0.0})");
        }

        var outOfRange = category switch
        {
            SensorCategory.Environmental => value > 45 || value < -5,
            SensorCategory.Power => value > 350 || value < 0,
            _ => false
        };

        if (outOfRange)
        {
            return (3, $"Out-of-range {category} reading ({value:0.0})");
        }

        if (disconnected)
        {
            return (2, "Device reported a disconnect");
        }

        return (0, string.Empty);
    }

    public static double BaselineFor(SensorCategory category) => category switch
    {
        SensorCategory.Environmental => 22,
        SensorCategory.Power => 220,
        SensorCategory.Actuator => 1,
        _ => 1
    };

    public static double NoiseFor(SensorCategory category) => category switch
    {
        SensorCategory.Environmental => 1.2,
        SensorCategory.Power => 15,
        SensorCategory.Actuator => 0.0,
        _ => 1
    };
}
