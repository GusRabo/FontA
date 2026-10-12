using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FontA.Rendering;

public sealed record ThumbnailRequest(
    string FontPath, int FaceIndex, string Family, int Weight, bool Italic,
    long FileStamp, string Text, float Size, int Width, int Height);

/// <summary>
/// Two-level cache of text thumbnails stored as 8-bit coverage masks (theme-independent: the caller
/// tints them). Level 1 is an in-memory LRU, level 2 is deflated files on disk keyed by a hash of
/// everything that affects the pixels, including the font file's timestamp.
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    private const int Version = 1;
    private readonly string _dir;
    private readonly int _memoryCapacity;
    private readonly Stack<FontPreviewRenderer> _idle = new();
    private readonly SemaphoreSlim _renderers;
    private readonly List<FontPreviewRenderer> _all = new();
    private int _renderCount;
    private readonly object _memLock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Mask)>> _mem = new();
    private readonly LinkedList<(string Key, byte[] Mask)> _lru = new();

    /// <summary>Number of thumbnails actually drawn (cache misses). Useful for tests and diagnostics.</summary>
    public int RenderCount => Volatile.Read(ref _renderCount);

    /// <summary>A sensible number of parallel renderers: DirectWrite scales with cores, but leave some for the UI.</summary>
    public static int DefaultParallelism => Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    public ThumbnailCache(string directory, int memoryCapacity = 1500, int? parallelism = null)
    {
        int n = Math.Max(1, parallelism ?? DefaultParallelism);
        _renderers = new SemaphoreSlim(n, n);
        // Renderers are created lazily, up to n, so idle apps pay nothing.
        _dir = directory;
        _memoryCapacity = memoryCapacity;
        Directory.CreateDirectory(directory);
    }

    /// <summary>Returns Width*Height bytes of coverage (0 = transparent, 255 = full ink). Thread-safe.</summary>
    public byte[] GetMask(ThumbnailRequest r)
    {
        var key = KeyFor(r);
        if (TryMemory(key, out var mask)) return mask;

        var file = PathFor(key);
        if (TryDisk(file, r.Width * r.Height, out mask))
        {
            Remember(key, mask);
            return mask;
        }

        mask = Draw(r);
        Remember(key, mask);
        TryWrite(file, mask);
        return mask;
    }

    /// <summary>Deletes cached files older than the given age (call occasionally on startup).</summary>
    public void Prune(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var f in Directory.EnumerateFiles(_dir, "*.bin", SearchOption.AllDirectories))
            try { if (File.GetLastAccessTimeUtc(f) < cutoff) File.Delete(f); } catch (IOException) { }
    }

    private byte[] Draw(ThumbnailRequest r)
    {
        byte[] bgra;
        _renderers.Wait();
        var renderer = Rent();
        try
        {
            bgra = renderer.Render(new PreviewRequest(r.FontPath, r.Family, r.Text, r.Size, r.Width, r.Height,
                Weight: r.Weight, Italic: r.Italic));
        }
        finally
        {
            Return(renderer);
            _renderers.Release();
        }
        Interlocked.Increment(ref _renderCount);
        var mask = new byte[r.Width * r.Height];
        for (int i = 0; i < mask.Length; i++) mask[i] = bgra[i * 4 + 3];
        return mask;
    }

    // A renderer is only ever used by one thread at a time (guarded by the semaphore), which is what Direct2D requires.
    private FontPreviewRenderer Rent()
    {
        lock (_idle)
        {
            if (_idle.Count > 0) return _idle.Pop();
            var created = new FontPreviewRenderer();
            _all.Add(created);
            return created;
        }
    }

    private void Return(FontPreviewRenderer r)
    {
        lock (_idle) _idle.Push(r);
    }

    private bool TryMemory(string key, out byte[] mask)
    {
        lock (_memLock)
        {
            if (_mem.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                mask = node.Value.Mask;
                return true;
            }
        }
        mask = Array.Empty<byte>();
        return false;
    }

    private void Remember(string key, byte[] mask)
    {
        lock (_memLock)
        {
            if (_mem.ContainsKey(key)) return;
            _mem[key] = _lru.AddFirst((key, mask));
            while (_lru.Count > _memoryCapacity)
            {
                _mem.Remove(_lru.Last!.Value.Key);
                _lru.RemoveLast();
            }
        }
    }

    private static bool TryDisk(string file, int expected, out byte[] mask)
    {
        mask = Array.Empty<byte>();
        try
        {
            if (!File.Exists(file)) return false;
            using var fs = File.OpenRead(file);
            using var z = new DeflateStream(fs, CompressionMode.Decompress);
            var buf = new byte[expected];
            int read = 0, n;
            while (read < expected && (n = z.Read(buf, read, expected - read)) > 0) read += n;
            if (read != expected) return false;
            mask = buf;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false; // treat damaged/locked cache entries as misses
        }
    }

    private static void TryWrite(string file, byte[] mask)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + "." + Environment.CurrentManagedThreadId + ".tmp";
            using (var fs = File.Create(tmp))
            using (var z = new DeflateStream(fs, CompressionLevel.Fastest))
                z.Write(mask, 0, mask.Length);
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache is best-effort.
        }
    }

    private string PathFor(string key) => Path.Combine(_dir, key[..2], key + ".bin");

    private static string KeyFor(ThumbnailRequest r)
    {
        var s = $"v{Version}|{r.FontPath}|{r.FaceIndex}|{r.FileStamp}|{r.Family}|{r.Weight}|{r.Italic}|{r.Text}|{r.Size}|{r.Width}x{r.Height}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..40];
    }

    public void Dispose()
    {
        lock (_idle)
            foreach (var r in _all) r.Dispose();
        _renderers.Dispose();
    }
}
