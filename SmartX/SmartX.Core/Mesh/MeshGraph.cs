using SmartX.Core.Collections;

namespace SmartX.Core.Mesh;

/// <summary>An undirected wireless link between two nodes. Cost is the relative transmit power for one hop.</summary>
public sealed record MeshEdge(string A, string B, int Cost)
{
    public string Other(string node) => node == A ? B : A;
}

/// <summary>A spanning tree (or forest, if the mesh is partitioned) rooted at the gateway.</summary>
public sealed record MstResult(
    string Root,
    IReadOnlyList<MeshEdge> Edges,
    int TotalCost,
    IReadOnlyDictionary<string, string> ParentOf,
    IReadOnlyDictionary<string, int> HopsToRoot);

/// <summary>Cheapest-path tree from the gateway (Dijkstra): the lowest-cost route for every individual node.</summary>
public sealed record ShortestPathResult(
    string Root,
    IReadOnlyDictionary<string, string> ParentOf,
    IReadOnlyDictionary<string, int> Distance,
    IReadOnlyDictionary<string, int> HopsToRoot);

/// <summary>One hop of a route with its transmit cost.</summary>
public sealed record MeshHop(string From, string To, int Cost);

public enum TraversalKind
{
    Bfs,
    Dfs
}

/// <summary>
/// The IoT mesh as an undirected weighted graph. Vertices are ESP32 sensors plus the gateway;
/// edges are wireless links. Stored as an adjacency list (Dictionary of node to its edges), which
/// is O(V + E) in space and ideal for a sparse mesh where each node only hears a few neighbours.
/// Nodes can be marked down; every algorithm below works on the live graph and simply never
/// walks through a down node, which is how the network "heals" around a failure.
/// </summary>
public sealed class MeshGraph
{
    private readonly Dictionary<string, List<MeshEdge>> _adjacency = new(StringComparer.Ordinal);
    private readonly HashSet<string> _down = new(StringComparer.Ordinal);

    public MeshGraph(string gatewayId)
    {
        GatewayId = gatewayId;
        AddNode(gatewayId);
    }

    public string GatewayId { get; }

    public IReadOnlyCollection<string> Nodes => _adjacency.Keys;

    public IReadOnlyList<MeshEdge> Edges =>
        _adjacency.Values.SelectMany(list => list).Distinct().OrderBy(e => e.A, StringComparer.Ordinal).ThenBy(e => e.B, StringComparer.Ordinal).ToList();

    public bool HasNode(string id) => _adjacency.ContainsKey(id);

    public bool IsDown(string id) => _down.Contains(id);

    public void AddNode(string id)
    {
        if (!_adjacency.ContainsKey(id))
        {
            _adjacency[id] = [];
        }
    }

    public void AddEdge(string a, string b, int cost)
    {
        if (a == b)
        {
            return;
        }

        AddNode(a);
        AddNode(b);

        if (_adjacency[a].Any(e => e.Other(a) == b))
        {
            return; // already linked
        }

        var edge = new MeshEdge(a, b, cost);
        _adjacency[a].Add(edge);
        _adjacency[b].Add(edge);
    }

    public void SetDown(string id, bool down)
    {
        if (!_adjacency.ContainsKey(id) || id == GatewayId)
        {
            return; // the gateway is the sink; it is not allowed to fail in this model
        }

        if (down)
        {
            _down.Add(id);
        }
        else
        {
            _down.Remove(id);
        }
    }

    public IReadOnlyList<string> DownNodes => _down.OrderBy(x => x, StringComparer.Ordinal).ToList();

    public IReadOnlyList<MeshEdge> LinksOf(string id) =>
        _adjacency.TryGetValue(id, out var list) ? list : [];

    // ------------------------------------------------------------------ traversal

    /// <summary>
    /// Breadth-first search: a FIFO queue visits nodes in rings of increasing hop count from
    /// the start. O(V + E). Because it finds the fewest hops, it is the natural way to answer
    /// "which devices are currently connected to this gateway or subnet".
    /// Down nodes are never entered. If <paramref name="within"/> is given, the search is
    /// confined to that node set (a subnet) plus the start.
    /// </summary>
    public IReadOnlyList<string> Bfs(string start, IReadOnlySet<string>? within = null)
    {
        var order = new List<string>();
        if (!_adjacency.ContainsKey(start) || IsDown(start))
        {
            return order;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            order.Add(current);

            foreach (var edge in OrderedLinks(current))
            {
                var next = edge.Other(current);
                if (IsDown(next) || !Allowed(next, within) || !seen.Add(next))
                {
                    continue;
                }

                queue.Enqueue(next);
            }
        }

        return order;
    }

