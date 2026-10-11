namespace SmartX.Core.Commands;

/// <summary>
/// Stack&lt;T&gt;-backed command history. Every manual override is pushed onto
/// the stack (up to the latest 200); "Undo" pops the most recent one (LIFO)
/// and produces its inverse command. Undo commands are only recorded in the
/// bounded audit log and are not themselves undoable.
/// </summary>
public sealed class CommandHistory
{
    private const int MaxEntries = 200;

    private readonly object _lock = new();
    private readonly Stack<DeviceCommand> _undoStack = new();
    private readonly List<DeviceCommand> _log = [];

    public int UndoDepth
    {
        get { lock (_lock) { return _undoStack.Count; } }
    }

    public DeviceCommand Issue(string deviceId, CommandType type, string source)
    {
        var command = new DeviceCommand(Guid.NewGuid(), deviceId, type, DateTimeOffset.UtcNow, source, false, null);
        lock (_lock)
        {
            PushUndoable(command);
            Append(command);
        }
        return command;
    }

    /// <summary>
    /// Pops the most recent command and returns the inverse command to apply,
    /// or null when there is nothing left to undo.
    /// </summary>
    public DeviceCommand? Undo()
    {
        lock (_lock)
        {
            if (!_undoStack.TryPop(out var last))
            {
                return null;
            }

            var undo = new DeviceCommand(Guid.NewGuid(), last.DeviceId, last.Type.Inverse(), DateTimeOffset.UtcNow, "undo", true, last.Id);
            Append(undo);
            return undo;
        }
    }

    public DeviceCommand? PeekLast()
    {
        lock (_lock)
        {
            return _undoStack.TryPeek(out var last) ? last : null;
        }
    }

    /// <summary>Newest-first copy of the audit log.</summary>
    public IReadOnlyList<DeviceCommand> Snapshot()
    {
        lock (_lock)
        {
            var copy = new List<DeviceCommand>(_log);
            copy.Reverse();
            return copy;
        }
    }

    private void Append(DeviceCommand command)
    {
        _log.Add(command);
        if (_log.Count > MaxEntries)
        {
            _log.RemoveAt(0);
        }
    }

    private void PushUndoable(DeviceCommand command)
    {
        _undoStack.Push(command);
        if (_undoStack.Count <= MaxEntries)
        {
            return;
        }

        var retained = _undoStack.Take(MaxEntries).Reverse().ToArray();
        _undoStack.Clear();
        foreach (var entry in retained)
        {
            _undoStack.Push(entry);
        }
    }
}
