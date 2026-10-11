namespace SmartX.Core.Insights;

/// <summary>Tunable retention, decay, and ranking settings for the action-pattern engine.</summary>
public sealed record ActionPatternOptions
{
    public TimeSpan FollowWindow { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan PatternHalfLife { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan WatchHalfLife { get; init; } = TimeSpan.FromHours(6);
    public double MinSupport { get; init; } = 1.5;
    public double PriorWeight { get; init; } = 1.0;
    public double WatchThreshold { get; init; } = 2.5;
    public double PruneBelow { get; init; } = 0.05;
    public int MaxRecentContexts { get; init; } = 40;
    public int MaxContexts { get; init; } = 200;
}
