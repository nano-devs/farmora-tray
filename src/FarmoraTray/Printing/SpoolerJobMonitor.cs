using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using FarmoraTray.Services;
using Microsoft.Win32;

namespace FarmoraTray.Printing;

/// <summary>
/// Preflight a printer, then wait until the spooler finishes the job.
/// A job that only sits in the queue is deleted and reported as not ready.
/// </summary>
internal static class SpoolerJobMonitor
{
    private const string StandardTcpPortsKey =
        @"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports\";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan QueuedTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PrintingTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan AbsenceGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(60);
    // Backstop above the slowest bounded path: the handler submits just before 60s,
    // the job then sits queued for almost 15s, then prints for 120s.
    private static readonly TimeSpan AbsoluteTimeout = TimeSpan.FromSeconds(210);
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(2);

    public static void EnsureReady(string printerName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Farmora Tray printing requires Windows.");
        }

        using var session = NativeSpooler.Open(printerName);
        var snapshot = session.GetSnapshot();
        if (PrinterReadiness.TryGetPrinterNotReadyReason(snapshot.Status, snapshot.Attributes, out var reason))
        {
            throw new PrinterNotReadyException(printerName, reason);
        }

        ProbeDeviceConnection(printerName, snapshot.PortName);
        ProbeStandardTcpPort(printerName, snapshot.PortName);
    }

    public static uint[] SnapshotJobIds(string printerName)
    {
        using var session = NativeSpooler.Open(printerName);
        var jobs = session.EnumJobs();
        var ids = new uint[jobs.Count];
        for (var i = 0; i < jobs.Count; i++)
        {
            ids[i] = jobs[i].Id;
        }

        return ids;
    }

    public static void WaitForRawJob(string printerName, uint jobId)
    {
        using var session = NativeSpooler.Open(printerName);
        var started = Stopwatch.StartNew();
        var seenPrinting = false;
        TimeSpan? pendingSince = null;
        TimeSpan? printingSince = null;

        while (true)
        {
            FailIfAbsoluteTimeout(session, printerName, jobId, started.Elapsed);

            var snapshot = session.GetSnapshot();
            if (!session.TryGetJob(jobId, out var job))
            {
                // StartDocPrinter already returned this id, so a missing job is terminal.
                // A stuck queue keeps the job visible; a fast printer may remove it first.
                ResolveDisappearedJob(session, jobId, printerName, snapshot, seenPrinting);
                return;
            }

            if (ApplyVerdict(
                    session,
                    printerName,
                    job,
                    snapshot,
                    started.Elapsed,
                    ref seenPrinting,
                    ref pendingSince,
                    ref printingSince))
            {
                return;
            }

            Thread.Sleep(PollInterval);
        }
    }

    public static void WaitForShellJob(
        string printerName,
        IReadOnlyCollection<uint> existingJobIds,
        string documentFileName,
        Process handler)
    {
        using var session = NativeSpooler.Open(printerName);
        var existing = new HashSet<uint>(existingJobIds);
        var started = Stopwatch.StartNew();
        uint? tracked = null;
        var seenPrinting = false;
        TimeSpan? pendingSince = null;
        TimeSpan? printingSince = null;
        TimeSpan? handlerExitedAt = null;
        TimeSpan? ambiguousSince = null;

        while (true)
        {
            if (started.Elapsed >= AbsoluteTimeout)
            {
                if (tracked is uint timedOutId)
                {
                    DeleteAndFail(session, timedOutId, printerName, "timed out waiting for the print job");
                }

                throw new PrinterNotReadyException(printerName, "timed out waiting for the print job");
            }

            if (handlerExitedAt == null && HandlerHasExited(handler))
            {
                handlerExitedAt = started.Elapsed;
            }

            var snapshot = session.GetSnapshot();
            var jobs = session.EnumJobs();
            var pick = PrinterReadiness.PickJob(jobs, existing, documentFileName, tracked);

            if (pick.LostTracked && tracked is uint lostId)
            {
                ResolveDisappearedJob(session, lostId, printerName, snapshot, seenPrinting);
                return;
            }

            if (pick.JobId is uint jobId)
            {
                ambiguousSince = null;
                if (tracked != jobId)
                {
                    tracked = jobId;
                    seenPrinting = false;
                    pendingSince = null;
                    printingSince = null;
                }

                if (!TryFindJob(jobs, jobId, out var job))
                {
                    ResolveDisappearedJob(session, jobId, printerName, snapshot, seenPrinting);
                    return;
                }

                if (ApplyVerdict(
                        session,
                        printerName,
                        job,
                        snapshot,
                        started.Elapsed,
                        ref seenPrinting,
                        ref pendingSince,
                        ref printingSince))
                {
                    return;
                }
            }
            else
            {
                var freshCount = PrinterReadiness.CountNewJobs(jobs, existing);
                if (freshCount == 0)
                {
                    ambiguousSince = null;
                    if (handlerExitedAt == null && started.Elapsed >= HandlerTimeout)
                    {
                        throw new PrinterNotReadyException(
                            printerName,
                            "the PDF print handler did not submit a job");
                    }

                    if (handlerExitedAt is TimeSpan exitedAt && started.Elapsed - exitedAt >= AbsenceGrace)
                    {
                        ConcludeMissingJob(printerName, snapshot);
                        return;
                    }
                }
                else
                {
                    ambiguousSince ??= started.Elapsed;
                    var graceElapsed = handlerExitedAt is TimeSpan exited
                        && started.Elapsed - exited >= AbsenceGrace;
                    if (started.Elapsed - ambiguousSince.Value >= QueuedTimeout || graceElapsed)
                    {
                        throw new PrinterNotReadyException(
                            printerName,
                            "a print job stayed queued but could not be matched to this document");
                    }
                }
            }

            Thread.Sleep(PollInterval);
        }
    }

    /// <returns>true when the job reached a terminal success state.</returns>
    private static bool ApplyVerdict(
        NativeSpooler.PrinterSession session,
        string printerName,
        ObservedJob job,
        PrinterSnapshot snapshot,
        TimeSpan elapsed,
        ref bool seenPrinting,
        ref TimeSpan? pendingSince,
        ref TimeSpan? printingSince)
    {
        var verdict = PrinterReadiness.EvaluateJob(job.Status, snapshot.Status, snapshot.Attributes);
        switch (verdict.Kind)
        {
            case PrinterReadiness.JobKind.Succeeded:
                return true;
            case PrinterReadiness.JobKind.Failed:
                DeleteAndFail(session, job.Id, printerName, verdict.Reason ?? "print job failed");
                return false;
            case PrinterReadiness.JobKind.Printing:
                seenPrinting = true;
                pendingSince = null;
                printingSince ??= elapsed;
                ThrowIfStuck(session, printerName, job.Id, printing: true, printingSince.Value, elapsed, snapshot);
                break;
            default:
                if (seenPrinting)
                {
                    printingSince ??= elapsed;
                    ThrowIfStuck(session, printerName, job.Id, printing: true, printingSince.Value, elapsed, snapshot);
                }
                else
                {
                    pendingSince ??= elapsed;
                    ThrowIfStuck(session, printerName, job.Id, printing: false, pendingSince.Value, elapsed, snapshot);
                }

                break;
        }

        return false;
    }

    private static void ThrowIfStuck(
        NativeSpooler.PrinterSession session,
        string printerName,
        uint jobId,
        bool printing,
        TimeSpan since,
        TimeSpan now,
        PrinterSnapshot snapshot)
    {
        var limit = printing ? PrintingTimeout : QueuedTimeout;
        if (now - since < limit)
        {
            return;
        }

        var reason = printing
            ? "print job timed out while printing"
            : "print job stayed queued";
        if (PrinterReadiness.TryGetPrinterNotReadyReason(snapshot.Status, snapshot.Attributes, out var why))
        {
            reason = $"{reason} ({why})";
        }

        DeleteAndFail(session, jobId, printerName, reason);
    }

    private static void ResolveDisappearedJob(
        NativeSpooler.PrinterSession session,
        uint jobId,
        string printerName,
        PrinterSnapshot snapshot,
        bool seenPrinting)
    {
        if (!PrinterReadiness.TryGetPrinterNotReadyReason(snapshot.Status, snapshot.Attributes, out var why))
        {
            // The job left the queue and the printer is still ready, including a job that
            // finished between polls before JOB_STATUS_PRINTING was observed.
            return;
        }

        var reason = seenPrinting
            ? $"print job stopped while printing ({why})"
            : $"print job disappeared while the printer is not ready ({why})";
        DeleteAndFail(session, jobId, printerName, reason);
    }

    private static void ConcludeMissingJob(string printerName, PrinterSnapshot snapshot)
    {
        if (PrinterReadiness.TryGetPrinterNotReadyReason(snapshot.Status, snapshot.Attributes, out var why))
        {
            throw new PrinterNotReadyException(printerName, $"no print job appeared ({why})");
        }
    }

    private static void FailIfAbsoluteTimeout(
        NativeSpooler.PrinterSession session,
        string printerName,
        uint jobId,
        TimeSpan elapsed)
    {
        if (elapsed >= AbsoluteTimeout)
        {
            DeleteAndFail(session, jobId, printerName, "timed out waiting for the print job");
        }
    }

    private static void DeleteAndFail(
        NativeSpooler.PrinterSession session,
        uint jobId,
        string printerName,
        string reason)
    {
        try
        {
            session.TryDeleteJob(jobId);
        }
        catch
        {
            // A failed delete must not replace the original not-ready error.
        }

        throw new PrinterNotReadyException(printerName, reason);
    }

    private static bool TryFindJob(IReadOnlyList<ObservedJob> jobs, uint jobId, out ObservedJob job)
    {
        foreach (var candidate in jobs)
        {
            if (candidate.Id == jobId)
            {
                job = candidate;
                return true;
            }
        }

        job = default;
        return false;
    }

    private static bool HandlerHasExited(Process handler)
    {
        try
        {
            return handler.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void ProbeDeviceConnection(string printerName, string? portName)
    {
        string reason;
        try
        {
            if (PrinterReadiness.IsUsbMonitorPort(portName)
                && NativeDeviceConnection.TryListUsbPrintPorts() is { } connectedPorts
                && PrinterReadiness.TryGetUsbPortNotConnectedReason(portName, connectedPorts, out reason))
            {
                throw new PrinterNotReadyException(printerName, reason);
            }

            if (PrinterReadiness.MayBeDirectlyAttachedPort(portName)
                && PrinterReadiness.TryGetDeviceNotConnectedReason(
                    printerName,
                    NativeDeviceConnection.ListDevices(),
                    out reason))
            {
                throw new PrinterNotReadyException(printerName, reason);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or SecurityException
                                       or ExternalException
                                       or DllNotFoundException
                                       or EntryPointNotFoundException)
        {
        }
    }

    private static void ProbeStandardTcpPort(string printerName, string? portName)
    {
        if (!IsSafePortName(portName))
        {
            return;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(StandardTcpPortsKey + portName);
            if (key == null)
            {
                return;
            }

            var host = key.GetValue("HostName") as string;
            if (string.IsNullOrWhiteSpace(host))
            {
                host = key.GetValue("IPAddress") as string;
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                return;
            }

            host = host.Trim();
            if (!TryReadPort(key.GetValue("PortNumber"), out var port))
            {
                return;
            }

            ConnectOrThrow(printerName, host, port);
        }
        catch (PrinterNotReadyException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            // Missing rights or an unreadable key. Skip the probe rather than fail a USB/WSD printer.
        }
    }

    private static bool IsSafePortName(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName) || portName.Length > 200)
        {
            return false;
        }

        foreach (var ch in portName)
        {
            if (ch is '\\' or '/' or '\0')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadPort(object? value, out int port)
    {
        switch (value)
        {
            case null:
                port = 9100;
                return true;
            case int number when number is > 0 and <= 65535:
                port = number;
                return true;
            case long number when number is > 0 and <= 65535:
                port = (int)number;
                return true;
            case string text when int.TryParse(text, out var parsed) && parsed is > 0 and <= 65535:
                port = parsed;
                return true;
            default:
                port = 0;
                return false;
        }
    }

    private static void ConnectOrThrow(string printerName, string host, int port)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TcpTimeout);
        try
        {
            client.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            throw new PrinterNotReadyException(
                printerName,
                $"cannot reach printer at {FormatEndpoint(host, port)}");
        }
    }

    private static string FormatEndpoint(string host, int port)
        => host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
}
