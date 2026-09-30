using FarmoraTray.Printing;

namespace FarmoraTray.Services;

public sealed class RawPrintService
{
    private readonly PrinterDiscovery _printers;

    public RawPrintService(PrinterDiscovery printers)
    {
        _printers = printers;
    }

    public void Print(string printerName, byte[] data, string documentName = "Farmora Tray Thermal")
    {
        if (!_printers.IsInstalled(printerName))
        {
            throw new PrinterNotFoundException(printerName);
        }

        SpoolerJobMonitor.EnsureReady(printerName);
        var jobId = NativeRawPrinter.SendBytes(printerName, data, documentName);
        SpoolerJobMonitor.WaitForRawJob(printerName, jobId);
    }
}

public sealed class PrinterNotFoundException : Exception
{
    public PrinterNotFoundException(string printerName)
        : base($"Printer '{printerName}' is not installed on this PC.")
    {
        PrinterName = printerName;
    }

    public string PrinterName { get; }
}

public sealed class PrinterNotConfiguredException : Exception
{
    public PrinterNotConfiguredException(string documentKind)
        : base($"No printer configured for '{documentKind}'.")
    {
        DocumentKind = documentKind;
    }

    public string DocumentKind { get; }
}

public sealed class PrinterNotReadyException : Exception
{
    public PrinterNotReadyException(string printerName, string reason)
        : base($"Printer '{printerName}' is not ready: {reason}.")
    {
        PrinterName = printerName;
        Reason = reason;
    }

    public string PrinterName { get; }

    public string Reason { get; }
}
