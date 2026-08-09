namespace FarmoraTray.Models;

public sealed class PdfPrintRequest
{
    public string? PdfBase64 { get; set; }
}

public sealed class RawPrintRequest
{
    public string? RawBase64 { get; set; }
}

public enum PrintMode
{
    DotMatrix,
    Thermal
}
