using Microsoft.AspNetCore.SignalR;

namespace SmartX.Api.Hubs;

/// <summary>
/// Push channel for the Real-Time Command Stream pillar (kept separate from
/// TelemetryHub so the two pillars don't share a connection lifecycle).
/// Server -> client events: CommandIssued, CommandUndone, PriorityAlert,
/// PipelineStats. Clients only listen; commands are issued over REST.
/// </summary>
public sealed class CommandHub : Hub
{
}
