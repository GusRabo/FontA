namespace FontA.Core.Library;

/// <summary>Typed view over the library's settings table. Every setting has a safe default, so a missing value is never an error.</summary>
public sealed class AppSettings
{
    private readonly FontLibrary _lib;
    public AppSettings(FontLibrary lib) => _lib = lib;

    public const int MinUnusedDays = 1, MaxUnusedDays = 365, DefaultUnusedDays = 30;

    /// <summary>Closing the window keeps FontA running in the notification area instead of exiting.</summary>
    public bool HideToTray { get => Get("behavior.hideToTray", false); set => Set("behavior.hideToTray", value); }

    /// <summary>Start FontA when you sign in to Windows.</summary>
    public bool StartWithWindows { get => Get("behavior.startWithWindows", false); set => Set("behavior.startWithWindows", value); }

    /// <summary>When FontA is started by Windows at sign-in, keep the window hidden (it lives in the tray).</summary>
    public bool StartHidden { get => Get("behavior.startHidden", false); set => Set("behavior.startHidden", value); }

    /// <summary>On exit, deactivate the fonts you activated (fonts marked Always stay active).</summary>
    public bool DeactivateOnExit { get => Get("behavior.deactivateOnExit", true); set => Set("behavior.deactivateOnExit", value); }

    /// <summary>Re-read every font folder when FontA starts, picking up added, changed and deleted files.</summary>
    public bool RescanOnStartup { get => Get("folders.rescanOnStartup", true); set => Set("folders.rescanOnStartup", value); }

    /// <summary>Watch font folders while FontA runs and update the library when files change.</summary>
    public bool WatchFolders { get => Get("folders.watch", true); set => Set("folders.watch", value); }

    /// <summary>Check GitHub for new versions in the background and install them when FontA quits. Only applies to installed copies.</summary>
    public bool AutoUpdate { get => Get("updates.auto", true); set => Set("updates.auto", value); }

    /// <summary>How long Windows took per font the last times FontA switched many fonts on or off on this PC (milliseconds).</summary>
    public double ActivationMsPerFont
    {
        get => double.TryParse(_lib.GetSetting("activation.msPerFont"), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var v) && v is >= 1 and <= 10_000
            ? v : FontA.Core.Activation.ActivationPlan.DefaultMsPerFont;
        set => _lib.SetSetting("activation.msPerFont", Math.Clamp(value, 1, 10_000).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Use the light colour scheme instead of the default dark one.</summary>
    public bool LightTheme { get => Get("ui.lightTheme", false); set => Set("ui.lightTheme", value); }

    /// <summary>Preview text size in the font list.</summary>
    public int ListSize
    {
        get => int.TryParse(_lib.GetSetting("ui.listSize"), out var s) && s is >= 12 and <= 160 ? s : 36;
        set => _lib.SetSetting("ui.listSize", Math.Clamp(value, 12, 160).ToString());
    }

    public bool UnusedEnabled { get => Get("unused.enabled", false); set => Set("unused.enabled", value); }

    public int UnusedDays
    {
        get => int.TryParse(_lib.GetSetting("unused.days"), out var d) && d is >= MinUnusedDays and <= MaxUnusedDays ? d : DefaultUnusedDays;
        set => _lib.SetSetting("unused.days", Math.Clamp(value, MinUnusedDays, MaxUnusedDays).ToString());
    }

    private bool Get(string key, bool fallback) => _lib.GetSetting(key) switch { "1" => true, "0" => false, _ => fallback };
    private void Set(string key, bool value) => _lib.SetSetting(key, value ? "1" : "0");
}
