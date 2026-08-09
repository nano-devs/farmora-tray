using System.Text.Json;
using FarmoraTray.Models;

namespace FarmoraTray.Services;

public static class PrintPayloadReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<byte[]?> ReadPdfAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentType?.Contains("application/pdf", StringComparison.OrdinalIgnoreCase) == true)
        {
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms, cancellationToken);
            return ms.ToArray();
        }

        if (request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var body = await JsonSerializer.DeserializeAsync<PdfPrintRequest>(request.Body, JsonOptions, cancellationToken);
            if (string.IsNullOrWhiteSpace(body?.PdfBase64))
            {
                return null;
            }

            return Convert.FromBase64String(body.PdfBase64);
        }

        // Fallback: try JSON then raw bytes
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (bytes.Length == 0)
        {
            return null;
        }

        if (bytes[0] == (byte)'{')
        {
            var body = JsonSerializer.Deserialize<PdfPrintRequest>(bytes, JsonOptions);
            if (string.IsNullOrWhiteSpace(body?.PdfBase64))
            {
                return null;
            }

            return Convert.FromBase64String(body.PdfBase64);
        }

        return bytes;
    }

    public static async Task<byte[]?> ReadRawAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var body = await JsonSerializer.DeserializeAsync<RawPrintRequest>(request.Body, JsonOptions, cancellationToken);
            if (string.IsNullOrWhiteSpace(body?.RawBase64))
            {
                return null;
            }

            return Convert.FromBase64String(body.RawBase64);
        }

        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, cancellationToken);
        var bytes = ms.ToArray();
        return bytes.Length == 0 ? null : bytes;
    }
}
