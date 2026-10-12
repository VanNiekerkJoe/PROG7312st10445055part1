using SmartX.Core.Commands;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Insights;

/// <summary>
/// The Automated Action Engine. It learns from operator behaviour with a
/// first-order Markov-style transition table:
///
///     context  --(timestamped weight)-->  follow-up command
///
/// A <b>context</b> is either an alert ("alert:Power:Anomalous") or a search
/// ("search:pump a"). Whenever an operator issues a command shortly after a
/// context was observed, the (context, follow-up) pair's timestamped weight is
/// bumped. Its effective weight decays by half every week, so recent behavior
/// influences confidence and minimum support more than stale history.
/// Suggestions use smoothed confidence (effective weight / total effective
/// weight plus a prior), so thin evidence is not presented as certainty.
///
/// Learned patterns need a minimum support (to avoid suggesting on one-offs),
/// so a small rule table covers the cold-start case, and a per-device anomaly
/// counter drives the "problematic device" watch-list.
/// </summary>
public sealed class ActionPatternEngine
{
    private readonly TimeProvider _clock;
    private readonly ActionPatternOptions _options;
    private readonly object _lock = new();

    private readonly Dictionary<string, Dictionary<string, Evidence>> _transitions = new(StringComparer.Ordinal);
    private readonly Queue<ContextEvent> _recent = new();
    private readonly Dictionary<string, DecayedCount> _alertCountByDevice = new(StringComparer.Ordinal);

    private sealed record Evidence(double Real, double Demo, DateTimeOffset At)
    {
        public double Total => Real + Demo;
    }

    private sealed record DecayedCount(double Value, DateTimeOffset At);
    private sealed record ContextEvent(string Key, string? DeviceId, DateTimeOffset At);
    private sealed record WeightedFollowUp(string Key, double Real, double Demo)
    {
        public double Total => Real + Demo;
    }

    public ActionPatternEngine(TimeProvider? clock = null, ActionPatternOptions? options = null)
    {
        _clock = clock ?? TimeProvider.System;
        _options = options ?? new ActionPatternOptions();
        ValidateOptions(_options);
    }

    public static string AlertContext(SensorCategory category, AnomalySeverity severity) =>
        $"alert:{category}:{severity}";

    public static string SearchContext(string term) =>
        $"search:{NormalizeTerm(term)}";

    /// <summary>Number of distinct (context, follow-up) pairs currently held.</summary>
    public int LearnedPatternCount
    {
        get
        {
            lock (_lock)
            {
                return _transitions.Values.Sum(followUps => followUps.Count);
            }
        }
    }

    public void ObserveAlert(string deviceId, SensorCategory category, AnomalySeverity severity, DateTimeOffset at)
    {
        lock (_lock)
        {
            AddContext(new ContextEvent(AlertContext(category, severity), deviceId, at));
            _alertCountByDevice[deviceId] = _alertCountByDevice.TryGetValue(deviceId, out var existing)
                ? AddDecayedCount(existing, at)
                : new DecayedCount(1, at);
        }
    }

    public void ObserveSearch(string term, DateTimeOffset at)
    {
        var normalized = NormalizeTerm(term);
        if (normalized.Length < 2)
        {
            return;
        }

        lock (_lock)
        {
            AddContext(new ContextEvent($"search:{normalized}", null, at));
        }
    }

