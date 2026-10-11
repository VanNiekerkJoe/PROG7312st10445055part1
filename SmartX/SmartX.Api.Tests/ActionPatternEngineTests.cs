using SmartX.Core.Commands;
using SmartX.Core.Insights;
using SmartX.Core.Telemetry;
using Xunit;

namespace SmartX.Api.Tests;

public class ActionPatternEngineTests
{
    private static DeviceStatus Status(string id, SensorCategory category, AnomalySeverity severity) => new()
    {
        DeviceId = id,
        ZoneName = "Zone",
        Category = category,
        LastValue = 500,
        ZScore = 4,
        Severity = severity,
        LastSeen = DateTimeOffset.UtcNow,
        ThroughputLastMinute = 1
    };

    [Fact]
    public void LearnsFollowUpCommand_AfterRepeatedAlerts()
    {
        var engine = new ActionPatternEngine();
        var t = DateTimeOffset.UtcNow;

        for (var i = 0; i < 3; i++)
        {
            engine.ObserveAlert("P1", SensorCategory.Power, AnomalySeverity.Anomalous, t.AddMinutes(i * 5));
            engine.ObserveCommand("P1", CommandType.RelayOff, t.AddMinutes(i * 5).AddSeconds(10));
        }

        var suggestions = engine.Suggest(null, [Status("P2", SensorCategory.Power, AnomalySeverity.Anomalous)]);

        var learned = Assert.Single(suggestions, s => s.Basis == "learned");
        Assert.Equal("P2", learned.DeviceId);
        Assert.Equal(CommandType.RelayOff, learned.Command);
        Assert.Equal(1.0, learned.Confidence);
    }

    [Fact]
    public void CommandOutsideFollowWindow_IsNotLearned()
    {
        var engine = new ActionPatternEngine();
        var t = DateTimeOffset.UtcNow;

        for (var i = 0; i < 3; i++)
        {
            engine.ObserveAlert("P1", SensorCategory.Power, AnomalySeverity.Anomalous, t);
            engine.ObserveCommand("P1", CommandType.RelayOff, t.AddMinutes(10));
        }

        Assert.Equal(0, engine.LearnedPatternCount);
    }

    [Fact]
    public void FallsBackToRules_WhenNothingLearnedYet()
    {
        var engine = new ActionPatternEngine();

        var suggestions = engine.Suggest(null, [Status("E1", SensorCategory.Environmental, AnomalySeverity.Anomalous)]);

        var rule = Assert.Single(suggestions);
        Assert.Equal("rule", rule.Basis);
        Assert.Equal(CommandType.PumpOff, rule.Command);
    }

    [Fact]
    public void SearchPattern_SurfacesBeforeOperatorCommands()
    {
        var engine = new ActionPatternEngine();
        var t = DateTimeOffset.UtcNow;

        for (var i = 0; i < 2; i++)
        {
            engine.ObserveSearch("Pump A", t.AddMinutes(i));
            engine.ObserveCommand("ACT1", CommandType.PumpOn, t.AddMinutes(i).AddSeconds(5));
        }

        var suggestions = engine.Suggest("pump a", []);

        var learned = Assert.Single(suggestions);
        Assert.Equal("ACT1", learned.DeviceId);
        Assert.Equal(CommandType.PumpOn, learned.Command);
    }

    [Fact]
    public void RepeatOffender_AppearsOnWatchList()
    {
        var engine = new ActionPatternEngine();
        var t = DateTimeOffset.UtcNow;

        for (var i = 0; i < 3; i++)
        {
            engine.ObserveAlert("FLAKY", SensorCategory.Power, AnomalySeverity.Anomalous, t.AddMinutes(i));
        }

        var suggestions = engine.Suggest(null, []);

        Assert.Contains(suggestions, s => s.Basis == "history" && s.DeviceId == "FLAKY");
    }
}
