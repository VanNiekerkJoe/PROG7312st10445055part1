# Smart-X IoT Mesh Ecosystem: project completion report

> **Draft for review.** Written from the repository history and the Part 3 implementation. Read it through, correct anything that does not match your experience, and add your own specific examples (the rubric rewards links back to the actual tasks) before exporting to PDF.

**Module:** PROG7312 &middot; **Student:** Joe Leo van Niekerk (ST10445055) &middot; **Repository:** https://github.com/VanNiekerkJoe/PROG7312st10445055part1

## 1. What was delivered

Smart-X simulates a fleet of ESP32 sensors (hydroponic benches, utility panels, valve banks, grid meters) reporting to one gateway. It was built in three parts on **.NET 10**: an ASP.NET Core Minimal API with SignalR, a Blazor WebAssembly dashboard, a device simulator, and an xUnit project.

| Part | Delivered | Core data structures |
|---|---|---|
| 1. Ingestion and telemetry | Device registration, recursive validation of the Facility / Zone / Sub-Zone / Device tree, generic `TelemetryPacket<T>`, operator-overloaded `SensorReading`, jagged-array batching, encrypted attachments, the Anomaly Constellation dashboard | Generics, jagged arrays, recursion |
| 2. Real-time commands | Two-lane ingest pipeline with back-pressure, heartbeat sweep, manual overrides with undo, audit log, predictive action engine | `Queue`, `PriorityQueue`, `Stack`, `Dictionary`, `HashSet` |
| 3. Network topology | Mesh graph, route tracking by MAC, BFS and DFS reachability with detours, Prim and Kruskal spanning trees, AVL-backed historical range queries | AVL tree, min-heap, graph (adjacency list), union-find |

All three modules run inside one navigation bar. Authentication (Firebase) protects every REST route and both SignalR hubs.

## 2. Architecture in one paragraph

Packets enter through `POST /api/telemetry/ingest`, are classified at the door (z-score against a rolling per-device baseline), and are queued in a normal lane or a priority lane. A background `IngestProcessor` drains a bounded batch per tick, resolves the sender by MAC in an O(1) dictionary, appends the reading to that device's AVL history, raises or suppresses alerts through a `HashSet`, and pushes one status update per device per tick over SignalR. Part 3 sits beside this pipeline: `MeshTopologyService` builds a `MeshGraph` from the live deployment tree, overlays failures (heartbeat loss or operator-simulated), and answers routing, traversal and history questions for the Mesh topology page. Because the graph is derived from the same registry as Parts 1 and 2, a device registered or deleted anywhere appears in or leaves the mesh automatically.

## 3. Challenges and how they were overcome

### Task 3 (Network topology and mesh routing)

**3.1 History had no range query.** Part 2's `SortedDictionary` could return "the latest N" but not "everything between two times" without enumerating from the start. I replaced it with a hand-written AVL tree behind the same `SortedHistoryLog` interface, so the Part 2 history tests kept passing unchanged. The range query only descends into a subtree that can still contain keys in the window. Measured in the unit tests: a 100-reading window in a 20,000-reading tree visits about 115 nodes, and inserting 10,000 keys in sorted order (the worst case for an unbalanced tree) gives a height of 14. The tests also delete half the keys and re-check order and height, because rebalancing on delete is where AVL implementations most often go wrong.

**3.2 There is no real radio data.** A mesh needs link costs, and the simulator only posts sensor values. I generate links deterministically from the deployment tree (rings of relays per sub-zone, one uplink per sub-zone, cross-subnet backup links, costs from a stable hash). Determinism mattered: a random mesh would make demos and tests unrepeatable, and string `GetHashCode` is randomised per process in .NET, so I used an FNV-1a hash instead.

**3.3 Defining "down", "stranded" and "where to hop".** The brief asks the system to identify which devices are down and where to hop in that case. A node that is alive but whose every route crosses a failure is neither connected nor down. I added the third state, *stranded*, and made `Connectivity()` classify every node into exactly one of the three. For "where to hop", I compare each affected node's route on the healthy graph with its route on the live graph and show both paths and costs.

