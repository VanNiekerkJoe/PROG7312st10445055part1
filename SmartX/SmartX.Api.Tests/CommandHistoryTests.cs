using SmartX.Core.Commands;
using Xunit;

namespace SmartX.Api.Tests;

public class CommandHistoryTests
{
    [Fact]
    public void Undo_RevertsMostRecentCommandFirst()
    {
        var history = new CommandHistory();
        history.Issue("DEV1", CommandType.PumpOn, "test");
        history.Issue("DEV2", CommandType.RelayOff, "test");

        var firstUndo = history.Undo();
        var secondUndo = history.Undo();

        Assert.Equal("DEV2", firstUndo!.DeviceId);
        Assert.Equal(CommandType.RelayOn, firstUndo.Type);
        Assert.Equal("DEV1", secondUndo!.DeviceId);
        Assert.Equal(CommandType.PumpOff, secondUndo.Type);
    }

    [Fact]
    public void Undo_OnEmptyStack_ReturnsNull()
    {
        Assert.Null(new CommandHistory().Undo());
    }

    [Fact]
    public void Undo_CannotUndoAnUndoCommand()
    {
        var history = new CommandHistory();
        history.Issue("DEV1", CommandType.PumpOn, "test");

        var undo = history.Undo();

        Assert.NotNull(undo);
        Assert.True(undo.IsUndo);
        Assert.Null(history.Undo());
        Assert.Equal(0, history.UndoDepth);
    }

    [Fact]
    public void UndoStack_KeepsOnlyTheMostRecent200Commands()
    {
        var history = new CommandHistory();
        for (var i = 0; i < 201; i++)
        {
            history.Issue($"DEV{i}", CommandType.PumpOn, "test");
        }

        Assert.Equal(200, history.UndoDepth);
        Assert.Equal("DEV200", history.Undo()!.DeviceId);
        for (var i = 199; i >= 1; i--)
        {
            Assert.Equal($"DEV{i}", history.Undo()!.DeviceId);
        }

        Assert.Null(history.Undo());
    }

    [Fact]
    public void UndoDepth_TracksStackSize_AndLogKeepsEverything()
    {
        var history = new CommandHistory();
        history.Issue("DEV1", CommandType.ValveOpen, "test");
        history.Issue("DEV1", CommandType.ValveClose, "test");
        Assert.Equal(2, history.UndoDepth);

        history.Undo();
        Assert.Equal(1, history.UndoDepth);
        Assert.Equal(3, history.Snapshot().Count);
        Assert.True(history.Snapshot()[0].IsUndo);
    }

    [Theory]
    [InlineData(CommandType.PumpOn, CommandType.PumpOff)]
    [InlineData(CommandType.RelayOff, CommandType.RelayOn)]
    [InlineData(CommandType.ValveOpen, CommandType.ValveClose)]
    public void Inverse_IsSymmetric(CommandType type, CommandType expected)
    {
        Assert.Equal(expected, type.Inverse());
        Assert.Equal(type, type.Inverse().Inverse());
    }
}
