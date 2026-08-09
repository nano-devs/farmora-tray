using System.Runtime.InteropServices;

namespace FarmoraTray.Printing;

internal static class NativePrinterEnumeration
{
    private const int PRINTER_ENUM_LOCAL = 0x00000002;
    private const int PRINTER_ENUM_CONNECTIONS = 0x00000004;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    public static IReadOnlyList<string> ListPrinterNames()
    {
        var flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
        var level = 2;

        EnumPrinters(flags, null, level, IntPtr.Zero, 0, out var needed, out _);
        if (needed <= 0)
        {
            return Array.Empty<string>();
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumPrinters(flags, null, level, buffer, needed, out _, out var returned))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 0 && error != ERROR_INSUFFICIENT_BUFFER)
                {
                    throw new InvalidOperationException($"EnumPrinters failed with Win32 error {error}.");
                }
            }

            var names = new List<string>(returned);
            var stride = Marshal.SizeOf<PRINTER_INFO_2>();
            for (var i = 0; i < returned; i++)
            {
                var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buffer + (i * stride));
                if (!string.IsNullOrWhiteSpace(info.pPrinterName))
                {
                    names.Add(info.pPrinterName);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PRINTER_INFO_2
    {
        public string? pServerName;
        public string? pPrinterName;
        public string? pShareName;
        public string? pPortName;
        public string? pDriverName;
        public string? pComment;
        public string? pLocation;
        public IntPtr pDevMode;
        public string? pSepFile;
        public string? pPrintProcessor;
        public string? pDatatype;
        public string? pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool EnumPrinters(
        int flags,
        string? name,
        int level,
        IntPtr pPrinterEnum,
        int cbBuf,
        out int pcbNeeded,
        out int pcReturned);
}
