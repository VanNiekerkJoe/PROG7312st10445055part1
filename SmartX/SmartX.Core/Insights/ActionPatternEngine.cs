using SmartX.Core.Commands;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Insights;

/// <summary>
/// The Automated Action Engine. It learns from operator behaviour with a
/// first-order Markov-style transition table:
///
///     context  --(count)-->  follow-up command
///
/// A <b>context</b> is either an alert ("alert:Power:Anomalous") or a search
/// ("search:pump a"). Whenever an operator issues a command shortly after a
/// context was observed, the (context, follow-up) pair's counter is bumped.
/// Suggestions are then ranked by confidence = count / total for that context
/// (relative frequency), so the pattern "the operator usually switches the
/// relay off after a power anomaly" surfaces before they search for it.
///
/// Learned patterns need a minimum support (to avoid suggesting on one-offs),
/// so a small rule table covers the cold-start case, and a per-device anomaly
/// counter drives the "problematic device" watch-list.
/// </summary>
public sealed class ActionPatternEngine
{
    private static readonly TimeSpan FollowWindow = TimeSpan.FromSeconds(90);
    private const int MinSupport = 2;
    private const int MaxRecentContexts = 40;

    private readonly object _lock = new();

    // context key -> (follow-up key -> count)
    private readonly Dictionary<string, Dictionary<string, int>> _transitions = new(StringComparer.Ordinal);
    private readonly List<ContextEvent> _recent = [];
    private readonly Dictionary<string, int> _anomalyCountByDevice = new(StringComparer.Ordinal);

    private sealed record ContextEvent(string Key, string? DeviceId, DateTimeOffset At);

    public static string AlertContext(SensorCategory category, AnomalySeverity severity) =>
        $"alert:{category}:{severity}";

    public static string SearchContext(string term) =>
        $"search:{term.Trim().ToLowerInvariant()}";

