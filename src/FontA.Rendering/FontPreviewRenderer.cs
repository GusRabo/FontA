using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Vortice.WIC;
using D2DFactory = Vortice.Direct2D1.ID2D1Factory;

namespace FontA.Rendering;

public sealed record PreviewRequest(
    string FontPath,
    string Family,
    string Text,
    float Size,
    int Width,
    int Height,
    IReadOnlyDictionary<string, float>? Axes = null,
    IReadOnlyCollection<string>? Features = null,
    int Weight = 400,
    bool Italic = false);

/// <summary>Renders text with DirectWrite directly from a font file (no install needed), honouring variation axes and OpenType features.</summary>
public sealed class FontPreviewRenderer : IDisposable
{
    private readonly IDWriteFactory5 _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory5>();
    private readonly D2DFactory _d2d = D2D1.D2D1CreateFactory<D2DFactory>();
    private readonly IWICImagingFactory _wic = new();

    /// <summary>Returns premultiplied BGRA pixels, Width*Height*4 bytes. Transparent background.</summary>
    public byte[] Render(PreviewRequest r, Color4? foreground = null)
    {
        using var file = _dwrite.CreateFontFileReference(r.FontPath);
        using var builder = _dwrite.CreateFontSetBuilder();
        builder.AddFontFile(file);
        using var set = builder.CreateFontSet();
        using var collection = _dwrite.CreateFontCollectionFromFontSet(set);

        using var format = _dwrite.CreateTextFormat(r.Family, collection,
            (FontWeight)r.Weight, r.Italic ? FontStyle.Italic : FontStyle.Normal, FontStretch.Normal, r.Size, "en-us");
        using var layout = _dwrite.CreateTextLayout(r.Text, format, r.Width, r.Height);
        var range = new TextRange(0, (uint)r.Text.Length);

        if (r.Axes is { Count: > 0 })
        {
            using var layout4 = layout.QueryInterface<IDWriteTextLayout4>();
            layout4.SetFontAxisValues(r.Axes.Select(a => new FontAxisValue { AxisTag = AxisTag(a.Key), Value = a.Value }).ToArray(), (uint)r.Axes.Count, range);
        }

        if (r.Features is { Count: > 0 })
        {
            using var typography = _dwrite.CreateTypography();
            foreach (var f in r.Features)
                typography.AddFontFeature(new FontFeature { NameTag = FeatureTag(f), Parameter = 1 });
            layout.SetTypography(typography, range);
        }

        using var bitmap = _wic.CreateBitmap((uint)r.Width, (uint)r.Height, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        using var target = _d2d.CreateWicBitmapRenderTarget(bitmap, new RenderTargetProperties());
        using var brush = target.CreateSolidColorBrush(foreground ?? new Color4(0f, 0f, 0f, 1f));
        target.BeginDraw();
        target.Clear(new Color4(0f, 0f, 0f, 0f));
        target.DrawTextLayout(System.Numerics.Vector2.Zero, layout, brush);
        target.EndDraw();

        var pixels = new byte[r.Width * r.Height * 4];
        bitmap.CopyPixels((uint)(r.Width * 4), pixels);
        return pixels;
    }

    private static FontAxisTag AxisTag(string t) => (FontAxisTag)Pack(t);
    private static FontFeatureTag FeatureTag(string t) => (FontFeatureTag)Pack(t);
    private static uint Pack(string t) => (uint)(t[0] | t[1] << 8 | t[2] << 16 | t[3] << 24);

    public void Dispose()
    {
        _wic.Dispose();
        _d2d.Dispose();
        _dwrite.Dispose();
    }
}
