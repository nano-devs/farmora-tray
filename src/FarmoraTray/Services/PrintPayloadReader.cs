using System.Text.Json;
using FarmoraTray.Models;

namespace FarmoraTray.Services;

public static class PrintPayloadReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
