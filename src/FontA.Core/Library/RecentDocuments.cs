using System.Text;

namespace FontA.Core.Library;

/// <summary>
/// Documents Windows lists under "Recent": opening a file from Explorer or an app's File &gt; Open adds a shortcut named
/// after it to the Recent folder, and the shortcut's date is when you last opened it. This reads those shortcuts
/// (never the documents themselves) so a document you only looked at, without saving, still counts as used.
/// </summary>
public static class RecentDocuments
{
    public static string DefaultFolder => Environment.GetFolderPath(Environment.SpecialFolder.Recent);

    /// <summary>Design documents opened on or after <paramref name="since"/>, with when they were last opened.</summary>
    public static IReadOnlyList<(string Path, DateTimeOffset OpenedAt)> Read(string folder, DateTimeOffset since)
    {
        var found = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder)) return Array.Empty<(string, DateTimeOffset)>();
        try
        {
            foreach (var lnk in Directory.EnumerateFiles(folder, "*.lnk"))
            {
                var stem = System.IO.Path.GetFileNameWithoutExtension(lnk);          // "Poster.indd" for "Poster.indd.lnk"
                if (!DocumentFontScanner.IsDocument(stem)) continue;
                var opened = new DateTimeOffset(File.GetLastWriteTimeUtc(lnk), TimeSpan.Zero);
                if (opened < since) continue;
                var target = TargetOf(lnk);
                if (target == null || !DocumentFontScanner.IsDocument(target)) continue;
                if (!found.TryGetValue(target, out var prev) || opened > prev) found[target] = opened;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return found.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>The file a shortcut points to. Asks Windows first (always right for any name), and reads the file itself if that isn't possible.</summary>
    public static string? TargetOf(string lnkPath)
    {
        try
        {
            if (OperatingSystem.IsWindows() && Type.GetTypeFromProgID("WScript.Shell") is { } t)
            {
                dynamic shell = Activator.CreateInstance(t)!;
                try
                {
                    string? target = shell.CreateShortcut(lnkPath).TargetPath;
                    if (!string.IsNullOrEmpty(target)) return target;
                }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or NotSupportedException) { }
        return ParseTarget(lnkPath);
    }

    /// <summary>The local file a .lnk points to, read straight from the shortcut file (no COM, no shell). Null if it isn't a local file.</summary>
    internal static string? ParseTarget(string lnkPath)
    {
        try
        {
            var d = File.ReadAllBytes(lnkPath);
            if (d.Length < 0x4C || BitConverter.ToUInt32(d, 0) != 0x4C) return null;
            uint flags = BitConverter.ToUInt32(d, 0x14);
            int pos = 0x4C;
            if ((flags & 1) != 0)                                       // skip the shell item list
            {
                if (pos + 2 > d.Length) return null;
                pos += 2 + BitConverter.ToUInt16(d, pos);
            }
            if ((flags & 2) == 0 || pos + 28 > d.Length) return null;   // no LinkInfo block
            int info = pos;
            int headerSize = (int)BitConverter.ToUInt32(d, info + 4);
            uint infoFlags = BitConverter.ToUInt32(d, info + 8);
            if ((infoFlags & 1) == 0) return null;                      // only a network location

            string? basePath, suffix;
            if (headerSize >= 0x24 && info + 0x24 <= d.Length)
            {
                basePath = Wide(d, info + (int)BitConverter.ToUInt32(d, info + 28));
                suffix = Wide(d, info + (int)BitConverter.ToUInt32(d, info + 32));
            }
            else
            {
                basePath = Ansi(d, info + (int)BitConverter.ToUInt32(d, info + 16));
                suffix = Ansi(d, info + (int)BitConverter.ToUInt32(d, info + 24));
            }
            if (string.IsNullOrEmpty(basePath)) return null;
            return suffix is { Length: > 0 } ? System.IO.Path.Combine(basePath, suffix) : basePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The code page old-style shortcut paths are written in (Windows-1252 on most PCs).</summary>
    private static readonly Encoding AnsiEncoding = Ansi();

    private static Encoding Ansi()
    {
        try { return CodePagesEncodingProvider.Instance.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage) ?? Encoding.Latin1; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.Latin1; }
    }

    private static string? Ansi(byte[] d, int at)
    {
        if (at <= 0 || at >= d.Length) return null;
        int end = Array.IndexOf(d, (byte)0, at);
        return end < 0 ? null : AnsiEncoding.GetString(d, at, end - at);
    }

    private static string? Wide(byte[] d, int at)
    {
        if (at <= 0 || at + 1 >= d.Length) return null;
        int end = at;
        while (end + 1 < d.Length && (d[end] != 0 || d[end + 1] != 0)) end += 2;
        return Encoding.Unicode.GetString(d, at, end - at);
    }
}