    /// <summary>
    /// Depth-first search using an explicit stack (so a long daisy chain cannot overflow the call
    /// stack). It follows one branch as far as it goes before backtracking, which is what you want
    /// when exploring a chain of relays rather than looking for the nearest node.
    /// </summary>
    public IReadOnlyList<string> Dfs(string start, IReadOnlySet<string>? within = null)
    {
        var order = new List<string>();
        if (!_adjacency.ContainsKey(start) || IsDown(start))
        {
            return order;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(start);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            order.Add(current);

            // Push in reverse so the alphabetically first neighbour is explored first.
            foreach (var edge in OrderedLinks(current).Reverse())
            {
                var next = edge.Other(current);
                if (!IsDown(next) && Allowed(next, within) && !seen.Contains(next))
                {
                    stack.Push(next);
                }
            }
        }

        return order;
    }

    public IReadOnlyList<string> Traverse(TraversalKind kind, string start, IReadOnlySet<string>? within = null) =>
        kind == TraversalKind.Bfs ? Bfs(start, within) : Dfs(start, within);

    /// <summary>
    /// Which live nodes can currently reach the gateway, and which cannot. Down nodes are listed
    /// separately; "stranded" nodes are alive but cut off because every path crosses a down node.
    /// </summary>
    public (IReadOnlyList<string> Connected, IReadOnlyList<string> Down, IReadOnlyList<string> Stranded) Connectivity()
    {
        var connected = Bfs(GatewayId).Where(n => n != GatewayId).ToList();
        var connectedSet = new HashSet<string>(connected, StringComparer.Ordinal);
        var stranded = _adjacency.Keys
            .Where(n => n != GatewayId && !IsDown(n) && !connectedSet.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        return (connected, DownNodes, stranded);
    }

    // ------------------------------------------------------------------ spanning trees

    /// <summary>
    /// Prim's algorithm on the live graph, grown outward from the gateway with a min-heap.
    /// Start with just the gateway; repeatedly pop the cheapest edge leaving the tree; if its far
    /// end is new, add it and push that node's edges. Greedy choice is safe (cut property), so the
    /// result has the minimum total transmit cost. O(E log V) with a binary heap.
    /// ParentOf gives each node's next hop toward the gateway, so a sensor's route is simply
    /// "follow parents until you reach the gateway".
    /// </summary>
    public MstResult Prim()
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        var hops = new Dictionary<string, int>(StringComparer.Ordinal) { [GatewayId] = 0 };
        var inTree = new HashSet<string>(StringComparer.Ordinal) { GatewayId };
        var chosen = new List<MeshEdge>();
        var total = 0;

        var heap = new MinHeap<(MeshEdge Edge, string From), (int Cost, string A, string B)>(
            Comparer<(int Cost, string A, string B)>.Create(CompareEdgeKeys));

        void PushEdgesOf(string node)
        {
            foreach (var edge in _adjacency[node])
            {
                var other = edge.Other(node);
                if (!IsDown(other) && !inTree.Contains(other))
                {
                    heap.Enqueue((edge, node), (edge.Cost, edge.A, edge.B));
                }
            }
        }

        PushEdgesOf(GatewayId);

        while (heap.TryDequeue(out var item, out _))
        {
            var next = item.Edge.Other(item.From);
            if (!inTree.Add(next))
            {
                continue; // a cheaper edge already connected this node
            }

            chosen.Add(item.Edge);
            total += item.Edge.Cost;
            parent[next] = item.From;
            hops[next] = hops[item.From] + 1;
            PushEdgesOf(next);
        }

        return new MstResult(GatewayId, chosen, total, parent, hops);
    }

    /// <summary>
    /// Kruskal's algorithm: sort all live edges by cost and add each one unless it would close a
    /// loop, detected with a union-find (disjoint-set) structure using path compression and union
    /// by size. O(E log E). It produces a tree of the same total cost as Prim's, so the app runs
    /// both and shows that they agree.
    /// </summary>
    public int KruskalTotalCost(out IReadOnlyList<MeshEdge> chosenEdges)
    {
        var liveEdges = Edges
            .Where(e => !IsDown(e.A) && !IsDown(e.B))
            .OrderBy(e => e.Cost).ThenBy(e => e.A, StringComparer.Ordinal).ThenBy(e => e.B, StringComparer.Ordinal)
            .ToList();

        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        var size = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in _adjacency.Keys.Where(n => !IsDown(n)))
        {
            parent[node] = node;
            size[node] = 1;
        }

