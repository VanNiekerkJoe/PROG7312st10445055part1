using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Builds the default simulated Smart-X deployment tree: one facility, three
/// zones, each with sub-zones and a handful of devices. This is the "heavily
/// seeded with mock telemetry data" environment the assignment calls for:
/// simple randomised values, not modelled physics.
/// </summary>
public static class SeedData
{
    public static DeploymentNode BuildDefaultTree()
    {
        var random = new Random(42);

        DeploymentNode Device(string subZoneId, int index, SensorCategory category, string locationLabel)
        {
            var mac = RandomMac(random);
            return new DeploymentNode
            {
                Id = $"{subZoneId}-DEV{index}",
                Name = $"{CategoryPrefix(category)}-{subZoneId}-{index:D2}",
                NodeType = DeploymentNodeType.Device,
                Sensor = new SensorProfile
                {
                    MacAddress = mac,
                    Category = category,
                    LocationDescription = locationLabel
                }
            };
        }

        var zoneA = new DeploymentNode
        {
            Id = "ZONE-A",
            Name = "Zone A",
            NodeType = DeploymentNodeType.Zone,
            Children =
            [
                new DeploymentNode
                {
                    Id = "ZA-SUB1",
                    Name = "Sub-Zone A1 (Hydroponic Bay)",
                    NodeType = DeploymentNodeType.SubZone,
                    Children = Enumerable.Range(1, 4)
                        .Select(i => Device("ZA-SUB1", i, SensorCategory.Environmental, "Hydroponic Bay, Bench " + i))
                        .ToList()
                }
            ]
        };

        var zoneB = new DeploymentNode
        {
            Id = "ZONE-B",
            Name = "Zone B",
            NodeType = DeploymentNodeType.Zone,
            Children =
            [
                new DeploymentNode
                {
                    Id = "ZB-SUB1",
                    Name = "Sub-Zone B1 (Utility Room)",
                    NodeType = DeploymentNodeType.SubZone,
                    Children = Enumerable.Range(1, 3)
                        .Select(i => Device("ZB-SUB1", i, SensorCategory.Power, "Utility Room, Panel " + i))
                        .ToList()
                },
                new DeploymentNode
                {
                    Id = "ZB-SUB2",
                    Name = "Sub-Zone B2 (Valve Bank)",
                    NodeType = DeploymentNodeType.SubZone,
                    Children = Enumerable.Range(1, 3)
                        .Select(i => Device("ZB-SUB2", i, SensorCategory.Actuator, "Valve Bank, Line " + i))
                        .ToList()
                }
            ]
        };

        var zoneC = new DeploymentNode
        {
            Id = "ZONE-C",
            Name = "Zone C",
            NodeType = DeploymentNodeType.Zone,
            Children =
            [
                new DeploymentNode
                {
                    Id = "ZC-SUB1",
                    Name = "Sub-Zone C1 (Grid Substation)",
                    NodeType = DeploymentNodeType.SubZone,
                    Children = Enumerable.Range(1, 4)
                        .Select(i => Device("ZC-SUB1", i, SensorCategory.Power, "Grid Substation, Meter " + i))
                        .ToList()
                }
            ]
        };

        return new DeploymentNode
        {
            Id = "FACILITY-A",
            Name = "Facility A",
            NodeType = DeploymentNodeType.Facility,
            Children = [zoneA, zoneB, zoneC]
        };
    }

    private static string CategoryPrefix(SensorCategory category) => category switch
    {
        SensorCategory.Environmental => "ENV",
        SensorCategory.Power => "PWR",
        SensorCategory.Actuator => "ACT",
        _ => "GEN"
    };

    private static string RandomMac(Random random)
    {
        var bytes = new byte[6];
        random.NextBytes(bytes);
        bytes[0] = (byte)(bytes[0] & 0xFE | 0x02); // locally administered, unicast
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }
}
