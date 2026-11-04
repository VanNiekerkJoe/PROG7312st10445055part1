using System.Diagnostics;
using SmartX.Core.Devices;
using SmartX.Core.Mesh;
using SmartX.Core.Storage;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Part 3 service layer: builds the mesh graph from the live deployment tree, applies node
/// failures (real disconnects detected by the ingest pipeline plus operator-simulated outages),
/// and answers routing, traversal and historical-range questions for the Network Topology page.
/// The graph is rebuilt per request; at thousands of nodes that is still milliseconds, and it means
/// devices registered or removed in Part 1 appear in the mesh immediately with no extra wiring.
/// </summary>
public sealed class MeshTopologyService(SensorRegistry registry, SortedHistoryLog history)
{
    private readonly object _lock = new();
    private readonly HashSet<string> _simulatedDown = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ graph assembly

    private sealed record Snapshot(
        MeshGraph Graph,
        Dictionary<string, DeploymentNode> Devices,
        Dictionary<string, string> SubnetOf,
        Dictionary<string, string> DownReason);

    private Snapshot BuildSnapshot(bool applyFailures)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var devices = new Dictionary<string, DeploymentNode>(StringComparer.Ordinal);
        var subnetOf = new Dictionary<string, string>(StringComparer.Ordinal);

        void Walk(DeploymentNode node, string subnet)
        {
            var current = node.NodeType is DeploymentNodeType.SubZone or DeploymentNodeType.Zone or DeploymentNodeType.Facility
                ? node.Id
                : subnet;

            if (node.NodeType == DeploymentNodeType.Device && node.Sensor is not null)
            {
                devices[node.Id] = node;
                subnetOf[node.Id] = subnet;
                if (!groups.TryGetValue(subnet, out var list))
                {
                    list = [];
                    groups[subnet] = list;
                }

                list.Add(node.Id);
            }

            foreach (var child in node.Children.ToList())
            {
                Walk(child, current);
            }
        }

        Walk(registry.Root, registry.Root.Id);