**3.4 Proving the spanning tree is minimal.** Hand-checking one small graph is not proof. Prim's algorithm (heap-based) and Kruskal's algorithm (union-find) find the minimum by different routes, so the app computes both and shows whether they agree; a unit test compares them on 100 randomly generated meshes. Writing my own `MinHeap` instead of using `PriorityQueue` also meant the sift-up and sift-down logic had to be correct, which the heap test (sorted dequeue order) checks.

**3.5 A tree route is not a shortest path.** An MST minimises total network cost, not each sensor's own cost to the gateway. I kept the MST because the brief asks for it explicitly, and added Dijkstra over the same adjacency list and min-heap so the Route tracker shows both a node's tree route and its own cheapest path. A unit test builds the smallest graph where they differ, and a random-mesh test checks that the cheapest path never costs more than the tree route.

**3.6 Integrating the C# backend with the Blazor frontend.** The mesh page needed an SVG drawing that Blazor re-renders every three seconds. Two issues came up. First, the Razor compiler treats `<text>` as its own directive, so SVG labels placed directly inside a code block failed to compile; wrapping each in a `<g>` fixed it. Second, number formatting in a South African culture can use a decimal comma, which silently breaks SVG coordinates, so every coordinate goes through a helper that formats with the invariant culture. I also kept the page's refresh loop cancellable (`CancellationTokenSource` plus `PeriodicTimer`) so it stops when the user navigates away, which matters because Parts 1 and 2 share one long-lived SignalR connection.

### Earlier parts (still relevant to the finished system)

- **Toolchain mismatch.** The solution targets `net10.0`; Visual Studio 2022 cannot open `net10.0` projects, so the supported workflow is the `dotnet` CLI in separate terminals, documented in the README.
- **Authentication and CORS.** Adding Firebase meant the dashboard, REST API and SignalR hubs all had to accept the same token. WebSocket connections cannot send an `Authorization` header, so the hub accepts the token as a query parameter on `/hubs` paths only. A CORS mismatch between the HTTPS and HTTP client addresses produced a misleading "could not contact Firebase" error until both origins were allowed (commit `8be80f1`).
- **State across pages.** Switching between modules originally reset live data. A single app-lifetime `LiveStateService` now owns device, command and feed state (commit `7fea6de`).
- **Bounded growth.** Every growing structure (ingest lane, undo stack, audit log, history) has a cap, because an unbounded structure on a long-running gateway is a memory leak.

## 4. Key learnings, linked to the work

- **Choosing a structure by the question it must answer.** The same history data needed a different tree once the question changed from "latest N" to "between two times". The AVL tree, the adjacency list and the min-heap were each chosen because of a specific query in this assignment, not because they appear in the syllabus.
- **Invariants are testable.** Balance (height at most about 1.44 log2 n), "Prim equals Kruskal", and "a failed node never appears in a route" are properties, not examples, so they can be checked on random inputs as well as hand-picked ones.
- **Design the failure model first.** Deciding that a node can be *down* or *stranded*, and that the graph treats a down node as absent, made BFS, DFS, Prim and routing all heal with no special-case code.
- **Document the limits.** Simulated link costs, in-memory history and per-request graph rebuilds are stated in the README so a reader knows what the numbers mean.
- **Version control as evidence.** Small commits with descriptive messages made it possible to build the update ledger from history rather than memory.

## 5. Testing summary

`dotnet test SmartX.Api.Tests` covers the Part 1 domain rules, the Part 2 queues, undo stack, history and action engine, and the 16 Part 3 tests (AVL balance and range, delete rebalancing, heap order, Prim versus Kruskal on fixed and random meshes, BFS versus DFS order, healing around a failure, stranded detection, deterministic topology, route lookup by MAC, and a service-level history range query).

## 6. Future work

Real link quality (RSSI) as edge cost; persistent time-series storage; MQTT ingestion; horizontal scale-out. See *Technology recommendations*.
