using FontA.Core.Fonts;
using Microsoft.Data.Sqlite;

namespace FontA.Core.Library;

public sealed record DocumentScanProgress(int Done, int Total);

/// <summary>What a document scan found, and whether it could look at everything it was supposed to.</summary>
public sealed record DocumentScanResult(int Read, int Unchanged, int FontsMarked, int MissingFolders, int OnlineOnly, int Unreadable, bool Cancelled)
{
    /// <summary>
    /// True when nothing was left unchecked. Only then is "no document used this font" a fact. A folder on an unplugged
    /// drive, or documents that exist only in the cloud, could hide a font that is in use, so deactivating is paused.
    /// </summary>
    public bool Complete => MissingFolders == 0 && OnlineOnly == 0 && !Cancelled;
}

public sealed partial class FontLibrary
{
    // ---- folders of documents to read ----
    public bool AddDocumentFolder(string path)
    {
        using var c = Command("INSERT OR IGNORE INTO doc_folders(path) VALUES($p)", ("$p", NormalizeFolder(path)));
        return c.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<string> DocumentFolders() => Column("SELECT path FROM doc_folders ORDER BY path COLLATE NOCASE");

    public void RemoveDocumentFolder(string path)
    {
        Run("DELETE FROM doc_folders WHERE path=$p", ("$p", NormalizeFolder(path)));
        Run("DELETE FROM doc_files"); // what was read from them is no longer wanted
    }

    private const FileAttributes OnlineOnlyAttributes = (FileAttributes)(0x400000 | 0x40000 | 0x1000);   // recall on data access / on open / offline

    private sealed record Doc(string Path, long Size, DateTime ModifiedUtc, DateTimeOffset At, FileAttributes Attributes);

    /// <summary>
    /// Reads the design documents in the remembered document folders (and the ones Windows lists as recently opened)
    /// that were opened or saved within <paramref name="lookback"/>, and marks the library fonts they use as used at that
    /// time. Runs on its own connection so the window stays responsive. Files already read and unchanged are not read again.
    /// </summary>
    public DocumentScanResult ScanDocuments(TimeSpan lookback, DateTimeOffset now, IEnumerable<(string Path, DateTimeOffset OpenedAt)>? recentlyOpened = null,
        IProgress<DocumentScanProgress>? progress = null, CancellationToken ct = default)
    {
        var cutoff = now - lookback;
        int read = 0, unchanged = 0, marked = 0, missing = 0, onlineOnly = 0, unreadable = 0;

        using var db = new SqliteConnection(_connectionString);
        db.Open();

        // ---- which documents ----
        var docs = new Dictionary<string, Doc>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        using (var q = Cmd(db, "SELECT path FROM doc_folders"))
        using (var r = q.ExecuteReader())
            while (r.Read()) folders.Add(r.GetString(0));
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) { missing++; continue; }
            foreach (var d in EnumerateDocuments(folder, cutoff))
                docs[d.Path] = d;
        }
        if (recentlyOpened != null)
            foreach (var (path, openedAt) in recentlyOpened)
            {
                if (openedAt < cutoff || !DocumentFontScanner.IsDocument(path)) continue;
                FileInfo fi;
                try { fi = new FileInfo(path); if (!fi.Exists) continue; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { continue; }
                var modified = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
                var at = openedAt > modified ? openedAt : modified;
                if (docs.TryGetValue(fi.FullName, out var known) && known.At >= at) continue;
                docs[fi.FullName] = new Doc(fi.FullName, fi.Length, fi.LastWriteTimeUtc, at, fi.Attributes);
            }

        // ---- the font names to look for ----
        BackfillPostScriptNames(db, ct);
        if (ct.IsCancellationRequested) return new DocumentScanResult(0, 0, 0, missing, 0, 0, true);
        var index = new FontNameIndex();
        using (var q = Cmd(db, "SELECT path, ps_name, full_name FROM fonts"))
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                var path = r.GetString(0);
                var ps = r.GetString(1);
                if (ps.Length > 0 && ps != "-") index.Add(ps, path, postScript: true);
                index.Add(r.GetString(2), path, postScript: false);
            }

