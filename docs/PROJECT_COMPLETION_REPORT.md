# Smart-X IoT Mesh Ecosystem: project completion report

**Module:** PROG7312 &middot; **Student:** Joe Leo van Niekerk (ST10445055) &middot; **Repository:** https://github.com/VanNiekerkJoe/PROG7312st10445055part1

## 1. What was delivered

Smart-X simulates a fleet of ESP32 sensors (hydroponic benches, utility panels, valve banks, grid meters) reporting to one gateway. It was built in three parts on **.NET 10**: an ASP.NET Core Minimal API with SignalR, a Blazor WebAssembly dashboard, a device simulator, and an xUnit project.

| Part | Delivered | Core data structures |
|---|---|---|
| 1. Ingestion and telemetry | Device registration (single and imported list), recursive validation of the Facility / Zone / Sub-Zone / Device tree, generic `TelemetryPacket<T>`, operator-overloaded `SensorReading`, jagged-array batching, encrypted attachments, the Anomaly Constellation dashboard | Generics, jagged arrays, recursion |
| 2. Real-time commands | Two-lane ingest pipeline with back-pressure, heartbeat sweep, manual overrides with undo, audit log, predictive action engine | `Queue`, `PriorityQueue`, `Stack`, `Dictionary`, `HashSet` |
| 3. Network topology | Mesh graph, route tracking by MAC, self-healing around failed nodes, BFS and DFS reachability with detours, Prim and Kruskal spanning trees, Dijkstra shortest paths, AVL-backed historical range queries | AVL tree, min-heap, graph (adjacency list), union-find |

All three modules run inside one navigation bar. Authentication (Firebase) protects every REST route and both SignalR hubs. The solution has 73 automated tests, all passing, of which 19 cover Part 3.

## 2. Architecture in one paragraph

Packets enter through `POST /api/telemetry/ingest`, are classified at the door (z-score against a rolling per-device baseline), and are queued in a normal lane or a priority lane. A background `IngestProcessor` drains a bounded batch per tick, resolves the sender by MAC in an O(1) dictionary, appends the reading to that device's AVL history, raises or suppresses alerts through a `HashSet`, and pushes one status update per device per tick over SignalR. Part 3 sits beside this pipeline: `MeshTopologyService` builds a `MeshGraph` from the live deployment tree, overlays failures (heartbeat loss or operator-simulated), and answers routing, traversal and history questions for the Mesh topology page. Because the graph is derived from the same registry as Parts 1 and 2, a device registered or deleted anywhere appears in or leaves the mesh automatically.

## 3. Challenges and how they were overcome

### Part 1: Sensor ingestion and telemetry

**1.1 One pipeline for three value types.** Soil moisture is a float, power is an integer and valve state is a boolean. Using `object` would box every reading on a hot path. `TelemetryPacket<T>` is constrained with `where T : struct` and closed over `float`, `int` and `bool`, so readings stay unboxed and the compiler rejects an invalid payload type.

**1.2 Validating a nested deployment.** A device is only meaningful if its Facility, Zone and Sub-Zone are configured and its MAC address is unique. `DeploymentTreeValidator` walks the tree recursively and returns a validation result with a list of errors (for example "Duplicate MAC address"), so the dashboard can show exactly what is wrong; a unit test covers the duplicate-MAC case.

**1.3 Several dashboards must agree.** A clear or delete in one browser tab has to disappear everywhere. `SensorRegistry.ClearAllDevices()` and `RemoveDevice(id)` purge every index, the seeder re-reads the device list each tick, and the client re-syncs when SignalR reconnects, so dashboards cannot drift apart.

**1.4 Bulk import without losing good rows.** The device-list import registers each valid line independently and reports failed lines with their line numbers (commit `8621424`), so one malformed row in a 500-row file does not discard the rest.

### Part 2: Real-time command stream

