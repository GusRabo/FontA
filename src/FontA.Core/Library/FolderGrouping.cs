namespace FontA.Core.Library;

/// <summary>A folder's worth of fonts: the key identifies the folder, the display name is what the person sees.</summary>
public sealed record FolderGroup<T>(string Key, string Display, IReadOnlyList<T> Items);

/// <summary>Groups fonts by the folder their file is in, and names those folders the way a person would.</summary>
public static class FolderGrouping
{
    /// <summary>The key of the one group that holds every font living in a Windows fonts folder.</summary>
    public const string SystemKey = "system:";

    /// <summary>
    /// The folders Windows keeps its fonts in: the shared one (C:\Windows\Fonts) and the per-user one. Fonts in them are
    /// shown together as "System" instead of under whatever the folder is called. Replaceable for tests.
    /// </summary>
    public static IReadOnlyList<string> SystemFontFolders { get; set; } = DefaultSystemFontFolders();

    private static IReadOnlyList<string> DefaultSystemFontFolders()
    {
        var list = new List<string>();
        try
        {
            var shared = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (!string.IsNullOrEmpty(shared)) list.Add(shared);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local)) list.Add(Path.Combine(local, "Microsoft", "Windows", "Fonts"));
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException) { }
        return list;
    }

    /// <summary>True when the folder is, or is inside, one of the Windows fonts folders.</summary>
    public static bool IsSystemFolder(string folder)
    {
        foreach (var s in SystemFontFolders)
        {
            var root = s.TrimEnd('\\', '/');
            if (folder.Equals(root, StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// The group a font file belongs to: its folder, except that every font in a Windows fonts folder is in the one
    /// <see cref="SystemKey"/> group.
    /// </summary>
    public static string GroupKeyOf(string fontPath)
    {
        var folder = FolderOf(fontPath);
        return IsSystemFolder(folder) ? SystemKey : folder;
    }

    /// <summary>The folder containing a font file, normalised (no trailing separator, except for a drive root).</summary>
    public static string FolderOf(string fontPath)
    {
        var dir = Path.GetDirectoryName(fontPath);
        if (string.IsNullOrEmpty(dir)) return "";
        return dir == Path.GetPathRoot(dir) ? dir : dir.TrimEnd('\\', '/');
    }

    /// <summary>
    /// A folder's name is just that: the name of the folder ("Fonts", "2009", "Bold"), never a path and never anything
    /// taken from the fonts inside it. Hovering a folder in the app shows where it is.
    /// </summary>
    public static string Display(string folder, IReadOnlyList<string> roots)
    {
        if (folder == SystemKey) return "System";
        if (folder.Length == 0) return "(unknown)";
        return Leaf(folder);
    }

    private static string Leaf(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : folder;   // a drive root has no name of its own
    }

    /// <summary>
    /// Groups items by folder, ordered by name with numbers in natural order (2 before 10). Two different folders with
    /// the same name stay separate groups (ordered by path); hovering shows which is which.
    /// </summary>
    public static IReadOnlyList<FolderGroup<T>> Group<T>(IEnumerable<T> items, Func<T, string> pathOf, IReadOnlyList<string> roots)
    {
        var byFolder = new Dictionary<string, List<T>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var key = GroupKeyOf(pathOf(item));
            if (!byFolder.TryGetValue(key, out var list)) byFolder[key] = list = new List<T>();
            list.Add(item);
        }

        var named = byFolder.Select(kv => (Key: kv.Key, Display: Display(kv.Key, roots), Items: kv.Value)).ToList();

        return named
            .OrderBy(n => n.Key == SystemKey ? 0 : 1)                       // "System" first, then the rest by name
            .ThenBy(n => n.Display, Comparer<string>.Create(NaturalCompare))
            .ThenBy(n => n.Key, StringComparer.OrdinalIgnoreCase)
            .Select(n => new FolderGroup<T>(n.Key, n.Display, n.Items))
            .ToList();
    }

    /// <summary>Case-insensitive comparison that orders runs of digits by value ("2" before "10").</summary>
    public static int NaturalCompare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0'); var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length < nb.Length ? -1 : 1;     // fewer digits = smaller number
                int c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
