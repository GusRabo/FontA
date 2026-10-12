using System.Runtime.Versioning;
using Microsoft.Win32;

namespace FontA.Core.Activation;

/// <summary>
/// Starts FontA when the current user signs in, via the per-user Run key (no admin rights, nothing outside your account).
/// The command carries <see cref="AutoStartArgument"/> so the app can tell a Windows start from you opening it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupRegistration
{
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string AutoStartArgument = "--autostart";

    private readonly string _runKey;
    private readonly string _valueName;

    public StartupRegistration(string? runKey = null, string valueName = "FontA")
    {
        _runKey = runKey ?? DefaultRunKey;
        _valueName = valueName;
    }

    public static string CommandFor(string exePath) => $"\"{exePath}\" {AutoStartArgument}";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKey);
            return key?.GetValue(_valueName) is string;
        }
    }

    /// <summary>The registered command, or null when not registered.</summary>
    public string? Command
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKey);
            return key?.GetValue(_valueName) as string;
        }
    }

    public void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKey, writable: true);
        key.SetValue(_valueName, CommandFor(exePath), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKey, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Points an existing registration at the current executable. Run on every start so that moving or updating the
    /// app to a new folder doesn't leave Windows launching a path that no longer exists. Does nothing if not registered.
    /// </summary>
    public bool RefreshPath(string exePath)
    {
        if (Command is not { } current || current == CommandFor(exePath)) return false;
        Enable(exePath);
        return true;
    }
}
