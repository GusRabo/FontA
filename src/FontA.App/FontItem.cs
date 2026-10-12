using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using FontA.Core.Activation;
using FontA.Core.Library;
using FontA.Rendering;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FontA.App;

public sealed class FontItem : INotifyPropertyChanged
{
    public const int ThumbWidth = 296, ThumbHeight = 64;
    private const float ThumbSize = 24;

    /// <summary>Bounds queued thumbnail work; slightly above the renderer count so the pool never idles.</summary>
    private static readonly SemaphoreSlim Gate = new(ThumbnailCache.DefaultParallelism + 2);
    /// <summary>0 = black ink (light theme), 255 = white ink (dark theme).</summary>
    public static byte InkLevel { get; set; }

    private readonly LibraryFont _font;
    private readonly FontLibrary _lib;
    private readonly FontActivator _activator;
    private readonly ThumbnailCache _thumbs;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly long _stamp;
    private string _sample;
    private bool _favorite;
    private bool _alwaysActive;
    private bool _installed;
    private bool _missing;
    private readonly FontInstaller _installer;
    private ImageSource? _thumb;
    private CancellationTokenSource? _loading;

    public FontItem(LibraryFont font, FontLibrary lib, FontActivator activator, ThumbnailCache thumbs, FontInstaller installer, string sample)
    {
        _font = font; _lib = lib; _activator = activator; _thumbs = thumbs; _sample = sample; _favorite = font.IsFavorite; _alwaysActive = font.IsAlwaysActive; _installed = font.IsInstalled; _installer = installer;
        try { _stamp = File.GetLastWriteTimeUtc(font.Info.Path).Ticks; } catch (IOException) { }
    }

    public long Id => _font.Id;
    public FontA.Core.Fonts.FontInfo Info => _font.Info;
    public string Title => $"{_font.Info.Family} · {_font.Info.Style}" + (_font.Info.IsVariable ? " · variable" : "");

    public string Sample
    {
        get => _sample;
        set { _sample = value; if (_loading != null) StartLoad(); }
    }

    /// <summary>Bound by realized cards only (the grid is virtualized), so reading it requests the thumbnail.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_loading == null) StartLoad();
            return _thumb;
        }
    }

    /// <summary>Called when the card scrolls out of view so queued work is dropped.</summary>
    public void CancelThumbnail()
    {
        _loading?.Cancel();
        _loading = null;
    }

    private void StartLoad()
    {
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        var text = _sample;
        var info = _font.Info;
        var req = new ThumbnailRequest(info.Path, info.FaceIndex, info.Family, info.Weight, info.IsItalic,
            _stamp, text, ThumbSize, ThumbWidth, ThumbHeight);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100, cts.Token); // debounce typing / fast scrolling
                await Gate.WaitAsync(cts.Token);
                byte[] mask;
                try
                {
                    cts.Token.ThrowIfCancellationRequested();
                    mask = _thumbs.GetMask(req);
                }
                finally { Gate.Release(); }

                _ui.TryEnqueue(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _thumb = Tint(mask);
                    Raise(nameof(Thumbnail));
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                // DirectWrite reports COM failures (missing or corrupt file) as its own exception types, and nothing
                // observes this fire-and-forget task, so any failure just leaves the card without a thumbnail.
                if (!File.Exists(info.Path)) _ui.TryEnqueue(() => { _missing = true; Raise(nameof(MissingVisibility)); });
            }
        });
    }

    private static WriteableBitmap Tint(byte[] mask)
    {
        var px = new byte[mask.Length * 4];
        byte ink = InkLevel;
        for (int i = 0; i < mask.Length; i++)
        {
            byte a = mask[i];
            byte c = (byte)(ink * a / 255); // premultiplied
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = c;
            px[i * 4 + 3] = a;
        }
        var bmp = new WriteableBitmap(ThumbWidth, ThumbHeight);
        px.CopyTo(0, bmp.PixelBuffer, 0, px.Length);
        bmp.Invalidate();
        return bmp;
    }

    public bool IsFavorite
    {
        get => _favorite;
        set { _favorite = value; _lib.SetFavorite(_font.Id, value); Raise(); }
    }

    /// <summary>Remembered across runs: activated at startup and left active when FontA closes.</summary>
    public bool IsAlwaysActive
    {
        get => _alwaysActive;
        set
        {
            _alwaysActive = value;
            _lib.SetAlwaysActive(_font.Id, value);
            _lib.TouchUsed(_font.Id);
            if (value && !IsActive) IsActive = true;
            Raise();
        }
    }

    /// <summary>Set when the font file turns out to be gone (found while loading the thumbnail).</summary>
    public Microsoft.UI.Xaml.Visibility MissingVisibility =>
        _missing ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public bool IsInstalled => _installed;
    public string InstallLabel => _installed ? "Uninstall" : "Install";
    /// <summary>Installed fonts are managed by Windows, so the session toggles don't apply.</summary>
    public bool CanToggle => !_installed;

    /// <summary>Installs or uninstalls for the current user. Returns an error/notice message, or null on plain success.</summary>
    public string? SetInstalled(bool install)
    {
        try
        {
            string? notice = null;
            if (install)
            {
                _activator.Deactivate(_font.Info.Path); // the installed copy takes over
                var rec = _installer.Install(_font.Info);
                _lib.MarkInstalled(_font.Info.Path, rec.DestPath, rec.ValueName);
            }
            else if (_lib.GetInstall(_font.Info.Path) is { } rec)
            {
                if (!_installer.Uninstall(new InstallRecord(rec.DestPath, rec.ValueName)))
                    notice = "Uninstalled, but Windows still has the file open. It will be removed from your fonts folder later.";
                _lib.MarkUninstalled(_font.Info.Path);
            }
            _installed = install;
            Raise(nameof(IsInstalled)); Raise(nameof(InstallLabel)); Raise(nameof(CanToggle)); Raise(nameof(IsActive));
            return notice;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                    or System.Security.SecurityException)
        {
            return ex.Message;
        }
    }

    public bool IsActive
    {
        get => _installed || _activator.Active.Contains(_font.Info.Path);
        set { if (_installed) return; _lib.TouchUsed(_font.Id); if (value) _activator.Activate(_font.Info.Path); else _activator.Deactivate(_font.Info.Path); Raise(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}
