using FarmoraTray.Models;

namespace FarmoraTray.Services;

public sealed class PrintOrchestrator
{
    private readonly ConfigStore _config;
    private readonly PdfPrintService _pdf;
    private readonly RawPrintService _raw;

    public PrintOrchestrator(ConfigStore config, PdfPrintService pdf, RawPrintService raw)
    {
        _config = config;
        _pdf = pdf;
        _raw = raw;
    }

    public void PrintPdf(byte[] pdfBytes)
    {
        var printer = ResolvePrinter(PrintMode.DotMatrix);
        _pdf.Print(printer, pdfBytes, "dotmatrix");
    }

    public void PrintRaw(byte[] rawBytes)
    {
        var printer = ResolvePrinter(PrintMode.Thermal);
        _raw.Print(printer, rawBytes, "thermal");
    }

    private string ResolvePrinter(PrintMode mode)
    {
        var printer = _config.GetPrinter(mode);
        if (string.IsNullOrWhiteSpace(printer))
        {
            throw new PrinterNotConfiguredException(mode.ToString());
        }

        return printer;
    }
}
