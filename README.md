# Smart-X IoT Mesh Ecosystem — Part 1

**Sensor Data Ingestion & Telemetry Gateway**

A simulated, high-throughput IoT ingestion platform for a hybrid South African edge-computing
scenario (hydroponic farms, real estate utility trackers, smart grid installations). Part 1
implements the **Sensor Data Ingestion and Telemetry** pillar: a .NET 10 Minimal API that ingests
mixed-type sensor telemetry without boxing overhead, a live SignalR broadcast layer, and a Blazor
WebAssembly dashboard — the **Anomaly Constellation** — that visualises every sensor as a live,
colour-coded node.

| | |
|---|---|
| **Module** | PROG7312 |
| **Student** | Joe Leo van Niekerk — ST10445055 |
| **Group** | Group 3 |
| **Stack** | .NET 10 · ASP.NET Core Minimal API · SignalR · Blazor WebAssembly |

---

## Contents

- [Architecture at a glance](#architecture-at-a-glance)
- [Solution layout](#solution-layout)
- [Prerequisites](#prerequisites)
- [Getting started](#getting-started)
- [Using the dashboard](#using-the-dashboard)
- [API reference](#api-reference)
- [Real-time events (SignalR)](#real-time-events-signalr)
- [Technical requirements — where to find them](#technical-requirements--where-to-find-them)
- [Running the tests](#running-the-tests)
- [Docker](#docker)
- [Troubleshooting](#troubleshooting)
- [Known limitations](#known-limitations)
- [What's next (Part 2 / final PoE)](#whats-next-part-2--final-poe)

---

## Architecture at a glance

```
                     ┌─────────────────────────┐
                     │   TelemetrySeeder        │
                     │   (BackgroundService)    │  every 2s: random walk +
                     │                          │  occasional spike/disconnect
                     └───────────┬──────────────┘
                                 │ RecordReading(deviceId, value)
                                 ▼
┌─────────────────────────────────────────────────────────────┐
│                        SensorRegistry                        │
│  Deployment tree (Facility→Zone→SubZone→Device)               │
│  + per-device: rolling baseline, jagged-array batcher,        │
│    latest DeviceStatus, throughput counter                    │
└───────────┬─────────────────────────────────┬────────────────┘
            │ REST (SmartX.Api)                │ SignalR (TelemetryHub)
            ▼                                  ▼
   ┌──────────────────┐              ┌────────────────────────┐
   │ /api/sensors/...  │              │ DeviceStatusUpdated     │
   │ (CRUD + files)     │              │ DeviceRemoved           │
   └──────────────────┘              │ AllDevicesCleared        │
            ▲                         └───────────┬────────────┘
            │ HttpClient                           │ HubConnection
            └───────────────────┬───────────────────┘
                                 ▼
                     ┌─────────────────────────┐
                     │  SmartX.Client (Blazor)  │
                     │  Anomaly Constellation    │
                     │  dashboard                │
                     └─────────────────────────┘
```

**Data flow, end to end:** the seeder (or a real device via `POST /register` +  repeated
telemetry) produces a raw value → `SensorRegistry.RecordReading` pushes it through a
`TelemetryBatcher<T>` (jagged array of windows) and a rolling `SensorBaseline` (z-score) →
a `DeviceStatus` snapshot is broadcast over SignalR to every connected dashboard → the
**Anomaly Constellation** re-renders that node's position, colour and size in real time.

---

## Solution layout

```
SmartX.sln
├─ SmartX.Core            Domain model — no ASP.NET Core dependency
│  ├─ Telemetry/          TelemetryPacket<T>, SensorReading (operators), DeviceStatus, SensorCategory
│  ├─ Devices/            DeploymentNode tree, SensorProfile, DeploymentTreeValidator (recursion)
│  └─ Batching/           TelemetryBatcher<T> — jagged array (T[][]) → List<T>
├─ SmartX.Api             ASP.NET Core Minimal API
│  ├─ Endpoints/          SensorEndpoints.cs — all REST routes
│  ├─ Hubs/                TelemetryHub — SignalR hub
│  ├─ Services/            SensorRegistry, TelemetrySeeder, SeedData
│  └─ Dockerfile
├─ SmartX.Client           Blazor WebAssembly dashboard
│  ├─ Pages/               Home.razor (pillar menu), Ingestion.razor (dashboard)
│  ├─ Components/          ConstellationView, SensorDetailPanel, ZoneList, DeviceRegistrationForm, AlertTimeline
│  ├─ Services/            TelemetryApiClient (REST wrapper), TelemetryHubClient (SignalR wrapper)
│  └─ Dockerfile
├─ SmartX.Api.Tests        xUnit tests for SmartX.Core / SmartX.Api
└─ docker-compose.yml
```

---

## Prerequisites

- **.NET 10 SDK** — check with `dotnet --version` (should report `10.0.1xx`). If your installed
  SDK reports a different minor version, open `SmartX.Client/SmartX.Client.csproj` and bump the
  three `PackageReference` versions to match, or the client won't restore.
- **The `wasm-tools` workload** — required to build/run the Blazor WebAssembly client. Install
  once per machine, from an **elevated/administrator** terminal:
  ```
  dotnet workload install wasm-tools
  ```
  Without this, building or running `SmartX.Client` fails with `NETSDK1112: The runtime pack for
  Microsoft.NETCore.App.Runtime.Mono.browser-wasm was not downloaded`.
- **Visual Studio 2026 (18.0+)** if you want to use the IDE rather than the CLI. Visual Studio
  2022 (any 17.x) cannot open or build a `net10.0` project at all — this is a hard
  Microsoft-imposed limit, not a bug here. The `dotnet` CLI works regardless of which Visual
  Studio (if any) is installed, and is the recommended way to run this project.
- A modern browser (Blazor WebAssembly).

---

## Getting started

### 1. Restore and build

From the solution root:

```
dotnet restore
dotnet build
```

### 2. Run it (two terminals)

**Terminal 1 — API:**

```
cd SmartX.Api
dotnet run
```

Confirm it started on `https://localhost:5001` (check the console output — if .NET assigned a
different port, update `apiBaseAddress` in `SmartX.Client/Program.cs` and `Cors:ClientOrigin` in
`SmartX.Api/appsettings.json` to match).

The API immediately starts the `TelemetrySeeder` background service, which generates simple
randomised telemetry for the seeded facility tree (Facility A → Zones A/B/C → Sub-Zones →
~14 devices) every 2 seconds and pushes updates over SignalR.

**Terminal 2 — Client:**

```
cd SmartX.Client
dotnet run
```

Open the URL it prints (default `https://localhost:5173`). You'll land on the pillar menu — click
**Sensor Data Ingestion & Telemetry** to open the dashboard.

### 3. (Optional) Run everything in Docker instead

See [Docker](#docker) below.

---

## Using the dashboard

| Area | What it shows |
|---|---|
| **Metric strip** | Active sensor count, anomalies in the last hour, disconnected count, total throughput/min. |
| **Zones panel** | One row per zone, colour-flagged to that zone's worst current device status. Click a zone to filter the constellation to it. |
| **Anomaly Constellation** | A live radial view — every sensor is a node whose colour (healthy/elevated/anomalous/disconnected), size and motion update as telemetry arrives. Click a node to select it. |
| **Selected node panel** | Full detail for the selected device (zone, category, reading, z-score, throughput), a file-attach control, and its list of previously uploaded attachments (each a clickable link to view/download). |
| **Alert timeline** | A rolling strip of non-healthy events from the last hour. |
| **+ Register sensor** | Opens a form to register a new device under any existing sub-zone. |
| **Stop/Start seeder** | Pauses or resumes the randomised telemetry generator. |
| **Clear all devices** | Removes every registered/simulated device from the server (keeps the Facility/Zone/Sub-Zone skeleton so you can re-register). |
| **Reload from API** | Manually re-syncs the dashboard's local cache from the server — useful after a dropped connection. |

---

## API reference

All routes are under `/api/sensors`, defined in `SmartX.Api/Endpoints/SensorEndpoints.cs`.

### Telemetry & tree

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/sensors` | Latest `DeviceStatus` snapshot for every known device. |
| `GET` | `/api/sensors/tree` | The full Facility → Zone → Sub-Zone → Device deployment tree, including each device's attachments. |
| `GET` | `/api/sensors/validate` | Re-runs the recursive tree validator; returns `{ isValid, errors }`. |

### Device lifecycle

| Method | Route | Description |
|---|---|---|
| `POST` | `/api/sensors/register` | Registers a new device under an existing sub-zone. Body: `{ parentSubZoneId, macAddress, category, locationDescription }`. Returns the **server-assigned** `DeviceStatus` — always use this ID, never the MAC address, to key client-side state. |
| `DELETE` | `/api/sensors/{deviceId}` | Removes a single device from the tree and every server-side index (baseline, latest status, batcher, throughput counter). |
| `POST` | `/api/sensors/clear` | Clears **every** device from the tree/registry, keeping the Facility/Zone/Sub-Zone skeleton intact for re-registration. |

### Attachments

| Method | Route | Description |
|---|---|---|
| `POST` | `/api/sensors/{deviceId}/attachments` | Multipart upload (`file` field) of a config file, deployment photo, or hardware log for a device. Saved to `uploads/{deviceId}/{fileName}` on the server and recorded against that device. |
| `GET` | `/api/sensors/{deviceId}/attachments` | Returns the list of attachment file names recorded against this device — the source of truth the dashboard uses to show previously uploaded files, including after a page reload. |
| `GET` | `/api/sensors/{deviceId}/attachments/{fileName}` | Serves the file itself (correct `Content-Type`, range-processing enabled) so it can be viewed or downloaded directly from a browser link. Only file names already recorded against that specific device are served — a request for a file not recorded under that device returns `404`, so one device's route can't be used to read another's files. |

### Dev-only seeder controls

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/sensors/seeder/status` | `{ isRunning: bool }` |
| `POST` | `/api/sensors/seeder/start` | Resumes the background telemetry generator. |
| `POST` | `/api/sensors/seeder/stop` | Pauses it — registered devices keep their last-known status until it's restarted. |

---

## Real-time events (SignalR)

Hub endpoint: `/hubs/telemetry` (see `SmartX.Api/Hubs/TelemetryHub.cs`).

| Event | Payload | Fired when |
|---|---|---|
| `DeviceStatusUpdated` | `DeviceStatus` | Every telemetry tick, and immediately after a successful `register` call. |
| `DeviceRemoved` | `deviceId: string` | A device is deleted via `DELETE /{deviceId}`. |
| `AllDevicesCleared` | — | The registry is cleared via `POST /clear`. |

Every connected dashboard — not just the one that made the API call — applies these, so multiple
open browser tabs stay in sync. On reconnect, the client automatically calls `GET /api/sensors`
to resolve any events it may have missed while offline.

---

## Technical requirements — where to find them

| Requirement | Implementation |
|---|---|
| **Generics, no boxing** | `TelemetryPacket<T> where T : struct` (`SmartX.Core/Telemetry/TelemetryPacket.cs`) — a distinct closed generic type per value type (`<float>`, `<int>`, `<bool>`), so ingestion never boxes into `object`. |
| **Operator overloading** | `SensorReading` (`SmartX.Core/Telemetry/SensorReading.cs`) overloads `+`, `-`, `>`, `<`, `>=`, `<=`, `==`, `!=` — e.g. `meter3 = meter1 + meter2` aggregates two meters' load. |
| **Advanced arrays → Collections** | `TelemetryBatcher<T>` (`SmartX.Core/Batching/TelemetryBatcher.cs`) accumulates raw readings into a growing jagged array (`T[][]`) of completed windows, then flushes the whole structure into a flat `List<TelemetryPacket<T>>`. |
| **Recursion** | `DeploymentTreeValidator.Validate` (`SmartX.Core/Devices/DeploymentTreeValidator.cs`) recursively walks the Facility → Zone → Sub-Zone → Device tree, checking MAC-address uniqueness across the whole tree and that every ancestor in a device's chain is itself configured. |
| **Sensor payload management** | `SensorProfile` (MAC address, category, deployment location) attached to each `Device`-type `DeploymentNode`; managed via the registration form and `POST /register`. |
| **Media/log attachment** | Multipart upload + view/download endpoints described above. |
| **Dynamic engagement feature** | The **Anomaly Constellation** — a live radial visualisation, not a progress bar — justified in the Task 1 research report against real-time-feedback and alert-fatigue literature. |
| **Startup interface, 3 pillars** | `Home.razor` — Sensor Ingestion is active; Command Stream and Mesh Topology are visibly present but disabled, per the brief. |

---

## Running the tests

```
cd SmartX.Api.Tests
dotnet test
```

Covers:
- `DeploymentTreeValidator` — valid tree, duplicate MAC address, unconfigured ancestor, missing sensor profile.
- `SensorReading` operator overloads (`+`, `-`, `>`).
- `TelemetryBatcher<T>`'s jagged-array-to-`List<T>` flush.
- `SensorRegistry` — device registration returns the real server ID (not the MAC), and `RemoveDevice`/`ClearAllDevices` actually forget devices everywhere (tree, statuses, indexes), not just locally.

---

## Docker

Dockerfiles are provided for both the API and the client, plus a `docker-compose.yml`.

```
docker compose up --build
```

This builds and serves the API on `:8080` and the client (via nginx) on `:8081`.

> **Known limitation:** the client currently reads the API's base address from a compile-time
> constant in `Program.cs` (`https://localhost:5001`), which only works for local `dotnet run`.
> Running both containers together will build and serve correctly, but the WASM bundle will still
> try to reach `localhost:5001` from the browser rather than the `smartx-api` container. Fixing
> this properly means moving `apiBaseAddress` into a `wwwroot/appsettings.json` fetched at
> startup instead of a hardcoded constant — flagged here rather than silently left broken.

---

## Troubleshooting

**`NETSDK1112: The runtime pack ... browser-wasm was not downloaded`**
The `wasm-tools` workload isn't installed. Run `dotnet workload install wasm-tools` from an
elevated terminal.

**`A fatal error was encountered. The library 'hostpolicy.dll' required to execute the
application was not found...`**
This is almost always a stale/corrupted `bin`/`obj` folder — often from copying or extracting the
project into a new location (a `(2)`/`(3)` suffix on the folder name is a strong sign this
happened) rather than a clean `git clone`. Fix:
```
# from the solution root
Get-ChildItem -Recurse -Include bin,obj -Directory | Remove-Item -Recurse -Force
dotnet restore
dotnet build
```

**Client won't restore / version mismatch on `Microsoft.AspNetCore.Components.WebAssembly`**
Your installed SDK's minor version doesn't match the `PackageReference` versions pinned in
`SmartX.Client/SmartX.Client.csproj`. Run `dotnet --version` and bump the three package versions
in that file to match.

**Visual Studio won't open the solution / build fails immediately**
You're on Visual Studio 2022. It cannot target `net10.0` at all, regardless of updates — use the
`dotnet` CLI (as above) or upgrade to Visual Studio 2026.

---

## Known limitations

- In-memory only — the `SensorRegistry` (tree, statuses, attachments index) resets on API
  restart. A real deployment would back this with a database.
- Docker Compose cross-container networking for the client → API call isn't wired up yet (see
  [Docker](#docker) above).
- The seeder uses a simple random walk with occasional spikes/disconnects, not modelled sensor
  physics — intentional, per the assignment's "heavily seed with mock data" guidance.

---

## What's next (Part 2 / final PoE)

The landing page's other two pillars are visible but intentionally disabled in Part 1:

- **Real-Time Command Stream & History** — Part 2.
- **Network Topology & Mesh Routing** — final PoE.

The `SmartX.Core` domain model (in particular the deployment tree and `TelemetryPacket<T>`
pipeline) is designed to be extended rather than replaced for these later stages.
