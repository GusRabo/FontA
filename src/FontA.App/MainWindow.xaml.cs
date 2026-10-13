using System.Collections.ObjectModel;
using FontA.Core.Activation;
using FontA.Core.Library;
using FontA.Rendering;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FontA.App;

public sealed partial class MainWindow : Window
{
    private readonly FontLibrary _lib;
    private readonly FontActivator _activator = new();
    private readonly FontInstaller _installer = new();
    private readonly FontPreviewRenderer _renderer = new();
    private readonly ThumbnailCache _thumbs;
    private FontItem? _selected;
    private readonly Dictionary<string, float> _axisValues = new();
    private readonly HashSet<string> _features = new();
    private bool _building;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _pruneCts;
    private int _liveIndexed;
    private long _nextLiveRefresh;
    
    private readonly AppSettings _settings;
    private readonly StartupRegistration _startup = new();
    private readonly TrayIcon _tray;
    private readonly FolderWatcher _watcher;
    private SettingsPage? _settingsPage;
    private bool _quitting;
    private readonly ActiveSessionJournal _journal;
    private System.Threading.Timer? _journalTimer;
    private readonly object _journalGate = new();
    private readonly UpdateService _updates = new();
    private bool _restartAfterExit;
    private bool _checkingForUpdates;
    private readonly Queue<(string Path, bool Quiet)> _scanQueue = new();
    private bool _scanQueueRunning;