        var graph = MeshTopologyBuilder.Build(groups.Select(g => new MeshGroup(g.Key, g.Value)).ToList());
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);

        if (applyFailures)
        {
            foreach (var status in registry.AllStatuses.Where(s => s.Severity == AnomalySeverity.Disconnected))
            {
                if (graph.HasNode(status.DeviceId))
                {
                    graph.SetDown(status.DeviceId, true);
                    reasons[status.DeviceId] = "No heartbeat (device reported disconnected)";
                }
            }

            lock (_lock)
            {
                foreach (var id in _simulatedDown)
                {
                    if (graph.HasNode(id))
                    {
                        graph.SetDown(id, true);
                        reasons[id] = "Simulated outage (operator)";
                    }
                }
            }
        }

        return new Snapshot(graph, devices, subnetOf, reasons);
    }

    private static string? Resolve(Snapshot snap, string query)
    {
        var q = query.Trim();
        if (snap.Devices.ContainsKey(q))
        {
            return q;
        }

        var byId = snap.Devices.Keys.FirstOrDefault(id => string.Equals(id, q, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId;
        }

        return snap.Devices.Values
            .FirstOrDefault(d => string.Equals(d.Sensor!.MacAddress, q, StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }

    // ------------------------------------------------------------------ topology

    public MeshTopologyResponse Topology()
    {
        var snap = BuildSnapshot(applyFailures: true);
        var graph = snap.Graph;
        var mst = graph.Prim();
        var kruskal = graph.KruskalTotalCost(out _);
        var connectivity = graph.Connectivity();

        var mstKeys = mst.Edges.Select(e => (e.A, e.B)).ToHashSet();

        var nodes = graph.Nodes
            .OrderBy(n => n == graph.GatewayId ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal)
            .Select(id =>
            {
                if (id == graph.GatewayId)
                {
                    return new MeshNodeDto(id, "Gateway", true, null, null, null, false, null, 0, null);
                }

                var node = snap.Devices[id];
                mst.ParentOf.TryGetValue(id, out var parent);
                mst.HopsToRoot.TryGetValue(id, out var hops);
                snap.DownReason.TryGetValue(id, out var reason);
                return new MeshNodeDto(
                    id, node.Name, false, node.Sensor!.MacAddress, snap.SubnetOf[id], node.Sensor.Category,
                    graph.IsDown(id), reason, parent is null ? -1 : hops, parent);
            })
            .ToList();

        var edges = graph.Edges
            .Select(e => new MeshEdgeDto(e.A, e.B, e.Cost, mstKeys.Contains((e.A, e.B))))
            .ToList();

        return new MeshTopologyResponse(
            nodes, edges, mst.TotalCost, kruskal,
            connectivity.Connected.Count, connectivity.Down.Count, connectivity.Stranded.Count,
            graph.Bfs(graph.GatewayId));
    }

    // ------------------------------------------------------------------ routing

    public MeshRouteResponse Route(string query)
    {
        var healthy = BuildSnapshot(applyFailures: false);
        var id = Resolve(healthy, query);
        if (id is null)
        {
            return new MeshRouteResponse(query, false, null, null, $"No sensor matches \"{query}\". Try a MAC address or a device ID.",
                [], 0, [], 0, false, []);
        }

        var mac = healthy.Devices[id].Sensor!.MacAddress;
        var normalMst = healthy.Graph.Prim();
        var normalPath = MeshGraph.PathToRoot(normalMst, id);
        var normalCost = healthy.Graph.PathCost(normalPath);

        var live = BuildSnapshot(applyFailures: true);
        if (live.Graph.IsDown(id))
        {
            var hops = live.Graph.LinksOf(id)
                .Select(e => e.Other(id))
                .Where(n => !live.Graph.IsDown(n))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            return new MeshRouteResponse(query, true, id, mac,
                $"{id} is down ({live.DownReason[id]}). Neighbouring nodes that can still carry traffic are listed as alternative hops.",
                normalPath, normalCost, [], 0, false, hops);
        }

        var liveMst = live.Graph.Prim();
        var livePath = MeshGraph.PathToRoot(liveMst, id);
        if (livePath.Count == 0)
        {
            return new MeshRouteResponse(query, true, id, mac,
                $"{id} is alive but stranded: every path to the gateway crosses a failed node.",
                normalPath, normalCost, [], 0, false, []);
        }

        var liveCost = live.Graph.PathCost(livePath);
        var rerouted = !livePath.SequenceEqual(normalPath);
        return new MeshRouteResponse(query, true, id, mac,
            rerouted
                ? $"Route healed around a failure: {livePath.Count - 1} hops, cost {liveCost} (was {normalCost})."
                : $"Healthy route: {livePath.Count - 1} hops, cost {liveCost}.",
            normalPath, normalCost, livePath, liveCost, rerouted, []);
    }

    // ------------------------------------------------------------------ traversal

    public MeshSubnetResponse Subnet(string? subnetId, TraversalKind kind)
    {
        var healthy = BuildSnapshot(applyFailures: false);
        var live = BuildSnapshot(applyFailures: true);
        var graph = live.Graph;

        HashSet<string>? members = null;
        var label = "Whole network (gateway)";
        if (!string.IsNullOrWhiteSpace(subnetId))
        {
            members = live.SubnetOf.Where(p => string.Equals(p.Value, subnetId, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            label = subnetId;
        }

        // Traverse the whole live mesh from the gateway (a subnet's packets may leave the subnet to
        // reach the gateway), then keep the part belonging to the requested subnet.
        var order = graph.Traverse(kind, graph.GatewayId);
        var reached = order.Where(n => n != graph.GatewayId && (members is null || members.Contains(n))).ToList();
        var reachedSet = reached.ToHashSet(StringComparer.Ordinal);

        var scope = members ?? live.Devices.Keys.ToHashSet(StringComparer.Ordinal);
        var down = scope.Where(graph.IsDown).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var stranded = scope.Where(n => !graph.IsDown(n) && !reachedSet.Contains(n)).OrderBy(x => x, StringComparer.Ordinal).ToList();

        var normalMst = healthy.Graph.Prim();
        var liveMst = graph.Prim();
        var detours = new List<MeshDetour>();
        foreach (var id in scope.Where(n => !graph.IsDown(n)).OrderBy(n => n, StringComparer.Ordinal))
        {
            var oldPath = MeshGraph.PathToRoot(normalMst, id);
            if (!oldPath.Any(graph.IsDown))
            {
                continue; // this node's normal route never touched a failed node
            }

            var newPath = MeshGraph.PathToRoot(liveMst, id);
            detours.Add(new MeshDetour(id, oldPath, newPath, healthy.Graph.PathCost(oldPath),
                newPath.Count == 0 ? 0 : graph.PathCost(newPath)));
        }

        return new MeshSubnetResponse(label, kind.ToString().ToUpperInvariant(), order, reached, down, stranded, detours);
    }

    public IReadOnlyList<string> Subnets() =>
        BuildSnapshot(applyFailures: false).SubnetOf.Values.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

    // ------------------------------------------------------------------ failure simulation

    public bool Fail(string query)
    {
        var snap = BuildSnapshot(false);
        var id = Resolve(snap, query);
        if (id is null)
        {
            return false;
        }

        lock (_lock)
        {
            _simulatedDown.Add(id);
        }

        return true;
    }

    public bool Restore(string query)
    {
        var snap = BuildSnapshot(false);
        var id = Resolve(snap, query);
        if (id is null)
        {
            return false;
        }

        lock (_lock)
        {
            _simulatedDown.Remove(id);
        }

        return true;
    }

    public void RestoreAll()
    {
        lock (_lock)
        {
            _simulatedDown.Clear();
        }
    }

    // ------------------------------------------------------------------ historical queries (AVL)

    public HistoryRangeResponse HistoryRange(string query, DateTimeOffset from, DateTimeOffset to)
    {
        var snap = BuildSnapshot(false);
        var id = Resolve(snap, query);
        if (id is null)
        {
            return new HistoryRangeResponse(false, $"No sensor matches \"{query}\".", null, null, from, to, [], 0, 0, 0, 0, 0, null, null, null);
        }

        if (to < from)
        {
            (from, to) = (to, from);
        }

        var watch = Stopwatch.StartNew();
        var (points, stats) = history.Range(id, from, to);
        watch.Stop();

        var mac = snap.Devices[id].Sensor!.MacAddress;
        var values = points.Select(p => p.Value).ToList();
        return new HistoryRangeResponse(
            true,
            points.Count == 0 ? "No readings stored in that window." : $"{points.Count} readings in window.",
            id, mac, from, to, points, stats.Matched, stats.NodesVisited, stats.TotalStored, stats.TreeHeight,
            watch.Elapsed.TotalMilliseconds * 1000.0,
            values.Count > 0 ? values.Min() : null,
            values.Count > 0 ? values.Max() : null,
            values.Count > 0 ? values.Average() : null);
    }

    /// <summary>Demo helper: fills a device's history with timestamped readings so range queries have something to chew on.</summary>
    public int Backfill(string query, int count, TimeSpan span)
    {
        var snap = BuildSnapshot(false);
        var id = Resolve(snap, query);
        if (id is null)
        {
            return -1;
        }

        count = Math.Clamp(count, 1, 3000);
        var category = snap.Devices[id].Sensor!.Category;
        var random = new Random(id.GetHashCode(StringComparison.Ordinal) ^ count);
        var now = DateTimeOffset.UtcNow;
        var step = span.TotalSeconds / count;

        for (var i = 0; i < count; i++)
        {
            var at = now - span + TimeSpan.FromSeconds(step * i);
            var wave = Math.Sin(i / 40.0);
            var value = category switch
            {
                SensorCategory.Environmental => 55 + wave * 12 + random.NextDouble() * 4,
                SensorCategory.Power => 480 + wave * 160 + random.NextDouble() * 30,
                _ => random.NextDouble() > 0.5 ? 1 : 0
            };
            history.Append(id, Math.Round(value, 2), AnomalySeverity.Healthy, at);
        }

        return count;
    }
}
