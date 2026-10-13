using System.Text;

namespace FontA.Core.Library;

/// <summary>
/// The font names FontA knows, keyed for fast matching against the text inside a document. Windows doesn't record which
/// program uses which font, but saved documents do name the fonts they use, so FontA reads the documents instead.
/// </summary>
public sealed class FontNameIndex
{
    private readonly Dictionary<string, List<string>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ulong> _postScriptHashes = new();

    /// <summary>Names from the PostScript name (Photoshop, After Effects, PDF) and the full name (some XMP font lists).</summary>
    public void Add(string name, string path, bool postScript)
    {
        name = name.Trim();
        if (name.Length < 3 || name.Length > DocumentFontScanner.MaxToken) return;
        if (!_byName.TryGetValue(name, out var paths)) _byName[name] = paths = new List<string>(1);
        if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
        if (postScript) _postScriptHashes.Add(DocumentFontScanner.Hash(name));
    }

    public int Count => _byName.Count;
    internal bool MayContain(ulong hash) => _postScriptHashes.Contains(hash);
    public IReadOnlyList<string>? PathsFor(string name) => _byName.TryGetValue(name, out var p) ? p : null;
}

/// <summary>
/// Reads the fonts a design document uses. Illustrator, InDesign and PDF files carry a plain-text list of fonts in their
/// XMP metadata. Photoshop and After Effects files don't, but they hold the font's PostScript name as text inside the
/// file, so those are scanned for names FontA already has. Only the names that match a font in the library are kept;
/// nothing else from the document is read into memory or stored.
/// </summary>
public static class DocumentFontScanner
{
    public static readonly string[] Extensions = { ".ai", ".indd", ".indt", ".pdf", ".psd", ".psb", ".aep", ".aet" };

    /// <summary>Longest name looked at. PostScript names are at most 63 characters.</summary>
    internal const int MaxToken = 80;

    /// <summary>Most of a Photoshop or After Effects file that is searched. Text layers sit well before the pixel data.</summary>
    public const long MaxBinaryBytes = 192L * 1024 * 1024;

    /// <summary>Most of an Illustrator/InDesign/PDF file that is searched for its XMP font list.</summary>
    public const long MaxXmpBytes = 1024L * 1024 * 1024;

