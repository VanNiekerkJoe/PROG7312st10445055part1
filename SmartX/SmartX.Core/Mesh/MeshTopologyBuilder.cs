namespace SmartX.Core.Mesh;

/// <summary>One subnet: a sub-zone of the deployment tree and the devices in it.</summary>
public sealed record MeshGroup(string GroupId, IReadOnlyList<string> DeviceIds);

/// <summary>
/// Turns the deployment tree into a plausible mesh. There is no real radio data in the
/// simulation, so links are generated deterministically: the same fleet always produces the
/// same mesh and the same link costs, which keeps demos and tests repeatable.
///   - each subnet is a ring of relays (every node hears its two neighbours);
///   - the first device of each subnet is the subnet's uplink to the gateway;
///   - the last device of each subnet also hears the next subnet's first device, so subnets
///     back each other up when an uplink fails;
///   - link cost is 1-9 (relative transmit power), from a stable hash of the endpoints.
/// </summary>
public static class MeshTopologyBuilder
{
    public const string GatewayId = "GATEWAY";

    public static MeshGraph Build(IReadOnlyList<MeshGroup> groups, string gatewayId = GatewayId)
    {
        var graph = new MeshGraph(gatewayId);
        var ordered = groups.Where(g => g.DeviceIds.Count > 0).OrderBy(g => g.GroupId, StringComparer.Ordinal).ToList();

        foreach (var group in ordered)
        {
            var ids = group.DeviceIds.OrderBy(x => x, StringComparer.Ordinal).ToList();
            foreach (var id in ids)
            {
                graph.AddNode(id);
            }

            for (var i = 0; i + 1 < ids.Count; i++)
            {
                graph.AddEdge(ids[i], ids[i + 1], LinkCost(ids[i], ids[i + 1]));
            }

            if (ids.Count > 2)
            {
                graph.AddEdge(ids[^1], ids[0], LinkCost(ids[^1], ids[0])); // close the ring
            }

            graph.AddEdge(gatewayId, ids[0], LinkCost(gatewayId, ids[0]));
        }

        for (var g = 0; g + 1 < ordered.Count; g++)
        {
            var left = ordered[g].DeviceIds.OrderBy(x => x, StringComparer.Ordinal).Last();
            var right = ordered[g + 1].DeviceIds.OrderBy(x => x, StringComparer.Ordinal).First();
            graph.AddEdge(left, right, LinkCost(left, right));
        }

        return graph;
    }

    /// <summary>Stable 1-9 cost from an FNV-1a hash of the two endpoint ids (order-independent).</summary>
    public static int LinkCost(string a, string b)
    {
        var (first, second) = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
        var hash = 2166136261u;
        foreach (var ch in first + "|" + second)
        {
            hash = (hash ^ ch) * 16777619u;
        }

        return 1 + (int)(hash % 9u);
    }
}
