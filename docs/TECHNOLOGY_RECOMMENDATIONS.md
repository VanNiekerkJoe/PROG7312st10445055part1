# Smart-X: technology recommendations

Each recommendation names the exact .NET integration point in this codebase, the benefit, and the trade-off.

## 1. MQTT ingestion with MQTTnet (scalability, protocol realism)

**What.** Add an MQTT subscriber (the MQTTnet NuGet package) as a second front door next to `POST /api/telemetry/ingest`. Devices publish to topics such as `smartx/{mac}/telemetry`; a hosted service subscribes and calls the existing `TelemetryIngestQueue.Enqueue`.

**Why it fits.** MQTT is the usual transport on constrained ESP32 hardware (small packets, QoS levels, retained messages, last-will for disconnect detection). The ingest queue, classifier, device dictionary and history are already transport-agnostic, so nothing downstream changes: the README already documents that design decision. Last-will messages would replace the 20-second silence sweep with an immediate disconnect signal.

**Trade-off.** Needs a broker (Mosquitto or EMQX) and TLS plus per-device credentials, which adds operational work.

## 2. CI/CD with GitHub Actions (quality and security)

**What.** A workflow on every push: `dotnet restore`, `dotnet build -warnaserror`, `dotnet test`, then `docker compose build`, plus CodeQL and `dotnet list package --vulnerable`.

**Why it fits.** The repository is already on GitHub with a test project and Dockerfiles for the API, client and simulator. The module's rule that non-compiling code earns no marks is exactly what a build gate prevents, and the fresh-clone start-up failure fixed in the update ledger would have been caught by a clean-runner build.

**Trade-off.** Blazor WebAssembly builds need the `wasm-tools` workload on the runner, which lengthens the pipeline; cache NuGet and workloads.

## 3. Time-series storage: TimescaleDB (PostgreSQL) via Npgsql (durability, long history)

**What.** Persist telemetry in a TimescaleDB hypertable through Npgsql or EF Core. Keep the AVL tree as the hot, in-memory window for the dashboard (last few thousand readings per device) and query the database for anything older.

**Why it fits.** History is in memory today and lost on restart. Time-series databases partition by time, compress old chunks and answer the same "between 14:00 and 16:00" range query with a time index, which is the AVL idea at disk scale. Npgsql is a first-class .NET provider, and the repository interface (`SortedHistoryLog.Append` / `Range`) is a clean seam to put behind an `ITelemetryStore`.

**Trade-off.** One more service to run (a Compose container locally). Write batching is needed so ingest throughput is not tied to database latency.

## 4. SignalR Redis backplane (horizontal scale-out)

**What.** `AddSignalR().AddStackExchangeRedis(...)` from `Microsoft.AspNetCore.SignalR.StackExchangeRedis`, plus moving shared state (device store, alerts) out of single-process singletons.

**Why it fits.** Live updates currently reach only dashboards connected to the same API instance. A backplane lets several API instances behind a load balancer broadcast to every client, which matters for the "thousands of dispersed nodes" scenario in the brief.

**Trade-off.** The in-memory singletons (`DeviceStateStore`, `ActiveAlertSet`) also need to be shared or sharded; the backplane alone is not enough.

## 5. LLM alert summaries with Microsoft.Extensions.AI (operator workflow)

**What.** A daily hosted service that collects the previous 24 hours of critical alerts, detours and stranded-node events, sends them through the `IChatClient` abstraction from `Microsoft.Extensions.AI` and stores a short plain-language briefing shown on the landing page.

**Why it fits.** The brief suggests LLM summaries. `IChatClient` keeps the provider swappable (Claude, Azure OpenAI, or a local model) without touching business logic, and the data already exists as structured records. Only aggregated, non-sensitive facts (device IDs, alert kinds, counts) would be sent; MAC addresses can be omitted.

**Trade-off.** Cost, latency and the risk of a confident wrong summary: show it as a convenience beside the raw alert list, never instead of it.

## 6. OpenTelemetry with Grafana (observability)

**What.** The OpenTelemetry .NET SDK exports traces and metrics (ingest queue depth, priority-lane latency, packets per second, Prim recompute time) to Prometheus and Grafana, or Application Insights.

**Why it fits.** `PipelineStats` already computes the interesting numbers; exporting them gives history, alerting and dashboards without building those in the app. It would also give evidence for scaling decisions, such as when graph rebuilds per request should become cached.

**Trade-off.** Extra containers locally; metric cardinality must be kept low (do not label metrics by MAC).

## 7. Real link quality for routing (algorithmic)

**What.** Replace the simulated link costs with RSSI or packet-loss reports from real ESP-NOW or Thread neighbours, so the existing Prim and Dijkstra routing run on measured costs instead of simulated ones.

**Why it fits.** The graph, MST and shortest-path code only need a cost per link, so swapping the simulated cost for a measured one changes `MeshTopologyBuilder` and nothing else.

**Trade-off.** Real radio costs change constantly, so the graph needs incremental updates and hysteresis to avoid route flapping.

## 8. Home Assistant integration (ecosystem)

**What.** Publish device state to Home Assistant over MQTT discovery, so Smart-X nodes appear as entities in the Home Assistant dashboard and automations.

**Why it fits.** Brief names Home Assistant explicitly. MQTT (recommendation 1) is its native integration path, so this costs little once the broker exists.

**Trade-off.** Two dashboards for one fleet; decide which is authoritative for manual overrides to avoid conflicting commands.
