using FontA.Core.Activation;
using FontA.Core.Library;

namespace FontA.Tests;

public class FolderTests
{
    private static readonly string Fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

    // ---------- naming and grouping ----------

    [Theory]
    [InlineData(@"C:\Fonts\2009\a.ttf", @"C:\Fonts\2009")]
    [InlineData(@"C:\Fonts\2009\sub\a.ttf", @"C:\Fonts\2009\sub")]
    [InlineData(@"C:\a.ttf", @"C:\")]
    [InlineData("", "")]
    public void Folder_of_a_font_file(string path, string expected) => Assert.Equal(expected, FolderGrouping.FolderOf(path));

    [Theory]
    [InlineData(@"C:\Fonts", "Fonts")]                          // an added folder shows as itself
    [InlineData(@"C:\Fonts\2009", "2009")]                      // a folder inside shows its path from there
    [InlineData(@"C:\Fonts\Display\Bold", @"Display\Bold")]
    [InlineData(@"c:\fonts\2010", "2010")]                      // case-insensitive
    [InlineData(@"D:\Elsewhere\Pack", "Pack")]                  // outside every added folder: its own name
    [InlineData(@"C:\", @"C:\")]                                // a drive root has no name
    [InlineData("", "(unknown)")]
    public void Folders_get_readable_names(string folder, string expected) =>
        Assert.Equal(expected, FolderGrouping.Display(folder, new[] { @"C:\Fonts" }));

    [Fact]
    public void The_closest_added_folder_decides_the_name()
    {
        var roots = new[] { @"C:\Fonts", @"C:\Fonts\Client" };
        Assert.Equal("Client", FolderGrouping.Display(@"C:\Fonts\Client", roots));
        Assert.Equal("2020", FolderGrouping.Display(@"C:\Fonts\Client\2020", roots));   // not "Client\2020"
        Assert.Equal(@"Client2", FolderGrouping.Display(@"C:\Fonts\Client2", roots));   // a look-alike prefix is not "inside"
    }

    [Fact]
    public void Natural_order_sorts_numbers_by_value_and_ignores_case()
    {
        var names = new[] { "2015", "2009", "10", "2", "b", "A", "a1", "a10", "a2" };
        Assert.Equal(new[] { "2", "10", "2009", "2015", "A", "a1", "a2", "a10", "b" },
            names.OrderBy(n => n, Comparer<string>.Create(FolderGrouping.NaturalCompare)).ToArray());
        Assert.Equal(0, FolderGrouping.NaturalCompare("Fonts", "FONTS"));
        Assert.True(FolderGrouping.NaturalCompare("007", "7") == 0);      // leading zeros don't change the value
    }

    [Fact]
    public void Fonts_are_grouped_by_folder_in_natural_order_with_unique_names()
    {
        var roots = new[] { @"C:\Fonts", @"D:\More" };
        var paths = new[]
        {
            @"C:\Fonts\2010\b.ttf", @"C:\Fonts\2009\a.ttf", @"C:\Fonts\2009\c.ttf", @"C:\Fonts\a.ttf",
            @"D:\More\2010\x.ttf",           // same name "2010" as a folder under the other root
            @"E:\Loose\z.ttf"
        };
        var groups = FolderGrouping.Group(paths, p => p, roots);

        Assert.Equal(new[] { "2009", "2010 (Fonts)", "2010 (More)", "Fonts", "Loose" }, groups.Select(g => g.Display).ToArray());
        Assert.Equal(2, groups.Single(g => g.Display == "2009").Items.Count);
        Assert.Equal(paths.Length, groups.Sum(g => g.Items.Count));          // every font lands in exactly one group
        Assert.Equal(groups.Count, groups.Select(g => g.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ---------- the library numbers behind the sidebar ----------

    private static FontLibrary NewLibrary(string root, params string[] fontFolders)
    {
        foreach (var f in fontFolders) Directory.CreateDirectory(Path.Combine(root, f));
        return new FontLibrary(Path.Combine(root, "lib.db"));
    }

    [Fact]
    public void Counts_recent_and_collections_reflect_the_library()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var lib = NewLibrary(root, "A", "B");
            File.Copy(Path.Combine(Fonts, "arial.ttf"), Path.Combine(root, "A", "arial.ttf"));
            File.Copy(Path.Combine(Fonts, "verdana.ttf"), Path.Combine(root, "A", "verdana.ttf"));
            File.Copy(Path.Combine(Fonts, "tahoma.ttf"), Path.Combine(root, "B", "tahoma.ttf"));
            lib.Scan(Path.Combine(root, "A")); lib.Scan(Path.Combine(root, "B"));

            var arial = lib.Search("Arial").Single(); var tahoma = lib.Search("Tahoma").Single();
            Assert.Equal(new LibraryCounts(3, 0, 0, 0, 0), lib.Counts());

            lib.SetFavorite(arial.Id, true);
            lib.SetAlwaysActive(tahoma.Id, true);
            lib.MarkInstalled(Path.Combine(root, "A", "verdana.ttf"), @"C:\x\v.ttf", "V (TrueType)");
            lib.TouchUsed(arial.Id, DateTimeOffset.UtcNow.AddMinutes(-5));
            lib.TouchUsed(tahoma.Id, DateTimeOffset.UtcNow);
            Assert.Equal(new LibraryCounts(3, 1, 1, 1, 2), lib.Counts());

            // Recent: most recently used first, only fonts that were used at all
            Assert.Equal(new[] { "Tahoma", "Arial" }, lib.Search(recentOnly: true).Select(f => f.Info.Family).ToArray());

            var pc = lib.PathCounts();
            Assert.Equal(3, pc.Count);
            Assert.All(pc, p => Assert.Equal(1, p.Count));
            Assert.Equal(new[] { Path.Combine(root, "A", "verdana.ttf") }, lib.InstalledPaths().ToArray());

            var work = lib.CreateCollection("Work");
            lib.AddToCollection(work, arial.Id); lib.AddToCollection(work, tahoma.Id);
            lib.AddTag(arial.Id, "serif"); lib.AddTag(tahoma.Id, "serif"); lib.AddTag(tahoma.Id, "ui");
            Assert.Equal(2, lib.CollectionCounts()[work]);
            Assert.Equal(2, lib.TagCounts()["serif"]); Assert.Equal(1, lib.TagCounts()["ui"]);
            Assert.Equal(new[] { Path.Combine(root, "A", "arial.ttf"), Path.Combine(root, "B", "tahoma.ttf") },
                lib.CollectionPaths(work).Order().ToArray());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public void Recent_never_shows_more_than_the_limit_and_touching_paths_in_bulk_works()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var lib = NewLibrary(root, "A");
            foreach (var n in new[] { "arial.ttf", "verdana.ttf", "tahoma.ttf" }) File.Copy(Path.Combine(Fonts, n), Path.Combine(root, "A", n));
            lib.Scan(Path.Combine(root, "A"));
            var paths = lib.PathCounts().Select(p => p.Path).ToList();

            Assert.Empty(lib.Search(recentOnly: true));                   // nothing used yet
            lib.TouchUsedPaths(paths, DateTimeOffset.UtcNow);             // e.g. a whole folder was activated
            Assert.Equal(3, lib.Search(recentOnly: true).Count);
            Assert.Equal(3, lib.Counts().Recent);
            Assert.True(FontLibrary.RecentLimit >= 3);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    // ---------- activating and deactivating many fonts ----------

    [Fact]
    public void Bulk_activation_reports_progress_and_deactivation_reverses_it()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var files = new[] { "arial.ttf", "verdana.ttf", "tahoma.ttf" }.Select(n => Path.Combine(dir, n)).ToList();
        foreach (var f in files) File.Copy(Path.Combine(Fonts, Path.GetFileName(f)), f);
        var act = new FontActivator();
        var reports = new List<(int Done, int Total)>();
        try
        {
            Assert.Equal(3, act.ActivateMany(files.Append(Path.Combine(dir, "missing.ttf")), new SyncProgress(reports.Add)));
            Assert.All(files, f => Assert.True(act.IsActive(f)));
            Assert.Equal((4, 4), reports.Last());                          // finished, counting the missing file as handled

            Assert.Equal(2, act.DeactivateMany(files.Take(2)));
            Assert.False(act.IsActive(files[0])); Assert.False(act.IsActive(files[1])); Assert.True(act.IsActive(files[2]));
            Assert.Equal(0, act.DeactivateMany(files.Take(2)));            // already off: nothing to do

            var snapshot = act.Active;                                     // a copy: later changes don't alter it
            act.DeactivateAll();
            Assert.Single(snapshot);
            Assert.Empty(act.Active);
        }
        finally { act.DeactivateAll(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void Bulk_activation_stops_when_cancelled()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var files = new[] { "arial.ttf", "verdana.ttf", "tahoma.ttf" }.Select(n => Path.Combine(dir, n)).ToList();
        foreach (var f in files) File.Copy(Path.Combine(Fonts, Path.GetFileName(f)), f);
        var act = new FontActivator();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Equal(0, act.ActivateMany(files, ct: cts.Token));
            Assert.Empty(act.Active);
        }
        finally { act.DeactivateAll(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_activator_is_safe_to_use_from_many_threads()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var files = Enumerable.Range(0, 6).Select(i => Path.Combine(dir, $"f{i}.ttf")).ToList();
        foreach (var f in files) File.Copy(Path.Combine(Fonts, "arial.ttf"), f);
        var act = new FontActivator();
        try
        {
            Parallel.For(0, 200, i =>
            {
                var f = files[i % files.Count];
                if (i % 2 == 0) act.Activate(f); else act.Deactivate(f);
                _ = act.IsActive(f); _ = act.Active.Count;
            });
            act.DeactivateAll();
            Assert.Empty(act.Active);
        }
        finally { act.DeactivateAll(); Directory.Delete(dir, true); }
    }

    private sealed class SyncProgress(Action<(int Done, int Total)> a) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) v) => a(v);
    }
}

public class ActivationPlanTests
{
    [Theory]
    [InlineData(10, 5000, false)]      // a handful of fonts never asks, even on a very slow PC
    [InlineData(19, 5000, false)]
    [InlineData(30, 100, false)]       // 3 s: quick
    [InlineData(100, 100, false)]      // 10 s: under the limit
    [InlineData(100, 150, true)]       // 15 s: asks
    [InlineData(60, 500, true)]        // 30 s on a slow PC: asks
    [InlineData(300, 1, false)]        // exactly at the always-ask limit, and fast
    [InlineData(301, 1, true)]         // more than 300 always asks
    public void Asks_first_when_the_job_is_long_or_huge(int fonts, double msPerFont, bool expected) =>
        Assert.Equal(expected, FontA.Core.Activation.ActivationPlan.NeedsConfirmation(fonts, msPerFont));

    [Fact]
    public void Estimates_and_describes_times_in_plain_words()
    {
        var plan = typeof(FontA.Core.Activation.ActivationPlan);
        Assert.Equal(TimeSpan.FromSeconds(30), FontA.Core.Activation.ActivationPlan.Estimate(60, 500));
        Assert.Equal("a few seconds", FontA.Core.Activation.ActivationPlan.Describe(TimeSpan.FromSeconds(2)));
        Assert.Equal("about 30 seconds", FontA.Core.Activation.ActivationPlan.Describe(TimeSpan.FromSeconds(31)));
        Assert.Equal("about 3 minutes", FontA.Core.Activation.ActivationPlan.Describe(TimeSpan.FromSeconds(170)));
        Assert.Equal($"about {1.5:0.#} hours", FontA.Core.Activation.ActivationPlan.Describe(TimeSpan.FromMinutes(90)));   // numbers follow the PC's own format
        Assert.NotNull(plan);
    }

    [Fact]
    public void Measurements_blend_in_by_sample_size_and_stay_sane()
    {
        const double d = FontA.Core.Activation.ActivationPlan.DefaultMsPerFont;
        Assert.Equal(500, FontA.Core.Activation.ActivationPlan.Blend(d, 500, 40), 3);        // a big sample replaces the guess
        Assert.Equal(d, FontA.Core.Activation.ActivationPlan.Blend(d, 500, 0), 3);           // no sample: unchanged
        var small = FontA.Core.Activation.ActivationPlan.Blend(100, 500, 2);                 // 2 fonts: only 10% weight
        Assert.InRange(small, 100, 200);
        Assert.Equal(10_000, FontA.Core.Activation.ActivationPlan.Blend(d, 1e9, 100), 3);    // absurd readings are clamped
        Assert.Equal(d, FontA.Core.Activation.ActivationPlan.Blend(d, double.NaN, 100), 3);
    }

    [Fact]
    public void The_measured_speed_is_remembered_between_runs()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using (var lib = new FontA.Core.Library.FontLibrary(Path.Combine(dir, "lib.db")))
            {
                var s = new FontA.Core.Library.AppSettings(lib);
                Assert.Equal(FontA.Core.Activation.ActivationPlan.DefaultMsPerFont, s.ActivationMsPerFont);
                s.ActivationMsPerFont = 487.456;
                s.ActivationMsPerFont = 487.456;
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var lib = new FontA.Core.Library.FontLibrary(Path.Combine(dir, "lib.db")))
                Assert.Equal(487.46, new FontA.Core.Library.AppSettings(lib).ActivationMsPerFont, 2);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }
}

public class ActiveSessionJournalTests
{
    private static readonly string Fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

    [Fact]
    public void A_journal_from_the_same_windows_session_is_read_back()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var boot = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        try
        {
            var j = new FontA.Core.Activation.ActiveSessionJournal(Path.Combine(dir, "j.txt"), () => boot);
            Assert.Empty(j.LoadIfSameSession());                                           // no journal yet
            j.Save(new[] { @"C:\a.ttf", @"D:\My Fonts\b c.otf" });
            Assert.Equal(new[] { @"C:\a.ttf", @"D:\My Fonts\b c.otf" }, j.LoadIfSameSession());

            // a few seconds of clock jitter in the uptime counter is fine
            var jitter = new FontA.Core.Activation.ActiveSessionJournal(Path.Combine(dir, "j.txt"), () => boot.AddSeconds(40));
            Assert.Equal(2, jitter.LoadIfSameSession().Count);

            j.Save(Array.Empty<string>());                                                 // everything switched off
            Assert.Empty(j.LoadIfSameSession());
            Assert.False(File.Exists(Path.Combine(dir, "j.txt.tmp")));                     // no temp file left behind
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_journal_from_before_a_restart_is_ignored_because_windows_switched_those_fonts_off()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var before = new FontA.Core.Activation.ActiveSessionJournal(Path.Combine(dir, "j.txt"), () => new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
            before.Save(new[] { @"C:\a.ttf" });
            var after = new FontA.Core.Activation.ActiveSessionJournal(Path.Combine(dir, "j.txt"), () => new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero));
            Assert.Empty(after.LoadIfSameSession());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a journal\nC:\a.ttf")]
    [InlineData("boot=abc\nC:\a.ttf")]
    public void Damaged_journals_are_ignored_not_fatal(string content)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "j.txt"), content);
            Assert.Empty(new FontA.Core.Activation.ActiveSessionJournal(Path.Combine(dir, "j.txt")).LoadIfSameSession());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void After_an_unclean_exit_the_next_run_adopts_the_fonts_that_are_still_on()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var f1 = Path.Combine(dir, "one.ttf"); var f2 = Path.Combine(dir, "two.ttf");
        File.Copy(Path.Combine(Fonts, "arial.ttf"), f1); File.Copy(Path.Combine(Fonts, "verdana.ttf"), f2);
        var journal = new FontA.Core.Activation.ActiveSessionJournal(Path.Combine(dir, "j.txt"));
        var first = new FontA.Core.Activation.FontActivator();
        var second = new FontA.Core.Activation.FontActivator();
        try
        {
            int changes = 0;
            first.Changed += () => { changes++; journal.Save(first.Active); };
            first.ActivateMany(new[] { f1, f2 });
            Assert.True(changes >= 1);

            // "first" is now abandoned without cleaning up, as if the app had crashed. A new run starts from the journal.
            Assert.False(second.IsActive(f1));
            var adopted = second.Adopt(journal.LoadIfSameSession().Append(Path.Combine(dir, "gone.ttf")));
            Assert.Equal(2, adopted);                                   // the missing file is skipped
            Assert.True(second.IsActive(f1) && second.IsActive(f2));

            second.DeactivateMany(new[] { f1, f2 });                    // the new run can switch them off again
            Assert.Empty(second.Active);
        }
        finally { first.DeactivateAll(); second.DeactivateAll(); Directory.Delete(dir, true); }
    }
}
