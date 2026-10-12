using System.Buffers.Binary;
using System.Diagnostics;
using FontA.Core.Activation;
using FontA.Core.Fonts;
using FontA.Core.Library;
using FontA.Core.Security;

namespace FontA.Tests;

/// <summary>Tests for hostile input: malformed fonts, crafted folders, tampered data, and untrusted strings.</summary>
public class SecurityTests
{
    private static readonly string Fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

    // ---------- the font parser ----------

    [Theory]
    [InlineData("arial.ttf")]
    [InlineData("bahnschrift.ttf")]   // variable font: has an fvar table
    [InlineData("calibri.ttf")]       // many GSUB/GPOS features
    public void Mutated_fonts_never_throw_or_hang(string file)
    {
        var original = File.ReadAllBytes(Path.Combine(Fonts, file));
        var rng = new Random(20261012);
        var clock = Stopwatch.StartNew();

        for (int i = 0; i < 600; i++)
        {
            var data = (byte[])original.Clone();
            switch (i % 5)
            {
                case 0:   // damage the header and table directory, where every offset and length lives
                    for (int k = 0; k < 1 + rng.Next(12); k++) data[rng.Next(Math.Min(data.Length, 600))] = (byte)rng.Next(256);
                    break;
                case 1:   // damage anywhere
                    for (int k = 0; k < 1 + rng.Next(40); k++) data[rng.Next(data.Length)] = (byte)rng.Next(256);
                    break;
                case 2:   // truncate
                    data = data[..rng.Next(data.Length)];
                    break;
                case 3:   // turn lengths and offsets in the directory into huge numbers
                    for (int t = 0; t < 12 && 12 + t * 16 + 16 <= data.Length; t++)
                        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12 + t * 16 + (rng.Next(2) == 0 ? 8 : 12)),
                            rng.Next(2) == 0 ? uint.MaxValue : (uint)rng.Next(int.MaxValue));
                    break;
                default:  // claim an absurd number of tables
                    if (data.Length > 6) BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), (ushort)rng.Next(60000, 65536));
                    break;
            }
            var result = OpenTypeReader.Parse(data, "fuzz.ttf");   // must not throw
            Assert.NotNull(result);
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"parsing hostile input took {clock.Elapsed}");
    }

    [Fact]
    public void Collections_with_absurd_headers_are_rejected_cleanly()
    {
        // A 'ttcf' header claiming 4 billion faces, then one whose face offset points far outside the file.
        var huge = new byte[64];
        "ttcf"u8.CopyTo(huge);
        BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(8), uint.MaxValue);
        Assert.Empty(OpenTypeReader.Parse(huge, "x.ttc"));

        var lost = (byte[])huge.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(lost.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(lost.AsSpan(12), uint.MaxValue);
        Assert.Empty(OpenTypeReader.Parse(lost, "x.ttc"));
    }

    [Fact]
    public void A_table_pointing_outside_the_file_makes_the_font_invalid()
    {
        var font = File.ReadAllBytes(Path.Combine(Fonts, "arial.ttf"));
        Assert.Single(OpenTypeReader.Parse(font, "ok.ttf"));              // sanity: the unmodified font parses

        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(12 + 8), (uint)font.Length + 1000);   // first table starts past the end
        Assert.Empty(OpenTypeReader.Parse(font, "bad.ttf"));
    }

    [Fact]
    public void Files_over_the_size_cap_are_refused_by_length_alone()
    {
        var arial = Path.Combine(Fonts, "arial.ttf");
        var length = new FileInfo(arial).Length;

        Assert.Single(OpenTypeReader.Read(arial, maxBytes: length));            // exactly at the cap: accepted
        Assert.Empty(OpenTypeReader.Read(arial, maxBytes: length - 1));         // one byte over: refused, though it is a valid font
        Assert.True(OpenTypeReader.MaxFileBytes is > 1024 * 1024 and <= 1024L * 1024 * 1024);   // the real cap is sane
        Assert.Single(OpenTypeReader.Read(arial));                              // and an ordinary font is nowhere near it
    }

    // ---------- crafted folders ----------

    private static void MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true, RedirectStandardOutput = true, UseShellExecute = false
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "could not create a junction for the test");
    }

    [Fact]
    public void Scanning_never_follows_junctions_loops_or_escapes()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var scan = Path.Combine(root, "scan"); var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(Path.Combine(scan, "real")); Directory.CreateDirectory(outside);
        File.Copy(Path.Combine(Fonts, "arial.ttf"), Path.Combine(scan, "real", "arial.ttf"));
        File.Copy(Path.Combine(Fonts, "verdana.ttf"), Path.Combine(outside, "verdana.ttf"));
        MakeJunction(Path.Combine(scan, "real", "loop"), scan);        // points back up: an endless loop if followed
        MakeJunction(Path.Combine(scan, "escape"), outside);           // points outside the folder that was chosen
        try
        {
            var clock = Stopwatch.StartNew();
            var found = FontLibrary.EnumerateFontFiles(scan).ToList();
            Assert.Equal(new[] { Path.Combine(scan, "real", "arial.ttf") }, found);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));

            using var lib = new FontLibrary(Path.Combine(root, "lib.db"));
            lib.Scan(scan);
            Assert.Equal("Arial", lib.Search().Single().Info.Family);   // nothing from outside, no duplicates from the loop
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path.Combine(scan, "real", "loop"));      // removes the link only, never its target
            Directory.Delete(Path.Combine(scan, "escape"));
            Directory.Delete(root, true);
        }
    }

    // ---------- tampered data and untrusted strings ----------

    [Fact]
    public void Uninstall_refuses_to_touch_anything_outside_the_fonts_folder()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var keyPath = $@"Software\FontA.Tests\{Guid.NewGuid():N}";
        var fontsDir = Path.Combine(dir, "fonts"); Directory.CreateDirectory(fontsDir);
        var victim = Path.Combine(dir, "precious.ttf"); File.WriteAllText(victim, "do not delete me");
        var nested = Path.Combine(fontsDir, "sub"); Directory.CreateDirectory(nested);
        var nestedFile = Path.Combine(nested, "x.ttf"); File.WriteAllText(nestedFile, "also keep");
        var notAFont = Path.Combine(fontsDir, "notes.txt"); File.WriteAllText(notAFont, "keep");
        var installer = new FontInstaller(keyPath, fontsDir, notifySystem: false);
        try
        {
            // A damaged or tampered database could hand back any of these as "the installed copy".
            foreach (var evil in new[] { victim, nestedFile, notAFont, Path.Combine(fontsDir, "..", "precious.ttf"), @"C:\Windows\System32\kernel32.dll" })
                Assert.False(installer.Uninstall(new InstallRecord(evil, "Whatever (TrueType)")));

            Assert.True(File.Exists(victim)); Assert.True(File.Exists(nestedFile)); Assert.True(File.Exists(notAFont));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\FontA.Tests", throwOnMissingSubKey: false);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Font_names_from_inside_the_file_are_sanitised_before_reaching_the_registry()
    {
        Assert.Equal("EvilFont", FontInstaller.SafeValueName("Evil\u0000\u0007\r\nFont\t"));
        Assert.Equal("Font", FontInstaller.SafeValueName("\u0001\u0002   "));
        Assert.Equal(200, FontInstaller.SafeValueName(new string('A', 5000)).Length);
    }

    [Fact]
    public void Hostile_text_is_data_never_sql()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.Copy(Path.Combine(Fonts, "arial.ttf"), Path.Combine(dir, "arial.ttf"));
        using (var lib = new FontLibrary(Path.Combine(dir, "lib.db")))
        {
            lib.Scan(dir);
            var font = lib.Search().Single();
            string[] nasty = { "'; DROP TABLE fonts;--", "\" OR 1=1 --", "Robert'); DELETE FROM tags;--", "%", "_", "\\" };

            foreach (var s in nasty)
            {
                lib.AddTag(font.Id, s);
                var c = lib.CreateCollection(s);
                lib.AddToCollection(c, font.Id);
                _ = lib.Search(s); _ = lib.Search(tag: s); _ = lib.Search(collectionId: c);
                lib.SetSetting("k" + s, s);
                Assert.Equal(s, lib.GetSetting("k" + s));
            }
            Assert.Single(lib.Search());                                  // the fonts table is still there and intact
            Assert.Equal(nasty.Length, lib.TagsFor(font.Id).Count);       // every hostile tag stored verbatim, none executed
            Assert.Equal(nasty.Length, lib.Collections().Count);
            Assert.Empty(lib.Search("'; DROP TABLE fonts;--"));           // searching for it matches nothing, harmlessly
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(dir, true);
    }

    // ---------- the update address ----------

    [Theory]
    [InlineData("https://github.com/GusRabo/FontA", true)]
    [InlineData("https://github.com/GusRabo/FontA/", true)]
    [InlineData("https://GitHub.com/GusRabo/FontA", true)]
    [InlineData("http://github.com/GusRabo/FontA", false)]               // not HTTPS
    [InlineData("https://evil.example.com/GusRabo/FontA", false)]        // not GitHub
    [InlineData("https://github.com.evil.example/GusRabo/FontA", false)] // look-alike host
    [InlineData("https://user:pass@github.com/GusRabo/FontA", false)]    // embedded credentials
    [InlineData("https://github.com:8443/GusRabo/FontA", false)]         // unusual port
    [InlineData("https://github.com/GusRabo/FontA?x=1", false)]
    [InlineData("https://github.com/GusRabo", false)]                    // owner only
    [InlineData("https://github.com/GusRabo/FontA/extra", false)]
    [InlineData("file:///C:/releases", false)]
    [InlineData(@"C:\releases", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_github_https_owner_repo_addresses_are_trusted_for_updates(string? url, bool expected) =>
        Assert.Equal(expected, RepoUrlPolicy.IsTrusted(url));
}
