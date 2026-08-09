using System.Runtime.InteropServices;

namespace FarmoraTray.Printing;

/// <summary>
/// Sends raw bytes to a Windows printer via the spooler (ESC/POS, etc.).
/// </summary>
public static class NativeRawPrinter
{
    public static void SendBytes(string printerName, byte[] data, string documentName = "Farmora Tray")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        ArgumentNullException.ThrowIfNull(data);

        if (!OpenPrinter(printerName, out var printerHandle, IntPtr.Zero))
        {
            throw new InvalidOperationException($"Unable to open printer '{printerName}'.");
        }

        try
        {
            var docInfo = new DOCINFOA
            {
                pDocName = documentName,
                pDataType = "RAW"
            };

            if (StartDocPrinter(printerHandle, 1, docInfo) == 0)
            {
                throw new InvalidOperationException($"StartDocPrinter failed for '{printerName}'.");
            }

            try
            {
                if (!StartPagePrinter(printerHandle))
                {
                    throw new InvalidOperationException($"StartPagePrinter failed for '{printerName}'.");
                }

                try
                {
                    var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
                    try
                    {
                        if (!WritePrinter(printerHandle, pinned.AddrOfPinnedObject(), data.Length, out var written)
                            || written != data.Length)
                        {
                            throw new InvalidOperationException(
                                $"WritePrinter failed for '{printerName}' (wrote {written} of {data.Length} bytes).");
                        }
                    }
                    finally
                    {
                        pinned.Free();
                    }
                }
                finally
                {
                    EndPagePrinter(printerHandle);
                }
            }
            finally
            {
                EndDocPrinter(printerHandle);
            }
        }
        finally
        {
            ClosePrinter(printerHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private sealed class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)]
        public string? pDocName;

        [MarshalAs(UnmanagedType.LPStr)]
        public string? pOutputFile;

        [MarshalAs(UnmanagedType.LPStr)]
        public string? pDataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int StartDocPrinter(IntPtr hPrinter, int level, [In] DOCINFOA di);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);
}
