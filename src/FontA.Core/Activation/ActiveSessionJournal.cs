using System.Text;

namespace FontA.Core.Activation;

/// <summary>
/// A small file recording which fonts FontA has switched on. Windows keeps activated fonts until you sign out, even if
/// FontA is killed or crashes, so without a record the next launch would show them as inactive while they are still
/// on. The file is rewritten whenever the set changes; at the next start FontA reads it back (if Windows hasn't been
/// restarted since, which would have switched them all off) so what you see matches what is active.
/// </summary>
public sealed class ActiveSessionJournal
{
    private static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(3);
    private const string BootPrefix = "boot=";

    private readonly string _path;
    private readonly Func<DateTimeOffset> _bootTime;
    private readonly object _gate = new();

    /// <param name="bootTime">When Windows started. Overridable for tests.</param>
    public ActiveSessionJournal(string path, Func<DateTimeOffset>? bootTime = null)
    {
        _path = path;
        _bootTime = bootTime ?? CurrentBootTime;
    }

    /// <summary>When this Windows session began, worked out from the system uptime counter.</summary>
    public static DateTimeOffset CurrentBootTime() => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Records the currently active paths. Written to a temporary file first, so a crash mid-write can't corrupt it.</summary>
    public void Save(IEnumerable<string> activePaths)
    {
        lock (_gate)
        {
            try
            {
                var tmp = _path + ".tmp";
                var lines = new List<string> { BootPrefix + _bootTime().ToUnixTimeSeconds() };
                lines.AddRange(activePaths);
                File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the journal is a convenience, never a reason to fail an activation.
            }
        }
    }

    /// <summary>
    /// The paths recorded by an earlier run, but only if Windows hasn't restarted since (a restart switches every
    /// activated font off). Empty if there is no journal, it is damaged, or it is from a previous Windows session.
    /// </summary>
    public IReadOnlyList<string> LoadIfSameSession()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return Array.Empty<string>();
                var lines = File.ReadAllLines(_path, Encoding.UTF8);
                if (lines.Length == 0 || !lines[0].StartsWith(BootPrefix, StringComparison.Ordinal)) return Array.Empty<string>();
                if (!long.TryParse(lines[0].AsSpan(BootPrefix.Length), out var savedBoot)) return Array.Empty<string>();
                var saved = DateTimeOffset.FromUnixTimeSeconds(savedBoot);
                if ((_bootTime() - saved).Duration() > BootTolerance) return Array.Empty<string>();
                return lines.Skip(1).Where(l => l.Length > 0).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                return Array.Empty<string>();
            }
        }
    }
}
