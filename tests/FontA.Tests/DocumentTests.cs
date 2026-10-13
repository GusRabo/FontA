using System.Text;
using FontA.Core.Library;
using Microsoft.Data.Sqlite;

namespace FontA.Tests;

public class DocumentTests
{
    private static readonly string Fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private sealed class Setup : IDisposable
    {
        public string Root = Directory.CreateTempSubdirectory().FullName;
        public FontLibrary Lib;
        public string Arial, Verdana;
        public string Docs;

        public Setup()
        {
            var fontDir = Path.Combine(Root, "fonts"); Directory.CreateDirectory(fontDir);
            Docs = Path.Combine(Root, "docs"); Directory.CreateDirectory(Docs);
            Arial = Path.Combine(fontDir, "arial.ttf"); Verdana = Path.Combine(fontDir, "verdana.ttf");
            File.Copy(Path.Combine(Fonts, "arial.ttf"), Arial);
            File.Copy(Path.Combine(Fonts, "verdana.ttf"), Verdana);
            Lib = new FontLibrary(Path.Combine(Root, "lib.db"));
            Lib.Scan(fontDir);
            Lib.AddDocumentFolder(Docs);
        }

        public string Doc(string name, byte[] content, DateTimeOffset? modified = null)
        {
            var p = Path.Combine(Docs, name);
            File.WriteAllBytes(p, content);
            File.SetLastWriteTimeUtc(p, (modified ?? Now).UtcDateTime);
            return p;
        }

        /// <summary>Days since the library last recorded this font as used, or null if it never has.</summary>
        public double? Age(string fontPath)
        {
            using var c = new SqliteConnection($"Data Source={Path.Combine(Root, "lib.db")}");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT MAX(last_used) FROM fonts WHERE path=$p";
            cmd.Parameters.AddWithValue("$p", fontPath);
            var v = cmd.ExecuteScalar();
            return v is long t && t > 0 ? (Now - DateTimeOffset.FromUnixTimeSeconds(t)).TotalDays : null;
        }

        public void AssertUsedToday(string fontPath) => Assert.InRange(Age(fontPath) ?? 999, -0.01, 0.5);
        public void Dispose() { Lib.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    private static byte[] Bytes(params object[] parts)
    {
        var l = new List<byte>();
        foreach (var p in parts)
            switch (p)
            {
                case string s: l.AddRange(Encoding.ASCII.GetBytes(s)); break;
                case byte[] b: l.AddRange(b); break;
                default: throw new ArgumentException();
            }
        return l.ToArray();
    }

    private static byte[] Utf16Be(string s) => Encoding.BigEndianUnicode.GetBytes(s);

    [Fact]
    public void Font_files_now_remember_their_postscript_name()
    {
        using var s = new Setup();
        var found = s.Lib.Search("Arial").First(f => f.Info.Path == s.Arial);
        Assert.Equal("ArialMT", found.Info.PostScriptName.Length > 0 ? found.Info.PostScriptName : "(missing)");
    }

    [Fact]
    public void An_indesign_or_illustrator_font_list_marks_those_fonts_used()
    {
        using var s = new Setup();
        s.Doc("poster.indd", Bytes(new byte[5000], "<xmpTPg:Fonts><rdf:Bag><rdf:li rdf:parseType=\"Resource\"><stFnt:fontName>ArialMT</stFnt:fontName><stFnt:fontFamily>Arial</stFnt:fontFamily></rdf:li></rdf:Bag></xmpTPg:Fonts>", new byte[100]));
        s.Doc("logo.ai", Bytes("<rdf:li stFnt:fontName=\"Verdana\" stFnt:fontFamily=\"Verdana\"/>"));

        var r = s.Lib.ScanDocuments(TimeSpan.FromDays(30), Now);

        Assert.True(r.Complete);
        Assert.Equal(2, r.Read);
        s.AssertUsedToday(s.Arial);     // used today
        s.AssertUsedToday(s.Verdana);
    }

    [Fact]
    public void A_font_name_inside_photoshop_or_aftereffects_data_is_found_at_any_byte_offset()
    {
        using var s = new Setup();
        // Photoshop and After Effects store the name as two-byte text; the alignment inside the file is arbitrary.
        s.Doc("a.psd", Bytes(new byte[1001], new byte[] { 0xFE, 0xFF }, Utf16Be("ArialMT"), new byte[40]));
        s.Doc("b.aep", Bytes(new byte[3000], "x", Utf16Be("Verdana"), new byte[9]));

        s.Lib.ScanDocuments(TimeSpan.FromDays(30), Now);

        s.AssertUsedToday(s.Arial);
        s.AssertUsedToday(s.Verdana);
    }

    [Fact]
    public void Plain_and_subset_names_in_a_pdf_are_found_and_unrelated_text_is_not()
    {
        using var s = new Setup();
        s.Doc("a.pdf", Bytes("%PDF-1.7\n/BaseFont /ABCDEF+ArialMT /Subtype /TrueType\n", "ArialMTX Verdanax"));

        s.Lib.ScanDocuments(TimeSpan.FromDays(30), Now);

        s.AssertUsedToday(s.Arial);
        Assert.Null(s.Age(s.Verdana));          // "Verdanax" is not "Verdana"
    }

    [Fact]
    public void A_document_never_makes_a_font_look_older_and_old_documents_are_ignored()
    {
        using var s = new Setup();
        s.Lib.TouchUsedPath(s.Arial, Now);
        s.Doc("old.psd", Bytes(Utf16Be("ArialMT")), Now.AddDays(-200));
        s.Doc("older.psd", Bytes(Utf16Be("Verdana")), Now.AddDays(-45));   // outside a 30-day window

        var r = s.Lib.ScanDocuments(TimeSpan.FromDays(30), Now);

        Assert.Equal(0, r.Read);
        s.AssertUsedToday(s.Arial);
        Assert.Null(s.Age(s.Verdana));

        s.Lib.NoteUsedPaths(new[] { s.Arial }, Now.AddDays(-10));          // an older sighting can't lower it
        s.AssertUsedToday(s.Arial);
    }

    [Fact]
    public void Unchanged_documents_are_not_read_again_but_still_count()
    {
        using var s = new Setup();
        s.Doc("a.psd", Bytes(Utf16Be("ArialMT")));
        Assert.Equal(1, s.Lib.ScanDocuments(TimeSpan.FromDays(30), Now).Read);

        var again = s.Lib.ScanDocuments(TimeSpan.FromDays(30), Now);
        Assert.Equal(0, again.Read);
        Assert.Equal(1, again.Unchanged);
        Assert.Equal(1, again.FontsMarked);
    }

    [Fact]
    public void A_missing_folder_means_the_check_is_not_complete()
    {
        using var s = new Setup();
        s.Lib.AddDocumentFolder(Path.Combine(s.Root, "unplugged-drive"));
        var r = s.Lib.ScanDocuments(TimeSpan.FromDays(30), Now);
        Assert.Equal(1, r.MissingFolders);
        Assert.False(r.Complete);      // so FontA must not decide anything is unused
    }

    [Fact]
    public void Online_only_documents_are_left_unread_and_make_the_check_incomplete()
    {
        using var s = new Setup();
        var p = s.Doc("cloud.psd", Bytes(Utf16Be("ArialMT")));
        File.SetAttributes(p, FileAttributes.Offline);
        var r = s.Lib.ScanDocuments(TimeSpan.FromDays(30), Now);
        Assert.Equal(1, r.OnlineOnly);
        Assert.False(r.Complete);
        Assert.Null(s.Age(s.Arial));
    }

    [Fact]
    public void Recently_opened_documents_count_even_when_they_were_saved_long_ago()
    {
        using var s = new Setup();
        var old = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".psd");   // outside every document folder
        try
        {
            File.WriteAllBytes(old, Bytes(Utf16Be("Verdana")));
            File.SetLastWriteTimeUtc(old, Now.AddDays(-300).UtcDateTime);

            s.Lib.ScanDocuments(TimeSpan.FromDays(30), Now, new[] { (old, Now.AddDays(-3)) });

            Assert.InRange(s.Age(s.Verdana) ?? 999, 2.9, 3.1);     // used when it was opened, three days ago
        }
        finally { File.Delete(old); }
    }

    [Fact]
    public void Fonts_stored_before_postscript_names_were_read_get_them_on_the_first_scan()
    {
        var s = new Setup();
        var db = Path.Combine(s.Root, "lib.db");
        s.Lib.Dispose();
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE fonts SET ps_name=''"; cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        s.Lib = new FontLibrary(db);
        s.Doc("a.psd", Bytes(Utf16Be("ArialMT")));

        s.Lib.ScanDocuments(TimeSpan.FromDays(30), Now);

        s.AssertUsedToday(s.Arial);
        s.Dispose();
    }

    [Fact]
    public void Changing_the_library_makes_old_documents_be_read_again()
    {
        using var s = new Setup();
        s.Doc("a.psd", Bytes(Utf16Be("Tahoma")));              // Tahoma isn't in the library yet
        s.Lib.ScanDocuments(TimeSpan.FromDays(30), Now);

        var fontDir = Path.Combine(s.Root, "fonts");
        File.Copy(Path.Combine(Fonts, "tahoma.ttf"), Path.Combine(fontDir, "tahoma.ttf"));
        s.Lib.Scan(fontDir);
        var r = s.Lib.ScanDocuments(TimeSpan.FromDays(30), Now);

        Assert.Equal(1, r.Read);
        s.AssertUsedToday(Path.Combine(fontDir, "tahoma.ttf"));
    }

    [Fact]
    public void A_recent_shortcut_is_read_down_to_the_file_it_points_at()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = Path.Combine(dir, "Café poster.indd");           // non-ASCII on purpose
            File.WriteAllBytes(target, new byte[10]);
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link = ((dynamic)shell).CreateShortcut(Path.Combine(dir, "Café poster.indd.lnk"));
            link.TargetPath = target;
            link.Save();

            Assert.Equal(target, RecentDocuments.TargetOf(Path.Combine(dir, "Café poster.indd.lnk")));
            var list = RecentDocuments.Read(dir, Now.AddDays(-1));
            Assert.Equal(target, Assert.Single(list).Path);
            Assert.Empty(RecentDocuments.Read(dir, Now.AddDays(1)));       // opened before the window
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_shortcut_can_also_be_read_without_asking_windows()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = Path.Combine(dir, "poster.psd");
            File.WriteAllBytes(target, new byte[10]);
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link = ((dynamic)shell).CreateShortcut(Path.Combine(dir, "poster.psd.lnk"));
            link.TargetPath = target;
            link.Save();
            Assert.Equal(target, RecentDocuments.ParseTarget(Path.Combine(dir, "poster.psd.lnk")));
            Assert.Null(RecentDocuments.ParseTarget(target));            // not a shortcut at all
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Garbage_never_crashes_the_scanner()
    {
        using var s = new Setup();
        s.Doc("junk.ai", new byte[2_000_000]);
        s.Doc("junk.psd", new Random(1).GetBytes(3_000_000));
        s.Doc("short.indd", Bytes("stFnt:fontName"));
        s.Doc("cut.indd", Bytes("stFnt:fontName=\"Aria"));
        var r = s.Lib.ScanDocuments(TimeSpan.FromDays(30), Now);
        Assert.Equal(4, r.Read);
        Assert.Equal(0, r.Unreadable);
    }
}

internal static class RandomExtensions
{
    public static byte[] GetBytes(this Random r, int n) { var b = new byte[n]; r.NextBytes(b); return b; }
}
