using SmartX.Api.Services;
using SmartX.Core.Collections;
using SmartX.Core.Mesh;
using SmartX.Core.Storage;
using SmartX.Core.Telemetry;
using Xunit;

namespace SmartX.Api.Tests;

public class AvlTreeTests
{
    [Fact]
    public void Insert_KeepsKeysOrdered_AndTreeBalanced_EvenForSortedInput()
    {
        var tree = new AvlTree<int, int>();
        for (var i = 0; i < 10_000; i++)
        {
            tree.Set(i, i); // sorted input would degrade a plain BST to a linked list
        }

        Assert.Equal(10_000, tree.Count);
        Assert.True(tree.Height <= 15, $"height {tree.Height} should be about log2(n)");
        Assert.Equal(Enumerable.Range(0, 10_000), tree.InOrder().Select(p => p.Key));
    }

    [Fact]
    public void Range_ReturnsOnlyKeysInsideWindow_AndVisitsFewNodes()
    {
        var tree = new AvlTree<int, int>();
        foreach (var key in Enumerable.Range(0, 20_000).OrderBy(k => (k * 7919) % 20_000))
        {
            tree.Set(key, key);
        }

        var (items, stats) = tree.Range(5_000, 5_099);

        Assert.Equal(100, items.Count);
        Assert.Equal(5_000, items[0].Key);
        Assert.Equal(5_099, items[^1].Key);
        Assert.True(stats.NodesVisited < 250, $"visited {stats.NodesVisited} of {stats.TotalStored}");
    }

    [Fact]
    public void Remove_RebalancesAndKeepsRemainingKeysSorted()
    {
        var tree = new AvlTree<int, string>();
        for (var i = 0; i < 1_000; i++)
        {
            tree.Set(i, i.ToString());
        }

        for (var i = 0; i < 1_000; i += 2)
        {
            Assert.True(tree.Remove(i));
        }

        Assert.False(tree.Remove(0));
        Assert.Equal(500, tree.Count);
        Assert.True(tree.Height <= 12);
        Assert.Equal(Enumerable.Range(0, 500).Select(i => i * 2 + 1), tree.InOrder().Select(p => p.Key));
    }

    [Fact]
    public void Last_ReturnsNewestEntriesOldestFirst()
    {
        var tree = new AvlTree<int, int>();
        for (var i = 1; i <= 10; i++)
        {
            tree.Set(i, i);
        }

        Assert.Equal(new[] { 8, 9, 10 }, tree.Last(3).Select(p => p.Key));
    }

    [Fact]
    public void SortedHistoryLog_Range_ReturnsReadingsBetweenTwoTimestamps()
    {
        var log = new SortedHistoryLog(capacityPerDevice: 1_000);
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        for (var i = 0; i < 600; i++)
        {
            log.Append("NODE-A", i, AnomalySeverity.Healthy, start.AddSeconds(i * 10));
        }

        var (points, stats) = log.Range("NODE-A", start.AddSeconds(1_000), start.AddSeconds(1_090));

        Assert.Equal(10, points.Count);
        Assert.Equal(100, points[0].Value);
        Assert.Equal(109, points[^1].Value);
        Assert.True(stats.NodesVisited < stats.TotalStored);
        Assert.Empty(log.Range("UNKNOWN", start, start.AddHours(1)).Points);
    }
}

public class MinHeapTests
{
    [Fact]
    public void Dequeue_ReturnsElementsInAscendingPriorityOrder()
    {
        var heap = new MinHeap<string, int>();
        foreach (var n in new[] { 9, 3, 7, 1, 8, 2, 5 })
        {
            heap.Enqueue($"n{n}", n);
        }

        var order = new List<int>();
        while (heap.TryDequeue(out _, out var priority))
        {
            order.Add(priority);
        }

        Assert.Equal(new[] { 1, 2, 3, 5, 7, 8, 9 }, order);
        Assert.False(heap.TryDequeue(out _, out _));
    }
}

public class MeshGraphTests
{
    private static MeshGraph SampleMesh()
    {
        // G --1-- A --2-- B
        //  \      |      /
        //   4     5    1
        //    \    |   /
        //       C ----
        var g = new MeshGraph("G");
        g.AddEdge("G", "A", 1);
        g.AddEdge("A", "B", 2);
        g.AddEdge("A", "C", 5);
        g.AddEdge("B", "C", 1);
        g.AddEdge("G", "C", 4);
        return g;
    }

