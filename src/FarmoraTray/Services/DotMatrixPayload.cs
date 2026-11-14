using System.Text.Json;
using FarmoraTray.Models;

namespace FarmoraTray.Services;

public enum DotMatrixFormat
{
    /// <summary>A PDF, printed through the registered PDF handler (printto).</summary>
    Pdf,

    /// <summary>Printer-ready bytes (Epson ESC/P), sent straight to the spooler.</summary>
    Raw
}

/// <summary>
/// Works out what a <c>POST /dotmatrix</c> body is. Both formats are accepted on the same route
/// so older frontends that send PDF keep working while new ones send ESC/P:
/// <list type="bullet">
/// <item>Bytes (any content type), or JSON <c>{ "pdfBase64" }</c> / <c>{ "rawBase64" }</c>.</item>
/// <item>The bytes decide the format: a body that starts with <c>%PDF-</c> is a PDF, anything else is raw.
/// ESC/P starts with <c>ESC @</c>, so it can never look like a PDF.</item>
/// <item>A body declared as PDF (<c>application/pdf</c> or <c>pdfBase64</c>) that isn't one is rejected
/// rather than handed to the PDF reader.</item>
/// </list>
/// Pure (no ASP.NET types) so it can be unit tested off Windows.
/// </summary>
public static class DotMatrixPayload
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    public sealed record Result(DotMatrixFormat Format, byte[] Bytes);

    /// <exception cref="InvalidDotMatrixPayloadException">The body is empty, malformed or mislabelled.</exception>
    public static Result Parse(string? contentType, byte[] body)
    {
        if (body.Length == 0)
            throw new InvalidDotMatrixPayloadException(MissingBodyDetail);

        var declaredPdf = Is(contentType, "application/pdf");
        var bytes = body;

        if (Is(contentType, "application/json") || (!declaredPdf && !Is(contentType, "application/octet-stream") && StartsWithJson(body)))
        {
            (bytes, declaredPdf) = ReadJson(body);
        }

        var format = LooksLikePdf(bytes) ? DotMatrixFormat.Pdf : DotMatrixFormat.Raw;
        if (declaredPdf && format != DotMatrixFormat.Pdf)
            throw new InvalidDotMatrixPayloadException("The body was sent as PDF but does not start with %PDF-.");

        return new Result(format, bytes);
    }

    public const string MissingBodyDetail =
        "Send PDF bytes (application/pdf), ESC/P bytes (application/octet-stream), " +
        "or JSON { \"pdfBase64\": \"...\" } / { \"rawBase64\": \"...\" }.";

    /// <summary>True when the data starts with %PDF- (after an optional UTF-8 BOM or whitespace).</summary>
    public static bool LooksLikePdf(ReadOnlySpan<byte> data)
    {
        var i = 0;
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            i = 3;
        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;
        return data[i..].StartsWith(PdfMagic);
    }

    private static (byte[] Bytes, bool DeclaredPdf) ReadJson(byte[] body)
    {
        DotMatrixPrintRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<DotMatrixPrintRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            throw new InvalidDotMatrixPayloadException("The JSON body could not be read.");
        }

        var hasPdf = !string.IsNullOrWhiteSpace(request?.PdfBase64);
        var hasRaw = !string.IsNullOrWhiteSpace(request?.RawBase64);
        if (hasPdf == hasRaw)
        {
            throw new InvalidDotMatrixPayloadException(hasPdf
                ? "Send either pdfBase64 or rawBase64, not both."
                : MissingBodyDetail);
        }

        var base64 = hasPdf ? request!.PdfBase64! : request!.RawBase64!;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new InvalidDotMatrixPayloadException($"{(hasPdf ? "pdfBase64" : "rawBase64")} is not valid Base64.");
        }

        if (bytes.Length == 0)
            throw new InvalidDotMatrixPayloadException(MissingBodyDetail);

        return (bytes, hasPdf);
    }

    private static bool StartsWithJson(byte[] body)
    {
        foreach (var b in body)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                continue;
            return b == (byte)'{';
        }

        return false;
    }

    private static bool Is(string? contentType, string mediaType) =>
        contentType?.Contains(mediaType, StringComparison.OrdinalIgnoreCase) == true;
}

public sealed class InvalidDotMatrixPayloadException(string detail) : Exception(detail);
