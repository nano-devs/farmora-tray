using FarmoraTray.Services;

namespace FarmoraTray.Apis;

public static class PrintApi
{
    public static IEndpointConventionBuilder MapPrintApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/");

        group.MapPost("/dotmatrix", (HttpRequest request, PrintOrchestrator print, CancellationToken ct) =>
            HandlePdfAsync(request, print, ct));

        group.MapPost("/thermal", (HttpRequest request, PrintOrchestrator print, CancellationToken ct) =>
            HandleRawAsync(request, print, ct));

        return group;
    }

    private static async Task<IResult> HandlePdfAsync(
        HttpRequest request,
        PrintOrchestrator print,
        CancellationToken cancellationToken)
    {
        byte[]? pdf;
        try
        {
            pdf = await PrintPayloadReader.ReadPdfAsync(request, cancellationToken);
        }
        catch (FormatException)
        {
            return Results.BadRequest(new { title = "Invalid payload", detail = "pdfBase64 is not valid Base64." });
        }

        if (pdf is null || pdf.Length == 0)
        {
            return Results.BadRequest(new
            {
                title = "Invalid payload",
                detail = "Send application/pdf bytes or JSON { \"pdfBase64\": \"...\" }."
            });
        }

        return Execute(() => print.PrintPdf(pdf));
    }

    private static async Task<IResult> HandleRawAsync(
        HttpRequest request,
        PrintOrchestrator print,
        CancellationToken cancellationToken)
    {
        byte[]? raw;
        try
        {
            raw = await PrintPayloadReader.ReadRawAsync(request, cancellationToken);
        }
        catch (FormatException)
        {
            return Results.BadRequest(new { title = "Invalid payload", detail = "rawBase64 is not valid Base64." });
        }

        if (raw is null || raw.Length == 0)
        {
            return Results.BadRequest(new
            {
                title = "Invalid payload",
                detail = "Send application/octet-stream bytes or JSON { \"rawBase64\": \"...\" }."
            });
        }

        return Execute(() => print.PrintRaw(raw));
    }

    private static IResult Execute(Action action)
    {
        try
        {
            action();
            return Results.NoContent();
        }
        catch (PrinterNotConfiguredException ex)
        {
            return Results.NotFound(new { title = "Printer not configured", detail = ex.Message });
        }
        catch (PrinterNotFoundException ex)
        {
            return Results.NotFound(new { title = "Printer not found", detail = ex.Message });
        }
        catch (Exception ex)
        {
            return Results.Json(
                new { title = "Print failed", detail = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