**2.1 `PriorityQueue` is not stable.** Two critical alerts of equal urgency could be served out of arrival order. The priority is `(-urgency, sequence)`, so equal urgency stays first-come-first-served; a unit test asserts the tie-break.

**2.2 What "undo" means.** Undo pops the stack and issues the inverse command. If that inverse were pushed back, repeated undo would flip a device forever. Inverse commands are logged and flagged `IsUndo` but never pushed, so undo walks backwards through the operator's own commands and returns nothing once the stack is empty. A test proves it.

**2.3 A burst must not freeze the browser.** A reconnecting fleet can send thousands of packets at once. The ingest endpoint only classifies and enqueues; the processor handles at most 50 packets per 100 ms tick and broadcasts only the latest status per device per tick; the client coalesces change notifications to one every 250 ms. A single device sending hundreds of packets becomes one SignalR message.

**2.4 Detecting a silent device over HTTP.** MQTT has last-will messages; HTTP does not. `IngestProcessor` sweeps every 5 seconds and marks any device silent for more than 20 seconds as Disconnected, and a `HashSet` of active alerts stops the same alert being raised on every sweep.

**2.5 A recommendation engine that does not train on itself.** Suggestions could reinforce their own output if applying one counted as operator behaviour. The fix (commit `011aad6`) is that commands applied from a suggestion are not learned from, one command teaches each distinct context once, and suggestions only target devices that still exist. Confidence uses add-one smoothing and recency decay with an injected clock, so the tests move time forward instead of waiting.

**2.6 Indicators that match the device.** The Pump, Relay and Valve indicators are now wired to the server's per-output state (commits `f54c411`, `51fe2e6`) instead of the client's own guess, so a second dashboard or an undo shows the true state.

**2.7 Authentication across REST and WebSockets.** Adding Firebase meant the dashboard, REST API and SignalR hubs all had to accept the same token. Browsers cannot send an `Authorization` header on a WebSocket connection, so the hub accepts the token as a query parameter on `/hubs` paths only. A CORS mismatch between the HTTPS and HTTP client addresses then produced a misleading "could not contact Firebase" error until both origins were allowed (commit `8be80f1`).

**2.8 State lost between pages.** Switching modules originally reset live data and risked opening a second hub connection. A single app-lifetime `LiveStateService` now owns device, command and feed state, plus the operator's search text, filters and selected device (commit `7fea6de`).

### Part 3: Network topology and mesh routing

**3.1 History had no range query.** Part 2's `SortedDictionary` could return "the latest N" but not "everything between two times" without enumerating from the start. I replaced it with a hand-written AVL tree behind the same `SortedHistoryLog` interface, so the Part 2 history tests kept passing unchanged. The range query only descends into a subtree that can still contain keys in the window. In a test run, a 100-reading window in a 20,000-reading tree visited about 115 nodes, and inserting 10,000 keys in sorted order (the worst case for an unbalanced tree) gave a height of 14. The unit tests also delete half the keys and re-check order and height, because rebalancing on delete is where AVL implementations most often go wrong.

**3.2 There is no real radio data.** A mesh needs link costs, and the simulator only posts sensor values. Links are generated deterministically from the deployment tree (rings of relays per sub-zone, one uplink per sub-zone, cross-subnet backup links, costs from a stable hash). Determinism matters: a random mesh would make demos and tests unrepeatable, and `string.GetHashCode` is randomised per process in .NET, so an FNV-1a hash is used instead.

**3.3 Defining "down", "stranded" and "where to hop".** The brief asks the system to identify which devices are down and where to hop in that case. A node that is alive but whose every route crosses a failure is neither connected nor down, so there is a third state, *stranded*, and `Connectivity()` classifies every node into exactly one of the three. For "where to hop", each affected node's route on the healthy graph is compared with its route on the live graph, and both paths and costs are shown.

