using FarmoraTray.Services;

namespace FarmoraTray.Apis;

public static class PrintApi
{
    public static IEndpointConventionBuilder MapPrintApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/");

        group.MapPost("/dotmatrix", (HttpRequest request, PrintOrchestrator print, CancellationToken ct) =>
            HandleDotMatrixAsync(request, print, ct));

        group.MapPost("/thermal", (HttpRequest request, PrintOrchestrator print, CancellationToken ct) =>
            HandleRawAsync(request, print, ct));

        return group;
    }

    /// <summary>
    /// Dot matrix accepts PDF (printed via the PDF handler, as before) or raw ESC/P bytes
    /// (sent straight to the printer). See <see cref="DotMatrixPayload"/> for how they are told apart.
    /// </summary>
    private static async Task<IResult> HandleDotMatrixAsync(
        HttpRequest request,
        PrintOrchestrator print,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);

        DotMatrixPayload.Result payload;
        try
        {
            payload = DotMatrixPayload.Parse(request.ContentType, buffer.ToArray());
        }
        catch (InvalidDotMatrixPayloadException ex)
        {
            return Results.BadRequest(new { title = "Invalid payload", detail = ex.Message });
        }

        return payload.Format == DotMatrixFormat.Pdf
            ? Execute(() => print.PrintPdf(payload.Bytes))
            : Execute(() => print.PrintDotMatrixRaw(payload.Bytes));
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
        catch (PrinterNotReadyException ex)
        {
            return Results.Json(
                new { title = "Printer not ready", detail = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new { title = "Print failed", detail = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
