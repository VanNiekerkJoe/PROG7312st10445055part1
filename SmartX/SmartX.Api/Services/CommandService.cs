using Microsoft.AspNetCore.SignalR;
using SmartX.Api.Hubs;
using SmartX.Core.Commands;
using SmartX.Core.Insights;
using SmartX.Core.Storage;

namespace SmartX.Api.Services;

/// <summary>
/// Applies manual override commands: validates the target device, pushes the
/// command onto the undo Stack, updates the device's desired output state,
/// feeds the pattern engine, and broadcasts to every connected dashboard.
/// </summary>
public sealed class CommandService(
    CommandHistory history,
    DeviceStateStore devices,
    SensorRegistry registry,
    ActionPatternEngine patterns,
    IHubContext<CommandHub> commandHub)
{
    public async Task<CommandResult> IssueAsync(
        string deviceId,
        CommandType type,
        string source,
        bool fromSuggestion = false)
    {
        if (!registry.DeviceNodes.Any(n => n.Id == deviceId))
        {
            return new CommandResult(false, $"Device '{deviceId}' was not found.", null);
        }

        devices.Sync(registry.DeviceNodes);

        var command = history.Issue(deviceId, type, fromSuggestion ? "suggestion" : source);
        devices.SetOutput(deviceId, type);
        if (!fromSuggestion)
        {
            patterns.ObserveCommand(deviceId, type, command.IssuedAt);
        }

        await commandHub.Clients.All.SendAsync("CommandIssued", command);
        return new CommandResult(true, $"{type.Label()} sent to {deviceId}.", command);
    }

    public async Task<CommandResult> UndoAsync()
    {
        var undo = history.Undo();
        if (undo is null)
        {
            return new CommandResult(false, "Nothing to undo.", null);
        }

        devices.SetOutput(undo.DeviceId, undo.Type);

        await commandHub.Clients.All.SendAsync("CommandUndone", undo);
        return new CommandResult(true, $"Reverted: {undo.Type.Label()} sent to {undo.DeviceId}.", undo);
    }
}
