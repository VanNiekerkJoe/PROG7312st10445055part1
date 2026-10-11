# Smart-X IoT Mesh Ecosystem: Part 1

Sensor Data Ingestion & Telemetry pillar. Built on .NET 10 (ASP.NET Core Minimal API + SignalR) with a Blazor WebAssembly dashboard.

## Solution layout

```
SmartX.sln
 SmartX.Core         Domain model: TelemetryPacket<T>, SensorReading (operator overloads),
                      DeploymentNode tree + recursive validator, TelemetryBatcher (jagged arrays -> List<T>)
 SmartX.Api           ASP.NET Core Minimal API, SignalR hub, in-memory registry, randomized seeder
 SmartX.Client         Blazor WebAssembly dashboard (landing page + Anomaly Constellation view)
 SmartX.Api.Tests     xUnit tests for the Core domain
```

## Prerequisites

- **.NET 10 SDK** (check with `dotnet --version`; should report `10.0.1xx`). If your installed
  SDK reports a different minor version, open `SmartX.Client/SmartX.Client.csproj` and bump the
  three `PackageReference` versions to match, or the client won't restore.
- **The `wasm-tools` workload**, required to build/run the Blazor WebAssembly client. Install it
  once per machine, from an **elevated/administrator** terminal:
  ```
  dotnet workload install wasm-tools
  ```
  Building or running `SmartX.Client` without this fails with `NETSDK1112: The runtime pack for
  Microsoft.NETCore.App.Runtime.Mono.browser-wasm was not downloaded`. See **Troubleshooting**
  below if the install itself fails.
- If you're using Visual Studio rather than the `dotnet` CLI: **Visual Studio 2026 (18.0+)**.
  Visual Studio 2022 (any 17.x) cannot open or build a `net10.0` project at all — this is a hard
  Microsoft-imposed limit, not a bug here. The `dotnet` CLI works regardless of which Visual
  Studio (if any) is installed.
- A modern browser (Blazor WebAssembly).

## Restoring and building

From the solution root:

```
dotnet restore
dotnet build
```

## Running locally (two terminals)

**Terminal 1: API**

```
cd SmartX.Api
dotnet run
```

Confirm it started on `https://localhost:5001` (check the console output; if .NET assigned
a different port, update `apiBaseAddress` in `SmartX.Client/Program.cs` and `Cors:ClientOrigin`
in `SmartX.Api/appsettings.json` to match).

The API immediately starts the `TelemetrySeeder` background service, which generates simple
randomized telemetry for the seeded facility tree (Facility A → Zones A/B/C → Sub-Zones →
~14 devices) every 2 seconds and pushes updates over SignalR.

**Terminal 2: Client**

```
cd SmartX.Client
dotnet run
```

Open the URL it prints (default `https://localhost:5173`). You'll land on the pillar menu;
click **Sensor Data Ingestion & Telemetry** to open the dashboard.

## What you should see

- Metric strip: active sensor count, anomalies in the last hour, disconnected count, throughput.
- Zone list, colour-flagged when a zone's worst device status is elevated/anomalous/disconnected.
- The **Anomaly Constellation**: a live radial view of every sensor, updating in real time as the
  seeder pushes readings. Click any node to open its detail panel and attach a file.
- Alert timeline strip along the bottom, populated as non-healthy statuses occur.

## API endpoints (SmartX.Api)

| Method | Route                          | Purpose                                                                 |
|--------|--------------------------------|--------------------------------------------------------------------------|
| GET    | `/api/sensors`                 | Latest `DeviceStatus` snapshot for every known device.                  |
| GET    | `/api/sensors/tree`            | Full Facility → Zone → Sub-Zone → Device deployment tree.               |
| GET    | `/api/sensors/validate`        | Re-runs the recursive tree validator, returns `{ isValid, errors }`.    |
| POST   | `/api/sensors/register`        | Registers a new device under a sub-zone; returns the server-assigned `DeviceStatus` (real device ID, not the MAC). |
| DELETE | `/api/sensors/{deviceId}`      | Removes a single device from the tree and every server-side index.      |
| POST   | `/api/sensors/clear`           | Clears **every** device from the tree/registry, keeping the Facility/Zone/Sub-Zone skeleton. |
| POST   | `/api/sensors/{deviceId}/attachments` | Multipart upload of a config file / photo / log for a device.    |
| GET/POST | `/api/sensors/seeder/status`, `/start`, `/stop` | Dev controls for the background telemetry generator.   |

