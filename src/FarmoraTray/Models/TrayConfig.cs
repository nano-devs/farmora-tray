namespace FarmoraTray.Models;

public sealed class TrayConfig
{
    public string ApiKey { get; set; } = "";
    public string? AllowedOrigin { get; set; }
    public int Port { get; set; } = 9123;
    public PrinterMappings Printers { get; set; } = new();
}

public sealed class PrinterMappings
{
    public string? DotMatrix { get; set; }
    public string? Thermal { get; set; }
}

public sealed class UpdateConfigRequest
{
    public string? AllowedOrigin { get; set; }
    public PrinterMappingsUpdate? Printers { get; set; }
}

public sealed class PrinterMappingsUpdate
{
    public string? DotMatrix { get; set; }
    public string? Thermal { get; set; }
}

public sealed class ConfigResponse
{
    public string? AllowedOrigin { get; set; }
    public int Port { get; set; }
    public PrinterMappings Printers { get; set; } = new();
}
