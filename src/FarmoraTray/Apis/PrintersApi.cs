using FarmoraTray.Services;

namespace FarmoraTray.Apis;

public static class PrintersApi
{
    public static IEndpointConventionBuilder MapPrintersApi(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/printers", (PrinterDiscovery discovery) =>
        {
            return Results.Ok(new { printers = discovery.ListInstalledPrinters() });
        })
        .WithName("ListPrinters");
    }
}
