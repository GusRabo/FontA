namespace FontA.Core.Library;

/// <summary>A folder's worth of fonts: the key identifies the folder, the display name is what the person sees.</summary>
public sealed record FolderGroup<T>(string Key, string Display, IReadOnlyList<T> Items);

/// <summary>Groups fonts by the folder their file is in, and names those folders the way a person would.</summary>
public static class FolderGrouping
{
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
            var key = FolderOf(pathOf(item));
            if (!byFolder.TryGetValue(key, out var list)) byFolder[key] = list = new List<T>();
            list.Add(item);
        }

        var named = byFolder.Select(kv => (Key: kv.Key, Display: Display(kv.Key, roots), Items: kv.Value)).ToList();

        return named
            .OrderBy(n => n.Display, Comparer<string>.Create(NaturalCompare))
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
