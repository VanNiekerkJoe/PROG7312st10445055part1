namespace SmartX.Core.Commands;

/// <summary>One manual override command (or the undo of one) sent to a device.</summary>
public sealed record DeviceCommand(
    Guid Id,
    string DeviceId,
    CommandType Type,
    DateTimeOffset IssuedAt,
    string Source,
    bool IsUndo,
    Guid? UndoesCommandId);

/// <summary>Outcome of issuing or undoing a command, returned by the API.</summary>
public sealed record CommandResult(bool Ok, string Message, DeviceCommand? Command);

/// <summary>Full command log plus how many commands can still be undone.</summary>
public sealed record CommandLogResponse(IReadOnlyList<DeviceCommand> Commands, int UndoDepth);

public sealed record IssueCommandRequest(string DeviceId, CommandType Type);
