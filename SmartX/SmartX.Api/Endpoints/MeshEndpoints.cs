using SmartX.Api.Services;
using SmartX.Core.Mesh;

namespace SmartX.Api.Endpoints;

public static class MeshEndpoints
{
    public static void MapMeshEndpoints(this WebApplication app)
    {
        var mesh = app.MapGroup("/api/mesh").WithTags("Mesh");
        mesh.RequireAuthorization();

        // Whole graph: nodes, links, the Prim MST, and connectivity counts.
        mesh.MapGet("/", (MeshTopologyService service) => Results.Ok(service.Topology()));

        mesh.MapGet("/subnets", (MeshTopologyService service) => Results.Ok(service.Subnets()));

        // Route of one sensor by MAC address or device ID, healthy versus healed.
        mesh.MapGet("/route/{idOrMac}", (string idOrMac, MeshTopologyService service) =>
        {
            var route = service.Route(idOrMac);
            return route.Found ? Results.Ok(route) : Results.NotFound(route);
        });

        // BFS / DFS: who is connected to the gateway (or one subnet), who is down, and where traffic hops instead.
        mesh.MapGet("/reachable", (string? subnet, string? algo, MeshTopologyService service) =>
        {
            var kind = string.Equals(algo, "dfs", StringComparison.OrdinalIgnoreCase) ? TraversalKind.Dfs : TraversalKind.Bfs;
            return Results.Ok(service.Subnet(subnet, kind));
        });

        mesh.MapPost("/nodes/{idOrMac}/fail", (string idOrMac, MeshTopologyService service) =>
            service.Fail(idOrMac) ? Results.Ok(new { failed = idOrMac }) : Results.NotFound(new { error = $"No sensor matches \"{idOrMac}\"." }));

        mesh.MapPost("/nodes/{idOrMac}/restore", (string idOrMac, MeshTopologyService service) =>
            service.Restore(idOrMac) ? Results.Ok(new { restored = idOrMac }) : Results.NotFound(new { error = $"No sensor matches \"{idOrMac}\"." }));

        mesh.MapPost("/restore-all", (MeshTopologyService service) =>
        {
            service.RestoreAll();
            return Results.Ok(new { restored = "all" });
        });

        // Range query served by the AVL tree: readings between two timestamps for one sensor.
        mesh.MapGet("/history/{idOrMac}", (string idOrMac, DateTimeOffset? from, DateTimeOffset? to, int? limit, MeshTopologyService service) =>
        {
            var end = to ?? DateTimeOffset.UtcNow;
            var start = from ?? end.AddHours(-1);
            var result = service.HistoryRange(idOrMac, start, end);
            if (!result.Found)
            {
                return Results.NotFound(result);
            }

            var cap = Math.Clamp(limit ?? 500, 1, 5000);
            return Results.Ok(result.Points.Count > cap ? result with { Points = result.Points.TakeLast(cap).ToList() } : result);
        });

        mesh.MapPost("/history/{idOrMac}/backfill", (string idOrMac, int? count, int? hours, MeshTopologyService service) =>
        {
            var added = service.Backfill(idOrMac, count ?? 1500, TimeSpan.FromHours(Math.Clamp(hours ?? 6, 1, 72)));
            return added < 0
                ? Results.NotFound(new { error = $"No sensor matches \"{idOrMac}\"." })
                : Results.Ok(new { added });
        });
    }
}
