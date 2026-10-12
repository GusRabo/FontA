using FontA.Core.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FontA.App;

public sealed partial class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private readonly AppSettings _settings;
    private bool _loading = true; // XAML and Load() raise change events; ignore them until the window is set up

    public SettingsWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _settings = main.Settings;
        PlaceOver(main);
        // Windows may put a new window at its own default spot when it first shows: place it again once it is up.
        bool placed = false;
        Activated += (_, _) => { if (!placed) { placed = true; PlaceOver(main); } };

        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var how = _main.Updates.IsReady ? "installed" : "not installed (development build)";
        VersionText.Text = $"FontA {version}  ·  {how}  ·  library layout {FontLibrary.SchemaVersion}";

        LoadValues();
        RebuildFolders();
        main.FoldersChanged += RebuildFolders;
        Closed += (_, _) => main.FoldersChanged -= RebuildFolders;
        _loading = false;
    }

    /// <summary>
    /// Opens centred over the main window, and stays above it: an owned window rides on top of its owner, and
    /// minimises and restores with it, instead of drifting behind it.
    /// </summary>
    private void PlaceOver(Window owner)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var ownerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, ownerHwnd);

        double scale = GetDpiForWindow(ownerHwnd) / 96.0;
        var area = owner.AppWindow.Size;
        int w = Math.Min((int)(660 * scale), Math.Max(area.Width - 40, 400));
        int h = Math.Min((int)(860 * scale), Math.Max(area.Height - 40, 400));
        var at = owner.AppWindow.Position;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(at.X + (area.Width - w) / 2, at.Y + (area.Height - h) / 2, w, h));
    }

    private const int GWLP_HWNDPARENT = -8;
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void LoadValues()
    {
        RescanToggle.IsOn = _settings.RescanOnStartup;
        WatchToggle.IsOn = _settings.WatchFolders;
        TrayToggle.IsOn = _settings.HideToTray;
        StartToggle.IsOn = _settings.StartWithWindows;
        HiddenToggle.IsOn = _settings.StartHidden;
        ExitToggle.IsOn = _settings.DeactivateOnExit;
        UnusedToggle.IsOn = _settings.UnusedEnabled;
        UpdateToggle.IsOn = _settings.AutoUpdate && _main.Updates.IsReady;
        UpdateToggle.IsEnabled = CheckUpdatesButton.IsEnabled = _main.Updates.IsReady;
        UpdateNote.Text = _main.Updates.Describe();
        UpdateStatusText.Text = _main.Updates.HasPending ? $"FontA {_main.Updates.PendingVersion} is ready; it installs when you quit FontA." : "";
        DaysBox.Value = _settings.UnusedDays;
    }

    private void OnToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var toggle = (ToggleSwitch)sender;
        bool on = toggle.IsOn;

        if (toggle == RescanToggle) _settings.RescanOnStartup = on;
        else if (toggle == WatchToggle) _settings.WatchFolders = on;
        else if (toggle == TrayToggle) _settings.HideToTray = on;
        else if (toggle == StartToggle) _settings.StartWithWindows = on;
        else if (toggle == HiddenToggle) _settings.StartHidden = on;
        else if (toggle == ExitToggle) _settings.DeactivateOnExit = on;
        else if (toggle == UnusedToggle) _settings.UnusedEnabled = on;
        else if (toggle == UpdateToggle) _settings.AutoUpdate = on;

        var error = _main.ApplyBehaviorSettings();
        if (error != null && toggle == StartToggle)
        {
            _settings.StartWithWindows = !on;          // the registry change failed, so don't pretend it worked
            _loading = true; StartToggle.IsOn = !on; _loading = false;
        }
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;

        if (toggle == UnusedToggle) _main.OnUnusedSettingsChanged();
        if (toggle == UpdateToggle && on) OnCheckForUpdates(this, null!);
    }

    private void OnDaysChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loading || double.IsNaN(e.NewValue)) return;
        _settings.UnusedDays = (int)e.NewValue;
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking GitHub…";
        var result = await _main.CheckForUpdatesAsync(manual: true);
        UpdateStatusText.Text = result.Status == UpdateStatus.Downloaded
            ? $"{result.Message} It installs when you quit FontA, or use Restart now in the main window."
            : result.Message;
        CheckUpdatesButton.IsEnabled = _main.Updates.IsReady;
    }

    private void OnCheckNow(object sender, RoutedEventArgs e) => _main.CheckUnusedNow();

    private async void OnAddFolder(object sender, RoutedEventArgs e) => await _main.AddFolderViaPickerAsync();

    private void RebuildFolders()
    {
        FolderList.Children.Clear();
        var folders = _main.Library.Folders();
        if (folders.Count == 0)
        {
            FolderList.Children.Add(new TextBlock { Text = "No folders yet. Add one to start building your library.", Opacity = 0.7 });
            return;
        }

        foreach (var folder in folders)
        {
            var row = new Grid { ColumnSpacing = 12, MinHeight = 40 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = folder, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!Directory.Exists(folder))
                text.Children.Add(new TextBlock
                {
                    Text = "Not available right now. Its fonts stay in your library until it returns.",
                    Opacity = 0.7,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
                });
            row.Children.Add(text);

            var remove = new Button { Content = "Remove" };
            remove.Click += async (_, _) => await ConfirmRemoveAsync(folder);
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            FolderList.Children.Add(row);
        }
    }

    private async Task ConfirmRemoveAsync(string folder)
    {
        var dialog = new ContentDialog
        {
            Title = "Remove this folder?",
            Content = $"{folder}\n\nFontA stops reading it and removes its fonts from your library, along with their favorites, tags " +
                      "and collection memberships. The font files themselves are not touched. Fonts you installed for your " +
                      "Windows account stay.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) _main.RemoveFolder(folder);
    }
}