    [Fact]
    public void Prim_ProducesMinimumTotalCost_AndMatchesKruskal()
    {
        var g = SampleMesh();

        var mst = g.Prim();

        Assert.Equal(4, mst.TotalCost); // G-A (1) + A-B (2) + B-C (1)
        Assert.Equal(3, mst.Edges.Count);
        Assert.Equal(mst.TotalCost, g.KruskalTotalCost(out _));
        Assert.Equal(new[] { "C", "B", "A", "G" }, MeshGraph.PathToRoot(mst, "C"));
    }

    [Fact]
    public void Prim_AndKruskal_AgreeOnManyRandomMeshes()
    {
        var random = new Random(7);
        for (var trial = 0; trial < 100; trial++)
        {
            var g = new MeshGraph("G");
            var n = 4 + random.Next(10);
            for (var i = 1; i < n; i++)
            {
                g.AddEdge(i == 1 ? "G" : "N" + random.Next(1, i), "N" + i, 1 + random.Next(9));
            }

            for (var extra = 0; extra < n; extra++)
            {
                g.AddEdge("N" + random.Next(1, n), random.Next(2) == 0 ? "G" : "N" + random.Next(1, n), 1 + random.Next(9));
            }

            Assert.Equal(g.Prim().TotalCost, g.KruskalTotalCost(out _));
        }
    }

    [Fact]
    public void FailedNode_IsSkippedByTraversal_AndRouteHealsAround()
    {
        var g = SampleMesh();
        var before = MeshGraph.PathToRoot(g.Prim(), "B");
        Assert.Equal(new[] { "B", "A", "G" }, before);

        g.SetDown("A", true);

        Assert.DoesNotContain("A", g.Bfs("G"));
        Assert.DoesNotContain("A", g.Dfs("G"));
        Assert.Equal(new[] { "B", "C", "G" }, MeshGraph.PathToRoot(g.Prim(), "B"));
    }

    [Fact]
    public void ShortestPath_CanBeCheaperForOneNodeThanItsSpanningTreeRoute()
    {
        // G-A 2, G-B 2, A-B 1: the MST takes G-A then A-B (total 3), so B routes via A at cost 3,
        // but B's own cheapest path is the direct link at cost 2.
        var g = new MeshGraph("G");
        g.AddEdge("G", "A", 2);
        g.AddEdge("G", "B", 2);
        g.AddEdge("A", "B", 1);

        var mst = g.Prim();
        var shortest = g.ShortestPaths();

        Assert.Equal(3, g.PathCost(MeshGraph.PathToRoot(mst, "B")));
        Assert.Equal(2, shortest.Distance["B"]);
        Assert.Equal(new[] { "B", "G" }, MeshGraph.PathToRoot(shortest, "B"));
        Assert.Equal(3, mst.TotalCost);
    }

    [Fact]
    public void ShortestPath_NeverCostsMoreThanTheTreeRoute_AndSkipsDownNodes()
    {
        var random = new Random(11);
        for (var trial = 0; trial < 100; trial++)
        {
            var g = new MeshGraph("G");
            var n = 5 + random.Next(10);
            for (var i = 1; i < n; i++)
            {
                g.AddEdge(i == 1 ? "G" : "N" + random.Next(1, i), "N" + i, 1 + random.Next(9));
            }

            for (var extra = 0; extra < n; extra++)
            {
                g.AddEdge("N" + random.Next(1, n), random.Next(2) == 0 ? "G" : "N" + random.Next(1, n), 1 + random.Next(9));
            }

            g.SetDown("N" + random.Next(1, n), true);
            var mst = g.Prim();
            var shortest = g.ShortestPaths();

            Assert.Equal(mst.ParentOf.Count, shortest.ParentOf.Count);
            foreach (var node in mst.ParentOf.Keys)
            {
                Assert.True(shortest.Distance[node] <= g.PathCost(MeshGraph.PathToRoot(mst, node)));
                Assert.Equal(shortest.Distance[node], g.PathCost(MeshGraph.PathToRoot(shortest, node)));
            }
        }
    }

    [Fact]
    public void HopsOf_ListsEachLinkWithItsCost()
    {
        var g = SampleMesh();

        var hops = g.HopsOf(new[] { "B", "A", "G" });

        Assert.Equal(2, hops.Count);
        Assert.Equal(2, hops[0].Cost);
        Assert.Equal("G", hops[1].To);
    }

