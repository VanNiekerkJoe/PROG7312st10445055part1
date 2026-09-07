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

- .NET 10 SDK (check with `dotnet --version`. If your installed SDK reports a different
  minor version than `10.0.0`), open `SmartX.Client/SmartX.Client.csproj` and bump the three
  `PackageReference` versions to match; the client won't restore otherwise.
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

## What's intentionally out of scope for Part 1

The **Command Stream** and **Mesh Topology** pillars are visible on the landing page but
disabled, per the brief. Only the Sensor Ingestion pillar is implemented here.

## A note on how this was built

This solution was scaffolded with AI assistance and has **not been compiled or run** in the
environment it was written in (no .NET SDK / NuGet access there). Build it locally first and
treat any compiler errors as expected first-pass issues to fix together, not a sign something
is fundamentally wrong with the architecture; paste the error text back and we'll resolve it.
