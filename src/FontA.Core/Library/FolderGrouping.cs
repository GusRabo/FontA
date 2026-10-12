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
    /// A short, readable name for a folder. A folder you added shows as its own name ("Fonts"); a folder inside one
    /// shows its path from there ("2009", or "Display\Bold"). Anything outside every added folder shows its own name.
    /// </summary>
    public static string Display(string folder, IReadOnlyList<string> roots)
    {
        if (folder.Length == 0) return "(unknown)";
        string? best = null;
        foreach (var root in roots)
        {
            var r = root.TrimEnd('\\', '/');
            bool inside = folder.Equals(r, StringComparison.OrdinalIgnoreCase)
                          || folder.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
            if (inside && (best == null || r.Length > best.Length)) best = r;   // the closest added folder wins
        }

        if (best == null) return Leaf(folder);
        if (folder.Equals(best, StringComparison.OrdinalIgnoreCase)) return Leaf(best);
        return folder[(best.Length + 1)..];
    }

    private static string Leaf(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : folder;   // a drive root has no name of its own
    }

    /// <summary>
    /// Groups items by folder, ordered by display name with numbers in natural order (2 before 10). If two different
    /// folders would show the same name, the name of the folder above is added so they can be told apart.
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
        foreach (var dup in named.GroupBy(n => n.Display, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g).ToList())
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(dup.Key.TrimEnd('\\', '/')) ?? "");
            if (parent.Length == 0) continue;
            named[named.IndexOf(dup)] = (dup.Key, $"{dup.Display} ({parent})", dup.Items);
        }

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
