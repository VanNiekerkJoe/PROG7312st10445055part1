namespace SmartX.Core.Commands;

/// <summary>
/// The manual override commands an operator can send to a device. Every
/// command has an exact inverse, which is what makes the "Undo" stack work:
/// reverting a command is just issuing its inverse.
/// </summary>
public enum CommandType
{
    PumpOn,
    PumpOff,
    RelayOn,
    RelayOff,
    ValveOpen,
    ValveClose
}

public static class CommandTypeExtensions
{
    public static CommandType Inverse(this CommandType type) => type switch
    {
        CommandType.PumpOn => CommandType.PumpOff,
        CommandType.PumpOff => CommandType.PumpOn,
        CommandType.RelayOn => CommandType.RelayOff,
        CommandType.RelayOff => CommandType.RelayOn,
        CommandType.ValveOpen => CommandType.ValveClose,
        CommandType.ValveClose => CommandType.ValveOpen,
        _ => type
    };

    /// <summary>True when the command leaves the device output energised/open.</summary>
    public static bool TurnsOutputOn(this CommandType type) =>
        type is CommandType.PumpOn or CommandType.RelayOn or CommandType.ValveOpen;

    public static string Label(this CommandType type) => type switch
    {
        CommandType.PumpOn => "Pump ON",
        CommandType.PumpOff => "Pump OFF",
        CommandType.RelayOn => "Relay ON",
        CommandType.RelayOff => "Relay OFF",
        CommandType.ValveOpen => "Valve OPEN",
        CommandType.ValveClose => "Valve CLOSE",
        _ => type.ToString()
    };
}
