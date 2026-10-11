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
