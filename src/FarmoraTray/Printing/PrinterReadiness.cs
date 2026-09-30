namespace FarmoraTray.Printing;

internal readonly record struct ObservedJob(uint Id, string? Document, uint Status);

internal readonly record struct JobPick(uint? JobId, bool LostTracked);

/// <summary>
/// Pure spooler flag decisions. No Win32 calls, so the rules can be checked without a printer.
/// </summary>
internal static class PrinterReadiness
{
    internal const uint AttributeWorkOffline = 0x00000400;

    internal const uint StatusPaused = 0x1;
    internal const uint StatusError = 0x2;
    internal const uint StatusPaperJam = 0x8;
    internal const uint StatusPaperOut = 0x10;
    internal const uint StatusPaperProblem = 0x40;
    internal const uint StatusOffline = 0x80;
    internal const uint StatusNotAvailable = 0x1000;
    internal const uint StatusUserIntervention = 0x100000;
    internal const uint StatusDoorOpen = 0x400000;

    internal const uint JobPaused = 0x1;
    internal const uint JobError = 0x2;
    internal const uint JobPrinting = 0x10;
    internal const uint JobOffline = 0x20;
    internal const uint JobPaperOut = 0x40;
    internal const uint JobPrinted = 0x80;
    internal const uint JobBlockedDevq = 0x200;
    internal const uint JobUserIntervention = 0x400;
    internal const uint JobComplete = 0x1000;

    internal enum JobKind
    {
        Pending,
        Printing,
        Succeeded,
        Failed
    }

    internal readonly record struct JobVerdict(JobKind Kind, string? Reason);

    public static bool TryGetPrinterNotReadyReason(uint status, uint attributes, out string reason)
    {
        var parts = new List<string>(4);
        if ((attributes & AttributeWorkOffline) != 0)
        {
            parts.Add("printer is set to work offline");
        }

        if ((status & StatusPaused) != 0)
        {
            parts.Add("printer is paused");
        }

        if ((status & StatusError) != 0)
        {
            parts.Add("printer reports an error");
        }

        if ((status & StatusPaperJam) != 0)
        {
            parts.Add("paper is jammed");
        }

        if ((status & StatusPaperOut) != 0)
        {
            parts.Add("printer is out of paper");
        }

        if ((status & StatusPaperProblem) != 0)
        {
            parts.Add("printer has a paper problem");
        }

        if ((status & StatusOffline) != 0)
        {
            parts.Add("printer is offline");
        }

        if ((status & StatusNotAvailable) != 0)
        {
            parts.Add("printer is not available");
        }

        if ((status & StatusUserIntervention) != 0)
        {
            parts.Add("printer needs user intervention");
        }

        if ((status & StatusDoorOpen) != 0)
        {
            parts.Add("printer door is open");
        }

        if (parts.Count == 0)
        {
            reason = "";
            return false;
        }

        reason = string.Join("; ", parts);
        return true;
    }

    public static JobVerdict EvaluateJob(uint jobStatus, uint printerStatus, uint printerAttributes)
    {
        if ((jobStatus & (JobPrinted | JobComplete)) != 0)
        {
            return new JobVerdict(JobKind.Succeeded, null);
        }

        if (TryGetJobFailureReason(jobStatus, printerStatus, printerAttributes, out var reason))
        {
            return new JobVerdict(JobKind.Failed, reason);
        }

        if ((jobStatus & JobPrinting) != 0)
        {
            return new JobVerdict(JobKind.Printing, null);
        }

        return new JobVerdict(JobKind.Pending, null);
    }

    /// <summary>
    /// Prefer a new job whose document name contains the temp PDF filename.
    /// If none do and exactly one new job exists, that job is ours.
    /// A previously tracked job that is gone is reported as <see cref="JobPick.LostTracked"/>.
    /// </summary>
    public static JobPick PickJob(
        IReadOnlyList<ObservedJob> jobs,
        IReadOnlySet<uint> existingIds,
        string documentToken,
        uint? trackedJobId)
    {
        ObservedJob? named = null;
        var freshCount = 0;
        ObservedJob onlyFresh = default;
        var trackedAlive = false;

        foreach (var job in jobs)
        {
            if (trackedJobId == job.Id)
            {
                trackedAlive = true;
            }

            if (existingIds.Contains(job.Id))
            {
                continue;
            }

            freshCount++;
            onlyFresh = job;
            if (named == null && DocumentMatches(job.Document, documentToken))
            {
                named = job;
            }
        }

        if (named != null)
        {
            return new JobPick(named.Value.Id, false);
        }

        if (trackedJobId is uint)
        {
            return trackedAlive
                ? new JobPick(trackedJobId, false)
                : new JobPick(null, true);
        }

        if (freshCount == 1)
        {
            return new JobPick(onlyFresh.Id, false);
        }

        return new JobPick(null, false);
    }

    public static int CountNewJobs(IReadOnlyList<ObservedJob> jobs, IReadOnlySet<uint> existingIds)
    {
        var count = 0;
        foreach (var job in jobs)
        {
            if (!existingIds.Contains(job.Id))
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryGetJobFailureReason(
        uint jobStatus,
        uint printerStatus,
        uint printerAttributes,
        out string reason)
    {
        var parts = new List<string>(4);
        if ((jobStatus & JobOffline) != 0)
        {
            parts.Add("print job is offline");
        }

        if ((jobStatus & JobError) != 0)
        {
            parts.Add("print job failed");
        }

        if ((jobStatus & JobPaperOut) != 0)
        {
            parts.Add("print job is out of paper");
        }

        if ((jobStatus & JobBlockedDevq) != 0)
        {
            parts.Add("print job is blocked");
        }

        if ((jobStatus & JobUserIntervention) != 0)
        {
            parts.Add("print job needs user intervention");
        }

        if ((jobStatus & JobPaused) != 0 && IsOfflineOrWorkOffline(printerStatus, printerAttributes))
        {
            parts.Add("print job is paused while the printer is offline");
        }

        if (parts.Count == 0)
        {
            reason = "";
            return false;
        }

        reason = string.Join("; ", parts);
        return true;
    }

    private static bool IsOfflineOrWorkOffline(uint status, uint attributes)
        => (attributes & AttributeWorkOffline) != 0 || (status & StatusOffline) != 0;

    private static bool DocumentMatches(string? document, string token)
        => !string.IsNullOrEmpty(token)
           && !string.IsNullOrEmpty(document)
           && document.Contains(token, StringComparison.OrdinalIgnoreCase);
}