**3.4 Proving the spanning tree is minimal.** Hand-checking one small graph is not proof. Prim's algorithm (heap-based) and Kruskal's algorithm (union-find) reach the minimum by different routes, so the app computes both and shows whether they agree; a unit test compares them on 100 randomly generated meshes. Writing a `MinHeap` instead of using `PriorityQueue` meant the sift-up and sift-down logic had to be right, which the heap test (sorted dequeue order) checks.

**3.5 A tree route is not a shortest path.** An MST minimises total network cost, not each sensor's own cost to the gateway. The MST is kept because the brief asks for it, and Dijkstra runs over the same adjacency list and min-heap so the Route tracker shows both a node's tree route and its own cheapest path. A unit test builds the smallest graph where they differ, and a random-mesh test checks that the cheapest path never costs more than the tree route.

**3.6 Integrating the C# backend with the Blazor frontend.** The mesh page draws an SVG that Blazor re-renders every three seconds. Building it surfaced one compile error: the Razor compiler treats `<text>` as its own directive, so SVG labels placed directly inside a code block fail to compile, and wrapping each in a `<g>` fixes it. Coordinates are also formatted with the invariant culture, because a culture that uses a decimal comma (such as South African English) would otherwise produce invalid SVG numbers. The refresh loop is cancellable (`CancellationTokenSource` plus `PeriodicTimer`) so it stops when the user navigates away, which matters because the three modules share one long-lived SignalR connection.

### Cross-cutting

**Toolchain.** The solution targets `net10.0`; Visual Studio 2022 cannot open `net10.0` projects, so the supported workflow is the `dotnet` CLI in separate terminals, documented in the README. A running API also locks `SmartX.Core.dll`, which makes a rebuild fail with `MSB3027` until the API is stopped; this is listed under Troubleshooting in the README.

**Bounded growth.** Every growing structure (ingest lane, undo stack, audit log, history) has a cap, because an unbounded structure on a long-running gateway is a memory leak.

## 4. Key learnings, linked to the work

- **Choose a structure by the question it must answer.** The same history data needed a different tree once the question changed from "latest N" (Part 2) to "between two times" (Part 3). The AVL tree, the adjacency list and the min-heap were each chosen because of a specific query in this assignment: a 100-reading window visiting about 115 of 20,000 nodes is the evidence.
- **Invariants are testable.** Balance (height at most about 1.44 log2 n), "Prim equals Kruskal", "a failed node never appears in a route" and "the cheapest path never costs more than the tree route" are properties, not examples, so they can be checked on random inputs as well as hand-picked ones. 73 tests pass.
- **Design the failure model first.** Deciding that a node can be *down* or *stranded*, and that the graph treats a down node as absent, made BFS, DFS, Prim, Dijkstra and routing all heal with no special-case code.
- **Make ties deterministic.** The same lesson appeared in every part: the stable `(-urgency, sequence)` priority in Part 2, the hash-based link costs, and the ordered tie-breaks in Prim's and Dijkstra's algorithms in Part 3. Without them, results change between runs and tests become flaky.
- **A learning system must not learn from itself.** Excluding applied suggestions from the engine's training data (Part 2) is the same discipline as keeping simulated link costs and seeded demo patterns clearly labelled as simulated: say what is real and what is not.
- **Document the limits.** Simulated link costs, in-memory history and per-request graph rebuilds are stated in the README so a reader knows what the numbers mean.

## 5. Testing summary

`dotnet test SmartX.Api.Tests` runs 73 tests, all passing. They cover the Part 1 domain rules, the Part 2 queues, undo stack, history and action engine, and 19 Part 3 tests: AVL balance and range, delete rebalancing, heap order, Prim versus Kruskal on fixed and random meshes, Dijkstra versus tree-route cost on a fixed example and 100 random meshes, BFS versus DFS order, healing around a failure, stranded detection, deterministic topology, route lookup by MAC, hop breakdown, and a service-level history range query.

## 6. Future work

Real link quality (RSSI) as edge cost; persistent time-series storage; MQTT or a managed cloud ingestion pipeline; container orchestration and horizontal scale-out. See *Technology recommendations*.
