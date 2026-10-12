using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;

namespace FontA.App;

/// <summary>
/// Watches font folders for added, changed, renamed or deleted font files and reports each affected folder once,
/// a couple of seconds after the activity settles (copying a family of fonts fires dozens of events).
/// Folders that can't be watched right now (unplugged drive, offline share) are skipped and picked up on the next Sync.
/// </summary>
internal sealed class FolderWatcher : IDisposable
{
    private static readonly HashSet<string> FontExtensions = new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf", ".ttc", ".otc" };

    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _debounce;
    private readonly Action<string> _changed;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="changed">Called on the UI thread with a folder to rescan.</param>
    public FolderWatcher(DispatcherQueue ui, Action<string> changed, TimeSpan? settle = null)
    {
        _ui = ui;
        _changed = changed;
        _debounce = ui.CreateTimer();
        _debounce.Interval = settle ?? TimeSpan.FromSeconds(2);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) =>
        {
            foreach (var folder in _pending.Keys.ToList())
            {
                _pending.TryRemove(folder, out _);
                _changed(folder);
            }
        };
    }

    /// <summary>Makes the set of watched folders match <paramref name="folders"/> (empty when disabled).</summary>
    public void Sync(IEnumerable<string> folders, bool enabled)
    {
        var wanted = enabled ? new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
        foreach (var gone in _watchers.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _watchers[gone].Dispose();
            _watchers.Remove(gone);
        }
        foreach (var folder in wanted)
        {
            if (_watchers.ContainsKey(folder) || !Directory.Exists(folder)) continue;
            try
            {
                var w = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024
                };
                FileSystemEventHandler onChange = (_, e) => Notice(folder, e.FullPath);
                w.Created += onChange; w.Changed += onChange; w.Deleted += onChange;
                w.Renamed += (_, e) => { Notice(folder, e.OldFullPath); Notice(folder, e.FullPath); };
                w.Error += (_, _) => Queue(folder);                  // buffer overflow: we may have missed events, so rescan
                w.EnableRaisingEvents = true;
                _watchers[folder] = w;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { /* try again at the next Sync */ }
        }
    }

    // Font files matter, and so do folders (deleting or renaming a folder removes every font inside it).
    private void Notice(string folder, string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0 || FontExtensions.Contains(ext)) Queue(folder);
    }

    private void Queue(string folder)
    {
        _pending[folder] = true;
        _ui.TryEnqueue(() => { _debounce.Stop(); _debounce.Start(); });  // restarting the timer is what makes it settle
    }

    public void Dispose()
    {
        _debounce.Stop();
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
    }
}