        string Find(string x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]]; // path compression (halving)
                x = parent[x];
            }

            return x;
        }

        var chosen = new List<MeshEdge>();
        var total = 0;
        foreach (var edge in liveEdges)
        {
            var ra = Find(edge.A);
            var rb = Find(edge.B);
            if (ra == rb)
            {
                continue; // would create a cycle
            }

            if (size[ra] < size[rb])
            {
                (ra, rb) = (rb, ra);
            }

            parent[rb] = ra;
            size[ra] += size[rb];
            chosen.Add(edge);
            total += edge.Cost;
        }

        chosenEdges = chosen;
        return total;
    }

    /// <summary>
    /// Dijkstra's algorithm from the gateway on the live graph, using the same min-heap as Prim's.
    /// Where the MST minimises the total cost of the whole network, this minimises each sensor's own
    /// cost to the gateway, so the two can differ; the page shows both so the trade-off is visible.
    /// O(E log V).
    /// </summary>
    public ShortestPathResult ShortestPaths()
    {
        var distance = new Dictionary<string, int>(StringComparer.Ordinal) { [GatewayId] = 0 };
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        var hops = new Dictionary<string, int>(StringComparer.Ordinal) { [GatewayId] = 0 };
        var settled = new HashSet<string>(StringComparer.Ordinal);

        var heap = new MinHeap<string, (int Distance, string Node)>(
            Comparer<(int Distance, string Node)>.Create((x, y) =>
            {
                var c = x.Distance.CompareTo(y.Distance);
                return c != 0 ? c : string.CompareOrdinal(x.Node, y.Node);
            }));
        heap.Enqueue(GatewayId, (0, GatewayId));

        while (heap.TryDequeue(out var node, out _))
        {
            if (!settled.Add(node))
            {
                continue; // stale heap entry: a cheaper one already settled this node
            }

            foreach (var edge in OrderedLinks(node))
            {
                var next = edge.Other(node);
                if (IsDown(next) || settled.Contains(next))
                {
                    continue;
                }

                var candidate = distance[node] + edge.Cost;
                if (!distance.TryGetValue(next, out var known) || candidate < known)
                {
                    distance[next] = candidate;
                    parent[next] = node;
                    hops[next] = hops[node] + 1;
                    heap.Enqueue(next, (candidate, next));
                }
            }
        }

        return new ShortestPathResult(GatewayId, parent, distance, hops);
    }

    public static IReadOnlyList<string> PathToRoot(ShortestPathResult tree, string node)
    {
        if (node != tree.Root && !tree.ParentOf.ContainsKey(node))
        {
            return [];
        }

        var path = new List<string> { node };
        var current = node;
        while (current != tree.Root)
        {
            current = tree.ParentOf[current];
            path.Add(current);
        }

        return path;
    }

    /// <summary>Splits a route into hops, each with its link cost.</summary>
    public IReadOnlyList<MeshHop> HopsOf(IReadOnlyList<string> path)
    {
        var hops = new List<MeshHop>();
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var edge = _adjacency[path[i]].FirstOrDefault(e => e.Other(path[i]) == path[i + 1]);
            hops.Add(new MeshHop(path[i], path[i + 1], edge?.Cost ?? 0));
        }

        return hops;
    }

    /// <summary>The route a packet takes from a node to the gateway along an MST: its chain of parents.</summary>
    public static IReadOnlyList<string> PathToRoot(MstResult mst, string node)
    {
        if (node != mst.Root && !mst.ParentOf.ContainsKey(node))
        {
            return [];
        }

        var path = new List<string> { node };
        var current = node;
        while (current != mst.Root)
        {
            current = mst.ParentOf[current];
            path.Add(current);
        }

        return path;
    }

    public int PathCost(IReadOnlyList<string> path)
    {
        var total = 0;
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var edge = _adjacency[path[i]].FirstOrDefault(e => e.Other(path[i]) == path[i + 1]);
            total += edge?.Cost ?? 0;
        }

        return total;
    }

    // ------------------------------------------------------------------ helpers

    private IEnumerable<MeshEdge> OrderedLinks(string node) =>
        _adjacency[node].OrderBy(e => e.Other(node), StringComparer.Ordinal);

    private static bool Allowed(string node, IReadOnlySet<string>? within) => within is null || within.Contains(node);

    private static int CompareEdgeKeys((int Cost, string A, string B) x, (int Cost, string A, string B) y)
    {
        var c = x.Cost.CompareTo(y.Cost);
        if (c != 0)
        {
            return c;
        }

        c = string.CompareOrdinal(x.A, y.A);
        return c != 0 ? c : string.CompareOrdinal(x.B, y.B);
    }
}
