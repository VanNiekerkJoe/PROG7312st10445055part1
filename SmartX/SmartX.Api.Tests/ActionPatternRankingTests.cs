using SmartX.Core.Commands;
using SmartX.Core.Insights;
using SmartX.Core.Telemetry;
using Xunit;

namespace SmartX.Api.Tests;

public sealed class ActionPatternRankingTests
{
    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
    public void SelectedSuggestionsRemainSortedByConfidenceAfterSlotSelection()
    {
        var now = DateTimeOffset.UtcNow;
        var engine = new ActionPatternEngine(new ManualTimeProvider(now));
        var power = Status("P1", SensorCategory.Power, AnomalySeverity.Anomalous);
        engine.SeedDemoPatterns([power]);

        for (var i = 0; i < 3; i++)
        {
            engine.ObserveAlert("P1", SensorCategory.Power, AnomalySeverity.Anomalous, now.AddMinutes(i));
        }

        var suggestions = engine.Suggest(null, [power]);

        Assert.Contains(suggestions, suggestion => suggestion.Basis == "history");
        Assert.Contains(suggestions, suggestion => suggestion.Basis == "learned");
        Assert.Equal(
            suggestions.OrderByDescending(suggestion => suggestion.Confidence)
                .ThenBy(suggestion => suggestion.Basis == "learned" ? 0 : suggestion.Basis == "rule" ? 1 : 2)
                .ThenBy(suggestion => suggestion.DeviceId, StringComparer.Ordinal)
                .ThenBy(suggestion => suggestion.Title, StringComparer.Ordinal)
                .Select(suggestion => suggestion.Id),
            suggestions.Select(suggestion => suggestion.Id));
        Assert.Equal("learned", suggestions[0].Basis);
        Assert.Equal(0.8, suggestions[0].Confidence);
    }

    [Fact]
    public void SmoothedConfidenceUsesAddOnePriorForDemoPatterns()
    {
        var now = DateTimeOffset.UtcNow;
        var engine = new ActionPatternEngine(new ManualTimeProvider(now));
        var power = Status("P1", SensorCategory.Power, AnomalySeverity.Anomalous);
        engine.SeedDemoPatterns([power]);

        var demoSuggestion = Assert.Single(
            engine.Suggest(null, [power]),
            suggestion => suggestion.DeviceId == "P1" && suggestion.Basis == "learned");
        Assert.Equal(0.8, demoSuggestion.Confidence);

        var threeObservationsEngine = new ActionPatternEngine(new ManualTimeProvider(now.AddMinutes(11)));
        for (var i = 0; i < 3; i++)
        {
            var at = now.AddMinutes(i * 5);
            threeObservationsEngine.ObserveAlert("E1", SensorCategory.Environmental, AnomalySeverity.Anomalous, at);
            threeObservationsEngine.ObserveCommand("E1", CommandType.PumpOff, at.AddSeconds(10));
        }

        var observationSuggestion = Assert.Single(
            threeObservationsEngine.Suggest(
                null,
                [Status("E1", SensorCategory.Environmental, AnomalySeverity.Anomalous)]),
            suggestion => suggestion.DeviceId == "E1" && suggestion.Basis == "learned");
        Assert.Equal(0.75, observationSuggestion.Confidence);
    }
}