    [Fact]
    public void Connectivity_ReportsDownAndStrandedNodes()
    {
        var g = new MeshGraph("G");
        g.AddEdge("G", "A", 1);
        g.AddEdge("A", "B", 1);
        g.SetDown("A", true);

        var (connected, down, stranded) = g.Connectivity();

        Assert.Empty(connected);
        Assert.Equal(new[] { "A" }, down);
        Assert.Equal(new[] { "B" }, stranded);
    }

    [Fact]
    public void Bfs_VisitsInRingsOfHops_AndDfs_FollowsChains()
    {
        var g = new MeshGraph("G");
        g.AddEdge("G", "A", 1);
        g.AddEdge("G", "B", 1);
        g.AddEdge("A", "A1", 1);
        g.AddEdge("B", "B1", 1);

        Assert.Equal(new[] { "G", "A", "B", "A1", "B1" }, g.Bfs("G"));
        Assert.Equal(new[] { "G", "A", "A1", "B", "B1" }, g.Dfs("G"));
    }

    [Fact]
    public void TopologyBuilder_IsDeterministic_AndConnectsEveryDevice()
    {
        var groups = new List<MeshGroup>
        {
            new("S1", ["S1-D1", "S1-D2", "S1-D3"]),
            new("S2", ["S2-D1", "S2-D2"])
        };

        var first = MeshTopologyBuilder.Build(groups);
        var second = MeshTopologyBuilder.Build(groups);

        Assert.Equal(first.Edges.Select(e => (e.A, e.B, e.Cost)), second.Edges.Select(e => (e.A, e.B, e.Cost)));
        Assert.Equal(5, first.Bfs(MeshTopologyBuilder.GatewayId).Count - 1);
        Assert.True(first.Edges.All(e => e.Cost is >= 1 and <= 9));
    }
}

public class MeshTopologyServiceTests
{
    [Fact]
    public void Topology_CoversEveryRegisteredDevice_AndPrimMatchesKruskal()
    {
        var registry = new SensorRegistry();
        var service = new MeshTopologyService(registry, new SortedHistoryLog());

        var topology = service.Topology();

        Assert.Equal(registry.DeviceNodes.Count + 1, topology.Nodes.Count);
        Assert.Equal(registry.DeviceNodes.Count, topology.ConnectedCount);
        Assert.Equal(topology.MstCost, topology.KruskalCost);
    }

    [Fact]
    public void Route_CanBeLookedUpByMac_AndHealsWhenAnUplinkFails()
    {
        var registry = new SensorRegistry();
        var service = new MeshTopologyService(registry, new SortedHistoryLog());
        var device = registry.DeviceNodes.First(d => d.Id == "ZA-SUB1-DEV3");

        var healthy = service.Route(device.Sensor!.MacAddress.ToLowerInvariant());
        Assert.True(healthy.Found);
        Assert.False(healthy.Rerouted);
        Assert.Equal("GATEWAY", healthy.LivePath[^1]);
        Assert.Equal(healthy.LivePath.Count - 1, healthy.Hops.Count);
        Assert.True(healthy.ShortestCost <= healthy.LiveCost);

        var uplink = healthy.LivePath[^2];
        Assert.True(service.Fail(uplink));
        var healed = service.Route("ZA-SUB1-DEV3");

        Assert.True(healed.Rerouted);
        Assert.DoesNotContain(uplink, healed.LivePath);
        Assert.Equal("GATEWAY", healed.LivePath[^1]);

        service.RestoreAll();
        Assert.False(service.Route("ZA-SUB1-DEV3").Rerouted);
    }

    [Fact]
    public void Route_UnknownIdentifier_IsNotFound()
    {
        var service = new MeshTopologyService(new SensorRegistry(), new SortedHistoryLog());

        Assert.False(service.Route("00:00:00:00:00:00").Found);
    }

    [Fact]
    public void HistoryRange_UsesTheTree_AndOnlyVisitsPartOfIt()
    {
        var registry = new SensorRegistry();
        var log = new SortedHistoryLog();
        var service = new MeshTopologyService(registry, log);

        Assert.Equal(1_500, service.Backfill("ZA-SUB1-DEV1", 1_500, TimeSpan.FromHours(6)));
        var now = DateTimeOffset.UtcNow;
        var result = service.HistoryRange("ZA-SUB1-DEV1", now.AddHours(-3), now.AddHours(-2));

        Assert.True(result.Found);
        Assert.InRange(result.Matched, 200, 300);
        Assert.True(result.NodesVisited < result.TotalStored / 2);
    }
}
