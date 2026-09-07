using Microsoft.AspNetCore.SignalR;

namespace SmartX.Api.Hubs;

/// <summary>
/// Push channel for the Sensor Ingestion pillar. The seeder (or real ingestion
/// endpoint) calls IHubContext&lt;TelemetryHub&gt; to broadcast DeviceStatus
/// updates; clients don't call server methods on this hub in Part 1, they
/// just listen. Command Stream (Part 2) is a separate hub, kept apart so the
/// two pillars don't share a connection lifecycle.
/// </summary>
public sealed class TelemetryHub : Hub
{
    public override Task OnConnectedAsync()
    {
        return base.OnConnectedAsync();
    }
}