`register`, `DELETE /{deviceId}` and `POST /clear` all broadcast over the `TelemetryHub` SignalR
hub (`DeviceStatusUpdated`, `DeviceRemoved`, `AllDevicesCleared`) so every connected dashboard —
not just the one that made the call — stays in sync.

### Fixed: "clear all" not actually clearing simulated nodes

Earlier drafts of the dashboard only cleared the *client's* local dictionary when you clicked
"clear all" / "stop seeder & clear all". The `SensorRegistry` on the server never forgot
anything, so:

- a page reload (or the "Reload from API" button) pulled the same devices straight back from
  `GET /api/sensors`, and
- the seeder held a **one-time snapshot** of the device list taken at startup, so it kept
  broadcasting `DeviceStatusUpdated` for devices you'd just "cleared" the moment it ticked again.

Fixed by making the clear operation authoritative on the server:

- `SensorRegistry.ClearAllDevices()` / `RemoveDevice(id)` actually remove the device node(s) from
  the tree and purge every dictionary keyed on that device ID (index, baseline, latest status,
  batcher, throughput counter).
- `TelemetrySeeder` re-reads `registry.DeviceNodes` **every tick** instead of once at startup, so
  it immediately stops feeding a removed/cleared device (and immediately starts feeding a newly
  registered one, which was a second, related bug).
- The new `POST /api/sensors/clear` and `DELETE /api/sensors/{deviceId}` endpoints broadcast
  `AllDevicesCleared` / `DeviceRemoved` over SignalR, and the client re-syncs from the API on
  SignalR reconnect, so multiple open dashboards and reconnects can't drift out of sync.
- Registering a device now also returns the server-assigned device ID/zone (instead of the client
  guessing and keying its local state on the MAC address), which was the root cause of newly
  registered devices showing up as duplicate/orphaned nodes.

## Running the tests

```
cd SmartX.Api.Tests
dotnet test
```

Covers the recursive `DeploymentTreeValidator` (valid tree, duplicate MAC address, unconfigured
ancestor, missing sensor profile), the `SensorReading` operator overloads (`+`, `-`, `>`), and
`TelemetryBatcher<T>`'s jagged-array-to-`List<T>` flush.

## Docker

`Dockerfile`s are provided for both the API and the client, plus a `docker-compose.yml`.

```
docker compose up --build
```

**Known limitation:** the client currently reads the API's base address from a compile-time
constant in `Program.cs` (`https://localhost:5001`), which only works for local `dotnet run`.
Running both containers together will build and serve correctly, but the WASM bundle will still
try to reach `localhost:5001` from the browser rather than the `smartx-api` container. If you
need the Docker Compose setup to actually talk cross-container, the fix is to move
`apiBaseAddress` into a `wwwroot/appsettings.json` fetched at startup instead of a hardcoded
constant, flagged here rather than silently left broken.

## Design notes

- **Generics (no boxing):** `TelemetryPacket<T> where T : struct` is closed per concrete type
  (`TelemetryPacket<float>`, `<int>`, `<bool>`) so ingestion never boxes a value into `object`.
- **Operator overloading:** `SensorReading` overloads `+`, `-`, `>`, `<`, `>=`, `<=`, `==`, `!=`
  for aggregation (`meter3 = meter1 + meter2`) and threshold comparison.
- **Jagged arrays → `List<T>`:** `TelemetryBatcher<T>` buffers raw readings into a growing
  `T[][]` of completed windows, then flushes the whole structure into a flat
  `List<TelemetryPacket<T>>`.
