using FontA.Core.Fonts;
using Microsoft.Data.Sqlite;

namespace FontA.Core.Library;

/// <summary>SQLite-backed index of scanned fonts plus user organisation (favorites, tags, collections).</summary>
public sealed class FontLibrary : IDisposable
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ttf", ".otf", ".ttc", ".otc" };

    /// <summary>
    /// Bump this and add a step to <see cref="Migrate"/> whenever the database layout changes. Every step must be safe
    /// to run on a database that already has the change, because databases from before versioning start at 0.
    /// </summary>
    public const int SchemaVersion = 2;

    private readonly SqliteConnection _db;
    private readonly string _connectionString;

    public FontLibrary(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        _db = new SqliteConnection(_connectionString);
        _db.Open();

        // A newer FontA may have changed the layout in ways this version doesn't understand. Refuse rather than damage it.
        int found = Scalar<int>("PRAGMA user_version");
        if (found > SchemaVersion)
        {
            _db.Dispose();
            SqliteConnection.ClearAllPools(); // don't leave the file locked by the pool
            throw new LibraryTooNewException(found, SchemaVersion);
        }

        Exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS fonts(
              id INTEGER PRIMARY KEY, path TEXT NOT NULL, face INTEGER NOT NULL,
              family TEXT NOT NULL, style TEXT NOT NULL, full_name TEXT NOT NULL,
              weight INTEGER, italic INTEGER, axes TEXT, features TEXT,
              favorite INTEGER NOT NULL DEFAULT 0, mtime INTEGER NOT NULL,
              UNIQUE(path, face));
            CREATE INDEX IF NOT EXISTS ix_fonts_family ON fonts(family);
            CREATE TABLE IF NOT EXISTS tags(font_id INTEGER, tag TEXT, PRIMARY KEY(font_id, tag));
            CREATE TABLE IF NOT EXISTS collections(id INTEGER PRIMARY KEY, name TEXT UNIQUE NOT NULL);
            CREATE TABLE IF NOT EXISTS collection_fonts(collection_id INTEGER, font_id INTEGER, PRIMARY KEY(collection_id, font_id));
            CREATE TABLE IF NOT EXISTS installs(source TEXT PRIMARY KEY, dest TEXT NOT NULL, value_name TEXT NOT NULL);
            """);
        EnsureColumn("always_active", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("last_used", "INTEGER NOT NULL DEFAULT 0");   // unix seconds; 0 = not recorded yet
        Exec("CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        Migrate(found);
        Exec($"PRAGMA user_version={SchemaVersion}");
    }

    /// <summary>Steps for layout changes after the baseline above, keyed by the version they produce.</summary>
    private void Migrate(int from)
    {
        // v2: remembered font folders.
        Exec("CREATE TABLE IF NOT EXISTS folders(path TEXT PRIMARY KEY)");
    }

    // ---- remembered font folders ----
    public static string NormalizeFolder(string path)
    {
        var full = Path.GetFullPath(path);
        return full == Path.GetPathRoot(full) ? full : full.TrimEnd('\\', '/'); // keep a drive root like "C:\" intact
    }

    private static string WithSeparator(string folder) => folder.EndsWith('\\') ? folder : folder + "\\";

    /// <summary>Remembers a folder to keep in the library. Returns false if it was already remembered.</summary>
    public bool AddFolder(string path)
    {
        using var c = Command("INSERT OR IGNORE INTO folders(path) VALUES($p)", ("$p", NormalizeFolder(path)));
        return c.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<string> Folders() => Column("SELECT path FROM folders ORDER BY path COLLATE NOCASE");

    /// <summary>
    /// Stops tracking a folder and removes the fonts found under it from the library (the files are never touched).
    /// Installed fonts stay, re-homed onto their installed copy, like when a source file disappears.
    /// </summary>
    public PruneResult RemoveFolder(string path)
    {
        var folder = NormalizeFolder(path);
        var prefix = WithSeparator(folder);
        int removed = 0, rehomed = 0;
        using var tx = _db.BeginTransaction();
        foreach (var p in Column("SELECT DISTINCT path FROM fonts"))
        {
            if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (RemoveOrRehome(_db, p)) rehomed++; else removed++;
        }
        Run("DELETE FROM folders WHERE path=$p", ("$p", folder));
        tx.Commit();
        return new PruneResult(removed, rehomed);
    }

    /// <summary>Databases created before a column existed get it added in place.</summary>
    private void EnsureColumn(string name, string definition)
    {
        bool has = false;
        using (var c = Command("PRAGMA table_info(fonts)"))
        using (var r = c.ExecuteReader())
            while (r.Read()) has |= r.GetString(1) == name;
        if (!has) Exec($"ALTER TABLE fonts ADD COLUMN {name} {definition}");
    }

    public string? GetSetting(string key)
    {
        using var c = Command("SELECT value FROM settings WHERE key=$k", ("$k", key));
        return c.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value) =>
        Run("INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value));

    // ---- usage tracking (for deactivating unused fonts) ----
    public void TouchUsed(long fontId, DateTimeOffset? at = null) =>
        Run("UPDATE fonts SET last_used=$t WHERE id=$id", ("$t", (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()), ("$id", fontId));

    public void TouchUsedPath(string path, DateTimeOffset at) =>
        Run("UPDATE fonts SET last_used=$t WHERE path=$p", ("$t", at.ToUnixTimeSeconds()), ("$p", path));

    /// <summary>How many fonts the "Recent" view shows.</summary>
    public const int RecentLimit = 100;

    /// <summary>
    /// Starts the clock for always-active fonts and for the given currently active fonts, if they have no usage record
    /// yet. Other fonts are left alone: they can't be deactivated by inactivity, and giving them a clock would make
    /// them all look "recently used". Returns how many were started.
    /// </summary>
    public int InitializeUsage(DateTimeOffset now, IEnumerable<string>? activePaths = null)
    {
        long t = now.ToUnixTimeSeconds();
        int started;
        using var tx = _db.BeginTransaction();
        using (var c = Command("UPDATE fonts SET last_used=$t WHERE last_used=0 AND always_active=1", ("$t", t)))
            started = c.ExecuteNonQuery();
        if (activePaths != null)
            foreach (var path in activePaths)
            {
                using var c = Command("UPDATE fonts SET last_used=$t WHERE last_used=0 AND path=$p", ("$t", t), ("$p", path));
                started += c.ExecuteNonQuery();
            }
        tx.Commit();
        return started;
    }

    /// <summary>Records use for many files in one go (for example when a whole folder is activated).</summary>
    public void TouchUsedPaths(IEnumerable<string> paths, DateTimeOffset at)
    {
        using var tx = _db.BeginTransaction();
        foreach (var path in paths)
        {
            using var c = Command("UPDATE fonts SET last_used=$t WHERE path=$p", ("$t", at.ToUnixTimeSeconds()), ("$p", path));
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ---- sidebar statistics ----

    /// <summary>One row per font file with how many library entries it holds (a .ttc holds several). Cheap even for huge libraries.</summary>
    public IReadOnlyList<(string Path, int Count)> PathCounts()
    {
        using var c = Command("SELECT path, COUNT(*) FROM fonts GROUP BY path");
        using var r = c.ExecuteReader();
        var list = new List<(string, int)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1)));
        return list;
    }

    public IReadOnlySet<string> InstalledPaths() => new HashSet<string>(Column("SELECT source FROM installs"), StringComparer.OrdinalIgnoreCase);

    public LibraryCounts Counts()
    {
        using var c = Command($"""
            SELECT COUNT(*),
                   COALESCE(SUM(favorite),0),
                   COALESCE(SUM(always_active),0),
                   COALESCE(SUM(EXISTS(SELECT 1 FROM installs i WHERE i.source=fonts.path)),0),
                   COALESCE(SUM(last_used>0),0)
            FROM fonts
            """);
        using var r = c.ExecuteReader();
        r.Read();
        return new LibraryCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), Math.Min(RecentLimit, r.GetInt32(4)));
    }

    public IReadOnlyDictionary<long, int> CollectionCounts()
    {
        using var c = Command("SELECT collection_id, COUNT(*) FROM collection_fonts GROUP BY collection_id");
        using var r = c.ExecuteReader();
        var d = new Dictionary<long, int>();
        while (r.Read()) d[r.GetInt64(0)] = r.GetInt32(1);
        return d;
    }

    /// <summary>The distinct file paths of the fonts in a collection.</summary>
    public IReadOnlyList<string> CollectionPaths(long collectionId) =>
        ColumnWith("SELECT DISTINCT path FROM fonts WHERE id IN (SELECT font_id FROM collection_fonts WHERE collection_id=$c)", ("$c", collectionId));

    /// <summary>
    /// Files whose most recent use (across all faces in the file) is before the cutoff. Installed fonts are excluded,
    /// as are fonts with no usage record.
    /// </summary>
    public IReadOnlyList<string> StalePaths(DateTimeOffset cutoff)
    {
        using var c = Command("""
            SELECT path FROM fonts
            WHERE NOT EXISTS(SELECT 1 FROM installs i WHERE i.source=fonts.path)
            GROUP BY path HAVING MAX(last_used) > 0 AND MAX(last_used) < $c
            ORDER BY path
            """, ("$c", cutoff.ToUnixTimeSeconds()));
        using var r = c.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void SetAlwaysActiveForPath(string path, bool value) =>
        Run("UPDATE fonts SET always_active=$v WHERE path=$p", ("$v", value ? 1 : 0), ("$p", path));

    /// <summary>
    /// Scans a folder, indexing new/changed files and dropping rows for files that disappeared.
    /// Runs on its own connection so the UI can keep using the library meanwhile, commits in batches
    /// so cancelling keeps the work done so far, and skips the removal pass when cancelled.
    /// </summary>
    public ScanResult Scan(string folder, IProgress<ScanProgress>? progress = null, CancellationToken ct = default, TimeSpan? reportInterval = null)
    {
        const int BatchSize = 50;
        long interval = (long)(reportInterval ?? TimeSpan.FromMilliseconds(50)).TotalMilliseconds;
        var files = EnumerateFontFiles(folder).ToList();

        int indexed = 0, unchanged = 0, removed = 0, rehomed = 0, done = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = -interval - 1;

        using var db = new SqliteConnection(_connectionString);
        db.Open();
        var tx = db.BeginTransaction();
        try
        {
            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;
                if (clock.ElapsedMilliseconds - lastReport >= interval)
                {
                    lastReport = clock.ElapsedMilliseconds;
                    progress?.Report(new ScanProgress(done, files.Count, file, indexed));
                }

                long mtime = File.GetLastWriteTimeUtc(file).Ticks;
                using (var q = Cmd(db, "SELECT mtime FROM fonts WHERE path=$p LIMIT 1", ("$p", file)))
                {
                    if (q.ExecuteScalar() is long known && known == mtime) { unchanged++; done++; continue; }
                }
                foreach (var f in OpenTypeReader.Read(file)) { Upsert(db, f, mtime); indexed++; }
                done++;

                if (done % BatchSize == 0)
                {
                    tx.Commit();
                    tx.Dispose();
                    tx = db.BeginTransaction();
                }
            }

            bool cancelled = ct.IsCancellationRequested;
            if (!cancelled)
            {
                var seen = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                var prefix = WithSeparator(NormalizeFolder(folder));
                var stale = new List<string>();
                using (var q = Cmd(db, "SELECT DISTINCT path FROM fonts"))
                using (var r = q.ExecuteReader())
                    while (r.Read())
                    {
                        var p = r.GetString(0);
                        if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !seen.Contains(p)) stale.Add(p);
                    }
                foreach (var p in stale)
                    if (RemoveOrRehome(db, p)) rehomed++; else removed++;
            }
            tx.Commit();
            progress?.Report(new ScanProgress(done, files.Count, "", indexed));
            return new ScanResult(indexed, unchanged, removed, cancelled, rehomed);
        }
        finally { tx.Dispose(); }
    }

    /// <summary>
    /// Font files under a folder, without ever entering a junction or symbolic link to another folder. Following them
    /// lets a crafted folder make a scan loop for as long as the path allows, or pull in files from outside the folder
    /// the person actually chose. (Add the link's target as its own folder if you want its fonts.)
    /// </summary>
    internal static IEnumerable<string> EnumerateFontFiles(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };
        return new System.IO.Enumeration.FileSystemEnumerable<string>(folder, (ref System.IO.Enumeration.FileSystemEntry e) => e.ToFullPath(), options)
        {
            ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry e) =>
                !e.IsDirectory && Extensions.Contains(Path.GetExtension(e.FileName).ToString()),
            ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry e) =>
                (e.Attributes & FileAttributes.ReparsePoint) == 0
        };
    }

    private static void Upsert(SqliteConnection db, FontInfo f, long mtime)
    {
        using var c = Cmd(db, """
        INSERT INTO fonts(path,face,family,style,full_name,weight,italic,axes,features,mtime)
        VALUES($p,$i,$fam,$sty,$full,$w,$it,$ax,$ft,$m)
        ON CONFLICT(path,face) DO UPDATE SET family=$fam,style=$sty,full_name=$full,weight=$w,italic=$it,axes=$ax,features=$ft,mtime=$m
        """,
        ("$p", f.Path), ("$i", f.FaceIndex), ("$fam", f.Family), ("$sty", f.Style), ("$full", f.FullName),
        ("$w", f.Weight), ("$it", f.IsItalic ? 1 : 0),
        ("$ax", string.Join(';', f.Axes.Select(a => $"{a.Tag},{a.Min},{a.Default},{a.Max}"))),
        ("$ft", string.Join(',', f.Features)), ("$m", mtime));
        c.ExecuteNonQuery();
    }

    public IReadOnlyList<LibraryFont> Search(string? text = null, bool favoritesOnly = false, string? tag = null, long? collectionId = null, bool alwaysActiveOnly = false, bool installedOnly = false, bool recentOnly = false)
    {
        var where = new List<string> { "1=1" };
        var args = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(text)) { where.Add("(family LIKE $q OR full_name LIKE $q)"); args.Add(("$q", $"%{text}%")); }
        if (favoritesOnly) where.Add("favorite=1");
        if (alwaysActiveOnly) where.Add("always_active=1");
        if (installedOnly) where.Add("EXISTS(SELECT 1 FROM installs i WHERE i.source=fonts.path)");
        if (recentOnly) where.Add("last_used>0");
        if (tag != null) { where.Add("id IN (SELECT font_id FROM tags WHERE tag=$t)"); args.Add(("$t", tag)); }
        if (collectionId != null) { where.Add("id IN (SELECT font_id FROM collection_fonts WHERE collection_id=$c)"); args.Add(("$c", collectionId)); }

        using var cmd = Command($"SELECT id,path,face,family,style,full_name,weight,italic,axes,features,favorite,always_active,EXISTS(SELECT 1 FROM installs i WHERE i.source=fonts.path) FROM fonts WHERE {string.Join(" AND ", where)} {(recentOnly ? $"ORDER BY last_used DESC LIMIT {RecentLimit}" : "ORDER BY family,weight,style")}", args.ToArray());
        using var r = cmd.ExecuteReader();
        var list = new List<LibraryFont>();
        while (r.Read())
        {
            var axes = r.IsDBNull(8) || r.GetString(8) == "" ? new List<VariationAxis>() :
                r.GetString(8).Split(';').Select(s => s.Split(',')).Select(p => new VariationAxis(p[0],
                    float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture))).ToList();
            var feats = r.IsDBNull(9) || r.GetString(9) == "" ? new List<string>() : r.GetString(9).Split(',').ToList();
            list.Add(new LibraryFont(r.GetInt64(0), new FontInfo(r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetInt32(6), r.GetInt32(7) != 0, axes, feats), r.GetInt32(10) != 0, r.GetInt32(11) != 0, r.GetInt32(12) != 0));
        }
        return list;
    }

    public void MarkInstalled(string sourcePath, string destPath, string valueName) =>
        Run("INSERT OR REPLACE INTO installs VALUES($s,$d,$v)", ("$s", sourcePath), ("$d", destPath), ("$v", valueName));

    /// <summary>
    /// Forgets an install. If the font's file is gone too (it was re-homed onto the installed copy and that copy was
    /// just deleted), the font no longer exists anywhere, so its library entry goes with it.
    /// </summary>
    public void MarkUninstalled(string sourcePath)
    {
        Run("DELETE FROM installs WHERE source=$s", ("$s", sourcePath));
        if (!File.Exists(sourcePath)) DeleteFontRows(_db, sourcePath);
    }

    /// <summary>
    /// Handles library entries whose file no longer exists: drops them, along with their tags and collection
    /// memberships. Installed fonts are kept: they move onto the copy Windows has, so you can still see and uninstall them.
    /// Checks every file, so it's best run in the background. Uses its own connection.
    /// </summary>
    public PruneResult PruneMissing(CancellationToken ct = default)
    {
        using var db = new SqliteConnection(_connectionString);
        db.Open();
        var paths = new List<string>();
        using (var q = Cmd(db, "SELECT DISTINCT path FROM fonts"))
        using (var r = q.ExecuteReader())
            while (r.Read()) paths.Add(r.GetString(0));

        var tracked = new List<string>();
        using (var q = Cmd(db, "SELECT path FROM folders"))
        using (var r = q.ExecuteReader())
            while (r.Read()) tracked.Add(r.GetString(0));

        int removed = 0, rehomed = 0;
        using var tx = db.BeginTransaction();
        foreach (var p in paths)
        {
            if (ct.IsCancellationRequested) break;
            if (File.Exists(p)) continue;
            if (IsOnUnavailableStorage(p, tracked)) continue; // an unplugged drive or offline share isn't a deleted font
            if (RemoveOrRehome(db, p)) rehomed++; else removed++;
        }
        tx.Commit();
        return new PruneResult(removed, rehomed);
    }

    /// <summary>
    /// True when a missing file may just be unreachable: its drive is gone, or it sits under a remembered folder that
    /// can't be opened right now (external drive, network share). Such fonts are kept until the storage returns.
    /// </summary>
    private static bool IsOnUnavailableStorage(string path, IEnumerable<string> trackedFolders)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root) && !Directory.Exists(root)) return true;
        foreach (var f in trackedFolders)
            if (path.StartsWith(WithSeparator(f), StringComparison.OrdinalIgnoreCase) && !Directory.Exists(f)) return true;
        return false;
    }

    /// <summary>Returns true if the entry was kept by moving it onto the installed copy, false if it was removed.</summary>
    private static bool RemoveOrRehome(SqliteConnection db, string path)
    {
        string? dest = null;
        using (var q = Cmd(db, "SELECT dest FROM installs WHERE source=$p", ("$p", path)))
            dest = q.ExecuteScalar() as string;

        if (dest != null && File.Exists(dest))
        {
            using (var c = Cmd(db, "UPDATE OR IGNORE fonts SET path=$d WHERE path=$p", ("$d", dest), ("$p", path))) c.ExecuteNonQuery();
            using (var c = Cmd(db, "UPDATE OR REPLACE installs SET source=$d WHERE source=$p", ("$d", dest), ("$p", path))) c.ExecuteNonQuery();
            DeleteFontRows(db, path); // any rows that couldn't move because the installed copy was already indexed
            return true;
        }

        if (dest != null)
        {
            using var c = Cmd(db, "DELETE FROM installs WHERE source=$p", ("$p", path)); // the installed copy is gone too
            c.ExecuteNonQuery();
        }
        DeleteFontRows(db, path);
        return false;
    }

    private static void DeleteFontRows(SqliteConnection db, string path)
    {
        foreach (var table in new[] { "tags", "collection_fonts" })
        {
            using var c = Cmd(db, $"DELETE FROM {table} WHERE font_id IN (SELECT id FROM fonts WHERE path=$p)", ("$p", path));
            c.ExecuteNonQuery();
        }
        using var d = Cmd(db, "DELETE FROM fonts WHERE path=$p", ("$p", path));
        d.ExecuteNonQuery();
    }

    public (string DestPath, string ValueName)? GetInstall(string sourcePath)
    {
        using var cmd = Command("SELECT dest,value_name FROM installs WHERE source=$s", ("$s", sourcePath));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1)) : null;
    }

    public void SetAlwaysActive(long fontId, bool value) => Run("UPDATE fonts SET always_active=$v WHERE id=$id", ("$v", value ? 1 : 0), ("$id", fontId));

    /// <summary>Distinct file paths of every always-active font (a .ttc counts once).</summary>
    public IReadOnlyList<string> AlwaysActivePaths() => Column("SELECT DISTINCT path FROM fonts WHERE always_active=1");

    public void SetFavorite(long fontId, bool value) => Run("UPDATE fonts SET favorite=$v WHERE id=$id", ("$v", value ? 1 : 0), ("$id", fontId));
    public void AddTag(long fontId, string tag) => Run("INSERT OR IGNORE INTO tags VALUES($id,$t)", ("$id", fontId), ("$t", tag.Trim()));
    public void RemoveTag(long fontId, string tag) => Run("DELETE FROM tags WHERE font_id=$id AND tag=$t", ("$id", fontId), ("$t", tag));
    public IReadOnlyList<string> Tags() => Column("SELECT DISTINCT tag FROM tags ORDER BY tag");
    public IReadOnlyList<string> TagsFor(long fontId) => Column("SELECT tag FROM tags WHERE font_id=" + fontId + " ORDER BY tag");

    /// <summary>
    /// Renames a collection, keeping its fonts. Returns false (changing nothing) if the new name is blank or another
    /// collection already uses it, ignoring case. Changing only the capitalisation of its own name is allowed.
    /// </summary>
    public bool RenameCollection(long id, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return false;
        var taken = Scalar<long>("SELECT COUNT(*) FROM collections WHERE name=$n COLLATE NOCASE AND id<>$id", ("$n", newName), ("$id", id));
        if (taken > 0) return false;
        using var cmd = Command("UPDATE collections SET name=$n WHERE id=$id", ("$n", newName), ("$id", id));
        return cmd.ExecuteNonQuery() == 1;
    }

    public long CreateCollection(string name)
    {
        Run("INSERT OR IGNORE INTO collections(name) VALUES($n)", ("$n", name));
        return Scalar<long>("SELECT id FROM collections WHERE name=$n", ("$n", name));
    }
    public IReadOnlyList<(long Id, string Name)> Collections()
    {
        using var cmd = Command("SELECT id,name FROM collections ORDER BY name");
        using var r = cmd.ExecuteReader();
        var l = new List<(long, string)>();
        while (r.Read()) l.Add((r.GetInt64(0), r.GetString(1)));
        return l;
    }
    public void DeleteCollection(long id)
    {
        Run("DELETE FROM collection_fonts WHERE collection_id=$c", ("$c", id));
        Run("DELETE FROM collections WHERE id=$c", ("$c", id));
    }
    public IReadOnlySet<long> CollectionsFor(long fontId)
    {
        using var cmd = Command("SELECT collection_id FROM collection_fonts WHERE font_id=$f", ("$f", fontId));
        using var r = cmd.ExecuteReader();
        var set = new HashSet<long>();
        while (r.Read()) set.Add(r.GetInt64(0));
        return set;
    }
    public void AddToCollection(long collectionId, long fontId) => Run("INSERT OR IGNORE INTO collection_fonts VALUES($c,$f)", ("$c", collectionId), ("$f", fontId));
    public void RemoveFromCollection(long collectionId, long fontId) => Run("DELETE FROM collection_fonts WHERE collection_id=$c AND font_id=$f", ("$c", collectionId), ("$f", fontId));

    // --- helpers ---
    private SqliteCommand Command(string sql, params (string, object?)[] args) => Cmd(_db, sql, args);

    private static SqliteCommand Cmd(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        var c = db.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return c;
    }
    private void Exec(string sql) { using var c = Command(sql); c.ExecuteNonQuery(); }
    private void Run(string sql, params (string, object?)[] args) { using var c = Command(sql, args); c.ExecuteNonQuery(); }
    private T Scalar<T>(string sql, params (string, object?)[] args)
    {
        using var c = Command(sql, args);
        var o = c.ExecuteScalar();
        return o is null or DBNull ? default! : (T)Convert.ChangeType(o, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }
    private List<string> Column(string sql) => ColumnWith(sql);

    private List<string> ColumnWith(string sql, params (string, object?)[] args)
    {
        using var c = Command(sql, args);
        using var r = c.ExecuteReader();
        var l = new List<string>();
        while (r.Read()) l.Add(r.GetString(0));
        return l;
    }

    public IReadOnlyDictionary<string, int> TagCounts()
    {
        using var c = Command("SELECT tag, COUNT(*) FROM tags GROUP BY tag");
        using var r = c.ExecuteReader();
        var d = new Dictionary<string, int>();
        while (r.Read()) d[r.GetString(0)] = r.GetInt32(1);
        return d;
    }

    public void Dispose() => _db.Dispose();
}

public sealed record ScanProgress(int Done, int Total, string CurrentFile, int Indexed);
public sealed record ScanResult(int Indexed, int Unchanged, int Removed, bool Cancelled, int Rehomed = 0);
public sealed record PruneResult(int Removed, int Rehomed);

/// <summary>The library file was written by a newer FontA than this one.</summary>
public sealed class LibraryTooNewException(int found, int supported)
    : Exception($"This font library was created by a newer version of FontA (layout {found}, this version understands up to {supported}). Update FontA to open it.")
{
    public int Found { get; } = found;
    public int Supported { get; } = supported;
}

public sealed record LibraryFont(long Id, FontInfo Info, bool IsFavorite, bool IsAlwaysActive = false, bool IsInstalled = false);

/// <summary>Numbers for the sidebar: how many fonts are in each of the built-in views.</summary>
public sealed record LibraryCounts(int Total, int Favorites, int AlwaysActive, int Installed, int Recent);
