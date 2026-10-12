using FontA.Core.Activation;

namespace FontA.Core.Library;

public sealed record DeactivatedFont(string Path, bool WasAlwaysActive);

/// <summary>
/// Deactivates fonts that haven't been used for a while. "Used" means the person touched the font in FontA: previewed
/// it, activated or deactivated it, or changed its always-active setting. Windows doesn't record which apps use
/// which fonts, so use inside other apps can't be seen; that is why the feature is opt-in and every run can be undone.
/// Installed fonts are managed by Windows and are never touched.
/// </summary>
public static class UnusedFontPolicy
{
    /// <summary>
    /// Deactivates stale fonts that are currently active (this session) or marked always-active, and clears the
    /// always-active mark so they don't come back at the next launch. Fonts FontA has no usage record for yet start
    /// their clock now, so turning the feature on never deactivates anything immediately.
    /// </summary>
    public static IReadOnlyList<DeactivatedFont> Apply(FontLibrary library, FontActivator activator, TimeSpan maxAge, DateTimeOffset now)
    {
        library.InitializeUsage(now);
        var stale = library.StalePaths(now - maxAge);
        if (stale.Count == 0) return Array.Empty<DeactivatedFont>();

        var always = new HashSet<string>(library.AlwaysActivePaths(), StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(activator.Active, StringComparer.OrdinalIgnoreCase);
        var done = new List<DeactivatedFont>();
        foreach (var path in stale)
        {
            bool wasAlways = always.Contains(path);
            if (!wasAlways && !active.Contains(path)) continue;
            activator.Deactivate(path);
            if (wasAlways) library.SetAlwaysActiveForPath(path, false);
            done.Add(new DeactivatedFont(path, wasAlways));
        }
        return done;
    }

    /// <summary>Reverses <see cref="Apply"/>: reactivates the fonts, restores always-active, and restarts their clocks.</summary>
    public static void Undo(FontLibrary library, FontActivator activator, IEnumerable<DeactivatedFont> fonts, DateTimeOffset now)
    {
        foreach (var f in fonts)
        {
            activator.Activate(f.Path);
            if (f.WasAlwaysActive) library.SetAlwaysActiveForPath(f.Path, true);
            library.TouchUsedPath(f.Path, now);
        }
    }
}
