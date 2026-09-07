namespace SmartX.Core.Telemetry;

/// <summary>
/// Tracks a device's rolling mean and standard deviation (via Welford's
/// online algorithm, so it never needs to re-scan history) and classifies
/// new readings against it. This is what turns a raw double into the
/// green/amber/red status the Anomaly Constellation renders.
/// </summary>
public sealed class SensorBaseline
{
    private double _mean;
    private double _sumSquaredDelta;
    private int _count;

    public double Mean => _mean;
    public double StdDev => _count > 1 ? Math.Sqrt(_sumSquaredDelta / (_count - 1)) : 0;

    public double Observe(double value)
    {
        _count++;
        var delta = value - _mean;
        _mean += delta / _count;
        var delta2 = value - _mean;
        _sumSquaredDelta += delta * delta2;

        return ZScoreFor(value);
    }

    public double ZScoreFor(double value)
    {
        var stdDev = StdDev;
        if (_count < 5 || stdDev < 0.0001) return 0;
        return (value - _mean) / stdDev;
    }

    public static AnomalySeverity Classify(double zScore) => Math.Abs(zScore) switch
    {
        < 1.5 => AnomalySeverity.Healthy,
        < 3.0 => AnomalySeverity.Elevated,
        _ => AnomalySeverity.Anomalous
    };
}
