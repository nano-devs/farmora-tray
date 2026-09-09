using System.Diagnostics;
using FarmoraTray.Services;

namespace FarmoraTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly WebApplication _app;
    private readonly ConfigStore _configStore;
    private readonly ContextMenuStrip _menu;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private int _exitStarted;
    private bool _trayDisposed;

    public TrayApplicationContext(WebApplication app, ConfigStore configStore)
    {
        _app = app;
        _configStore = configStore;

        var listenUrl = $"http://127.0.0.1:{configStore.Current.Port}";

        _menu = new ContextMenuStrip();
        _menu.Items.Add(new ToolStripMenuItem($"Listening {listenUrl}") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Copy API key", null, (_, _) => CopyApiKey());
        _menu.Items.Add("Open config folder", null, (_, _) => OpenConfigFolder());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, OnExitClicked);

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

    private async void OnExitClicked(object? sender, EventArgs e)
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