    public MainWindow()
    {
        InitializeComponent();
        var dir = DataDirectory;
        Directory.CreateDirectory(dir);
        _lib = new FontLibrary(Path.Combine(dir, "library.db"));
        _settings = new AppSettings(_lib);
        _lib.HideSystemFonts = !_settings.ShowSystemFonts;
        _journal = new ActiveSessionJournal(Path.Combine(dir, "active-fonts.txt"));
        // If an earlier run was killed or crashed, Windows still has its fonts switched on: pick them up so they show as active.
        _activator.Adopt(_journal.LoadIfSameSession());
        _activator.Changed += ScheduleJournalSave;
        _thumbs = new ThumbnailCache(Path.Combine(dir, "thumbs"), memoryCapacity: 150);
        _thumbs.Prune(TimeSpan.FromDays(60));
        ShouldStartHidden = _settings.StartHidden && Environment.GetCommandLineArgs().Contains(StartupRegistration.AutoStartArgument);

        var iconPath = AppIcon.SaveTo(Path.Combine(dir, "fonta.ico"));
        if (iconPath != null) AppWindow.SetIcon(iconPath);
        _tray = new TrayIcon(WindowNative.GetWindowHandle(this), "FontA", AppIcon.Create(32));
        _tray.ShowRequested += ShowFromTray;
        _tray.QuitRequested += Quit;
        AppWindow.Closing += OnAppWindowClosing;
        _watcher = new FolderWatcher(DispatcherQueue, folder => EnqueueScan(folder, quiet: true));

        Closed += (_, _) =>
        {
            _wheelHook?.Dispose();
            CancelQuietly(_scanCts); CancelQuietly(_pruneCts); CancelQuietly(_docCts);
            _watcher.Dispose(); _tray.Dispose();
            // An activation still running would keep adding fonts after the clean-up below has looked at what is active.
            CancelQuietly(_activationCts);
            try { _activationTask?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
            if (_settings.DeactivateOnExit) _activator.DeactivateExcept(_lib.AlwaysActivePaths());
            _journal.Save(_activator.Active);   // what is still on after the clean-up (the "Always" fonts), written once more for certain
            lock (_journalGate) { _journalTimer?.Dispose(); _journalTimer = null; }
            // A downloaded update installs once this process is gone: restarting if the person chose "Restart now".
            FontItem.ActivationChanged -= OnItemActivationChanged;
            _updates.ApplyWhenExited(_restartAfterExit);
            _lib.Dispose(); _renderer.Dispose(); _thumbs.Dispose();
        };
        ((FrameworkElement)Content).ActualThemeChanged += (_, _) => Refresh();
        FontItem.ActivationChanged += OnItemActivationChanged;

        // Keep "start with Windows" pointing at wherever this copy of the app now lives (it moves when you update it).
        try { if (_settings.StartWithWindows && Environment.ProcessPath is { } exe) _startup.RefreshPath(exe); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        ApplyTheme(_settings.LightTheme);
        FontItem.ListSize = _settings.ListSize;
        ListSizeSlider.Value = FontItem.ListSize;
        ListSizeText.Text = FontItem.ListSize + "px";
        RunUnusedCheck(manual: false, refresh: false); // before activating, so stale always-active fonts stay off
        var unusedTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        unusedTimer.Tick += (_, _) => RunUnusedCheck(manual: false, refresh: true);
        unusedTimer.Start();
        RebuildNav();
        Refresh();
        _ = ActivateAlwaysActiveAsync();
        _ = PruneMissingInBackground();
        RegisterShortcuts();
        SetUpWheel();
        ((FrameworkElement)Content).Loaded += (_, _) =>
        {
            UpdateScale();
            Content.XamlRoot.Changed += (_, _) => UpdateScale();
        };

        UpdateTrayVisibility();
        StartUpdateChecks();
        _watcher.Sync(_lib.Folders(), _settings.WatchFolders);
        if (_settings.RescanOnStartup)
            foreach (var folder in _lib.Folders())
                if (Directory.Exists(folder)) EnqueueScan(folder, quiet: false); // an unplugged drive is skipped, never "emptied"
    }

    // ---- what the settings page talks to ----
    /// <summary>
    /// Where the library, thumbnails and icon live. Debug builds honour FONTA_DATA_DIR so tests can use a throwaway
    /// library; release builds always use the per-user folder, so an environment variable can't redirect your data.
    /// </summary>
    public static string DataDirectory =>
#if DEBUG
        Environment.GetEnvironmentVariable("FONTA_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FontA");
#else
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FontA");
#endif

    public UpdateService Updates => _updates;
    public AppSettings Settings => _settings;
    public FontLibrary Library => _lib;

    /// <summary>True when Windows started FontA at sign-in and the person asked for it to stay out of sight.</summary>
    public bool ShouldStartHidden { get; }

    /// <summary>Rewrites the journal soon after fonts were switched, grouping a burst of changes into one write.</summary>
    private void ScheduleJournalSave()
    {
        lock (_journalGate)
        {
            _journalTimer ??= new System.Threading.Timer(_ => _journal.Save(_activator.Active), null, Timeout.Infinite, Timeout.Infinite);
            _journalTimer.Change(500, Timeout.Infinite);
        }
    }

    // A scan or check can finish (and dispose its token source) at the very moment the window closes.
    private static void CancelQuietly(CancellationTokenSource? cts)
    {
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Raised when the list of remembered font folders changes.</summary>
    public event Action? FoldersChanged;

    public void ShowFromTray()
    {
        AppWindow.Show();
        (AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.Restore();
        Activate();
    }

    /// <summary>Really exits, even when "hide to tray" is on.</summary>
    public void Quit()
    {
        _quitting = true;
        Close();
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        // Never hold up Windows signing out or shutting down.
        if (_quitting || _tray.SessionEnding || !_settings.HideToTray) return;
        args.Cancel = true;
        sender.Hide();
    }

    private void UpdateTrayVisibility()
    {
        // Start-hidden needs the icon too, otherwise there would be no way back to a hidden window.
        if (_settings.HideToTray || _settings.StartHidden) _tray.Show(); else _tray.Hide();
    }

    /// <summary>Applies the start-with-Windows, tray and folder-watching settings. Returns an error message, or null.</summary>
    public string? ApplyBehaviorSettings()
    {
        string? error = null;
        try
        {
            if (_settings.StartWithWindows)
            {
                if (Environment.ProcessPath is { } exe) _startup.Enable(exe);
            }
            else _startup.Disable();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = "Couldn't change the Windows startup setting: " + ex.Message;
        }
        UpdateTrayVisibility();
        _watcher.Sync(_lib.Folders(), _settings.WatchFolders);
        return error;
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public async Task AddFolderViaPickerAsync()
    {
        var path = await PickFolderAsync();
        if (path == null) return;
        var folder = new { Path = path };

        _lib.AddFolder(folder.Path);            // remembered: rescanned at startup and watched for changes
        _watcher.Sync(_lib.Folders(), _settings.WatchFolders);
        FoldersChanged?.Invoke();
        EnqueueScan(folder.Path, quiet: false);
    }

    public void RemoveFolder(string path)
    {
        _lib.RemoveFolder(path);
        _watcher.Sync(_lib.Folders(), _settings.WatchFolders);
        FoldersChanged?.Invoke();
        RebuildNav();
        Refresh();
    }

    // ---- updates (GitHub Releases through Velopack) ----
    private void StartUpdateChecks()
    {
        if (!_updates.IsReady) return;
        var timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        timer.Tick += async (_, _) => { if (_settings.AutoUpdate) await CheckForUpdatesAsync(manual: false); };
        timer.Start();
        _ = CheckSoonAfterStartAsync();
    }

    private async Task CheckSoonAfterStartAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(15)); // keep the first seconds free for loading the library
        if (_settings.AutoUpdate) await CheckForUpdatesAsync(manual: false);
    }

    /// <summary>Checks for, and downloads, a newer version. Shows the "ready" banner when one is waiting.</summary>
    public async Task<UpdateResult> CheckForUpdatesAsync(bool manual)
    {
        if (_checkingForUpdates) return new UpdateResult(UpdateStatus.Failed, "Already checking for updates.");
        _checkingForUpdates = true;
        try
        {
            var result = await _updates.CheckAndDownloadAsync();
            if (result.Status == UpdateStatus.Downloaded) ShowUpdateReady(result.Version);
            return result;
        }
        finally { _checkingForUpdates = false; }
    }

    private void ShowUpdateReady(string? version)
    {
        UpdateBar.Title = version == null ? "A FontA update is ready" : $"FontA {version} is ready";
        UpdateBar.Message = "Restart to finish updating. Otherwise it installs the next time you quit FontA.";
        UpdateBar.IsOpen = true;
    }

    private void OnRestartForUpdate(object s, RoutedEventArgs e)
    {
        _restartAfterExit = true;
        Quit();
    }

    private void OnOpenSettings(object s, RoutedEventArgs e)
    {
        // Settings show inside this window, in place of the font list. The page is built the first time it is wanted.
        if (_settingsPage == null)
        {
            _settingsPage = new SettingsPage(this);
            SettingsHost.Children.Add(_settingsPage);
        }
        if (SettingsOpen) { CloseSettings(); return; }       // the gear again goes back
        SettingsHost.Visibility = Visibility.Visible;
    }

    public bool SettingsOpen => SettingsHost.Visibility == Visibility.Visible;

    /// <summary>Back from the settings to the fonts.</summary>
    public void CloseSettings()
    {
        SettingsHost.Visibility = Visibility.Collapsed;
        FontGrid.Focus(FocusState.Pointer);          // keyboard focus returns to the list without drawing a focus frame
    }

    /// <summary>Checks every indexed file still exists, without blocking startup, and refreshes if anything changed.</summary>
    private async Task PruneMissingInBackground()
    {
        using var cts = _pruneCts = new CancellationTokenSource();
        PruneResult result;
        try { result = await Task.Run(() => _lib.PruneMissing(cts.Token)); }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { return; }
        finally { _pruneCts = null; } // the source is disposed when this method ends, so it must not be cancelled afterwards
        if (cts.IsCancellationRequested || result.Removed + result.Rehomed == 0) return;
        RebuildNav();
        Refresh();
    }

    private void OnSearchChanged(AutoSuggestBox s, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (SettingsOpen && e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) CloseSettings();   // searching goes back to the fonts
        Refresh();
    }
    private void OnSampleChanged(object s, TextChangedEventArgs e) { foreach (var i in AllItems) i.Sample = SampleBox.Text; RenderPreview(); }

    private async void OnAddFolder(object s, RoutedEventArgs e) => await AddFolderViaPickerAsync();

    // ---- list size and colour scheme ----
    private DispatcherTimer? _sizeTimer;

    private void OnListSizeChanged(object s, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ListSizeText == null) return;   // raised while the window is still being built
        int size = (int)Math.Round(e.NewValue);
        ListSizeText.Text = size + "px";
        // Redrawing every visible row on each step of a drag would stutter: wait for the slider to settle.
        _sizeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _sizeTimer.Tick -= OnSizeSettled;
        _sizeTimer.Tick += OnSizeSettled;
        _sizeTimer.Stop(); _sizeTimer.Start();
    }

    private void OnSizeSettled(object? s, object e)
    {
        _sizeTimer?.Stop();
        int size = (int)Math.Round(ListSizeSlider.Value);
        if (size == FontItem.ListSize) return;
        FontItem.ListSize = size;
        FontItem.ClearBitmapPool();
        _settings.ListSize = size;
        foreach (var item in AllItems) item.ApplyListSize();
    }

    /// <summary>Draws thumbnails at the display's real pixel density (also when the window moves to another monitor).</summary>
    private void UpdateScale()
    {
        double s = Content.XamlRoot?.RasterizationScale ?? 1;
        s = Math.Round(Math.Clamp(s, 1, 3) * 4) / 4;      // quarter steps: fewer distinct cache entries
        if (Math.Abs(s - FontItem.Scale) < 0.01) return;
        FontItem.Scale = s;
        FontItem.ClearBitmapPool();
        foreach (var item in AllItems) item.ApplyListSize();
    }

    private void OnResetListSize(object s, RoutedEventArgs e) => ListSizeSlider.Value = FontItem.DefaultListSize;

    private void OnToggleTheme(object s, RoutedEventArgs e)
    {
        var root = (FrameworkElement)Content;
        bool toLight = root.ActualTheme == ElementTheme.Dark;
        _settings.LightTheme = toLight;
        ApplyTheme(toLight);   // the grid redraws through ActualThemeChanged
    }

    /// <summary>Dark is the default; the title bar follows so there is no white strip above a dark window.</summary>
    private void ApplyTheme(bool light)
    {
        ((FrameworkElement)Content).RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        var back = light ? Windows.UI.Color.FromArgb(255, 243, 243, 243) : Windows.UI.Color.FromArgb(255, 32, 32, 32);
        var fore = light ? Windows.UI.Color.FromArgb(255, 0, 0, 0) : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        bar.BackgroundColor = bar.InactiveBackgroundColor = bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = back;
        bar.ForegroundColor = bar.InactiveForegroundColor = bar.ButtonForegroundColor = bar.ButtonInactiveForegroundColor = fore;
    }

    private void OnFavoriteClick(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is FontItem f) f.IsFavorite = !f.IsFavorite;
        RefreshNavStates();
    }

    private void OnAlwaysClick(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is FontItem { CanToggle: true } f) f.IsAlwaysActive = !f.IsAlwaysActive;
        RefreshNavStates();
    }

