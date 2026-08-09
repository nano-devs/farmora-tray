using FarmoraTray.Models;
using FarmoraTray.Services;

namespace FarmoraTray.Apis;

public static class ConfigApi
{
    public static IEndpointConventionBuilder MapConfigApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/config");

        group.MapGet("/", (ConfigStore store) =>
        {
            var current = store.Current;
            return Results.Ok(new ConfigResponse
            {
                AllowedOrigin = current.AllowedOrigin,
                Port = current.Port,
                Printers = current.Printers
            });
        })
        .WithName("GetConfig");

        group.MapPut("/", (UpdateConfigRequest request, ConfigStore store) =>
        {
            var updated = store.Update(request);
            return Results.Ok(new ConfigResponse
            {
                AllowedOrigin = updated.AllowedOrigin,
                Port = updated.Port,
                Printers = updated.Printers
            });
        })
        .WithName("UpdateConfig");

        return group;
    }
}
