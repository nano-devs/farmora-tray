using FarmoraTray.Printing;

namespace FarmoraTray.Services;

public sealed class PrinterDiscovery
{
    public IReadOnlyList<string> ListInstalledPrinters() => NativePrinterEnumeration.ListPrinterNames();

    public bool IsInstalled(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
        {
            return false;
        }

        foreach (var printer in ListInstalledPrinters())
        {
            if (string.Equals(printer, printerName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
