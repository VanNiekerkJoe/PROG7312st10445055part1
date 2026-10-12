namespace SmartX.Api.Endpoints;

public sealed record FirebaseClientConfiguration(string ApiKey, string ProjectId);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/auth/config", (IConfiguration configuration) =>
        {
            var apiKey = configuration["Firebase:ApiKey"];
            var projectId = configuration["Firebase:ProjectId"];
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(projectId))
            {
                return Results.Problem(
                    "Firebase authentication is not configured on the API.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new FirebaseClientConfiguration(apiKey, projectId));
        }).AllowAnonymous();
    }
}
