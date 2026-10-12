namespace FontA.Core.Activation;

/// <summary>
/// Activates fonts for the current session only (FR_PRIVATE would hide them from other apps,
/// so we use no flag: installed until logoff/removal, no admin rights, no files copied).
/// </summary>
public sealed class FontActivator
{
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Active => _active;

    public bool Activate(string path)
    {
        if (!OperatingSystem.IsWindows() || _active.Contains(path)) return _active.Contains(path);
        if (FontNative.AddFontResourceEx(path, 0, IntPtr.Zero) == 0) return false;
        _active.Add(path);
        Broadcast();
        return true;
    }

    /// <summary>Activates many fonts with a single font-change broadcast. Returns how many are active afterwards.</summary>
    public int ActivateMany(IEnumerable<string> paths)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        bool changed = false;
        foreach (var path in paths)
        {
            if (_active.Contains(path) || !File.Exists(path)) continue;
            if (FontNative.AddFontResourceEx(path, 0, IntPtr.Zero) != 0) { _active.Add(path); changed = true; }
        }
        if (changed) Broadcast();
        return _active.Count;
    }

    /// <summary>Deactivates everything this activator turned on except the given paths (used on exit for always-active fonts).</summary>
    public void DeactivateExcept(IEnumerable<string> keep)
    {
        var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var p in _active.Where(p => !keepSet.Contains(p)).ToList())
        {
            _active.Remove(p);
            FontNative.RemoveFontResourceEx(p, 0, IntPtr.Zero);
            changed = true;
        }
        if (changed) Broadcast();
    }

    public bool Deactivate(string path)
    {
        if (!OperatingSystem.IsWindows() || !_active.Remove(path)) return false;
        FontNative.RemoveFontResourceEx(path, 0, IntPtr.Zero);
        Broadcast();
        return true;
    }

    public void DeactivateAll()
    {
        foreach (var p in _active.ToList()) Deactivate(p);
    }

    private static void Broadcast() => FontNative.BroadcastFontChange();
}
