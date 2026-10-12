using FontA.Rendering;

namespace FontA.Tests;

public class RenderingTests
{
    private static readonly string Fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

    private static long Ink(byte[] bgra)
    {
        long sum = 0;
        for (int i = 3; i < bgra.Length; i += 4) sum += bgra[i];
        return sum;
    }

    private static PreviewRequest Req(string file, string family, IReadOnlyDictionary<string, float>? axes = null, IReadOnlyCollection<string>? feats = null, string text = "Hamburgefonstiv fi") =>
        new(Path.Combine(Fonts, file), family, text, 48, 600, 100, axes, feats);

    [Fact]
    public void Renders_ink()
    {
        using var r = new FontPreviewRenderer();
        Assert.True(Ink(r.Render(Req("arial.ttf", "Arial"))) > 0);
    }

    [Fact]
    public void Variable_axis_changes_output()
    {
        using var r = new FontPreviewRenderer();
        var light = Ink(r.Render(Req("bahnschrift.ttf", "Bahnschrift", new Dictionary<string, float> { ["wght"] = 300 })));
        var bold = Ink(r.Render(Req("bahnschrift.ttf", "Bahnschrift", new Dictionary<string, float> { ["wght"] = 700 })));
        Assert.True(bold > light * 1.1, $"light={light} bold={bold}");
    }

    [Fact]
    public void OpenType_feature_changes_output()
    {
        using var r = new FontPreviewRenderer();
        // Segoe UI small caps availability varies; Calibri has smcp/ss01 etc. via 'c2sc'/'smcp'.
        var plain = r.Render(Req("calibri.ttf", "Calibri", text: "Small Caps Test"));
        var smcp = r.Render(Req("calibri.ttf", "Calibri", feats: new[] { "smcp" }, text: "Small Caps Test"));
        Assert.NotEqual(Ink(plain), Ink(smcp));
    }
}

public class ThumbnailCacheTests
{
    private static readonly string Arial = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf");
    private static ThumbnailRequest Req(string text) => new(Arial, 0, "Arial", 400, false, 1, text, 24, 296, 64);

    [Fact]
    public void Caches_in_memory_and_on_disk()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        byte[] first;
        using (var c = new ThumbnailCache(dir))
        {
            first = c.GetMask(Req("Hello"));
            Assert.Equal(296 * 64, first.Length);
            Assert.Contains(first, b => b > 0);
            Assert.Same(first, c.GetMask(Req("Hello")));          // memory hit
            Assert.Equal(1, c.RenderCount);
            c.GetMask(Req("Other"));                               // different text -> new render
            Assert.Equal(2, c.RenderCount);
        }
        using (var c2 = new ThumbnailCache(dir))                   // fresh process-like instance
        {
            Assert.Equal(first, c2.GetMask(Req("Hello")));         // disk hit
            Assert.Equal(0, c2.RenderCount);
            c2.Prune(TimeSpan.FromDays(1));                        // recent files survive
            Assert.Equal(2, Directory.EnumerateFiles(dir, "*.bin", SearchOption.AllDirectories).Count());
        }
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Corrupt_cache_file_is_a_miss()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        using var c = new ThumbnailCache(dir);
        c.GetMask(Req("X"));
        foreach (var f in Directory.EnumerateFiles(dir, "*.bin", SearchOption.AllDirectories)) File.WriteAllBytes(f, new byte[] { 1, 2, 3 });
        using var c2 = new ThumbnailCache(dir);
        Assert.Contains(c2.GetMask(Req("X")), b => b > 0);
        Assert.Equal(1, c2.RenderCount);
    }

    [Fact]
    public void Parallel_requests_match_sequential_results()
    {
        var texts = Enumerable.Range(0, 60).Select(i => $"Sample text {i}").ToList();
        using var seq = new ThumbnailCache(Directory.CreateTempSubdirectory().FullName, parallelism: 1);
        using var par = new ThumbnailCache(Directory.CreateTempSubdirectory().FullName, parallelism: 4);

        var expected = texts.Select(t => seq.GetMask(Req(t))).ToList();
        var actual = new byte[texts.Count][];
        Parallel.For(0, texts.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => actual[i] = par.GetMask(Req(texts[i])));

        for (int i = 0; i < texts.Count; i++) Assert.Equal(expected[i], actual[i]);
        Assert.Equal(texts.Count, par.RenderCount);
    }
}