    /// <summary>Number of distinct (context, follow-up) pairs learned so far.</summary>
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
            _anomalyCountByDevice[deviceId] = _anomalyCountByDevice.GetValueOrDefault(deviceId) + 1;
        }
    }

    public void ObserveSearch(string term, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return;
        }

        lock (_lock)
        {
            AddContext(new ContextEvent(SearchContext(term), null, at));
        }
    }

    /// <summary>Records a manual (non-undo) command against every context still inside the follow window.</summary>
    public void ObserveCommand(string deviceId, CommandType type, DateTimeOffset at)
    {
        lock (_lock)
        {
            foreach (var context in _recent)
            {
                if (at < context.At || at - context.At > FollowWindow)
                {
                    continue;
                }

                if (context.Key.StartsWith("alert:", StringComparison.Ordinal))
                {
                    // An alert only teaches us about commands sent to the device that raised it.
                    if (!string.Equals(context.DeviceId, deviceId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Bump(context.Key, type.ToString(), 1);
                }
                else
                {
                    Bump(context.Key, $"{deviceId}|{type}", 1);
                }
            }
        }
    }

    public void ForgetDevice(string deviceId)
    {
        lock (_lock)
        {
            _anomalyCountByDevice.Remove(deviceId);
            _recent.RemoveAll(c => c.DeviceId == deviceId);
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
            Bump(AlertContext(SensorCategory.Power, AnomalySeverity.Anomalous), CommandType.RelayOff.ToString(), 4);
            Bump(AlertContext(SensorCategory.Power, AnomalySeverity.Disconnected), CommandType.RelayOn.ToString(), 3);
            Bump(AlertContext(SensorCategory.Environmental, AnomalySeverity.Anomalous), CommandType.PumpOff.ToString(), 4);
            Bump(AlertContext(SensorCategory.Actuator, AnomalySeverity.Anomalous), CommandType.ValveClose.ToString(), 3);

            var actuator = devices.FirstOrDefault(d => d.Category == SensorCategory.Actuator);
            if (actuator is not null)
            {
                Bump(SearchContext("pump"), $"{actuator.DeviceId}|{CommandType.PumpOn}", 3);
                Bump(SearchContext("valve"), $"{actuator.DeviceId}|{CommandType.ValveOpen}", 2);
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
        var suggestions = new Dictionary<string, SuggestedAction>(StringComparer.Ordinal);
        var problem = statuses
            .Where(s => s.Severity is AnomalySeverity.Anomalous or AnomalySeverity.Disconnected)
            .ToList();

        lock (_lock)
        {
            // 1. Learned: what the operator usually does after this kind of alert.
            foreach (var device in problem)
            {
                var context = AlertContext(device.Category, device.Severity);
                if (!_transitions.TryGetValue(context, out var followUps))
                {
                    continue;
                }

                var total = followUps.Values.Sum();
                foreach (var (followUp, count) in followUps.OrderByDescending(f => f.Value).Take(2))
                {
                    if (count < MinSupport || !Enum.TryParse<CommandType>(followUp, out var command))
                    {
                        continue;
                    }

                    var id = $"learned:{device.DeviceId}:{command}";
                    suggestions[id] = new SuggestedAction(
                        id,
                        $"{command.Label()} on {device.DeviceId}",
                        $"{ReadingContext(device)} Operators chose {command.Label()} in {count} of {total} supported follow-ups after similar {device.Category} {device.Severity} alerts. This is a learned response, not proof of the cause; review the latest reading and device output before applying because the command changes the output immediately.",
                        device.DeviceId,
                        command,
                        Math.Round((double)count / total, 2),
                        "learned");
                }
            }

            // 2. Learned: what the operator usually does after searching for this term.
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var term = searchTerm.Trim().ToLowerInvariant();
                foreach (var (context, followUps) in _transitions)
                {
                    var isSearchContext = context.StartsWith("search:", StringComparison.Ordinal);
                    if (!isSearchContext
                        || (!context[7..].Contains(term, StringComparison.Ordinal) && !term.Contains(context[7..], StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    var total = followUps.Values.Sum();
                    foreach (var (followUp, count) in followUps.OrderByDescending(f => f.Value).Take(2))
                    {
                        var parts = followUp.Split('|');
                        if (count < MinSupport || parts.Length != 2 || !Enum.TryParse<CommandType>(parts[1], out var command))
                        {
                            continue;
                        }

                        var id = $"search:{parts[0]}:{command}";
                        suggestions[id] = new SuggestedAction(
                            id,
                            $"{command.Label()} on {parts[0]}",
                            $"This command followed the search \"{context[7..]}\" in {count} of {total} supported operator workflows. It is a learned workflow preference, not a diagnosis; confirm {parts[0]}'s current state and intended outcome before applying because the command changes its output immediately.",
                            parts[0],
                            command,
                            Math.Round((double)count / total, 2),
                            "learned");
                    }
                }
            }

            // 3. Cold-start rules for problem devices with no learned pattern yet.
            foreach (var device in problem)
            {
                if (suggestions.Keys.Any(k => k.StartsWith($"learned:{device.DeviceId}:", StringComparison.Ordinal)))
                {
                    continue;
                }

                var rule = RuleFor(device);
                if (rule is not null)
                {
                    suggestions[rule.Id] = rule;
                }
            }

            // 4. Watch-list: devices that keep misbehaving.
            foreach (var (deviceId, count) in _anomalyCountByDevice.OrderByDescending(a => a.Value).Take(2))
            {
                if (count < 3)
                {
                    continue;
                }

                var id = $"watch:{deviceId}";
                suggestions[id] = new SuggestedAction(
                    id,
                    $"Inspect {deviceId}",
                    $"This device has generated {count} alerts during the current API session. Review its recent telemetry for repeated spikes or dropouts, then check its power/network connection, sensor placement, and calibration. This is an inspection hint rather than a confirmed diagnosis; no command is applied automatically.",
                    deviceId,
                    null,
                    Math.Round(Math.Min(0.95, count / 10.0), 2),
                    "history");
            }
        }

        return suggestions.Values
            .OrderByDescending(s => s.Confidence)
            .ThenBy(s => s.Title, StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }

    private static SuggestedAction? RuleFor(DeviceStatus device)
    {
        if (device.Severity == AnomalySeverity.Disconnected)
        {
            return MakeRule(
                device,
                CommandType.RelayOn,
                $"{ReadingContext(device)} No recent telemetry can indicate a power, network, gateway-range, or device fault. Relay ON may restore power, but can energise connected equipment; confirm it is safe to do so, then check the device supply and connection before applying.",
                0.4);
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
        _recent.Add(context);
        if (_recent.Count > MaxRecentContexts)
        {
            _recent.RemoveAt(0);
        }
    }

    private void Bump(string context, string followUp, int by)
    {
        if (!_transitions.TryGetValue(context, out var followUps))
        {
            followUps = new Dictionary<string, int>(StringComparer.Ordinal);
            _transitions[context] = followUps;
        }

        followUps[followUp] = followUps.GetValueOrDefault(followUp) + by;
    }
}