- **Recursion:** `DeploymentTreeValidator.Validate` recursively walks the Facility → Zone →
  Sub-Zone → Device tree, checking MAC-address uniqueness and that every ancestor in a device's
  chain (e.g. Sub-Zone B → Zone 1 → Facility A) is itself configured.
- **Engagement feature (Anomaly Constellation):** justified in the Task 1 research report
  against real-time visual feedback and alert-fatigue literature; the same radial node/edge
  layout is designed to be extended into the mesh topology view required later in the PoE.

## What's intentionally out of scope

**Mesh Topology** stays disabled until the final PoE. Part 1 shipped Sensor Ingestion; Part 2
(below) adds the Real-Time Command Stream.

## A note on how this was built

This solution was scaffolded with AI assistance and has **not been compiled or run** in the
environment it was written in (no .NET SDK / NuGet access there). Build it locally first and
treat any compiler errors as expected first-pass issues to fix together, not a sign something
is fundamentally wrong with the architecture; paste the error text back and we'll resolve it.

---

# Part 2: Real-Time Processing and Device Management

Part 2 turns the static ingestion gateway into a real-time processing engine. Devices now
**hit the API like real hardware** (`POST /api/telemetry/ingest`), packets flow through a
two-lane queue, and operators get a **Real-Time Command Stream** page with manual overrides,
undo, search/filter and automated "Suggested Actions".

## Running Part 2 (three terminals)

```
# Terminal 1: API
cd SmartX.Api
dotnet run

# Terminal 2: Client
cd SmartX.Client
dotnet run

# Terminal 3: simulated devices (acts like a fleet of ESP32 nodes)
cd SmartX.DeviceSimulator
dotnet run                      # defaults to https://localhost:5001
# dotnet run -- http://localhost:5000   # or pass the API URL
```

Open the client URL, then use the top navigation bar: **Sensor ingestion** (Part 1) and
**Command stream** (Part 2). Docker: `docker compose up --build` starts the API, client and
simulator (`SMARTX_API_URL=http://smartx-api:8080` is set for the simulator).

Navigation between these modules preserves the command search/category/state filters, selected
device, and ingestion zone/constellation severity filters in the app-lifetime `LiveStateService`.
Both SignalR clients retry reconnects indefinitely with a delay capped at 30 seconds, then
re-fetch server state after reconnect so missed device or command events do not require a page
refresh.

Simulator keys: **S** = spike a random sensor, **X** = silence a random device for 30 s, **Q** = quit.

## How to test the new data structures

| Requirement | Where | How to see it working |
|---|---|---|
| `Queue<T>` (FIFO) | `SmartX.Core/Ingestion/TelemetryIngestQueue.cs` | On **Command stream** click **Flood queue (2000 packets)**. The *Standard queue* tile climbs, then drains in arrival order (about 500 packets/s). |
| `PriorityQueue` (critical alerts) | same file | While the queue is draining, select a device and click **Fire priority alert on selected**. The alert appears at the top of the Live stream (red row) immediately and the *Priority lane* tile ticks up, even though thousands of packets are still queued. |
| `Stack<T>` (undo) + `Queue<T>` (audit log) | `SmartX.Core/Commands/CommandHistory.cs` | Select a device, send **Pump ON**, then **Relay OFF**. Click **Undo last command**: Relay ON is issued first (LIFO), then Pump OFF. The stack retains at most the latest 200 commands; a queue keeps the latest 200 audit entries with O(1) oldest-entry eviction. Undo commands are logged but cannot themselves be undone. |
| `Dictionary<TKey,TValue>` | `SmartX.Core/Storage/DeviceStateStore.cs` | Every ingested packet resolves its device by **MAC address** with one hash lookup (O(1)); a second dictionary maps device ID to MAC for commands. |
| `SortedDictionary` | `SmartX.Core/Storage/SortedHistoryLog.cs` | Select a device: the timeline graph is read from the timestamp-sorted structure (`GET /api/telemetry/history/{deviceId}`). `Latest()` uses `Skip`, so reading the latest points is O(n), but each device is capped at 300 points. This keeps insertion/removal O(log n); a `SortedList` would offer indexed reads but make insertion/removal O(n). Red/amber dots mark non-healthy readings. |
| `HashSet<T>` | `SmartX.Core/Storage/ActiveAlertSet.cs` | Press **X** in the simulator. After ~20 s the silent device raises **one** Disconnected alert; the *Duplicate alerts blocked* tile then climbs every sweep instead of re-alerting. |

