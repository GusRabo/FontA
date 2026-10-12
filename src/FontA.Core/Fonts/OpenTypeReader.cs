using System.Buffers.Binary;
using System.Text;

namespace FontA.Core.Fonts;

/// <summary>
/// Minimal clean-room OpenType/TrueType/TTC metadata reader (name, OS/2, head, fvar, GSUB, GPOS).
/// Font files are untrusted input (they arrive in downloads and shared folders), so this reader checks every offset
/// and length against the file before using it, caps what it will read, and never throws for a malformed file.
/// Only fonts that pass these checks are indexed, and only indexed fonts are ever rendered or activated by FontA.
/// </summary>
public static class OpenTypeReader
{
    /// <summary>Largest font file FontA will read. Real fonts, including large CJK collections, are far smaller.</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    private const int MaxFacesInCollection = 1024;

    public static IReadOnlyList<FontInfo> Read(string path) => Read(path, MaxFileBytes);

    /// <summary>Reads a font file, refusing it if it is larger than <paramref name="maxBytes"/> (a lower limit lets tests prove the cap).</summary>
    internal static IReadOnlyList<FontInfo> Read(string path, long maxBytes)
    {
        byte[]? data = LoadCapped(path, maxBytes);
        return data == null ? new List<FontInfo>() : Parse(data, path);
    }

    /// <summary>Parses font bytes already in memory. Never throws for malformed data.</summary>
    internal static IReadOnlyList<FontInfo> Parse(byte[] data, string path)
    {
        var result = new List<FontInfo>();
        if (data.Length < 12) return result;

        try
        {
            if (Tag(data, 0) == "ttcf")
            {
                uint count = U32(data, 8);
                if (count == 0 || count > MaxFacesInCollection) return result;
                if (12L + count * 4 > data.Length) return result;
                for (int i = 0; i < count; i++)
                {
                    uint faceOffset = U32(data, 12 + i * 4);
                    if (faceOffset > data.Length - 12) continue;   // a face that starts outside the file
                    var info = ReadFace(data, (int)faceOffset, path, i);
                    if (info != null) result.Add(info);
                }
            }
            else
            {
                var info = ReadFace(data, 0, path, 0);
                if (info != null) result.Add(info);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Hostile or corrupt input can fail in many ways; whatever the cause, keep what parsed cleanly and move on.
        }
        return result;
    }

    /// <summary>Reads the file unless it is missing, unreadable or larger than <see cref="MaxFileBytes"/>.</summary>
    private static byte[]? LoadCapped(string path, long maxBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > maxBytes) return null;
            var buffer = new byte[(int)fs.Length];
            fs.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null; // includes a file that shrank while we read it
        }
    }

    private static FontInfo? ReadFace(byte[] d, int offset, string path, int faceIndex)
    {
        if (offset < 0 || offset > d.Length - 12) return null;
        string sfnt = Tag(d, offset);
        if (sfnt is not ("OTTO" or "true" or "typ1") && U32(d, offset) != 0x00010000) return null;

        int numTables = U16(d, offset + 4);
        if (numTables == 0 || 12L + offset + numTables * 16L > d.Length) return null;   // directory runs past the end
        var tables = new Dictionary<string, (int Off, int Len)>();
        for (int i = 0; i < numTables; i++)
        {
            int rec = offset + 12 + i * 16;
            uint off = U32(d, rec + 8), len = U32(d, rec + 12);
            // A table must lie inside the file (tables are padded to 4 bytes, so allow that much slack at the end).
            if (off > d.Length || len > d.Length || (long)off + len > d.Length + 3L) return null;
            tables[Tag(d, rec)] = ((int)off, (int)len);
        }

        var names = tables.TryGetValue("name", out var n) ? ReadNames(d, n.Off) : new();
        string family = names.GetValueOrDefault(16) ?? names.GetValueOrDefault(1) ?? Path.GetFileNameWithoutExtension(path);
        string style = names.GetValueOrDefault(17) ?? names.GetValueOrDefault(2) ?? "Regular";
        string full = names.GetValueOrDefault(4) ?? $"{family} {style}";

        int weight = 400;
        bool italic = false;
        if (tables.TryGetValue("OS/2", out var os2) && os2.Len >= 64)
        {
            weight = U16(d, os2.Off + 4);
            italic = (U16(d, os2.Off + 62) & 1) != 0;
        }
        if (tables.TryGetValue("head", out var head) && head.Len >= 46)
            italic |= (U16(d, head.Off + 44) & 2) != 0;

        var axes = new List<VariationAxis>();
        if (tables.TryGetValue("fvar", out var fvar))
        {
            int axesOff = fvar.Off + U16(d, fvar.Off + 4);
            int axisCount = U16(d, fvar.Off + 8);
            int axisSize = U16(d, fvar.Off + 10);
            for (int i = 0; i < axisCount; i++)
            {
                int a = axesOff + i * axisSize;
                axes.Add(new VariationAxis(Tag(d, a), Fixed(d, a + 4), Fixed(d, a + 8), Fixed(d, a + 12)));
            }
        }

        var features = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in new[] { "GSUB", "GPOS" })
            if (tables.TryGetValue(t, out var g) && g.Len >= 10)
                ReadFeatureTags(d, g.Off, features);

        return new FontInfo(path, faceIndex, family, style, full, weight, italic, axes, features.ToList());
    }

    private static void ReadFeatureTags(byte[] d, int tableOff, SortedSet<string> into)
    {
        int featureList = tableOff + U16(d, tableOff + 6);
        int count = U16(d, featureList);
        for (int i = 0; i < count; i++)
            into.Add(Tag(d, featureList + 2 + i * 6));
    }

    private static Dictionary<int, string> ReadNames(byte[] d, int off)
    {
        int count = U16(d, off + 2);
        int strings = off + U16(d, off + 4);
        var best = new Dictionary<int, (int Score, string Value)>();
        for (int i = 0; i < count; i++)
        {
            int r = off + 6 + i * 12;
            int platform = U16(d, r), encoding = U16(d, r + 2), lang = U16(d, r + 4);
            int id = U16(d, r + 6), len = U16(d, r + 8), so = strings + U16(d, r + 10);
            string value;
            int score;
            if (platform == 3 || platform == 0)
            {
                value = Encoding.BigEndianUnicode.GetString(d, so, len);
                score = platform == 3 && lang == 0x409 ? 3 : 2;
            }
            else if (platform == 1 && encoding == 0)
            {
                value = Encoding.Latin1.GetString(d, so, len);
                score = 1;
            }
            else continue;
            if (!best.TryGetValue(id, out var cur) || score > cur.Score) best[id] = (score, value);
        }
        return best.ToDictionary(kv => kv.Key, kv => kv.Value.Value);
    }

    private static string Tag(byte[] d, int o) => Encoding.ASCII.GetString(d, o, 4);
    private static int U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
    private static float Fixed(byte[] d, int o) => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(o)) / 65536f;
}
