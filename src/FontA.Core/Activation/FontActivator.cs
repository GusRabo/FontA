namespace FontA.Core.Activation;

/// <summary>
/// Activates fonts for the current session only (FR_PRIVATE would hide them from other apps,
/// so we use no flag: installed until logoff/removal, no admin rights, no files copied).
/// Safe to call from several threads: activating a whole folder runs in the background while the UI keeps asking
/// which fonts are active.
/// </summary>
public sealed class FontActivator
{
    private readonly object _gate = new();
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (on whichever thread made the change) after fonts were switched on or off. Used to keep the journal current.</summary>
    public event Action? Changed;

    /// <summary>A snapshot of the activated paths (a copy, so it stays valid while others change the set).</summary>
    public IReadOnlyCollection<string> Active
    {
        get { lock (_gate) return new HashSet<string>(_active, StringComparer.OrdinalIgnoreCase); }
    }

    public bool IsActive(string path)
    {
        lock (_gate) return _active.Contains(path);
    }

    /// <summary>
    /// Takes over fonts an earlier run switched on and Windows still has active, without switching them again, so they
    /// show as active and can be switched off. Returns how many were adopted (files that no longer exist are skipped).
    /// </summary>
    public int Adopt(IEnumerable<string> paths)
    {
        int adopted = 0;
        lock (_gate)
            foreach (var path in paths)
                if (File.Exists(path) && _active.Add(path)) adopted++;
        if (adopted > 0) Changed?.Invoke();
        return adopted;
    }

    public bool Activate(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        lock (_gate)
        {
            if (_active.Contains(path)) return true;
            if (FontNative.AddFontResourceEx(path, 0, IntPtr.Zero) == 0) return false;
            _active.Add(path);
        }
        Broadcast();
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Activates many fonts with a single font-change broadcast. Reports (done, total) as it goes and stops early when
    /// cancelled. Returns how many are active afterwards.
    /// </summary>
    public int ActivateMany(IEnumerable<string> paths, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var list = paths as IReadOnlyList<string> ?? paths.ToList();
        bool changed = false;
        int done = 0;
        foreach (var path in list)
        {
            if (ct.IsCancellationRequested) break;
            lock (_gate)
            {
                if (!_active.Contains(path) && File.Exists(path) && FontNative.AddFontResourceEx(path, 0, IntPtr.Zero) != 0)
                {
                    _active.Add(path);
                    changed = true;
                }
            }
            done++;
            if (progress != null && (done % 25 == 0 || done == list.Count)) progress.Report((done, list.Count));
        }
        if (changed) { Broadcast(); Changed?.Invoke(); }
        lock (_gate) return _active.Count;
    }

    /// <summary>Deactivates many fonts with a single font-change broadcast. Returns how many were deactivated.</summary>
    public int DeactivateMany(IEnumerable<string> paths, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var list = paths as IReadOnlyList<string> ?? paths.ToList();
        int removed = 0, done = 0;
        foreach (var path in list)
        {
            if (ct.IsCancellationRequested) break;
            lock (_gate)
            {
                if (_active.Remove(path))
                {
                    FontNative.RemoveFontResourceEx(path, 0, IntPtr.Zero);
                    removed++;
                }
            }
            done++;
            if (progress != null && (done % 25 == 0 || done == list.Count)) progress.Report((done, list.Count));
        }
        if (removed > 0) { Broadcast(); Changed?.Invoke(); }
        return removed;
    }

    /// <summary>Deactivates everything this activator turned on except the given paths (used on exit for always-active fonts).</summary>
    public void DeactivateExcept(IEnumerable<string> keep)
    {
        var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        List<string> drop;
        lock (_gate) drop = _active.Where(p => !keepSet.Contains(p)).ToList();
        DeactivateMany(drop);
    }

    public bool Deactivate(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        lock (_gate)
        {
            if (!_active.Remove(path)) return false;
            FontNative.RemoveFontResourceEx(path, 0, IntPtr.Zero);
        }
        Broadcast();
        Changed?.Invoke();
        return true;
    }

    public void DeactivateAll() => DeactivateMany(Active.ToList());

    private static void Broadcast() => FontNative.BroadcastFontChange();
}
