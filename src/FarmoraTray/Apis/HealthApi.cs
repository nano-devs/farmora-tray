using System.Reflection;

namespace FarmoraTray.Apis;

public static class HealthApi
{
    public static IEndpointConventionBuilder MapHealthApi(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/health", () =>
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            return Results.Ok(new { status = "ok", version });
        })
        .WithName("Health");
    }
}