### How to simulate a high-priority alert

1. Start the API, client and simulator, and open **Command stream**.
2. Click **Flood queue (2000 packets)** to build a backlog in the standard lane.
3. Select any sensor (Environmental or Power) on the left.
4. Click **Fire priority alert on selected**. A forced critical packet (3x the device's value)
   is posted to `/api/telemetry/simulate-alert`, enters the **priority lane** and is processed
   before the backlog. Alternatively press **S** in the simulator window, or POST a reading
   that is far outside the sensor's range to `/api/telemetry/ingest`.

Packets are classified **at ingest time** (`TelemetryClassifier`): a z-score of 3 or more
against the device's rolling baseline, an out-of-range value, or a device-reported disconnect
puts the packet in the priority lane.

## Predictive Action and Recommendation Engine

`SmartX.Core/Insights/ActionPatternEngine.cs` learns from operator behaviour with a first-order
transition table: `context -> follow-up command -> timestamped evidence weight`. A context is an alert
(`alert:Power:Anomalous`) or a search (`search:pump a`). Every manual command issued within
90 seconds of a context bumps that pair's evidence. Evidence decays exponentially with a
7-day half-life, so old behavior gradually loses influence and can fall below the minimum
effective support of 2. Suggestions are ranked by **confidence = recency-weighted evidence /
total recency-weighted evidence** for the context. Three other sources feed the same panel:

- **Rules** for cold start (e.g. anomalous Power -> Relay OFF) when nothing is learned yet;
- **Watch-list** of devices that keep raising alerts (3+), shown as "Inspect ...";
- **Search patterns**: if the operator usually runs "Pump ON" after searching "pump", it is
  suggested while they search.

Suggestions appear in the **Suggested actions / automated insights** panel with a confidence bar
and an **Apply** button. Click **Train demo patterns** to preload a believable operator history,
then trigger an alert (or search "pump" and press Enter) to see learned suggestions.

## Navigation and state

`NavMenu` (top bar) is part of `MainLayout`, so it is on every page. `LiveStateService` is an
app-lifetime singleton that owns the SignalR subscriptions, device statuses, command history,
live feed and the operator's search/filter selections. Navigating between pages therefore never
loses state, never opens a second hub connection, and UI updates are coalesced to 4 per second
so a packet burst cannot freeze the browser.

## New API surface

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/telemetry/ingest` | Device front door: `{ macAddress, value, disconnected? }` |
| POST | `/api/telemetry/simulate-alert` | Demo: forced critical packet for `{ deviceId }` |
| POST | `/api/telemetry/burst?count=2000` | Demo: flood the standard lane |
| GET | `/api/telemetry/stats` | Queue depths, processed counts, alert counts |
| GET | `/api/telemetry/history/{deviceId}?count=60` | Timeline points (sorted) |
| GET / POST | `/api/commands` | Command log + undo depth / issue `{ deviceId, type }` |
| POST | `/api/commands/undo` | Pop the stack and send the inverse command |
| GET | `/api/insights?search=` | Suggested actions |
| POST | `/api/insights/search`, `/train-demo` | Log a search / preload demo patterns |

SignalR: `CommandHub` at `/hubs/commands` pushes `CommandIssued`, `CommandUndone`,
`PriorityAlert` and `PipelineStats`; `TelemetryHub` still pushes `DeviceStatusUpdated`.

## Part 2 tests

```
dotnet test SmartX.Api.Tests
```

New tests cover FIFO order, priority bypass and tie-breaking, back-pressure, undo order,
MAC lookup and sync, duplicate-alert suppression, sorted history ordering/capacity, and the
learned / rule / watch-list suggestion paths.
