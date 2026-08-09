using System.Diagnostics;

namespace FarmoraTray.Services;

/// <summary>
/// Prints a PDF by writing a temp file and invoking the Windows "printto" shell verb
/// (Edge / Acrobat / the registered PDF handler).
/// </summary>
public sealed class PdfPrintService
{
    private readonly PrinterDiscovery _printers;

    public PdfPrintService(PrinterDiscovery printers)
    {
        _printers = printers;
    }

    public void Print(string printerName, byte[] pdfBytes, string documentName = "Farmora Tray PDF")
    {
        if (!_printers.IsInstalled(printerName))
        {
            throw new PrinterNotFoundException(printerName);
        }

        if (pdfBytes.Length == 0)
        {
            throw new ArgumentException("PDF payload is empty.", nameof(pdfBytes));
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Farmora Tray PDF printing requires Windows.");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FarmoraTray");
        Directory.CreateDirectory(tempDir);
        var pdfPath = Path.Combine(tempDir, $"{SanitizeFileName(documentName)}-{Guid.NewGuid():N}.pdf");

        File.WriteAllBytes(pdfPath, pdfBytes);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pdfPath,
                UseShellExecute = true,
                Verb = "printto",
                Arguments = $"\"{printerName}\"",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Unable to start the PDF print handler. Ensure a PDF application is installed (e.g. Microsoft Edge).");

            // Most handlers return quickly after spooling; wait a bounded time then orphan-clean.
            if (!process.WaitForExit(60_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignored
                }
            }
        }
        finally
        {
            // Give the handler a moment to open the file before deleting.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                try
                {
                    if (File.Exists(pdfPath))
                    {
                        File.Delete(pdfPath);
                    }
                }
                catch
                {
                    // ignored — temp cleaner / reboot will remove later
                }
            });
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "document" : cleaned;
    }
}
