using System.Diagnostics;
using FarmoraTray.Services;

namespace FarmoraTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly WebApplication _app;
    private readonly ConfigStore _configStore;
    private readonly UpdateService _updates;
    private readonly SynchronizationContext _uiContext;
    private readonly ToolStripMenuItem _checkUpdatesItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ContextMenuStrip _menu;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private int _exitStarted;
    private bool _trayDisposed;

    public TrayApplicationContext(WebApplication app, ConfigStore configStore, UpdateService updates)
    {
        _app = app;
        _configStore = configStore;
        _updates = updates;

        var listenUrl = $"http://127.0.0.1:{configStore.Current.Port}";

        _menu = new ContextMenuStrip();
        _menu.Items.Add(new ToolStripMenuItem($"Version {updates.CurrentVersion}") { Enabled = false });
        _menu.Items.Add(new ToolStripMenuItem($"Listening {listenUrl}") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Copy API key", null, (_, _) => CopyApiKey());
        _menu.Items.Add("Open config folder", null, (_, _) => OpenConfigFolder());
        _checkUpdatesItem = new ToolStripMenuItem("Check for updates", null, OnCheckUpdatesClicked)
        {
            Enabled = updates.IsInstalled
        };
        _menu.Items.Add(_checkUpdatesItem);
        // Only for Setup.exe installs: a dotnet run / zip path would go stale.
        _autoStartItem = new ToolStripMenuItem("Start with Windows", null, OnAutoStartClicked)
        {
            Enabled = updates.IsInstalled
        };
        _menu.Items.Add(_autoStartItem);
        _menu.Opening += (_, _) => RefreshAutoStartItem();
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, OnExitClicked);

        // Creating the menu installs the WinForms synchronization context for this thread.
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _updates.RestartRequested += OnRestartRequested;

        _icon = LoadIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Visible = true,
            Text = $"Farmora Tray ({listenUrl})",
            ContextMenuStrip = _menu
        };

        if (configStore.ApiKeyWasGenerated)
        {
            _notifyIcon.ShowBalloonTip(
                15000,
                "Farmora Tray",
                "An API key was created for this PC. Right-click the tray icon and choose Copy API key.",
                ToolTipIcon.Info);
        }
    }

    private void CopyApiKey()
    {
        var key = _configStore.Current.ApiKey;
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        Clipboard.SetText(key);
        _notifyIcon.ShowBalloonTip(3000, "Farmora Tray", "API key copied.", ToolTipIcon.Info);
    }

    private void OpenConfigFolder()
    {
        var folder = Path.GetDirectoryName(_configStore.ConfigPath);
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }

    private void RefreshAutoStartItem()
    {
        try
        {
            _autoStartItem.Checked = AutoStart.IsEnabled();
        }
        catch
        {
            _autoStartItem.Checked = false;
        }
    }

    private void OnAutoStartClicked(object? sender, EventArgs e)
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var enable = !_autoStartItem.Checked;
        try
        {
            AutoStart.SetEnabled(enable, path);
            _autoStartItem.Checked = enable;
            _notifyIcon.ShowBalloonTip(
                3000,
                "Farmora Tray",
                enable ? "Farmora Tray will start when you sign in to Windows." : "Farmora Tray will no longer start with Windows.",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not change the startup setting.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Farmora Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async void OnCheckUpdatesClicked(object? sender, EventArgs e)
    {
        _checkUpdatesItem.Enabled = false;
        try
        {
            var result = await _updates.CheckNowAsync();
            var message = result switch
            {
                UpdateCheckResult.UpToDate => $"Farmora Tray {_updates.CurrentVersion} is up to date.",
                UpdateCheckResult.Busy => "An update check is already running.",
                UpdateCheckResult.Failed => "Could not check for updates. Check the internet connection and try again.",
                _ => null
            };

            if (message is not null && !_trayDisposed)
            {
                _notifyIcon.ShowBalloonTip(5000, "Farmora Tray", message, ToolTipIcon.Info);
            }
        }
        finally
        {
            if (!_trayDisposed)
            {
                _checkUpdatesItem.Enabled = true;
            }
        }
    }

    // Raised from a background thread once the updater is waiting for this process to exit.
    private void OnRestartRequested(string version)
    {
        _uiContext.Post(_ => ExitApp(), null);
    }

    private void OnExitClicked(object? sender, EventArgs e) => ExitApp();

    private async void ExitApp()
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0)
        {
            return;
        }

        HideTray();

        try
        {
            await _app.StopAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Farmora Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _updates.RestartRequested -= OnRestartRequested;
            HideTray();
        }

        base.Dispose(disposing);
    }

    private void HideTray()
    {
        if (_trayDisposed)
        {
            return;
        }

        _trayDisposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private static Icon LoadIcon()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path))
        {
            var extracted = Icon.ExtractAssociatedIcon(path);
            if (extracted is not null)
            {
                return extracted;
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
