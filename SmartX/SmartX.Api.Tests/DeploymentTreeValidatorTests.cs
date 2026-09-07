using SmartX.Core.Devices;
using SmartX.Core.Telemetry;
using Xunit;

namespace SmartX.Api.Tests;

public class DeploymentTreeValidatorTests
{
    private static DeploymentNode BuildValidTree()
    {
        var device = new DeploymentNode
        {
            Id = "DEV1",
            Name = "ENV-DEV1",
            NodeType = DeploymentNodeType.Device,
            Sensor = new SensorProfile
            {
                MacAddress = "02:AA:BB:CC:DD:01",
                Category = SensorCategory.Environmental,
                LocationDescription = "Sub-Zone B, Bench 1"
            }
        };

        var subZone = new DeploymentNode
        {
            Id = "SUBZONE-B",
            Name = "Sub-Zone B",
            NodeType = DeploymentNodeType.SubZone,
            Children = [device]
        };

        var zone = new DeploymentNode
        {
            Id = "ZONE-1",
            Name = "Zone 1",
            NodeType = DeploymentNodeType.Zone,
            Children = [subZone]
        };

        return new DeploymentNode
        {
            Id = "FACILITY-A",
            Name = "Facility A",
            NodeType = DeploymentNodeType.Facility,
            Children = [zone]
        };
    }

    [Fact]
    public void ValidTree_PassesValidation()
    {
        var result = DeploymentTreeValidator.Validate(BuildValidTree());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void DuplicateMacAddress_FailsValidation()
    {
        var root = BuildValidTree();
        var duplicateDevice = new DeploymentNode
        {
            Id = "DEV2",
            Name = "ENV-DEV2",
            NodeType = DeploymentNodeType.Device,
            Sensor = new SensorProfile
            {
                MacAddress = "02:AA:BB:CC:DD:01", // same MAC as DEV1
                Category = SensorCategory.Environmental,
                LocationDescription = "Sub-Zone B, Bench 2"
            }
        };
        root.Children[0].Children[0].Children.Add(duplicateDevice);

        var result = DeploymentTreeValidator.Validate(root);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Duplicate MAC address"));
    }

    [Fact]
    public void DeviceUnderUnnamedAncestor_FailsValidation()
    {
        var device = new DeploymentNode
        {
            Id = "DEV1",
            Name = "ENV-DEV1",
            NodeType = DeploymentNodeType.Device,
            Sensor = new SensorProfile
            {
                MacAddress = "02:AA:BB:CC:DD:02",
                Category = SensorCategory.Environmental,
                LocationDescription = "Unnamed sub-zone"
            }
        };

        var unnamedSubZone = new DeploymentNode
        {
            Id = "SUBZONE-X",
            Name = "", // ancestor not configured
            NodeType = DeploymentNodeType.SubZone,
            Children = [device]
        };

        var root = new DeploymentNode
        {
            Id = "FACILITY-A",
            Name = "Facility A",
            NodeType = DeploymentNodeType.Facility,
            Children = [unnamedSubZone]
        };

        var result = DeploymentTreeValidator.Validate(root);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("not configured"));
    }

    [Fact]
    public void DeviceWithoutSensorProfile_FailsValidation()
    {
        var device = new DeploymentNode
        {
            Id = "DEV1",
            Name = "ENV-DEV1",
            NodeType = DeploymentNodeType.Device,
            Sensor = null
        };

        var root = new DeploymentNode
        {
            Id = "FACILITY-A",
            Name = "Facility A",
            NodeType = DeploymentNodeType.Facility,
            Children = [device]
        };

        var result = DeploymentTreeValidator.Validate(root);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no sensor profile"));
    }
}