    private async void OnInstallSelected(object s, RoutedEventArgs e)
    {
        if (_selected != null) await ToggleInstallAsync(_selected);
    }

    /// <summary>Shows newly indexed fonts while a scan runs, backing off in proportion to how long a refresh takes.</summary>
    private void LiveRefresh(int indexed)
    {
        long now = Environment.TickCount64;
        if (indexed <= _liveIndexed || now < _nextLiveRefresh) return;
        _liveIndexed = indexed;
        Refresh(incremental: true);
        _nextLiveRefresh = now + Math.Max(750, (Environment.TickCount64 - now) * 5);
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FontItem item) await ToggleInstallAsync(item);
    }

    private async Task ToggleInstallAsync(FontItem item)
    {
        bool install = !item.IsInstalled;
        if (install)
        {
            var dlg = new ContentDialog
            {
                Title = $"Install {item.Info.Family}?",
                Content = "This copies the font file to your user fonts folder and registers it for your Windows account, " +
                          "so it stays available in all apps after sign-out and restart, even without FontA running. " +
                          "No administrator rights are needed, and you can uninstall it here at any time.",
                PrimaryButtonText = "Install",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        }

        var message = item.SetInstalled(install);
        if (item == _selected) InstallButton.Content = item.InstallLabel;
        // An uninstalled font leaves the Installed list, and one whose original file was already gone disappears entirely.
        if (_nav.Kind == NavKind.Installed || !File.Exists(item.Info.Path)) { RebuildNav(); Refresh(); }
        if (message == null) return;
        await new ContentDialog
        {
            Title = install ? "Couldn't install font" : "Font uninstalled",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        }.ShowAsync();
    }

    private void OnCancelScan(object s, RoutedEventArgs e)
    {
        ScanCancel.IsEnabled = false;
        ScanStatus.Text = "Cancelling…";
        _scanQueue.Clear();
        CancelQuietly(_scanCts);
        CancelQuietly(_activationCts);
    }

    /// <summary>Scans run one at a time. Folder-change scans are quiet; scans the person asked for show progress.</summary>
    /// <summary>Applies the "show fonts installed in Windows" setting to every view.</summary>
    public void ApplySystemFontVisibility()
    {
        _lib.HideSystemFonts = !_settings.ShowSystemFonts;
        if (_nav.Kind == NavKind.Folder && _nav.Key == FolderGrouping.SystemKey) _nav = new(NavKind.All, "All");
        RebuildNav();
        Refresh();
    }

    private void EnqueueScan(string path, bool quiet)
    {
        // No point reading the Windows fonts folder while its fonts are hidden.
        if (_lib.HideSystemFonts && FolderGrouping.IsSystemFolder(path)) return;
        if (_scanQueue.Any(q => string.Equals(q.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        _scanQueue.Enqueue((path, quiet));
        if (!_scanQueueRunning) _ = ProcessScanQueue();
    }

    private async Task ProcessScanQueue()
    {
        _scanQueueRunning = true;
        try
        {
            while (_scanQueue.Count > 0)
            {
                var (path, quiet) = _scanQueue.Dequeue();
                if (Directory.Exists(path)) await RunScan(path, quiet);
            }
        }
        finally { _scanQueueRunning = false; }
    }

    private async Task RunScan(string path, bool quiet)
    {
        _liveIndexed = 0;
        _nextLiveRefresh = 0;
        using var cts = _scanCts = new CancellationTokenSource();
        if (!quiet)
        {
            AddFolderButton.IsEnabled = false;
            ScanCancel.IsEnabled = true;
            ScanBar.IsIndeterminate = true;
            ScanStatus.Text = "Looking for fonts…";
            ScanPanel.Visibility = Visibility.Visible;
        }

        var progress = new Progress<ScanProgress>(p =>
        {
            if (cts.IsCancellationRequested || quiet) return;
            ScanBar.IsIndeterminate = p.Total == 0;
            ScanBar.Maximum = Math.Max(p.Total, 1);
            ScanBar.Value = p.Done;
            LiveRefresh(p.Indexed);
            ScanStatus.Text = $"Scanning {p.Done:N0} of {p.Total:N0} — {Path.GetFileName(p.CurrentFile)}";
        });

        ScanResult? result = null;
        string? error = null;
        try { result = await Task.Run(() => _lib.Scan(path, progress, cts.Token)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            error = ex.Message;
        }

        _scanCts = null;
        bool changed = result is { } r && (r.Indexed + r.Removed + r.Rehomed > 0);
        if (!quiet || changed)
        {
            RebuildNav();
            Refresh();
        }
        if (quiet) return;

        AddFolderButton.IsEnabled = true;
        ScanBar.IsIndeterminate = false;
        ScanBar.Value = ScanBar.Maximum;
        ScanCancel.IsEnabled = false;
        ScanStatus.Text = error != null ? $"Scan failed: {error}"
            : result!.Cancelled ? $"Scan cancelled — {result.Indexed:N0} fonts indexed so far"
            : $"Done — {result.Indexed:N0} new or updated, {result.Unchanged:N0} unchanged, {result.Removed:N0} removed"
              + (result.Rehomed > 0 ? $", {result.Rehomed:N0} kept (installed in Windows)" : "");
        _ = HideScanPanelLater(changed || error != null ? 4000 : 1500);
    }

    private async Task HideScanPanelLater(int milliseconds)
    {
        await Task.Delay(milliseconds);
        if (_scanCts == null && _scanQueue.Count == 0) ScanPanel.Visibility = Visibility.Collapsed;
    }

    private void OnFontSelected(object s, SelectionChangedEventArgs e)
    {
        _selected = FontGrid.SelectedItem as FontItem;
        CollectionsButton.IsEnabled = NewTagBox.IsEnabled = _selected != null;
        DetailsPane.Visibility = _selected == null ? Visibility.Collapsed : Visibility.Visible;
        if (_selected == null) return;
        InstallButton.Content = _selected.InstallLabel;
        _lib.TouchUsed(_selected.Id); // previewing a font counts as using it
        RebuildOrganize();
        var info = _selected.Info;
        _building = true;
        PreviewTitle.Text = _selected.Title;
        _axisValues.Clear(); _features.Clear();
        AxesPanel.Children.Clear();
        foreach (var a in info.Axes)
        {
            _axisValues[a.Tag] = a.Default;
            var slider = new Slider { Header = a.Tag, Minimum = a.Min, Maximum = a.Max, Value = a.Default, StepFrequency = (a.Max - a.Min) / 200, Tag = a.Tag };
            slider.ValueChanged += (_, ev) => { _axisValues[a.Tag] = (float)ev.NewValue; RenderPreview(); };
            AxesPanel.Children.Add(slider);
        }
        FeaturesPanel.Items.Clear();
        foreach (var f in info.Features)
        {
            var cb = new CheckBox { Content = f, MinWidth = 80 };
            cb.Checked += (_, _) => { _features.Add(f); RenderPreview(); };
            cb.Unchecked += (_, _) => { _features.Remove(f); RenderPreview(); };
            FeaturesPanel.Items.Add(cb);
        }
        _building = false;
        RenderPreview();
    }

    private void OnPreviewParamChanged(object s, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => RenderPreview();

    private void RenderPreview()
    {
        if (_building || _selected == null) return;
        var info = _selected.Info;
        double scale = FontItem.Scale;       // sharp on high-density displays
        int w = (int)Math.Round(308 * scale), h = (int)Math.Round(240 * scale);
        bool dark = ((FrameworkElement)Content).ActualTheme == ElementTheme.Dark;
        var ink = dark ? new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f) : new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f);
        var bytes = _renderer.Render(new PreviewRequest(info.Path, info.Family, SampleBox.Text, (float)(SizeSlider.Value * scale), w, h,
            _axisValues.Count > 0 ? new Dictionary<string, float>(_axisValues) : null,
            _features.Count > 0 ? _features.ToList() : null, info.Weight, info.IsItalic), ink);
        var bmp = new WriteableBitmap(w, h);
        using (var st = bmp.PixelBuffer.AsStream()) st.Write(bytes, 0, bytes.Length);
        bmp.Invalidate();
        PreviewImage.Source = bmp;
    }

    // ---- sidebar ----
    private async void OnNewCollection(object s, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "Collection name" };
        var dlg = new ContentDialog
        {
            Title = "New collection", Content = box, PrimaryButtonText = "Create", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
        var name = box.Text.Trim();
        _nav = new(NavKind.Collection, name, _lib.CreateCollection(name));
        RebuildNav();
        Refresh();
        RebuildOrganize();
    }

    // ---- deactivate unused fonts ----
    private List<DeactivatedFont> _lastDeactivated = new();

    /// <summary>Called by the settings page when the unused-fonts options change; turning it on starts the clocks.</summary>
    public void OnUnusedSettingsChanged()
    {
        if (_settings.UnusedEnabled) RunUnusedCheck(manual: false, refresh: true);
    }

    public void CheckUnusedNow() => RunUnusedCheck(manual: true, refresh: true);

    private async void RunUnusedCheck(bool manual, bool refresh) => await RunUnusedCheckAsync(manual, refresh);

    private bool _unusedBusy;

    private async Task RunUnusedCheckAsync(bool manual, bool refresh)
    {
        if (!manual && !_settings.UnusedEnabled) return;
        if (_unusedBusy) return;
        _unusedBusy = true;
        try
        {
            int days = _settings.UnusedDays;
            var now = DateTimeOffset.UtcNow;

            // Read the documents first: a font a document uses counts as used. Only a check that looked at everything it
            // was supposed to can say a font is unused, so a missing folder or online-only file pauses deactivating.
            if (_settings.DocumentScanEnabled)
            {
                var scan = await ScanDocumentsAsync(now);
                if (scan == null) return;
                if (!scan.Complete)
                {
                    UnusedBar.Title = "Unused-font check paused";
                    UnusedBar.Message = "Nothing was deactivated because " + DescribeGaps(scan) + " Fix that, or turn off document checking, and check again.";
                    UndoUnusedButton.Visibility = Visibility.Collapsed;
                    UnusedBar.IsOpen = true;
                    return;
                }
            }

            var done = UnusedFontPolicy.Apply(_lib, _activator, TimeSpan.FromDays(days), now);
            _lastDeactivated = done.ToList();

            if (done.Count == 0)
            {
                if (!manual) return;
                UnusedBar.Title = "Nothing to deactivate";
                UnusedBar.Message = $"Every active font was used within the last {days} days.";
                UndoUnusedButton.Visibility = Visibility.Collapsed;
                UnusedBar.IsOpen = true;
                return;
            }

            var names = done.Select(d => Path.GetFileNameWithoutExtension(d.Path)).Take(3).ToList();
            string list = string.Join(", ", names) + (done.Count > 3 ? $" and {done.Count - 3} more" : "");
            UnusedBar.Title = done.Count == 1 ? "1 unused font deactivated" : $"{done.Count} unused fonts deactivated";
            UnusedBar.Message = $"Not used in {days} days{(_settings.DocumentScanEnabled ? " (in FontA or your documents)" : "")}: {list}. Pin a font (Always) to keep it.";
            UndoUnusedButton.Visibility = Visibility.Visible;
            UnusedBar.IsOpen = true;
            if (refresh) { RebuildNav(); Refresh(); }
        }
        finally { _unusedBusy = false; }
    }

    private static string DescribeGaps(DocumentScanResult r)
    {
        var parts = new List<string>();
        if (r.MissingFolders > 0) parts.Add(r.MissingFolders == 1 ? "a document folder isn't available right now" : $"{r.MissingFolders} document folders aren't available right now");
        if (r.OnlineOnly > 0) parts.Add(r.OnlineOnly == 1 ? "a recent document is only in the cloud and wasn't downloaded to check" : $"{r.OnlineOnly} recent documents are only in the cloud and weren't downloaded to check");
        if (r.Cancelled) parts.Add("the check was interrupted");
        return string.Join(" and ", parts) + ".";
    }

    // ---- fonts used by documents ----
    private CancellationTokenSource? _docCts;

    /// <summary>What the settings page shows under "Documents".</summary>
    public string DocumentStatus { get; private set; } = "";
    public event Action? DocumentStatusChanged;

    /// <summary>Reads the documents and records the fonts they use. Returns null when another read was already running or was cancelled.</summary>
    private async Task<DocumentScanResult?> ScanDocumentsAsync(DateTimeOffset now)
    {
        CancelQuietly(_docCts);
        var cts = _docCts = new CancellationTokenSource();
        var lookback = TimeSpan.FromDays(_settings.UnusedDays);
        bool useRecent = _settings.UseRecentDocuments;
        DocumentStatus = "Reading documents…";
        DocumentStatusChanged?.Invoke();
        DocumentScanResult result;
        try
        {
            result = await Task.Run(() =>
            {
                var recent = useRecent ? RecentDocuments.Read(RecentDocuments.DefaultFolder, now - lookback) : null;
                return _lib.ScanDocuments(lookback, now, recent, null, cts.Token);
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DocumentStatus = "Couldn't read the documents: " + ex.Message;
            DocumentStatusChanged?.Invoke();
            return null;
        }

        if (cts.IsCancellationRequested && !ReferenceEquals(cts, _docCts)) return null;   // replaced by a newer read
        if (result.Complete) _settings.DocumentsCheckedAt = now.ToUnixTimeSeconds();
        DocumentStatus = result.Complete
            ? $"Last checked {DateTimeOffset.Now:d MMM HH:mm}: {result.Read + result.Unchanged} documents, {result.FontsMarked} font uses found."
            : "Couldn't check everything: " + DescribeGaps(result);
        DocumentStatusChanged?.Invoke();
        if (result.FontsMarked > 0) { RebuildNav(); Refresh(); }
        return result;
    }

    /// <summary>Called by the settings page when document checking is switched on or its folders change.</summary>
    public async void OnDocumentSettingsChanged()
    {
        if (!_settings.DocumentScanEnabled) { CancelQuietly(_docCts); DocumentStatus = ""; DocumentStatusChanged?.Invoke(); return; }
        await ScanDocumentsAsync(DateTimeOffset.UtcNow);
    }

    public async Task AddDocumentFolderViaPickerAsync()
    {
        var path = await PickFolderAsync();
        if (path == null) return;
        _lib.AddDocumentFolder(path);
        FoldersChanged?.Invoke();
        OnDocumentSettingsChanged();
    }

    public void RemoveDocumentFolder(string path)
    {
        _lib.RemoveDocumentFolder(path);
        FoldersChanged?.Invoke();
        OnDocumentSettingsChanged();
    }

    private void OnUndoUnused(object s, RoutedEventArgs e)
    {
        UnusedFontPolicy.Undo(_lib, _activator, _lastDeactivated, DateTimeOffset.UtcNow);
        _lastDeactivated = new();
        UnusedBar.IsOpen = false;
        RebuildNav();
        Refresh();
    }

    // ---- keyboard shortcuts ----
    private sealed record Shortcut(Windows.System.VirtualKey Key, Windows.System.VirtualKeyModifiers Mods,
                                   string Label, string Description, Func<bool> Run);

    private List<Shortcut> _shortcuts = new();
    private bool _helpOpen;

    private FontItem? SelectedFont => FontGrid.SelectedItem as FontItem;

    /// <summary>One table drives both the key handling and the F1 help, so they can't drift apart.</summary>
    private void RegisterShortcuts()
    {
        const Windows.System.VirtualKeyModifiers Ctrl = Windows.System.VirtualKeyModifiers.Control;
        const Windows.System.VirtualKeyModifiers CtrlShift = Ctrl | Windows.System.VirtualKeyModifiers.Shift;
        const Windows.System.VirtualKeyModifiers None = Windows.System.VirtualKeyModifiers.None;
        var K = (Func<char, Windows.System.VirtualKey>)(ch => (Windows.System.VirtualKey)char.ToUpperInvariant(ch));
        var Vk = Windows.System.VirtualKey.Escape;

        bool OnSelected(Action<FontItem> act) { if (SelectedFont is { } f) { act(f); return true; } return false; }

        _shortcuts = new List<Shortcut>
        {
            new(K('F'), Ctrl, "Ctrl+F", "Search fonts", () => { SearchBox.Focus(FocusState.Programmatic); return true; }),
            new(K('L'), Ctrl, "Ctrl+L", "Edit the sample text", () => { SampleBox.Focus(FocusState.Programmatic); SampleBox.SelectAll(); return true; }),
            new(K('O'), Ctrl, "Ctrl+O", "Add a folder of fonts", () => { if (!AddFolderButton.IsEnabled) return false; OnAddFolder(this, null!); return true; }),
            new((Windows.System.VirtualKey)188, Ctrl, "Ctrl+,", "Open settings", () => { OnOpenSettings(this, null!); return true; }),
            new(K('N'), Ctrl, "Ctrl+N", "New collection", () => { OnNewCollection(this, null!); return true; }),
            new(K('D'), Ctrl, "Ctrl+D", "Favorite / unfavorite the selected font", () => OnSelected(f => f.IsFavorite = !f.IsFavorite)),
            new(Windows.System.VirtualKey.Enter, Ctrl, "Ctrl+Enter", "Activate / deactivate the selected font",
                () => OnSelected(f => { if (f.CanToggle) f.IsActive = !f.IsActive; })),
            new(K('A'), CtrlShift, "Ctrl+Shift+A", "Toggle \"always active\" for the selected font",
                () => OnSelected(f => { if (f.CanToggle) f.IsAlwaysActive = !f.IsAlwaysActive; })),
            new(K('I'), CtrlShift, "Ctrl+Shift+I", "Install / uninstall the selected font for your account",
                () => OnSelected(f => _ = ToggleInstallAsync(f))),
            new((Windows.System.VirtualKey)187, Ctrl, "Ctrl+=", "Larger preview text", () => { ListSizeSlider.Value = Math.Min(ListSizeSlider.Maximum, ListSizeSlider.Value + 4); return true; }),
            new((Windows.System.VirtualKey)189, Ctrl, "Ctrl+-", "Smaller preview text", () => { ListSizeSlider.Value = Math.Max(ListSizeSlider.Minimum, ListSizeSlider.Value - 4); return true; }),
            new(Windows.System.VirtualKey.Number0, Ctrl, "Ctrl+0", "Reset preview size", () => { ListSizeSlider.Value = FontItem.DefaultListSize; return true; }),
            new(Windows.System.VirtualKey.Number1, Ctrl, "Ctrl+1", "Show all fonts", () => SelectNav(NavKind.All)),
            new(Windows.System.VirtualKey.Number2, Ctrl, "Ctrl+2", "Show favorites", () => SelectNav(NavKind.Favorites)),
            new(Windows.System.VirtualKey.Number3, Ctrl, "Ctrl+3", "Show always-active fonts", () => SelectNav(NavKind.AlwaysActive)),
            new(Windows.System.VirtualKey.Number4, Ctrl, "Ctrl+4", "Show installed fonts", () => SelectNav(NavKind.Installed)),
            new(Windows.System.VirtualKey.Number5, Ctrl, "Ctrl+5", "Show recently used fonts", () => SelectNav(NavKind.Recent)),
            new(Windows.System.VirtualKey.Number6, Ctrl, "Ctrl+6", "Show active fonts", () => SelectNav(NavKind.Active)),
            new(Windows.System.VirtualKey.Number7, Ctrl, "Ctrl+7", "Show inactive fonts", () => SelectNav(NavKind.Inactive)),
            new(Windows.System.VirtualKey.F2, None, "F2", "Rename the selected collection",
                () => { if (_nav.Kind != NavKind.Collection) return false; _ = RenameCollectionAsync(_nav.Id, _nav.Label); return true; }),
            new(Vk, None, "Esc", "Close the settings, cancel a running scan, otherwise clear the search", () =>
            {
                if (SettingsOpen) { CloseSettings(); return true; }
                if (_scanCts != null) { OnCancelScan(this, null!); return true; }
                if (string.IsNullOrEmpty(SearchBox.Text)) return false;
                SearchBox.Text = "";
                return true;
            }),
            new(Windows.System.VirtualKey.F1, None, "F1", "Show this list", () => { _ = ShowShortcutsAsync(); return true; }),
        };

        var root = (UIElement)Content;
        foreach (var s in _shortcuts)
        {
            var accel = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = s.Key, Modifiers = s.Mods };
            accel.Invoked += (_, a) => a.Handled = s.Run(); // unhandled keys (e.g. Esc with nothing to do) keep their normal meaning
            root.KeyboardAccelerators.Add(accel);
        }
    }

    private bool SelectNav(NavKind kind)
    {
        var item = NavList.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag is NavEntry { Kind: var k } && k == kind);
        if (item == null) return false;
        NavList.SelectedItem = item;
        return true;
    }

    private async Task ShowShortcutsAsync()
    {
        if (_helpOpen) return;
        _helpOpen = true;
        try
        {
            var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < _shortcuts.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var key = new TextBlock { Text = _shortcuts[i].Label, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
                var desc = new TextBlock { Text = _shortcuts[i].Description, TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(key, i); Grid.SetRow(desc, i); Grid.SetColumn(desc, 1);
                grid.Children.Add(key); grid.Children.Add(desc);
            }
            await new ContentDialog
            {
                Title = "Keyboard shortcuts",
                Content = new ScrollViewer { Content = grid, MaxHeight = 460 },
                CloseButtonText = "Close",
                XamlRoot = Content.XamlRoot
            }.ShowAsync();
        }
        finally { _helpOpen = false; }
    }

    private async Task RenameCollectionAsync(long id, string current)
    {
        var box = new TextBox { Text = current, PlaceholderText = "Collection name" };
        var error = new TextBlock
        {
            Text = "Enter a name that no other collection uses.",
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Visibility = Visibility.Collapsed
        };
        var dlg = new ContentDialog
        {
            Title = "Rename collection",
            Content = new StackPanel { Spacing = 8, Children = { box, error } },
            PrimaryButtonText = "Rename", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot
        };
        // Keep the dialog open on a blank or duplicate name so the person can fix it instead of starting over.
        dlg.PrimaryButtonClick += (_, a) =>
        {
            if (_lib.RenameCollection(id, box.Text)) return;
            a.Cancel = true;
            error.Visibility = Visibility.Visible;
        };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var name = box.Text.Trim();
        if (_nav.Kind == NavKind.Collection && _nav.Id == id) _nav = _nav with { Label = name };
        RebuildNav();
        RebuildOrganize(); // the "Collections" menu in the preview pane lists names too
    }

    // ---- per-font organisation (preview pane) ----
    private void RebuildOrganize()
    {
        if (_selected == null) return;
        var fontId = _selected.Id;
        var member = _lib.CollectionsFor(fontId);
        var flyout = new MenuFlyout();
        foreach (var (id, name) in _lib.Collections())
        {
            var toggle = new ToggleMenuFlyoutItem { Text = name, IsChecked = member.Contains(id) };
            toggle.Click += (_, _) =>
            {
                if (toggle.IsChecked) _lib.AddToCollection(id, fontId); else _lib.RemoveFromCollection(id, fontId);
                if (_nav.Kind == NavKind.Collection) Refresh();
            };
            flyout.Items.Add(toggle);
        }
        if (flyout.Items.Count == 0) flyout.Items.Add(new MenuFlyoutItem { Text = "No collections yet", IsEnabled = false });
        CollectionsButton.Flyout = flyout;

        TagsPanel.Items.Clear();
        foreach (var tag in _lib.TagsFor(fontId))
        {
            var chip = new Button { Content = tag + "  ✕", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2) };
            chip.Click += (_, _) =>
            {
                _lib.RemoveTag(fontId, tag);
                RebuildOrganize();
                RebuildNav();
                if (_nav.Kind == NavKind.Tag) Refresh();
            };
            TagsPanel.Items.Add(chip);
        }
    }

    private void OnNewTagKeyDown(object s, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || _selected == null || string.IsNullOrWhiteSpace(NewTagBox.Text)) return;
        _lib.AddTag(_selected.Id, NewTagBox.Text);
        NewTagBox.Text = "";
        RebuildOrganize();
        RebuildNav();
    }
}
