using SmartX.Api.Services;
using SmartX.Core.Commands;
using SmartX.Core.Insights;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Endpoints;

public sealed record SearchLogRequest(string Term);

public static class CommandEndpoints
{
    public static void MapCommandEndpoints(this WebApplication app)
    {
        var commands = app.MapGroup("/api/commands").WithTags("Commands");

        commands.MapGet("/", (CommandHistory history) =>
            Results.Ok(new CommandLogResponse(history.Snapshot(), history.UndoDepth)));

        commands.MapPost("/", async (IssueCommandRequest request, CommandService service) =>
        {
            var result = await service.IssueAsync(request.DeviceId, request.Type, "operator");
            return result.Ok ? Results.Ok(result) : Results.NotFound(result);
        });

        commands.MapPost("/undo", async (CommandService service) =>
        {
            var result = await service.UndoAsync();
            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
        });

        var insights = app.MapGroup("/api/insights").WithTags("Insights");

        // "Suggested Actions" for the dashboard: learned patterns, cold-start
        // rules and the problem-device watch-list.
        insights.MapGet("/", (string? search, ActionPatternEngine patterns, SensorRegistry registry) =>
            Results.Ok(patterns.Suggest(search, registry.AllStatuses)));

        // Logs a search so the engine can learn "search X, then command Y".
        insights.MapPost("/search", (SearchLogRequest request, ActionPatternEngine patterns) =>
        {
            patterns.ObserveSearch(request.Term, DateTimeOffset.UtcNow);
            return Results.Ok(new { logged = true });
        });

        insights.MapPost("/train-demo", (ActionPatternEngine patterns, SensorRegistry registry) =>
        {
            patterns.SeedDemoPatterns(registry.AllStatuses);
            return Results.Ok(new { patterns = patterns.LearnedPatternCount });
        });
    }
}
