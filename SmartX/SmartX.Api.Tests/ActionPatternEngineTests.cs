using SmartX.Core.Commands;
using SmartX.Core.Insights;
using SmartX.Core.Telemetry;
using Xunit;

namespace SmartX.Api.Tests;

public class ActionPatternEngineTests
{
    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void SetUtcNow(DateTimeOffset now) => _now = now;
    }

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
        var t = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(t);
        var engine = new ActionPatternEngine(clock);

        for (var i = 0; i < 3; i++)
        {
            engine.ObserveAlert("P1", SensorCategory.Power, AnomalySeverity.Anomalous, t.AddMinutes(i * 5));
            engine.ObserveCommand("P1", CommandType.RelayOff, t.AddMinutes(i * 5).AddSeconds(10));
        }

        clock.SetUtcNow(t.AddMinutes(11));
        var suggestions = engine.Suggest(null, [Status("P2", SensorCategory.Power, AnomalySeverity.Anomalous)]);

        var learned = Assert.Single(suggestions, s => s.Basis == "learned");
        Assert.Equal("P2", learned.DeviceId);
        Assert.Equal(CommandType.RelayOff, learned.Command);
        Assert.Equal(0.75, learned.Confidence);
        Assert.Contains("reading 500", learned.Detail);
        Assert.Contains("z-score +4.00", learned.Detail);
        Assert.Contains("Recency-weighted operator evidence", learned.Detail);
        Assert.Contains("effective observations", learned.Detail);
        Assert.Contains("halves every 7 days", learned.Detail);
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
        Assert.Contains("Environmental reading 500", rule.Detail);
        Assert.Contains("z-score +4.00", rule.Detail);
        Assert.Contains("rolling baseline", rule.Detail);
        Assert.Contains("confirm pausing the pump", rule.Detail);
    }

    [Fact]
    public void SearchPattern_SurfacesBeforeOperatorCommands()
    {
        var t = DateTimeOffset.UtcNow;
        var engine = new ActionPatternEngine(new ManualTimeProvider(t.AddMinutes(2)));

        for (var i = 0; i < 2; i++)
        {
            engine.ObserveSearch("Pump A", t.AddMinutes(i));
            engine.ObserveCommand("ACT1", CommandType.PumpOn, t.AddMinutes(i).AddSeconds(5));
        }

        var suggestions = engine.Suggest("pump a", [Status("ACT1", SensorCategory.Actuator, AnomalySeverity.Healthy)]);

        var learned = Assert.Single(suggestions);
        Assert.Equal("ACT1", learned.DeviceId);
        Assert.Equal(CommandType.PumpOn, learned.Command);
    }

    [Fact]
    public void OldSearchPatternsDecayBelowMinimumSupport()
    {
        var now = DateTimeOffset.UtcNow;
        var engine = new ActionPatternEngine(new ManualTimeProvider(now));
        var old = now.AddDays(-120);

        RecordSearchFollowUp(engine, "pump", "ACT1", CommandType.PumpOn, 4, old);

        Assert.Empty(engine.Suggest("pump", [Status("ACT1", SensorCategory.Actuator, AnomalySeverity.Healthy)]));
        Assert.Equal(0, engine.LearnedPatternCount);
    }

    [Fact]
    public void RecentSearchPatternOutweighsOlderPattern()
    {
        var recent = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(recent.AddMinutes(1));
        var engine = new ActionPatternEngine(clock);
        var old = recent.AddDays(-30);

        RecordSearchFollowUp(engine, "pump", "ACT1", CommandType.RelayOff, 8, old);
        RecordSearchFollowUp(engine, "pump", "ACT1", CommandType.PumpOn, 3, recent.AddMinutes(-5));

        var learned = Assert.Single(engine.Suggest("pump", [Status("ACT1", SensorCategory.Actuator, AnomalySeverity.Healthy)]));
        Assert.Equal(CommandType.PumpOn, learned.Command);
        Assert.Equal("ACT1", learned.DeviceId);
        Assert.InRange(learned.Confidence, 0.65, 0.7);
    }

    [Fact]
    public void RepeatOffender_AppearsOnWatchList()
    {
        var t = DateTimeOffset.UtcNow;
        var engine = new ActionPatternEngine(new ManualTimeProvider(t.AddMinutes(3)));

        for (var i = 0; i < 3; i++)
        {
            engine.ObserveAlert("FLAKY", SensorCategory.Power, AnomalySeverity.Anomalous, t.AddMinutes(i));
        }

        var suggestions = engine.Suggest(null, [Status("FLAKY", SensorCategory.Power, AnomalySeverity.Healthy)]);

        Assert.Contains(suggestions, s => s.Basis == "history" && s.DeviceId == "FLAKY");
    }

    [Fact]
    public void RepeatedContextsTeachOnlyOncePerCommand()
    {
        var t = DateTimeOffset.UtcNow;
        var engine = new ActionPatternEngine(new ManualTimeProvider(t.AddMinutes(2)));
        for (var i = 0; i < 3; i++)
        {
            engine.ObserveSearch("pump", t.AddMinutes(i));
        }

        engine.ObserveCommand("ACT1", CommandType.PumpOn, t.AddMinutes(3));

        var suggestions = engine.Suggest(
            "pump",
            [Status("ACT1", SensorCategory.Actuator, AnomalySeverity.Healthy)]);

        Assert.DoesNotContain(suggestions, suggestion => suggestion.Basis == "learned");
    }

    [Fact]
    public void DemoPatternsAreIdempotentAndClearlyLabelled()
    {
        var t = DateTimeOffset.UtcNow;
        var engine = new ActionPatternEngine(new ManualTimeProvider(t));
        var devices = new[] { Status("P1", SensorCategory.Power, AnomalySeverity.Anomalous) };

        engine.SeedDemoPatterns(devices);
        var pairCount = engine.LearnedPatternCount;
        engine.SeedDemoPatterns(devices);

        var learned = Assert.Single(engine.Suggest(
            null,
            [Status("P1", SensorCategory.Power, AnomalySeverity.Anomalous)]),
            suggestion => suggestion.Basis == "learned");
        Assert.Equal(pairCount, engine.LearnedPatternCount);
        Assert.Contains("seeded demo data", learned.Detail);
    }

    [Fact]
    public void RemovedDevicePatternsArePurgedAndNeverSuggested()
    {
        var t = DateTimeOffset.UtcNow;
        var engine = new ActionPatternEngine(new ManualTimeProvider(t.AddMinutes(3)));
        RecordSearchFollowUp(engine, "pump", "ACT1", CommandType.PumpOn, 2, t);

        engine.RetainOnly(new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(0, engine.LearnedPatternCount);
        Assert.Empty(engine.Suggest("pump", [Status("ACT1", SensorCategory.Actuator, AnomalySeverity.Healthy)]));
    }

    [Fact]
    public void SearchSuggestionsRequirePrefixMatchOfAtLeastThreeCharacters()
    {
        var t = DateTimeOffset.UtcNow;
        var engine = new ActionPatternEngine(new ManualTimeProvider(t.AddMinutes(3)));
        RecordSearchFollowUp(engine, "pump", "ACT1", CommandType.PumpOn, 2, t);
        var statuses = new[] { Status("ACT1", SensorCategory.Actuator, AnomalySeverity.Healthy) };

        Assert.Contains(engine.Suggest("pum", statuses), suggestion => suggestion.Basis == "learned");
        Assert.DoesNotContain(engine.Suggest("ump", statuses), suggestion => suggestion.Basis == "learned");
        Assert.DoesNotContain(engine.Suggest("p", statuses), suggestion => suggestion.Basis == "learned");
    }

    [Fact]
    public void WatchListAlertsDecayOverTime()
    {
        var t = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(t);
        var engine = new ActionPatternEngine(clock);
        for (var i = 0; i < 3; i++)
        {
            engine.ObserveAlert("FLAKY", SensorCategory.Power, AnomalySeverity.Anomalous, t.AddMinutes(i));
        }

        Assert.Contains(
            engine.Suggest(null, [Status("FLAKY", SensorCategory.Power, AnomalySeverity.Healthy)]),
            suggestion => suggestion.Basis == "history");
        clock.SetUtcNow(t.AddHours(24));

        Assert.DoesNotContain(
            engine.Suggest(null, [Status("FLAKY", SensorCategory.Power, AnomalySeverity.Healthy)]),
            suggestion => suggestion.Basis == "history");
    }

    [Fact]
    public void ContextTableRemainsWithinConfiguredLimit()
    {
        var t = DateTimeOffset.UtcNow;
        var engine = new ActionPatternEngine(
            new ManualTimeProvider(t.AddMinutes(10)),
            new ActionPatternOptions { MaxContexts = 2 });

        for (var i = 0; i < 5; i++)
        {
            var at = t.AddMinutes(i * 2);
            var term = $"sensor {i}";
            engine.ObserveSearch(term, at);
            engine.ObserveCommand("ACT1", CommandType.PumpOn, at.AddSeconds(1));
        }

        Assert.InRange(engine.LearnedPatternCount, 1, 2);
    }

    private static void RecordSearchFollowUp(
        ActionPatternEngine engine,
        string term,
        string deviceId,
        CommandType command,
        int repetitions,
        DateTimeOffset start)
    {
        for (var i = 0; i < repetitions; i++)
        {
            var at = start.AddMinutes(i * 2);
            engine.ObserveSearch(term, at);
            engine.ObserveCommand(deviceId, command, at.AddSeconds(5));
        }
    }
}
