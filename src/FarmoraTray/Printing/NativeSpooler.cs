using System.Runtime.InteropServices;

namespace FarmoraTray.Printing;

internal readonly record struct PrinterSnapshot(uint Status, uint Attributes, string? PortName);

/// <summary>
/// Win32 spooler calls shared by raw and PDF printing. Status checks use GetPrinter level 2,
/// which is fresher than EnumPrinters.
/// </summary>
internal static class NativeSpooler
{
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorInvalidParameter = 87;
    private const uint JobControlDelete = 5;
    private const int PrinterAccessAdminister = 0x00000004;
    private const int PrinterAccessUse = 0x00000008;
    private const int MaxBufferBytes = 16 * 1024 * 1024;

    public static PrinterSession Open(string printerName)
    {
        if (TryOpen(printerName, PrinterAccessUse | PrinterAccessAdminister, out var handle)
            || TryOpen(printerName, desiredAccess: null, out handle))
        {
            return new PrinterSession(printerName, handle);
        }

        throw new InvalidOperationException($"Unable to open printer '{printerName}'.");
    }

    private static bool TryOpen(string printerName, int? desiredAccess, out IntPtr handle)
    {
        bool opened;
        if (desiredAccess is not int access)
        {
            opened = OpenPrinter(printerName, out handle, IntPtr.Zero);
        }
        else
        {
            var defaults = new PrinterDefaults { DesiredAccess = access };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<PrinterDefaults>());
            try
            {
                Marshal.StructureToPtr(defaults, ptr, false);
                opened = OpenPrinter(printerName, out handle, ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        if (!opened && handle != IntPtr.Zero)
        {
            ClosePrinter(handle);
            handle = IntPtr.Zero;
        }

        return opened;
    }

    internal sealed class PrinterSession : IDisposable
    {
        private IntPtr _handle;

        internal PrinterSession(string printerName, IntPtr handle)
        {
            PrinterName = printerName;
            _handle = handle;
        }

        public string PrinterName { get; }

        public void Dispose()
        {
            var handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                ClosePrinter(handle);
            }
        }

        public PrinterSnapshot GetSnapshot()
        {
            GetPrinter(_handle, 2, IntPtr.Zero, 0, out var needed);
            if (needed <= 0 || needed > MaxBufferBytes)
            {
                throw new InvalidOperationException(
                    $"GetPrinter failed for '{PrinterName}' (Win32 {Marshal.GetLastWin32Error()}).");
            }

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetPrinter(_handle, 2, buffer, needed, out _))
                {
                    throw new InvalidOperationException(
                        $"GetPrinter failed for '{PrinterName}' (Win32 {Marshal.GetLastWin32Error()}).");
                }

                var info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
                return new PrinterSnapshot(info.Status, info.Attributes, info.pPortName);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public bool TryGetJob(uint jobId, out ObservedJob job)
        {
            job = default;
            if (!GetJob(_handle, jobId, 1, IntPtr.Zero, 0, out var needed))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorInvalidParameter)
                {
                    return false;
                }

                if (error != ErrorInsufficientBuffer || needed <= 0)
                {
                    throw new InvalidOperationException(
                        $"GetJob failed for '{PrinterName}' job {jobId} (Win32 {error}).");
                }
            }
            else if (needed <= 0)
            {
                return false;
            }

            if (needed > MaxBufferBytes)
            {
                throw new InvalidOperationException(
                    $"GetJob failed for '{PrinterName}' job {jobId} (buffer {needed} bytes).");
            }

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetJob(_handle, jobId, 1, buffer, needed, out _))
                {
                    return false;
                }

                var info = Marshal.PtrToStructure<JobInfo1>(buffer);
                job = new ObservedJob(info.JobId, info.pDocument, info.Status);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public IReadOnlyList<ObservedJob> EnumJobs()
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (!NativeEnumJobs(_handle, 0, uint.MaxValue, 1, IntPtr.Zero, 0, out var needed, out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (needed <= 0)
                    {
                        // No jobs: the size probe fails with insufficient buffer and a zero length.
                        if (error is 0 or ErrorInsufficientBuffer)
                        {
                            return Array.Empty<ObservedJob>();
                        }

                        throw new InvalidOperationException(
                            $"EnumJobs failed for '{PrinterName}' (Win32 {error}).");
                    }
                }
                else if (needed <= 0)
                {
                    return Array.Empty<ObservedJob>();
                }

                if (needed > MaxBufferBytes)
                {
                    throw new InvalidOperationException(
                        $"EnumJobs failed for '{PrinterName}' (buffer {needed} bytes).");
                }

                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!NativeEnumJobs(_handle, 0, uint.MaxValue, 1, buffer, needed, out _, out var returned))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == ErrorInsufficientBuffer && attempt < 2)
                        {
                            continue;
                        }

                        throw new InvalidOperationException(
                            $"EnumJobs failed for '{PrinterName}' (Win32 {error}).");
                    }

                    var jobs = new List<ObservedJob>(returned);
                    var stride = Marshal.SizeOf<JobInfo1>();
                    for (var i = 0; i < returned; i++)
                    {
                        var info = Marshal.PtrToStructure<JobInfo1>(buffer + (i * stride));
                        jobs.Add(new ObservedJob(info.JobId, info.pDocument, info.Status));
                    }

                    return jobs;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            throw new InvalidOperationException($"EnumJobs failed for '{PrinterName}'.");
        }

        public bool TryDeleteJob(uint jobId)
            => NativeSpooler.TryDeleteJob(_handle, jobId);
    }

    public static bool TryDeleteJob(IntPtr printer, uint jobId)
    {
        if (printer == IntPtr.Zero || jobId == 0)
        {
            return false;
        }

        return SetJob(printer, jobId, 0, IntPtr.Zero, JobControlDelete);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterDefaults
    {
        public IntPtr pDatatype;
        public IntPtr pDevMode;
        public int DesiredAccess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PrinterInfo2
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct JobInfo1
    {
        public uint JobId;
        public string? pPrinterName;
        public string? pMachineName;
        public string? pUserName;
        public string? pDocument;
        public string? pDatatype;
        public string? pStatus;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint TotalPages;
        public uint PagesPrinted;
        public SystemTime Submitted;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GetPrinter(
        IntPtr hPrinter,
        uint level,
        IntPtr pPrinter,
        int cbBuf,
        out int pcbNeeded);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GetJob(
        IntPtr hPrinter,
        uint jobId,
        uint level,
        IntPtr pJob,
        int cbBuf,
        out int pcbNeeded);

    [DllImport("winspool.drv", EntryPoint = "EnumJobs", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool NativeEnumJobs(
        IntPtr hPrinter,
        uint firstJob,
        uint noJobs,
        uint level,
        IntPtr pJob,
        int cbBuf,
        out int pcbNeeded,
        out int pcReturned);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetJob(
        IntPtr hPrinter,
        uint jobId,
        uint level,
        IntPtr pJob,
        uint command);
}
