using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace FarmoraTray.Services;

public enum UpdateCheckResult
{
    /// <summary>Running from <c>dotnet run</c> or an old zip install, so Velopack cannot update it.</summary>
    NotInstalled,
    UpToDate,
    Restarting,
    Busy,
    Failed
}

/// <summary>
/// Checks GitHub Releases for a newer Farmora Tray, downloads it in the background and
/// restarts into it once no print is running.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string RepoUrl = "https://github.com/nano-devs/farmora-tray";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);

    // Automatic restarts wait for a quiet till so a cashier is not mid-sale.
    private static readonly TimeSpan AutomaticQuietPeriod = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(30);

    private readonly UpdateManager _manager;
    private readonly PrintActivity _activity;
    private readonly ILogger<UpdateService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();

    public UpdateService(PrintActivity activity, ILogger<UpdateService> logger)
    {
        _activity = activity;
        _logger = logger;
        _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    }

    /// <summary>
    /// Raised once an update is staged and the updater is waiting for this process to exit.
    /// The handler must shut the app down within about a minute.
    /// </summary>
    public event Action<string>? RestartRequested;

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    public void Start()
    {
        if (!IsInstalled)
        {
            _logger.LogInformation("Automatic updates are off: Farmora Tray was not installed with Setup.exe.");
            return;
        }

        _ = Task.Run(() => RunLoopAsync(_stopping.Token));
    }

    /// <summary>Check now (tray menu). Waits for in-flight prints but not for a quiet period.</summary>
    public Task<UpdateCheckResult> CheckNowAsync() =>
        CheckDownloadAndApplyAsync(TimeSpan.Zero, _stopping.Token);

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstCheckDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                var result = await CheckDownloadAndApplyAsync(AutomaticQuietPeriod, ct);
                if (result == UpdateCheckResult.Restarting)
                {
                    return;
                }

                await Task.Delay(result == UpdateCheckResult.Failed ? RetryAfterFailure : CheckInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<UpdateCheckResult> CheckDownloadAndApplyAsync(TimeSpan quietPeriod, CancellationToken ct)
    {
        if (!IsInstalled)
        {
            return UpdateCheckResult.NotInstalled;
        }

        if (!await _gate.WaitAsync(0, ct))
        {
            return UpdateCheckResult.Busy;
        }

        try
        {
            // A previous run may already have downloaded an update without applying it.
            var target = _manager.UpdatePendingRestart;
            if (target is null)
            {
                var info = await _manager.CheckForUpdatesAsync();
                if (info is null)
                {
                    return UpdateCheckResult.UpToDate;
                }

                _logger.LogInformation(
                    "Downloading Farmora Tray {Version} (current {Current}).",
                    info.TargetFullRelease.Version,
                    CurrentVersion);
                await _manager.DownloadUpdatesAsync(info, cancelToken: ct);
                target = info.TargetFullRelease;
            }

            while (!_activity.IsIdle(quietPeriod))
            {
                await Task.Delay(IdlePollInterval, ct);
            }

            var version = target.Version.ToString();
            _logger.LogInformation("Restarting to apply Farmora Tray {Version}.", version);
            _manager.WaitExitThenApplyUpdates(target, silent: true, restart: true, restartArgs: []);
            RestartRequested?.Invoke(version);
            return UpdateCheckResult.Restarting;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Farmora Tray update check failed.");
            return UpdateCheckResult.Failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