    /// <summary>
    /// Records a manual command against every distinct context still inside the
    /// follow window. Each context is credited once per command.
    /// </summary>
    public void ObserveCommand(string deviceId, CommandType type, DateTimeOffset at)
    {
        lock (_lock)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var recent = _recent.ToArray();

            for (var i = recent.Length - 1; i >= 0; i--)
            {
                var context = recent[i];
                if (at < context.At || at - context.At > _options.FollowWindow)
                {
                    continue;
                }

                var isAlert = context.Key.StartsWith("alert:", StringComparison.Ordinal);
                if (isAlert && !string.Equals(context.DeviceId, deviceId, StringComparison.Ordinal))
                {
                    continue;
                }

                var distinctContext = isAlert ? $"{context.Key}#{context.DeviceId}" : context.Key;
                if (!seen.Add(distinctContext))
                {
                    continue;
                }

                Bump(context.Key, isAlert ? type.ToString() : $"{deviceId}|{type}", 1, at);
            }
        }
    }

    public void RetainOnly(IReadOnlySet<string> deviceIds)
    {
        lock (_lock)
        {
            foreach (var deviceId in _alertCountByDevice.Keys.Where(id => !deviceIds.Contains(id)).ToList())
            {
                _alertCountByDevice.Remove(deviceId);
            }

            ReplaceRecent(_recent.Where(context => context.DeviceId is null || deviceIds.Contains(context.DeviceId)));

            foreach (var followUps in _transitions.Values)
            {
                foreach (var key in followUps.Keys.Where(key => !DeviceInKeyExists(key, deviceIds)).ToList())
                {
                    followUps.Remove(key);
                }
            }

            PruneAll(_clock.GetUtcNow());
        }
    }

    public void ForgetDevice(string deviceId)
    {
        lock (_lock)
        {
            _alertCountByDevice.Remove(deviceId);
            ReplaceRecent(_recent.Where(context => context.DeviceId != deviceId));

            foreach (var followUps in _transitions.Values)
            {
                foreach (var key in followUps.Keys.Where(key => key.StartsWith(deviceId + "|", StringComparison.Ordinal)).ToList())
                {
                    followUps.Remove(key);
                }
            }

            PruneAll(_clock.GetUtcNow());
        }
    }

    /// <summary>
    /// Pre-loads a believable operator history for the devices that exist, so
    /// the learned-pattern path can be demonstrated without waiting for real
    /// operator behaviour to accumulate.
    /// </summary>
    public void SeedDemoPatterns(IEnumerable<DeviceStatus> devices)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            Seed(AlertContext(SensorCategory.Power, AnomalySeverity.Anomalous), CommandType.RelayOff.ToString(), 4, now);
            Seed(AlertContext(SensorCategory.Power, AnomalySeverity.Disconnected), CommandType.RelayOn.ToString(), 3, now);
            Seed(AlertContext(SensorCategory.Environmental, AnomalySeverity.Anomalous), CommandType.PumpOff.ToString(), 4, now);
            Seed(AlertContext(SensorCategory.Actuator, AnomalySeverity.Anomalous), CommandType.ValveClose.ToString(), 3, now);

            var actuator = devices.FirstOrDefault(d => d.Category == SensorCategory.Actuator);
            if (actuator is not null)
            {
                Seed(SearchContext("pump"), $"{actuator.DeviceId}|{CommandType.PumpOn}", 3, now);
                Seed(SearchContext("valve"), $"{actuator.DeviceId}|{CommandType.ValveOpen}", 2, now);
            }
        }
    }

    /// <summary>
    /// Builds the current suggestion list from what is happening right now
    /// (<paramref name="statuses"/>) and, optionally, what the operator is
    /// currently searching for.
    /// </summary>
    public IReadOnlyList<SuggestedAction> Suggest(string? searchTerm, IEnumerable<DeviceStatus> statuses, int max = 5)
    {
        var all = statuses.ToList();
        var knownDevices = all.Select(status => status.DeviceId).ToHashSet(StringComparer.Ordinal);
        var disconnectedDevices = all
            .Where(status => status.Severity == AnomalySeverity.Disconnected)
            .Select(status => status.DeviceId)
            .ToHashSet(StringComparer.Ordinal);
        var suggestions = new Dictionary<string, SuggestedAction>(StringComparer.Ordinal);
        var problem = all
            .Where(s => s.Severity is AnomalySeverity.Anomalous or AnomalySeverity.Disconnected)
            .ToList();
        var learnedForDevice = new HashSet<string>(StringComparer.Ordinal);

        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            PruneAll(now);

            // 1. Learned: what the operator usually does after this kind of alert.
            foreach (var device in problem.Where(s => s.Severity == AnomalySeverity.Anomalous))
            {
                var context = AlertContext(device.Category, device.Severity);
                if (!_transitions.TryGetValue(context, out var followUps))
                {
                    continue;
                }

                var weighted = Weigh(followUps, now);
                var total = weighted.Sum(followUp => followUp.Total);
                foreach (var followUp in Competitive(weighted))
                {
                    if (!Enum.TryParse<CommandType>(followUp.Key, out var command))
                    {
                        continue;
                    }

                    var detail =
                        $"{ReadingContext(device)} Recency-weighted operator evidence for {command.Label()} is " +
                        $"{FormatWeight(followUp.Total)} of {FormatWeight(total)} effective observations after similar " +
                        $"{device.Category} {device.Severity} alerts (evidence halves every {Describe(_options.PatternHalfLife)}). " +
                        $"{ProvenanceNote(followUp)}" +
                        "This is a learned response, not proof of the cause; review the latest reading and device output " +
                        "before applying because the command changes the output immediately.";

                    Offer(suggestions, new SuggestedAction(
                        $"learned:{device.DeviceId}:{command}",
                        $"{command.Label()} on {device.DeviceId}",
                        detail,
                        device.DeviceId,
                        command,
                        Confidence(followUp.Total, total),
                        "learned"));
                    learnedForDevice.Add(device.DeviceId);
                }
            }

            // 2. Learned: what the operator usually does after searching for this term.
            var term = NormalizeTerm(searchTerm ?? "");
            if (term.Length >= 2)
            {
                foreach (var (context, followUps) in _transitions)
                {
                    if (!context.StartsWith("search:", StringComparison.Ordinal)
                        || !SearchMatches(context["search:".Length..], term))
                    {
                        continue;
                    }

                    var weighted = Weigh(followUps, now);
                    var total = weighted.Sum(followUp => followUp.Total);
                    foreach (var followUp in Competitive(weighted))
                    {
                        var parts = followUp.Key.Split('|');
                        if (parts.Length != 2
                            || !knownDevices.Contains(parts[0])
                            || disconnectedDevices.Contains(parts[0])
                            || !Enum.TryParse<CommandType>(parts[1], out var command))
                        {
                            continue;
                        }

                        var detail =
                            $"This command followed the search \"{context["search:".Length..]}\" with " +
                            $"{FormatWeight(followUp.Total)} of {FormatWeight(total)} recency-weighted effective observations " +
                            $"(evidence halves every {Describe(_options.PatternHalfLife)}). " +
                            $"{ProvenanceNote(followUp)}" +
                            $"This is a learned workflow preference, not a diagnosis; confirm {parts[0]}'s current state " +
                            "and intended outcome before applying because the command changes its output immediately.";

                        Offer(suggestions, new SuggestedAction(
                            $"search:{parts[0]}:{command}",
                            $"{command.Label()} on {parts[0]}",
                            detail,
                            parts[0],
                            command,
                            Confidence(followUp.Total, total),
                            "learned"));
                    }
                }
            }

            // 3. Cold-start rules for problem devices with no learned pattern yet.
            foreach (var device in problem)
            {
                if (learnedForDevice.Contains(device.DeviceId))
                {
                    continue;
                }

                var rule = RuleFor(device, now);
                if (rule is not null)
                {
                    Offer(suggestions, rule);
                }
            }

            // 4. Watch-list: devices that have been misbehaving lately.
            var watch = _alertCountByDevice
                .Select(entry => (
                    DeviceId: entry.Key,
                    Value: Decay(entry.Value.Value, entry.Value.At, now, _options.WatchHalfLife)))
                .Where(item => knownDevices.Contains(item.DeviceId) && item.Value >= _options.WatchThreshold)
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.DeviceId, StringComparer.Ordinal)
                .Take(2);

            foreach (var (deviceId, value) in watch)
            {
                Offer(suggestions, new SuggestedAction(
                    $"watch:{deviceId}",
                    $"Inspect {deviceId}",
                    $"This device has raised about {Math.Round(value)} recent alerts (recency-weighted; the count halves every " +
                    $"{Describe(_options.WatchHalfLife)}). Review its recent telemetry for repeated spikes or dropouts, then check its " +
                    "power/network connection, sensor placement, and calibration. This is an inspection hint rather than a " +
                    "confirmed diagnosis; no command is applied automatically.",
                    deviceId,
                    null,
                    Math.Round(Math.Min(0.95, value / 10.0), 2),
                    "history"));
            }
        }

        var ranked = suggestions.Values
            .OrderByDescending(s => s.Confidence)
            .ThenBy(s => BasisRank(s.Basis))
            .ThenBy(s => s.DeviceId, StringComparer.Ordinal)
            .ThenBy(s => s.Title, StringComparer.Ordinal)
            .ToList();

        var selected = new List<SuggestedAction>();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        AddFirst(suggestion => suggestion.Id.StartsWith("search:", StringComparison.Ordinal));
        AddFirst(suggestion => suggestion.Basis == "history" || suggestion.Id.StartsWith("disconnect:", StringComparison.Ordinal));
        AddFirst(suggestion => suggestion.Id.StartsWith("learned:", StringComparison.Ordinal));
        AddFirst(suggestion => suggestion.Basis == "rule");
        foreach (var suggestion in ranked)
        {
            Add(suggestion);
        }

        return selected.Take(Math.Max(0, max)).ToList();

        void AddFirst(Func<SuggestedAction, bool> predicate)
        {
            var candidate = ranked.FirstOrDefault(suggestion =>
                !selectedIds.Contains(suggestion.Id) && predicate(suggestion));
            if (candidate is not null)
            {
                Add(candidate);
            }
        }

        void Add(SuggestedAction suggestion)
        {
            if (selectedIds.Add(suggestion.Id))
            {
                selected.Add(suggestion);
            }
        }
    }

    private static SuggestedAction? RuleFor(DeviceStatus device, DateTimeOffset now)
    {
        if (device.Severity == AnomalySeverity.Disconnected)
        {
            var age = now > device.LastSeen ? now - device.LastSeen : TimeSpan.Zero;
            var ageText = DescribeAge(age);
            var lastSeen = device.LastSeen.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            return new SuggestedAction(
                $"disconnect:{device.DeviceId}",
                $"Inspect {device.DeviceId}: no data for {ageText}",
                $"No telemetry has arrived for {ageText} (last received at {lastSeen}). Check the device's power, network, and gateway connection, then confirm it resumes reporting. A disconnected device may not be able to receive a remote command.",
                device.DeviceId,
                null,
                0.95,
                "rule");
        }

        if (device.Severity != AnomalySeverity.Anomalous)
        {
            return null;
        }

        return device.Category switch
        {
            SensorCategory.Power => MakeRule(
                device,
                CommandType.RelayOff,
                $"{ReadingContext(device)} The reading is well outside this sensor's rolling baseline. Relay OFF can shed the connected load; first confirm the affected equipment and whether interrupting it is safe, then inspect the load and wiring.",
                0.55),
            SensorCategory.Environmental => MakeRule(
                device,
                CommandType.PumpOff,
                $"{ReadingContext(device)} The reading is well outside this sensor's rolling baseline. Pump OFF can pause irrigation or circulation; check nearby sensors and confirm pausing the pump will not harm the zone before applying, then inspect the sensor and local conditions.",
                0.5),
            SensorCategory.Actuator => MakeRule(
                device,
                CommandType.ValveClose,
                $"{ReadingContext(device)} The actuator reading is well outside its rolling baseline. Valve CLOSE may isolate flow; verify the valve and downstream process are safe to close, then inspect the actuator and its recent command history.",
                0.5),
            _ => null
        };
    }

    private static string ReadingContext(DeviceStatus device)
    {
        var zScore = device.ZScore.ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture);
        var value = device.LastValue.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var lastSeen = device.LastSeen.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        return $"Current status: {device.Severity} {device.Category} reading {value} (z-score {zScore}), last received at {lastSeen}.";
    }

    private static SuggestedAction MakeRule(DeviceStatus device, CommandType command, string detail, double confidence)
    {
        var id = $"rule:{device.DeviceId}:{command}";
        return new SuggestedAction(id, $"{command.Label()} on {device.DeviceId}", detail, device.DeviceId, command, confidence, "rule");
    }

    private void AddContext(ContextEvent context)
    {
        _recent.Enqueue(context);
        while (_recent.Count > _options.MaxRecentContexts)
        {
            _recent.Dequeue();
        }
    }

    private void ReplaceRecent(IEnumerable<ContextEvent> contexts)
    {
        _recent.Clear();
        foreach (var context in contexts)
        {
            _recent.Enqueue(context);
        }
    }

    private Dictionary<string, Evidence> FollowUpsFor(string context)
    {
        if (!_transitions.TryGetValue(context, out var followUps))
        {
            followUps = new Dictionary<string, Evidence>(StringComparer.Ordinal);
            _transitions[context] = followUps;
        }

        return followUps;
    }

    private void Bump(string context, string followUp, double by, DateTimeOffset at)
    {
        var followUps = FollowUpsFor(context);

        if (!followUps.TryGetValue(followUp, out var existing))
        {
            followUps[followUp] = new Evidence(by, 0, at);
        }
        else if (at >= existing.At)
        {
            var factor = DecayFactor(existing.At, at, _options.PatternHalfLife);
            followUps[followUp] = new Evidence(existing.Real * factor + by, existing.Demo * factor, at);
        }
        else
        {
            var lateEvidence = by * DecayFactor(at, existing.At, _options.PatternHalfLife);
            followUps[followUp] = existing with { Real = existing.Real + lateEvidence };
        }

        if (_transitions.Count > _options.MaxContexts)
        {
            PruneAll(_clock.GetUtcNow());
        }
    }

    private void Seed(string context, string followUp, double weight, DateTimeOffset at)
    {
        var followUps = FollowUpsFor(context);
        followUps[followUp] = followUps.TryGetValue(followUp, out var existing)
            ? new Evidence(
                existing.Real * DecayFactor(existing.At, at, _options.PatternHalfLife),
                weight,
                at)
            : new Evidence(0, weight, at);
    }

    private List<WeightedFollowUp> Weigh(Dictionary<string, Evidence> followUps, DateTimeOffset at) =>
        followUps
            .Select(entry =>
            {
                var factor = DecayFactor(entry.Value.At, at, _options.PatternHalfLife);
                return new WeightedFollowUp(entry.Key, entry.Value.Real * factor, entry.Value.Demo * factor);
            })
            .Where(entry => entry.Total > 0)
            .ToList();

    private IEnumerable<WeightedFollowUp> Competitive(List<WeightedFollowUp> weighted)
    {
        var ranked = weighted
            .OrderByDescending(followUp => followUp.Total)
            .ThenBy(followUp => followUp.Key, StringComparer.Ordinal)
            .ToList();

        if (ranked.Count == 0)
        {
            yield break;
        }

        var leader = ranked[0].Total;
        foreach (var followUp in ranked.Take(2))
        {
            if (followUp.Total >= _options.MinSupport && followUp.Total >= leader * 0.5)
            {
                yield return followUp;
            }
        }
    }

    private double Confidence(double weight, double total) =>
        Math.Round(weight / (total + _options.PriorWeight), 2);

    private void PruneAll(DateTimeOffset now)
    {
        foreach (var (context, followUps) in _transitions.ToList())
        {
            foreach (var (key, evidence) in followUps.ToList())
            {
                var currentWeight = evidence.Total * DecayFactor(evidence.At, now, _options.PatternHalfLife);
                if (currentWeight < _options.PruneBelow)
                {
                    followUps.Remove(key);
                }
            }

            if (followUps.Count == 0)
            {
                _transitions.Remove(context);
            }
        }

        foreach (var (deviceId, count) in _alertCountByDevice.ToList())
        {
            if (Decay(count.Value, count.At, now, _options.WatchHalfLife) < _options.PruneBelow)
            {
                _alertCountByDevice.Remove(deviceId);
            }
        }

        if (_transitions.Count > _options.MaxContexts)
        {
            var weakest = _transitions
                .OrderBy(context => context.Value.Values.Sum(evidence =>
                    evidence.Total * DecayFactor(evidence.At, now, _options.PatternHalfLife)))
                .Take(_transitions.Count - _options.MaxContexts)
                .Select(context => context.Key)
                .ToList();

            foreach (var context in weakest)
            {
                _transitions.Remove(context);
            }
        }
    }

    private DecayedCount AddDecayedCount(DecayedCount existing, DateTimeOffset at)
    {
        if (at >= existing.At)
        {
            return new DecayedCount(
                Decay(existing.Value, existing.At, at, _options.WatchHalfLife) + 1,
                at);
        }

        return existing with
        {
            Value = existing.Value + Decay(1, at, existing.At, _options.WatchHalfLife)
        };
    }

    private static bool DeviceInKeyExists(string followUpKey, IReadOnlySet<string> deviceIds)
    {
        var separator = followUpKey.IndexOf('|');
        return separator < 0 || deviceIds.Contains(followUpKey[..separator]);
    }

    private static void Offer(Dictionary<string, SuggestedAction> suggestions, SuggestedAction candidate)
    {
        if (!suggestions.TryGetValue(candidate.Id, out var existing)
            || candidate.Confidence > existing.Confidence)
        {
            suggestions[candidate.Id] = candidate;
        }
    }

    private static int BasisRank(string basis) => basis switch
    {
        "learned" => 0,
        "rule" => 1,
        _ => 2
    };

    private static string ProvenanceNote(WeightedFollowUp followUp)
    {
        if (followUp.Demo <= 0)
        {
            return "";
        }

        return followUp.Real < 0.05
            ? "This pattern comes from seeded demo data, not real operator history. "
            : "Part of this evidence is seeded demo data. ";
    }

    private static string NormalizeTerm(string term) =>
        string.Join(' ', term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static bool SearchMatches(string learnedTerm, string typedTerm) =>
        learnedTerm == typedTerm
        || (typedTerm.Length >= 3
            && (learnedTerm.StartsWith(typedTerm, StringComparison.Ordinal)
                || typedTerm.StartsWith(learnedTerm, StringComparison.Ordinal)));

    private static double DecayFactor(DateTimeOffset from, DateTimeOffset to, TimeSpan halfLife)
    {
        var age = to - from;
        return age <= TimeSpan.Zero ? 1.0 : Math.Pow(0.5, age.TotalSeconds / halfLife.TotalSeconds);
    }

    private static double Decay(double value, DateTimeOffset from, DateTimeOffset to, TimeSpan halfLife) =>
        value * DecayFactor(from, to, halfLife);

    private static string Describe(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{span.TotalDays:0.#} days" :
        span.TotalHours >= 1 ? $"{span.TotalHours:0.#} hours" :
        $"{span.TotalMinutes:0.#} minutes";

    private static string DescribeAge(TimeSpan age) =>
        age.TotalDays >= 1 ? $"{Math.Floor(age.TotalDays)} {(Math.Floor(age.TotalDays) == 1 ? "day" : "days")}" :
        age.TotalHours >= 1 ? $"{Math.Floor(age.TotalHours)} {(Math.Floor(age.TotalHours) == 1 ? "hour" : "hours")}" :
        age.TotalMinutes >= 1 ? $"{Math.Floor(age.TotalMinutes)} {(Math.Floor(age.TotalMinutes) == 1 ? "minute" : "minutes")}" :
        $"{Math.Max(0, (int)Math.Floor(age.TotalSeconds))} {(Math.Floor(age.TotalSeconds) == 1 ? "second" : "seconds")}";

    private static string FormatWeight(double weight) =>
        weight.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static void ValidateOptions(ActionPatternOptions options)
    {
        if (options.FollowWindow <= TimeSpan.Zero
            || options.PatternHalfLife <= TimeSpan.Zero
            || options.WatchHalfLife <= TimeSpan.Zero
            || !double.IsFinite(options.MinSupport) || options.MinSupport <= 0
            || !double.IsFinite(options.PriorWeight) || options.PriorWeight <= 0
            || !double.IsFinite(options.WatchThreshold) || options.WatchThreshold <= 0
            || !double.IsFinite(options.PruneBelow) || options.PruneBelow <= 0
            || options.MaxRecentContexts <= 0
            || options.MaxContexts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Action pattern settings must be positive and finite.");
        }
    }
}
