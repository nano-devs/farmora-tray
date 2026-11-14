namespace FarmoraTray.Models;

/// <summary>JSON body for <c>POST /dotmatrix</c>: exactly one of the two fields.</summary>
public sealed class DotMatrixPrintRequest
{
    public string? PdfBase64 { get; set; }

    public string? RawBase64 { get; set; }
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