    public static bool IsDocument(string path) =>
        Array.Exists(Extensions, e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static bool UsesXmpList(string path) =>
        path.EndsWith(".ai", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".indd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".indt", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The library fonts (paths) a document uses. Throws IOException if the file can't be read.</summary>
    public static IReadOnlySet<string> Scan(string path, FontNameIndex index, CancellationToken ct = default)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

        bool xmp = UsesXmpList(path);
        if (xmp) ScanXmp(fs, index, names, ct);
        // A PDF without an XMP list (or a file whose list is empty) still names its fonts as /BaseFont entries.
        if (!xmp || (names.Count == 0 && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            fs.Position = 0;
            ScanTokens(fs, index, names, xmp ? MaxXmpBytes : MaxBinaryBytes, ct);
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
            if (index.PathsFor(n) is { } list) foreach (var p in list) paths.Add(p);
        return paths;
    }

    // ---- XMP font lists: <stFnt:fontName>ArialMT</stFnt:fontName> or stFnt:fontName="ArialMT" ----

    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("stFnt:fontName");

    private static void ScanXmp(Stream s, FontNameIndex index, HashSet<string> into, CancellationToken ct)
    {
        const int Chunk = 1 << 20, Overlap = 512;   // the tail kept between chunks is longer than a marker plus its value
        var buf = new byte[Chunk + Overlap];
        int carry = 0;
        long total = 0;

        void Search(int len, bool atEnd)
        {
            int from = 0;
            while (from < len)
            {
                int at = buf.AsSpan(from, len - from).IndexOf(Marker);
                if (at < 0) return;
                int start = from + at + Marker.Length;
                // The value may continue in the next chunk: leave this marker in the kept tail and finish it then.
                if (!atEnd && start + MaxToken + 4 > len) return;
                AddXmpValue(buf, start, len, index, into);
                from = start;
            }
        }

        while (total < MaxXmpBytes)
        {
            ct.ThrowIfCancellationRequested();
            int read = s.Read(buf, carry, Chunk);
            if (read <= 0) break;
            total += read;
            int len = carry + read;
            Search(len, atEnd: false);
            carry = Math.Min(Overlap, len);
            Buffer.BlockCopy(buf, len - carry, buf, 0, carry);
        }
        Search(carry, atEnd: true);
    }

    private static void AddXmpValue(byte[] b, int i, int len, FontNameIndex index, HashSet<string> into)
    {
        // After the marker comes either ">value</" or `="value"` (or a single-quoted attribute).
        if (i >= len) return;
        char open = (char)b[i];
        int start, end;
        if (open == '>') { start = i + 1; end = Array.IndexOf(b, (byte)'<', start, Math.Min(len - start, MaxToken + 1)); }
        else if (open == '=' && i + 1 < len && (b[i + 1] == '"' || b[i + 1] == '\''))
        { start = i + 2; end = Array.IndexOf(b, b[i + 1], start, Math.Min(len - start, MaxToken + 1)); }
        else return;
        if (end <= start) return;
        var value = Encoding.UTF8.GetString(b, start, end - start).Trim();
        if (index.PathsFor(value) != null) into.Add(value);
    }

    // ---- scan for font names as plain text, in ASCII and in two-byte (UTF-16 big-endian) form ----

    internal static ulong Hash(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s) h = (h ^ LowerAscii(c)) * 1099511628211UL;
        return h;
    }

    private static byte LowerAscii(int c) => (byte)(c is >= 'A' and <= 'Z' ? c | 0x20 : c);

    /// <summary>Characters a PostScript font name is made of.</summary>
    private static bool IsNameChar(byte b) =>
        b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_' or (byte)'+' or (byte)'.';

    private struct Run
    {
        public readonly char[] Chars = new char[MaxToken];
        public int Length;
        public ulong Hash = 14695981039346656037UL;
        public bool Overflow;
        public Run() { }

        public void Add(byte b)
        {
            if (Length >= MaxToken) { Overflow = true; return; }
            Chars[Length++] = (char)b;
            Hash = (Hash ^ LowerAscii(b)) * 1099511628211UL;
        }

        public void Reset() { Length = 0; Hash = 14695981039346656037UL; Overflow = false; }
    }

    private static void ScanTokens(Stream s, FontNameIndex index, HashSet<string> into, long maxBytes, CancellationToken ct)
    {
        var buf = new byte[1 << 20];
        var ascii = new Run();
        var wide = new Run();
        bool sawZero = false;   // the previous byte of a two-byte character (always 0 for plain Latin letters)
        long total = 0;
        while (total < maxBytes)
        {
            ct.ThrowIfCancellationRequested();
            int read = s.Read(buf, 0, (int)Math.Min(buf.Length, maxBytes - total));
            if (read <= 0) break;
            total += read;
            for (int i = 0; i < read; i++)
            {
                byte b = buf[i];
                bool name = IsNameChar(b);

                if (name) ascii.Add(b); else if (ascii.Length > 0) { Finish(ref ascii, index, into); }

                // Two-byte text is 00 c 00 c ...; a run can start at any byte offset, so track it by shape not position.
                if (sawZero)
                {
                    if (name) { wide.Add(b); sawZero = false; }
                    else { if (wide.Length > 0) Finish(ref wide, index, into, leadingJunk: true); sawZero = b == 0; }
                }
                else if (b == 0) sawZero = true;
                else if (wide.Length > 0) { Finish(ref wide, index, into, leadingJunk: true); }
            }
        }
        if (ascii.Length > 0) Finish(ref ascii, index, into);
        if (wide.Length > 0) Finish(ref wide, index, into, leadingJunk: true);
    }

    private static void Finish(ref Run r, FontNameIndex index, HashSet<string> into, bool leadingJunk = false)
    {
        try
        {
            if (r.Overflow || r.Length < 3) return;
            if (index.MayContain(r.Hash)) into.Add(new string(r.Chars, 0, r.Length));
            // PDF subset fonts are named ABCDEF+RealName.
            if (r.Length > 7 && r.Chars[6] == '+') TryFrom(ref r, 7, index, into);
            // In two-byte text a length byte just before the string can look like its first letter ("A" + "Verdana").
            if (leadingJunk)
                for (int skip = 1; skip <= 3 && r.Length - skip >= 3; skip++) TryFrom(ref r, skip, index, into);
        }
        finally { r.Reset(); }
    }

    private static void TryFrom(ref Run r, int from, FontNameIndex index, HashSet<string> into)
    {
        ulong h = 14695981039346656037UL;
        for (int i = from; i < r.Length; i++) h = (h ^ LowerAscii(r.Chars[i])) * 1099511628211UL;
        if (index.MayContain(h)) into.Add(new string(r.Chars, from, r.Length - from));
    }
}
