using Microsoft.Win32;

namespace FarmoraTray.Services;

/// <summary>
/// Starts Farmora Tray at the current user's logon through the per-user
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> key, the same place
/// Task Manager → Startup apps and Settings → Apps → Startup read from.
/// No admin rights needed. Only used for Setup.exe installs: the Velopack
/// <c>current\FarmoraTray.exe</c> path does not change between updates.
/// </summary>
public static class AutoStart
{
    /// <summary>Passed on the command line by the Run entry so a duplicate logon start exits quietly.</summary>
    public const string Argument = "--autostart";

    private const string ValueName = "FarmoraTray";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Task Manager and Settings record "Disabled" here without touching the Run value.
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    // Set when someone turns auto-start off from the tray menu, so updates do not turn it back on.
    private const string PrefsKeyPath = @"Software\FarmoraTray";
    private const string OptOutValueName = "AutoStartDisabled";

    /// <summary>True when the Run entry exists and Task Manager has not disabled it.</summary>
    public static bool IsEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        if (run?.GetValue(ValueName) is not string)
        {
            return false;
        }

        // First byte 0x02 (or an even value) = enabled, 0x03 = disabled by the user.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
        return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
    }

    /// <summary>Called from the install hook: turn auto-start on for a fresh install.</summary>
    public static void OnInstalled(string exePath)
    {
        ClearOptOut();
        Register(exePath);
    }

    /// <summary>
    /// Called from the update hook: keeps the entry pointing at this exe and turns it on for
    /// PCs installed before auto-start existed, unless someone switched it off in the tray menu.
    /// </summary>
    public static void OnUpdated(string exePath)
    {
        if (!IsOptedOut())
        {
            Register(exePath);
        }
    }

    /// <summary>Called from the uninstall hook.</summary>
    public static void OnUninstalling()
    {
        Unregister();
        ClearOptOut();
    }

    /// <summary>Tray menu toggle.</summary>
    public static void SetEnabled(bool enabled, string exePath)
    {
        if (enabled)
        {
            ClearOptOut();
            Register(exePath);

            // Clear a "Disabled" mark left by Task Manager so the entry really runs.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else
        {
            Unregister();
            using var prefs = Registry.CurrentUser.CreateSubKey(PrefsKeyPath);
            prefs.SetValue(OptOutValueName, 1, RegistryValueKind.DWord);
        }
    }

    private static void Register(string exePath)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        run.SetValue(ValueName, $"\"{exePath}\" {Argument}", RegistryValueKind.String);
    }

    private static void Unregister()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
        {
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static bool IsOptedOut()
    {
        using var prefs = Registry.CurrentUser.OpenSubKey(PrefsKeyPath);
        return prefs?.GetValue(OptOutValueName) is int value && value != 0;
    }

    private static void ClearOptOut()
    {
        using var prefs = Registry.CurrentUser.OpenSubKey(PrefsKeyPath, writable: true);
        prefs?.DeleteValue(OptOutValueName, throwOnMissingValue: false);
    }
}
