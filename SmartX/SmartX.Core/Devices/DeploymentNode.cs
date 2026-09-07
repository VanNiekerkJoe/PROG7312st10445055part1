using SmartX.Core.Telemetry;

namespace SmartX.Core.Devices;

public enum DeploymentNodeType
{
    Facility,
    Zone,
    SubZone,
    Device
}

/// <summary>
/// Registration record for a single physical/simulated sensor, attached to a
/// Device-type DeploymentNode leaf.
/// </summary>
public sealed class SensorProfile
{
    public required string MacAddress { get; init; }
    public required SensorCategory Category { get; init; }

    /// <summary>Human-readable deployment location, e.g. "Greenhouse 2 / Zone B".</summary>
    public required string LocationDescription { get; init; }

    /// <summary>
    /// File names of attached configuration files, deployment photos, or
    /// hardware logs uploaded via the multipart attachment endpoint.
    /// </summary>
    public List<string> Attachments { get; init; } = [];
}

/// <summary>
/// One node in the Smart-X facility deployment tree:
/// Facility -> Zone -> Sub-Zone -> Device.
/// Only Device-type nodes carry a SensorProfile; the rest are purely
/// structural/grouping nodes.
/// </summary>
public sealed class DeploymentNode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DeploymentNodeType NodeType { get; init; }
    public SensorProfile? Sensor { get; init; }
    public List<DeploymentNode> Children { get; init; } = [];
}
