# Smart-X: updates made after Parts 1 and 2

**Module:** PROG7312 &middot; **Student:** ST10445055 &middot; **Repository:** https://github.com/VanNiekerkJoe/PROG7312st10445055part1

This ledger lists every change made to the code after the Part 1 and Part 2 submissions, with the commit that made it. Rows marked **Lecturer feedback** respond to a comment on a previous part; the quoted comment goes in the "Feedback received" column. Rows marked **Self-identified** were found during testing.

> **Before submitting:** the "Feedback received" column must hold your lecturer's actual wording from the Part 1 and Part 2 marking sheets. Rows where it still says `PASTE` are not yet confirmed as feedback responses; delete the row, or reclassify it as Self-identified, if it was not.

| # | Source | Feedback received | What changed | Evidence (commit / file) |
|---|---|---|---|---|
| 1 | Lecturer feedback (Part 2) | PASTE | Suggested actions are ranked with safeguards and evidence thresholds; applying a suggestion no longer trains the engine on its own output. | `011aad6`, `09db265`; `Insights/ActionPatternEngine.cs`, `ActionPatternRankingTests.cs` |
| 2 | Lecturer feedback (Part 2) | PASTE | Suggested actions improved and removed from the list once applied. | `2c93d0e`, `ae85ff3` |
| 3 | Self-identified | n/a | Zone filtering bug fixed. | `eb7a9f5` |
| 4 | Self-identified | n/a | Manual override indicators (Pump, Relay, Valve) now follow the server's per-output state instead of a client-side guess. | `f54c411`, `51fe2e6` |
| 5 | Lecturer feedback (Part 1 / 2) | PASTE | Firebase email/password authentication added; every REST endpoint and both SignalR hubs now require a valid ID token. | `8621424`; `Program.cs`, `AuthEndpoints.cs`, `FirebaseAuthSession.cs` |
| 6 | Lecturer feedback (Part 1) | PASTE | Device list import from a tab-separated `.txt` file, with per-line error reporting. | `8621424`; `DeviceListImport.razor` |
| 7 | Self-identified | n/a | Landing page reworked; Firebase setup and device-list format documented in the README. | `8621424` |
| 8 | Self-identified | n/a | API rejections now surface their real status and details in the UI instead of a generic failure message. | `45ee704`; `TelemetryApiClient.cs`, `FirebaseAuthSession.cs` |
| 9 | Self-identified | n/a | Fresh-clone start-up issue fixed (per the commit message: a freshly cloned repository did not start). | `01d2258` |
| 10 | Self-identified | n/a | CORS fixed: the API allows the configured HTTPS client origin plus the extra HTTP origins, which removed "could not contact Firebase authentication" on the HTTP address. | `8be80f1`; `Program.cs` |
| 11 | Self-identified | n/a | Duplicate README inside the solution folder removed; one README at the repository root. | `a152a55` |

## Part 3 changes (this submission)

| # | What changed | Why | Evidence |
|---|---|---|---|
| 12 | **Replaced the history store's `SortedDictionary` with a hand-written AVL tree**, added `Range(from, to)`, raised the per-device cap from 300 to 5000 readings. | Part 2 history could only return "the latest N". The brief needs timestamp-range queries without iterating a large list. Existing history tests still pass unchanged. | `Storage/AvlTree.cs`, `Storage/SortedHistoryLog.cs` |
| 13 | New `MinHeap`, `MeshGraph` (adjacency list, BFS, DFS, Prim, Kruskal, union-find) and `MeshTopologyBuilder`. | Network graphing, traversal and minimum spanning tree requirements. | `Collections/MinHeap.cs`, `Mesh/*.cs` |
| 14 | `MeshTopologyService` and `/api/mesh/*` endpoints (topology, route by MAC or ID, reachability, fail / restore, history range, demo backfill). | Server-side logic for the Network Topology page; all routes authenticated like the rest of the API. | `Services/MeshTopologyService.cs`, `Endpoints/MeshEndpoints.cs` |
| 15 | New **Mesh topology** page and `MeshApiClient`; the disabled placeholder in the navigation bar is now a live link. | Main menu integration: all three modules active and interconnected. | `Pages/MeshTopology.razor`, `Services/MeshApiClient.cs`, `Components/NavMenu.razor` |
| 16 | Fixed a Razor compile error found while building the page: SVG `<text>` elements directly inside a code block are parsed as Razor's own `<text>` tag. Each is now wrapped in a `<g>`. | The page would not compile. | `Pages/MeshTopology.razor` |
| 17 | Added 16 unit tests for the AVL tree, heap, graph algorithms and mesh service. | Verify balance, range correctness, Prim equals Kruskal (including 100 random meshes), healing around a failed node. | `SmartX.Api.Tests/Part3MeshTests.cs` |
| 18 | README section 16, completion report and technology recommendations. | Documentation and reporting requirements. | `README.md`, `docs/` |