        // New fonts can match documents that were read before they existed, so a changed library means reading again.
        string stamp;
        using (var q = Cmd(db, "SELECT COUNT(*) || ':' || COALESCE(MAX(id),0) FROM fonts")) stamp = (string)q.ExecuteScalar()!;
        using (var q = Cmd(db, "SELECT value FROM settings WHERE key='docs.indexStamp'"))
            if (q.ExecuteScalar() as string != stamp)
            {
                using var c = Cmd(db, "DELETE FROM doc_files"); c.ExecuteNonQuery();
                using var s = Cmd(db, "INSERT INTO settings(key,value) VALUES('docs.indexStamp',$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$v", stamp));
                s.ExecuteNonQuery();
            }

        // ---- read them ----
        var tx = db.BeginTransaction();
        try
        {
            int done = 0;
            foreach (var d in docs.Values.OrderByDescending(x => x.At))
            {
                if (ct.IsCancellationRequested) break;
                progress?.Report(new DocumentScanProgress(done++, docs.Count));

                long mtime = d.ModifiedUtc.Ticks;
                string? cached = null;
                using (var q = Cmd(db, "SELECT fonts FROM doc_files WHERE path=$p AND mtime=$m AND size=$s", ("$p", d.Path), ("$m", mtime), ("$s", d.Size)))
                    cached = q.ExecuteScalar() as string;

                string[] fontPaths;
                if (cached != null) { fontPaths = cached.Length == 0 ? Array.Empty<string>() : cached.Split('|'); unchanged++; }
                else
                {
                    if ((d.Attributes & OnlineOnlyAttributes) != 0) { onlineOnly++; continue; }   // reading it would download it
                    try { fontPaths = DocumentFontScanner.Scan(d.Path, index, ct).ToArray(); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; continue; }
                    read++;
                    using var c = Cmd(db, "INSERT OR REPLACE INTO doc_files(path,mtime,size,fonts) VALUES($p,$m,$s,$f)",
                        ("$p", d.Path), ("$m", mtime), ("$s", d.Size), ("$f", string.Join('|', fontPaths)));
                    c.ExecuteNonQuery();
                }
                NoteUsedPaths(db, fontPaths, d.At);
                marked += fontPaths.Length;

                if (done % 25 == 0) { tx.Commit(); tx.Dispose(); tx = db.BeginTransaction(); }
            }

            bool cancelled = ct.IsCancellationRequested;
            if (!cancelled)
            {
                // Documents that fell out of the window (or were deleted) no longer need remembering.
                var keep = new HashSet<string>(docs.Keys, StringComparer.OrdinalIgnoreCase);
                var drop = new List<string>();
                using (var q = Cmd(db, "SELECT path FROM doc_files"))
                using (var r = q.ExecuteReader())
                    while (r.Read()) if (!keep.Contains(r.GetString(0))) drop.Add(r.GetString(0));
                foreach (var p in drop) { using var c = Cmd(db, "DELETE FROM doc_files WHERE path=$p", ("$p", p)); c.ExecuteNonQuery(); }
            }
            tx.Commit();
            progress?.Report(new DocumentScanProgress(docs.Count, docs.Count));
            return new DocumentScanResult(read, unchanged, marked, missing, onlineOnly, unreadable, cancelled);
        }
        finally { tx.Dispose(); }
    }

    private static IEnumerable<Doc> EnumerateDocuments(string folder, DateTimeOffset cutoff)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System   // hidden files are still documents (Dropbox and others hide some)
        };
        var cutoffUtc = cutoff.UtcDateTime;
        return new System.IO.Enumeration.FileSystemEnumerable<Doc>(folder,
            (ref System.IO.Enumeration.FileSystemEntry e) => new Doc(e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime, e.LastWriteTimeUtc, e.Attributes), options)
        {
            ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry e) =>
                !e.IsDirectory && e.LastWriteTimeUtc.UtcDateTime >= cutoffUtc && DocumentFontScanner.IsDocument(e.FileName.ToString()),
            ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0
        };
    }

    /// <summary>
    /// Libraries from before FontA read PostScript names have none stored. Reads them once, here, rather than rescanning
    /// every folder. A font that can't be read is marked "-" so it isn't tried again on every scan.
    /// </summary>
    private static void BackfillPostScriptNames(SqliteConnection db, CancellationToken ct)
    {
        var paths = new List<string>();
        using (var q = Cmd(db, "SELECT DISTINCT path FROM fonts WHERE ps_name=''"))
        using (var r = q.ExecuteReader())
            while (r.Read()) paths.Add(r.GetString(0));
        if (paths.Count == 0) return;

        var tx = db.BeginTransaction();
        try
        {
            int n = 0;
            foreach (var p in paths)
            {
                if (ct.IsCancellationRequested) break;
                var faces = OpenTypeReader.Read(p);
                if (faces.Count == 0)
                {
                    using var c = Cmd(db, "UPDATE fonts SET ps_name='-' WHERE path=$p AND ps_name=''", ("$p", p)); c.ExecuteNonQuery();
                }
                foreach (var f in faces)
                {
                    using var c = Cmd(db, "UPDATE fonts SET ps_name=$n WHERE path=$p AND face=$i", ("$n", f.PostScriptName.Length > 0 ? f.PostScriptName : "-"), ("$p", p), ("$i", f.FaceIndex));
                    c.ExecuteNonQuery();
                }
                if (++n % 200 == 0) { tx.Commit(); tx.Dispose(); tx = db.BeginTransaction(); }
            }
            tx.Commit();
        }
        finally { tx.Dispose(); }
    }
}
