using FontA.Core.Fonts;
using FontA.Core.Library;

namespace FontA.Tests;

public class CoreTests
{
    private static readonly string Fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

    [Fact]
    public void Reads_static_font_metadata()
    {
        var f = Assert.Single(OpenTypeReader.Read(Path.Combine(Fonts, "arial.ttf")));
        Assert.Equal("Arial", f.Family);
        Assert.Equal(400, f.Weight);
        Assert.False(f.IsVariable);
        Assert.Contains("kern", f.Features);
    }

    [Fact]
    public void Reads_variable_font_axes()
    {
        var f = Assert.Single(OpenTypeReader.Read(Path.Combine(Fonts, "bahnschrift.ttf")));
        Assert.True(f.IsVariable);
        Assert.Contains(f.Axes, a => a.Tag == "wght");
    }

    [Fact]
    public void Garbage_file_yields_nothing()
    {
        var p = Path.GetTempFileName();
        File.WriteAllBytes(p, new byte[64]);
        Assert.Empty(OpenTypeReader.Read(p));
        File.Delete(p);
    }

    [Fact]
    public void Library_scans_and_organises()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.Copy(Path.Combine(Fonts, "arial.ttf"), Path.Combine(dir, "arial.ttf"));
        var dbPath = Path.Combine(dir, "lib.db");
        using (var lib = new FontLibrary(dbPath))
        {
            Assert.Equal(1, lib.Scan(dir).Indexed);
            var again = lib.Scan(dir);
            Assert.Equal(0, again.Indexed); // unchanged -> skipped
            Assert.Equal(1, again.Unchanged);

            var font = Assert.Single(lib.Search("ari"));
            lib.SetFavorite(font.Id, true);
            lib.AddTag(font.Id, "sans");
            var c = lib.CreateCollection("Work");
            lib.AddToCollection(c, font.Id);

            Assert.Single(lib.Search(favoritesOnly: true));
            Assert.Single(lib.Search(tag: "sans"));
            Assert.Single(lib.Search(collectionId: c));
            Assert.Empty(lib.Search("nomatch"));

            Assert.Contains(c, lib.CollectionsFor(font.Id));
            lib.DeleteCollection(c);
            Assert.Empty(lib.CollectionsFor(font.Id));
            Assert.Empty(lib.Collections());

            File.Delete(Path.Combine(dir, "arial.ttf"));
            Assert.Equal(1, lib.Scan(dir).Removed);
            Assert.Empty(lib.Search());
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Always_active_persists_filters_and_migrates_old_databases()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.Copy(Path.Combine(Fonts, "arial.ttf"), Path.Combine(dir, "arial.ttf"));
        File.Copy(Path.Combine(Fonts, "verdana.ttf"), Path.Combine(dir, "verdana.ttf"));
        var db = Path.Combine(dir, "lib.db");

        // A database from before the feature existed: no always_active column.
        using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            old.Open();
            using var c = old.CreateCommand();
            c.CommandText = "CREATE TABLE fonts(id INTEGER PRIMARY KEY, path TEXT NOT NULL, face INTEGER NOT NULL, family TEXT NOT NULL, style TEXT NOT NULL, full_name TEXT NOT NULL, weight INTEGER, italic INTEGER, axes TEXT, features TEXT, favorite INTEGER NOT NULL DEFAULT 0, mtime INTEGER NOT NULL, UNIQUE(path, face))";
            c.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        long arialId;
        using (var lib = new FontLibrary(db))       // opens the old schema and migrates it
        {
            lib.Scan(dir);
            Assert.Empty(lib.AlwaysActivePaths());
            arialId = lib.Search("Arial").Single().Id;
            lib.SetAlwaysActive(arialId, true);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using (var lib = new FontLibrary(db))       // "next run"
        {
            Assert.Equal(Path.Combine(dir, "arial.ttf"), Assert.Single(lib.AlwaysActivePaths()));
            Assert.True(Assert.Single(lib.Search(alwaysActiveOnly: true)).IsAlwaysActive);
            Assert.Equal(2, lib.Search().Count);
            lib.Scan(dir);                           // a rescan must not reset the flag
            Assert.Single(lib.AlwaysActivePaths());
            lib.SetAlwaysActive(arialId, false);
            Assert.Empty(lib.AlwaysActivePaths());
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Activator_activates_in_bulk_and_keeps_pinned_fonts()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var a = Path.Combine(dir, "a.ttf"); var b = Path.Combine(dir, "b.ttf");
        File.Copy(Path.Combine(Fonts, "arial.ttf"), a);
        File.Copy(Path.Combine(Fonts, "verdana.ttf"), b);
        var act = new FontA.Core.Activation.FontActivator();
        try
        {
            Assert.Equal(2, act.ActivateMany(new[] { a, b, Path.Combine(dir, "missing.ttf") }));
            act.DeactivateExcept(new[] { a });
            Assert.Equal(new[] { a }, act.Active.ToArray());
        }
        finally { act.DeactivateAll(); }
        Assert.Empty(act.Active);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Scan_reports_progress_and_cancel_keeps_partial_work()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var names = new[] { "arial.ttf", "calibri.ttf", "verdana.ttf", "tahoma.ttf" };
        foreach (var n in names) File.Copy(Path.Combine(Fonts, n), Path.Combine(dir, n));
        using (var lib = new FontLibrary(Path.Combine(dir, "lib.db")))
        {
            var cts = new CancellationTokenSource();
            var reports = new List<ScanProgress>();
            var result = lib.Scan(dir, new SyncProgress<ScanProgress>(p => { reports.Add(p); if (p.Done >= 2) cts.Cancel(); }), cts.Token, TimeSpan.Zero);
            Assert.True(result.Cancelled);
            Assert.Equal(4, reports.Max(r => r.Total));
            Assert.InRange(lib.Search().Count, 1, 3);     // partial work kept, not everything

            var full = lib.Scan(dir);                      // resume: only the rest is indexed
            Assert.False(full.Cancelled);
            Assert.Equal(4, lib.Search().Count);
            Assert.True(full.Unchanged >= 1);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(dir, true);
    }

    private sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }

    [Fact]
    public void Installer_registers_copies_and_uninstalls_per_user()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var keyPath = $@"Software\FontA.Tests\{Guid.NewGuid():N}";
        var src = Path.Combine(dir, "src"); Directory.CreateDirectory(src);
        var arial = Path.Combine(src, "arial.ttf");
        File.Copy(Path.Combine(Fonts, "arial.ttf"), arial);
        var info = OpenTypeReader.Read(arial).Single();
        var installer = new FontA.Core.Activation.FontInstaller(keyPath, Path.Combine(dir, "fonts"), notifySystem: false);
        try
        {
            var rec = installer.Install(info);
            Assert.StartsWith(Path.Combine(dir, "fonts"), rec.DestPath);
            Assert.True(File.Exists(rec.DestPath));
            Assert.EndsWith("(TrueType)", rec.ValueName);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath))
                Assert.Equal(rec.DestPath, k!.GetValue(rec.ValueName));

            Assert.Equal(rec, installer.Install(info));                 // idempotent
            Assert.Single(Directory.GetFiles(Path.Combine(dir, "fonts")));

            Assert.True(installer.Uninstall(rec));
            Assert.False(File.Exists(rec.DestPath));
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath))
                Assert.Null(k!.GetValue(rec.ValueName));
            Assert.True(File.Exists(arial));                            // the original is untouched
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\FontA.Tests", throwOnMissingSubKey: false);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Installer_refuses_conflicts_and_bad_input()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var keyPath = $@"Software\FontA.Tests\{Guid.NewGuid():N}";
        var a = Path.Combine(dir, "arial.ttf");
        File.Copy(Path.Combine(Fonts, "arial.ttf"), a);
        var info = OpenTypeReader.Read(a).Single();
        var installer = new FontA.Core.Activation.FontInstaller(keyPath, Path.Combine(dir, "fonts"), notifySystem: false);
        try
        {
            Assert.Throws<FileNotFoundException>(() => installer.Install(info with { Path = Path.Combine(dir, "gone.ttf") }));
            var txt = Path.Combine(dir, "x.txt"); File.WriteAllText(txt, "no");
            Assert.Throws<InvalidOperationException>(() => installer.Install(info with { Path = txt }));

            // Same registry name already points at some other file -> refuse rather than hijack it.
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath))
                k.SetValue(info.FullName + " (TrueType)", @"C:\elsewhere\other.ttf");
            Assert.Throws<InvalidOperationException>(() => installer.Install(info));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\FontA.Tests", throwOnMissingSubKey: false);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Deleted_source_of_an_installed_font_is_rehomed_not_lost()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var src = Path.Combine(root, "src"); var installed = Path.Combine(root, "installed");
        Directory.CreateDirectory(src); Directory.CreateDirectory(installed);
        var srcFile = Path.Combine(src, "arial.ttf"); var destFile = Path.Combine(installed, "arial.ttf");
        File.Copy(Path.Combine(Fonts, "arial.ttf"), srcFile);
        File.Copy(srcFile, destFile);
        File.Copy(Path.Combine(Fonts, "verdana.ttf"), Path.Combine(src, "verdana.ttf"));

        using (var lib = new FontLibrary(Path.Combine(root, "lib.db")))
        {
            lib.Scan(src);
            var arial = lib.Search("Arial").Single();
            var verdana = lib.Search("Verdana").Single();
            lib.SetFavorite(arial.Id, true);
            lib.AddTag(arial.Id, "keep");
            lib.AddTag(verdana.Id, "gone");
            lib.MarkInstalled(srcFile, destFile, "Arial (TrueType)");

            File.Delete(srcFile);
            File.Delete(Path.Combine(src, "verdana.ttf"));
            var result = lib.Scan(src);

            Assert.Equal(1, result.Rehomed);                         // installed Arial survives
            Assert.Equal(1, result.Removed);                         // plain Verdana is dropped
            var kept = lib.Search().Single();
            Assert.Equal(destFile, kept.Info.Path);
            Assert.True(kept.IsInstalled);
            Assert.True(kept.IsFavorite);                            // same entry: favorite and tags carried over
            Assert.Equal(new[] { "keep" }, lib.TagsFor(kept.Id));
            Assert.Equal(new[] { "keep" }, lib.Tags());              // the removed font's tag is cleaned up
            Assert.Single(lib.Search(installedOnly: true));
            Assert.Equal((destFile, "Arial (TrueType)"), lib.GetInstall(destFile));

            // Uninstalling deletes the installed copy; the font then no longer exists anywhere.
            File.Delete(destFile);
            lib.MarkUninstalled(destFile);
            Assert.Empty(lib.Search());
            Assert.Empty(lib.Tags());
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }

    [Fact]
    public void PruneMissing_removes_gone_files_and_forgets_dead_installs()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        foreach (var n in new[] { "arial.ttf", "verdana.ttf", "tahoma.ttf" }) File.Copy(Path.Combine(Fonts, n), Path.Combine(root, n));
        using (var lib = new FontLibrary(Path.Combine(root, "lib.db")))
        {
            lib.Scan(root);
            lib.MarkInstalled(Path.Combine(root, "tahoma.ttf"), Path.Combine(root, "no-such-installed-copy.ttf"), "Tahoma (TrueType)");
            File.Delete(Path.Combine(root, "verdana.ttf"));
            File.Delete(Path.Combine(root, "tahoma.ttf"));   // installed, but its installed copy is gone too

            var pruned = lib.PruneMissing();
            Assert.Equal(new PruneResult(2, 0), pruned);
            Assert.Equal("Arial", Assert.Single(lib.Search()).Info.Family);
            Assert.Null(lib.GetInstall(Path.Combine(root, "tahoma.ttf")));
            Assert.Equal(new PruneResult(0, 0), lib.PruneMissing());   // nothing left to do
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }

    [Fact]
    public void Collections_can_be_renamed()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.Copy(Path.Combine(Fonts, "arial.ttf"), Path.Combine(dir, "arial.ttf"));
        using (var lib = new FontLibrary(Path.Combine(dir, "lib.db")))
        {
            lib.Scan(dir);
            var font = lib.Search().Single();
            var work = lib.CreateCollection("Work");
            var play = lib.CreateCollection("Play");
            lib.AddToCollection(work, font.Id);

            Assert.True(lib.RenameCollection(work, "  Client projects "));          // trimmed
            Assert.Contains((work, "Client projects"), lib.Collections());
            Assert.Single(lib.Search(collectionId: work));                          // fonts stay in it

            Assert.False(lib.RenameCollection(play, "client PROJECTS"));            // taken, ignoring case
            Assert.False(lib.RenameCollection(play, "   "));                        // blank
            Assert.False(lib.RenameCollection(999, "Nobody"));                      // no such collection
            Assert.Contains((play, "Play"), lib.Collections());                     // failed renames change nothing

            Assert.True(lib.RenameCollection(work, "client projects"));             // own name, new capitalisation
            Assert.True(lib.RenameCollection(play, "Play"));                        // same name is fine
            Assert.Equal(new[] { "client projects", "Play" }, lib.Collections().Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Unused_fonts_are_deactivated_after_the_cutoff_and_can_be_restored()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        string P(string n) => Path.Combine(root, n);
        File.Copy(Path.Combine(Fonts, "arial.ttf"), P("stale-always.ttf"));
        File.Copy(Path.Combine(Fonts, "verdana.ttf"), P("stale-session.ttf"));
        File.Copy(Path.Combine(Fonts, "tahoma.ttf"), P("recent-always.ttf"));
        File.Copy(Path.Combine(Fonts, "calibri.ttf"), P("stale-installed.ttf"));
        File.Copy(Path.Combine(Fonts, "arialbd.ttf"), P("stale-inactive.ttf"));

        var activator = new FontA.Core.Activation.FontActivator();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var age = TimeSpan.FromDays(30);
        try
        {
            using var lib = new FontLibrary(Path.Combine(root, "lib.db"));
            lib.Scan(root);
            long Id(string n) => lib.Search().Single(f => Path.GetFileName(f.Info.Path) == n).Id;

            lib.SetAlwaysActive(Id("stale-always.ttf"), true);
            lib.SetAlwaysActive(Id("recent-always.ttf"), true);
            lib.SetAlwaysActive(Id("stale-installed.ttf"), true);
            lib.MarkInstalled(P("stale-installed.ttf"), P("installed-copy.ttf"), "Calibri (TrueType)");
            activator.ActivateMany(new[] { P("stale-always.ttf"), P("stale-session.ttf"), P("recent-always.ttf"), P("stale-inactive.ttf") });
            activator.Deactivate(P("stale-inactive.ttf"));

            // No usage recorded yet: the first run only starts the clocks, so nothing is deactivated.
            Assert.Empty(UnusedFontPolicy.Apply(lib, activator, age, t0));

            // 20 days later one font is touched; 31 days after the start the rest are stale.
            lib.TouchUsed(Id("recent-always.ttf"), t0.AddDays(20));
            Assert.Empty(UnusedFontPolicy.Apply(lib, activator, age, t0.AddDays(29)));
            var done = UnusedFontPolicy.Apply(lib, activator, age, t0.AddDays(31));

            Assert.Equal(new[] { "stale-always.ttf", "stale-session.ttf" }, done.Select(d => Path.GetFileName(d.Path)).Order().ToArray());
            Assert.True(done.Single(d => d.Path == P("stale-always.ttf")).WasAlwaysActive);
            Assert.False(done.Single(d => d.Path == P("stale-session.ttf")).WasAlwaysActive);
            Assert.DoesNotContain(P("stale-always.ttf"), activator.Active);
            Assert.DoesNotContain(P("stale-session.ttf"), activator.Active);
            Assert.Contains(P("recent-always.ttf"), activator.Active);                  // used recently: untouched
            Assert.Contains(P("stale-installed.ttf"), lib.AlwaysActivePaths());          // installed: never touched
            Assert.DoesNotContain(P("stale-always.ttf"), lib.AlwaysActivePaths());       // won't return at next launch

            UnusedFontPolicy.Undo(lib, activator, done, t0.AddDays(31));
            Assert.Contains(P("stale-always.ttf"), activator.Active);
            Assert.Contains(P("stale-session.ttf"), activator.Active);
            Assert.Contains(P("stale-always.ttf"), lib.AlwaysActivePaths());
            Assert.Empty(UnusedFontPolicy.Apply(lib, activator, age, t0.AddDays(32)));  // restored fonts got a fresh clock
        }
        finally { activator.DeactivateAll(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        Directory.Delete(root, true);
    }

    [Fact]
    public void Settings_round_trip_and_old_databases_gain_usage_column()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var db = Path.Combine(dir, "lib.db");
        using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            old.Open();
            using var c = old.CreateCommand();
            c.CommandText = "CREATE TABLE fonts(id INTEGER PRIMARY KEY, path TEXT NOT NULL, face INTEGER NOT NULL, family TEXT NOT NULL, style TEXT NOT NULL, full_name TEXT NOT NULL, weight INTEGER, italic INTEGER, axes TEXT, features TEXT, favorite INTEGER NOT NULL DEFAULT 0, mtime INTEGER NOT NULL, UNIQUE(path, face)); INSERT INTO fonts(path,face,family,style,full_name,mtime) VALUES('a.ttf',0,'A','Regular','A',1)";
            c.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using (var lib = new FontLibrary(db))
        {
            Assert.Null(lib.GetSetting("unused.days"));
            lib.SetSetting("unused.days", "45");
            lib.SetSetting("unused.days", "60");
            Assert.Equal("60", lib.GetSetting("unused.days"));
            Assert.Equal(1, lib.InitializeUsage(DateTimeOffset.UtcNow));     // the pre-existing row had no usage record
            Assert.Equal(0, lib.InitializeUsage(DateTimeOffset.UtcNow));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Folders_are_remembered_and_removing_one_forgets_its_fonts()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var a = Path.Combine(root, "A"); var b = Path.Combine(root, "B");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.Copy(Path.Combine(Fonts, "arial.ttf"), Path.Combine(a, "arial.ttf"));
        File.Copy(Path.Combine(Fonts, "verdana.ttf"), Path.Combine(b, "verdana.ttf"));
        var db = Path.Combine(root, "lib.db");
        using (var lib = new FontLibrary(db))
        {
            Assert.True(lib.AddFolder(a + @"\"));            // trailing separator is normalised away
            Assert.False(lib.AddFolder(a));                   // already remembered
            Assert.True(lib.AddFolder(b));
            lib.Scan(a); lib.Scan(b);
            Assert.Equal(2, lib.Search().Count);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using (var lib = new FontLibrary(db))                 // "next run"
        {
            Assert.Equal(new[] { a, b }, lib.Folders());
            var result = lib.RemoveFolder(a);
            Assert.Equal(new PruneResult(1, 0), result);
            Assert.Equal(new[] { b }, lib.Folders());
            Assert.Equal("Verdana", lib.Search().Single().Info.Family);
            Assert.True(File.Exists(Path.Combine(a, "arial.ttf")));   // files are never touched
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }

    [Fact]
    public void Prune_keeps_fonts_whose_folder_is_unavailable()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var usb = Path.Combine(root, "usb"); var local = Path.Combine(root, "local");
        Directory.CreateDirectory(usb); Directory.CreateDirectory(local);
        File.Copy(Path.Combine(Fonts, "arial.ttf"), Path.Combine(usb, "arial.ttf"));
        File.Copy(Path.Combine(Fonts, "verdana.ttf"), Path.Combine(local, "verdana.ttf"));
        using (var lib = new FontLibrary(Path.Combine(root, "lib.db")))
        {
            lib.AddFolder(usb); lib.AddFolder(local);
            lib.Scan(usb); lib.Scan(local);

            Directory.Delete(usb, true);                       // the "drive" is unplugged...
            File.Delete(Path.Combine(local, "verdana.ttf"));   // ...while a font in an available folder is really deleted
            var result = lib.PruneMissing();

            Assert.Equal(new PruneResult(1, 0), result);
            Assert.Equal("Arial", lib.Search().Single().Info.Family);   // the unavailable folder's font survives
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }

    [Fact]
    public void Newer_database_is_refused_and_settings_have_safe_defaults()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var db = Path.Combine(dir, "lib.db");
        using (var lib = new FontLibrary(db))
        {
            var s = new AppSettings(lib);
            Assert.False(s.HideToTray); Assert.False(s.StartWithWindows); Assert.False(s.StartHidden);
            Assert.True(s.DeactivateOnExit); Assert.True(s.RescanOnStartup); Assert.True(s.WatchFolders);
            Assert.False(s.UnusedEnabled); Assert.Equal(30, s.UnusedDays);

            s.HideToTray = true; s.DeactivateOnExit = false; s.UnusedDays = 9999;
            Assert.True(s.HideToTray); Assert.False(s.DeactivateOnExit); Assert.Equal(365, s.UnusedDays);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            raw.Open();
            using var c = raw.CreateCommand();
            c.CommandText = $"PRAGMA user_version={FontLibrary.SchemaVersion + 1}";   // as if written by a future FontA
            c.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var ex = Assert.Throws<LibraryTooNewException>(() => new FontLibrary(db));
        Assert.Equal(FontLibrary.SchemaVersion + 1, ex.Found);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Start_with_windows_registers_and_repairs_the_path()
    {
        var keyPath = $@"Software\FontA.Tests\{Guid.NewGuid():N}";
        var reg = new FontA.Core.Activation.StartupRegistration(keyPath);
        try
        {
            Assert.False(reg.IsEnabled);
            Assert.False(reg.RefreshPath(@"C:\a\FontA.exe"));         // not registered: nothing to repair
            Assert.False(reg.IsEnabled);

            reg.Enable(@"C:\old\FontA.exe");
            Assert.Equal("\"C:\\old\\FontA.exe\" --autostart", reg.Command);
            Assert.True(reg.RefreshPath(@"C:\new\FontA.exe"));         // app moved or updated to a new folder
            Assert.Equal("\"C:\\new\\FontA.exe\" --autostart", reg.Command);
            Assert.False(reg.RefreshPath(@"C:\new\FontA.exe"));        // already current

            reg.Disable();
            Assert.False(reg.IsEnabled);
            reg.Disable();                                             // harmless twice
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\FontA.Tests", throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Library_tracks_installs()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.Copy(Path.Combine(Fonts, "arial.ttf"), Path.Combine(dir, "arial.ttf"));
        using (var lib = new FontLibrary(Path.Combine(dir, "lib.db")))
        {
            lib.Scan(dir);
            var path = Path.Combine(dir, "arial.ttf");
            Assert.False(lib.Search().Single().IsInstalled);
            lib.MarkInstalled(path, @"C:\dest\arial.ttf", "Arial (TrueType)");
            Assert.True(lib.Search().Single().IsInstalled);
            Assert.Single(lib.Search(installedOnly: true));
            Assert.Equal((@"C:\dest\arial.ttf", "Arial (TrueType)"), lib.GetInstall(path));
            lib.MarkUninstalled(path);
            Assert.False(lib.Search().Single().IsInstalled);
            Assert.Empty(lib.Search(installedOnly: true));
            Assert.Null(lib.GetInstall(path));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(dir, true);
    }
}
