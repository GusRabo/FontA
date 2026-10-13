using FontA.Core.Activation;
using FontA.Core.Library;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace FontA.App;

// The library views: the sidebar (built-in views, collections, folders, tags), the grid grouped by folder, and
// switching whole groups of fonts on and off.
public sealed partial class MainWindow
{
    private enum NavKind { All, Recent, Favorites, Active, Inactive, AlwaysActive, Installed, Collection, Folder, Tag }

    /// <param name="Key">The folder path, for folder rows.</param>
    private sealed record NavEntry(NavKind Kind, string Label, long Id = 0, string Key = "");

    private sealed record NavRow(NavEntry Entry, ActivationDot? Dot, TextBlock Count);

    /// <summary>Everything the sidebar shows, worked out in one pass over the library.</summary>
    private sealed record NavStats(
        LibraryCounts Counts,
        int ActiveFonts,
        IReadOnlyList<FolderGroup<(string Path, int Count)>> Folders,
        IReadOnlyDictionary<string, List<string>> FolderPaths,
        HashSet<string> ActivePaths,
        IReadOnlyList<(long Id, string Name)> Collections,
        IReadOnlyDictionary<long, int> CollectionCounts,
        IReadOnlyDictionary<long, List<string>> CollectionPaths,
        IReadOnlyDictionary<string, int> TagCounts);

    private const string NewCollectionTag = "new-collection";
    private NavEntry _nav = new(NavKind.All, "All");
    private List<FontGroup> _groups = new();
    private Dictionary<long, FontItem> _itemsById = new();
    private readonly CollectionViewSource _fontsView = new() { IsSourceGrouped = true };
    private readonly Dictionary<string, NavRow> _navRows = new();
    private readonly HashSet<string> _collapsedSections = new();
    private NavStats? _stats;
    private bool _activationBusy;


    private IEnumerable<FontItem> AllItems => _groups.SelectMany(g => g);

    private bool IsEffectivelyActive(LibraryFont f) => f.IsInstalled || _activator.IsActive(f.Info.Path);

    // ------------------------------------------------------------------ the grid

    /// <summary>
    /// Re-queries the library for the current view and shows it grouped by folder. A full refresh makes fresh cards
    /// (new tint, back to the top). An incremental one reuses the existing cards, so thumbnails already loaded and the
    /// scroll position survive; it is used while a scan adds fonts and when a toggle changes what a view contains.
    /// </summary>
    private void Refresh(bool incremental = false)
    {
        FontItem.InkLevel = ((FrameworkElement)Content).ActualTheme == ElementTheme.Dark ? (byte)255 : (byte)0;
        var found = QueryFonts();
        double offset = incremental ? Scroller(FontGrid)?.VerticalOffset ?? 0 : 0;

        var reuse = incremental ? _itemsById : new Dictionary<long, FontItem>();
        var next = new Dictionary<long, FontItem>(found.Count);
        FontItem Card(LibraryFont f)
        {
            if (!reuse.TryGetValue(f.Id, out var card)) card = new FontItem(f, _lib, _activator, _thumbs, _installer, SampleBox.Text);
            next[f.Id] = card;
            return card;
        }

        IEnumerable<FolderGroup<LibraryFont>> grouped = _nav.Kind == NavKind.Recent
            ? new[] { new FolderGroup<LibraryFont>("", "Recently used", found) }
            : FolderGrouping.Group(found, f => f.Info.Path, _lib.Folders());

        _groups = grouped.Select(g => new FontGroup(g.Key, g.Display, g.Items.Select(Card))).ToList();
        _itemsById = next;
        _fontsView.Source = _groups;
        FontGrid.ItemsSource = _fontsView.View;
        // A grouped view makes its first item "current", which would select it and count as using it. Nothing is
        // selected until the person picks something.
        _fontsView.View?.MoveCurrentToPosition(-1);
        if (offset > 0) RestoreScroll(FontGrid, offset);
        ScheduleMemoryTrim();
    }

    private IReadOnlyList<LibraryFont> QueryFonts()
    {
        var found = _lib.Search(SearchBox.Text,
            _nav.Kind == NavKind.Favorites,
            _nav.Kind == NavKind.Tag ? _nav.Label : null,
            _nav.Kind == NavKind.Collection ? _nav.Id : null,
            _nav.Kind == NavKind.AlwaysActive,
            _nav.Kind == NavKind.Installed,
            _nav.Kind == NavKind.Recent);

        return _nav.Kind switch
        {
            NavKind.Active => found.Where(IsEffectivelyActive).ToList(),
            NavKind.Inactive => found.Where(f => !IsEffectivelyActive(f)).ToList(),
            NavKind.Folder => found.Where(f => FolderGrouping.GroupKeyOf(f.Info.Path).Equals(_nav.Key, StringComparison.OrdinalIgnoreCase)).ToList(),
            _ => found
        };
    }

    private void OnContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue && args.Item is FontItem item) item.CancelThumbnail();
    }

    private void OnCardDotClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FontItem { CanToggle: true } card) card.IsActive = !card.IsActive;
    }

    private void OnGroupDotClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FontGroup group)
            _ = ToggleActiveAsync(group.Select(i => i.Info.Path));
    }

    /// <summary>A single font was switched by hand: bring its group heading and the sidebar up to date.</summary>
    private void OnItemActivationChanged(FontItem item)
    {
        foreach (var g in _groups)
            if (g.Contains(item)) { g.RefreshState(); break; }
        RefreshNavStates();
        if (_nav.Kind is NavKind.Active or NavKind.Inactive) Refresh(incremental: true);   // it may have left this view
    }

    // ------------------------------------------------------------------ switching many fonts

    private CancellationTokenSource? _activationCts;
    private Task? _activationTask;

    /// <summary>
    /// Switches a set of font files on, or off if they are all on already. Installed fonts are left alone (Windows manages
    /// those). Windows can take up to half a second per font, so this runs in the background with progress and a time
    /// estimate, can be cancelled, and asks first when the job is long.
    /// </summary>
    private async Task ToggleActiveAsync(IEnumerable<string> paths)
    {
        if (_activationBusy) return;
        var installed = _lib.InstalledPaths();
        var all = paths.Where(p => !installed.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (all.Count == 0) return;

        bool activate = !all.All(_activator.IsActive);
        var todo = activate ? all.Where(p => !_activator.IsActive(p)).ToList() : all.Where(_activator.IsActive).ToList();
        double msPerFont = _settings.ActivationMsPerFont;

        if (ActivationPlan.NeedsConfirmation(todo.Count, msPerFont) && !await ConfirmBigJobAsync(activate, todo.Count, msPerFont))
            return;
        await RunActivationAsync(todo, activate);
    }

    private async Task<bool> ConfirmBigJobAsync(bool activate, int count, double msPerFont)
    {
        var estimate = ActivationPlan.Describe(ActivationPlan.Estimate(count, msPerFont));
        var verb = activate ? "Activate" : "Deactivate";
        var ask = new ContentDialog
        {
            Title = $"{verb} {count:N0} fonts?",
            Content = $"This will take {estimate} on this PC. Windows updates its font list for every open app each time a font " +
                      "is switched, so large folders are slow. FontA keeps working while it runs, and you can cancel at any time." +
                      (activate ? "\n\nA very large number of active fonts can also slow down Windows and the apps you use." : ""),
            PrimaryButtonText = verb, CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot
        };
        return await ask.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Switches the given files on or off in the background, with a progress panel the person can cancel.</summary>
    private async Task RunActivationAsync(IReadOnlyList<string> todo, bool activate)
    {
        if (todo.Count == 0 || _activationBusy) return;
        _activationBusy = true;
        _activationCts = new CancellationTokenSource();
        var cts = _activationCts;
        bool usePanel = _scanCts == null;           // don't fight a running scan over the progress panel
        var started = DateTime.UtcNow;
        string verb = activate ? "Activating" : "Deactivating";
        if (usePanel)
        {
            ScanCancel.IsEnabled = true;
            ScanBar.IsIndeterminate = false; ScanBar.Maximum = Math.Max(todo.Count, 1); ScanBar.Value = 0;
            ScanStatus.Text = $"{verb} {todo.Count:N0} fonts…";
            ScanPanel.Visibility = Visibility.Visible;
        }

        int done = 0;
        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                done = p.Done;
                if (!usePanel || cts.IsCancellationRequested) return;
                ScanBar.Maximum = Math.Max(p.Total, 1); ScanBar.Value = p.Done;
                var elapsed = DateTime.UtcNow - started;
                var left = p.Done > 0 ? TimeSpan.FromTicks((long)(elapsed.Ticks / (double)p.Done * (p.Total - p.Done))) : TimeSpan.Zero;
                ScanStatus.Text = $"{verb} {p.Done:N0} of {p.Total:N0} fonts" + (p.Done < p.Total && p.Done >= 3 ? $" · {ActivationPlan.Describe(left)} left" : "") + "…";
            });
            _activationTask = Task.Run(() =>
            {
                if (activate) _activator.ActivateMany(todo, progress, cts.Token);
                else _activator.DeactivateMany(todo, progress, cts.Token);
            });
            await _activationTask;
            if (activate) _lib.TouchUsedPaths(todo.Where(_activator.IsActive).ToList(), DateTimeOffset.UtcNow);
        }
        finally
        {
            // Remember how fast Windows is on this PC, so the next estimate is realistic.
            int handled = Math.Max(done, 0);
            if (handled >= 3)
                _settings.ActivationMsPerFont = ActivationPlan.Blend(_settings.ActivationMsPerFont, (DateTime.UtcNow - started).TotalMilliseconds / handled, handled);
            _activationBusy = false;
            _activationCts = null;
            cts.Dispose();
            if (usePanel && _scanCts == null) ScanPanel.Visibility = Visibility.Collapsed;
        }
        AfterBulkActivationChange();
    }

    /// <summary>Turns on the fonts marked "Always" at startup, in the background so the window appears straight away.</summary>
    private async Task ActivateAlwaysActiveAsync()
    {
        var paths = _lib.AlwaysActivePaths().Where(File.Exists).Where(p => !_activator.IsActive(p)).ToList();
        if (paths.Count > 0) await RunActivationAsync(paths, activate: true);
    }

    private void AfterBulkActivationChange()
    {
        foreach (var g in _groups)
        {
            foreach (var card in g) card.RaiseActiveChanged();
            g.RefreshState();
        }
        RefreshNavStates();
        if (_nav.Kind is NavKind.Active or NavKind.Inactive) Refresh(incremental: true);
    }

    private IEnumerable<string> PathsFor(NavEntry entry) => entry.Kind switch
    {
        NavKind.Folder => _stats is { } s && s.FolderPaths.TryGetValue(entry.Key, out var p) ? p : Array.Empty<string>(),
        NavKind.Collection => _lib.CollectionPaths(entry.Id),
        _ => Array.Empty<string>()
    };

    // ------------------------------------------------------------------ the sidebar

    private static string RowKey(NavEntry e) => e.Kind switch
    {
        NavKind.Folder => $"Folder|{e.Key}",
        NavKind.Collection => $"Collection|{e.Id}",
        NavKind.Tag => $"Tag|{e.Label}",
        _ => e.Kind.ToString()
    };

    private NavStats ComputeStats()
    {
        var pathCounts = _lib.PathCounts();
        var installed = _lib.InstalledPaths();
        var active = new HashSet<string>(_activator.Active, StringComparer.OrdinalIgnoreCase);
        active.UnionWith(installed);

        var folders = FolderGrouping.Group(pathCounts, p => p.Path, _lib.Folders());
        var folderPaths = folders.ToDictionary(g => g.Key, g => g.Items.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);
        var collections = _lib.Collections();
        var collectionPaths = collections.ToDictionary(c => c.Id, c => _lib.CollectionPaths(c.Id).ToList());

        return new NavStats(_lib.Counts(), pathCounts.Where(p => active.Contains(p.Path)).Sum(p => p.Count), folders, folderPaths,
            active, collections, _lib.CollectionCounts(), collectionPaths, _lib.TagCounts());
    }

    /// <summary>Rebuilds the whole sidebar (when folders, collections or tags change), keeping its scroll position.</summary>
    private void RebuildNav()
    {
        _building = true;
        double offset = Scroller(NavList)?.VerticalOffset ?? 0;
        NavList.Items.Clear();
        _navRows.Clear();
        _stats = ComputeStats();

        AddRow(new(NavKind.All, "All"), "All");
        AddRow(new(NavKind.Recent, "Recent"), "Recent");
        AddRow(new(NavKind.Favorites, "Favorites"), "Favorites");
        AddRow(new(NavKind.Active, "Active"), "Active", dot: true, interactive: false);
        AddRow(new(NavKind.Inactive, "Inactive"), "Inactive", dot: true, interactive: false);
        AddRow(new(NavKind.AlwaysActive, "Always active"), "Always active");
        AddRow(new(NavKind.Installed, "Installed"), "Installed");

        AddHeader("COLLECTIONS");
        if (!_collapsedSections.Contains("COLLECTIONS"))
        {
            var add = new TextBlock { Text = "New collection", Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(34, 0, 0, 0) };
            NavList.Items.Add(new ListViewItem { Content = add, Tag = NewCollectionTag, Padding = new Thickness(4, 0, 12, 0), MinHeight = 38 });
            foreach (var (id, name) in _stats.Collections)
            {
                var item = AddRow(new(NavKind.Collection, name, id), name, dot: true);
                var rename = new MenuFlyoutItem { Text = "Rename…" };
                rename.Click += async (_, _) => await RenameCollectionAsync(id, name);
                var delete = new MenuFlyoutItem { Text = "Delete collection" };
                delete.Click += (_, _) =>
                {
                    _lib.DeleteCollection(id);
                    if (_nav.Kind == NavKind.Collection && _nav.Id == id) _nav = new(NavKind.All, "All");
                    RebuildNav();
                    Refresh();
                    RebuildOrganize();
                };
                item.ContextFlyout = new MenuFlyout { Items = { rename, delete } };
            }
        }

        AddHeader("FOLDERS");
        if (!_collapsedSections.Contains("FOLDERS"))
            foreach (var g in _stats.Folders)
            {
                var item = AddRow(new(NavKind.Folder, g.Display, 0, g.Key), g.Display, dot: true);
                ToolTipService.SetToolTip(item, g.Key == FolderGrouping.SystemKey ? "Fonts installed in Windows" : g.Key);
            }

        AddHeader("TAGS");
        if (!_collapsedSections.Contains("TAGS"))
            foreach (var tag in _lib.Tags())
                AddRow(new(NavKind.Tag, tag), "# " + tag);

        RefreshNavStates();
        var wanted = RowKey(_nav);
        foreach (var o in NavList.Items.OfType<ListViewItem>())
            if (o.Tag is NavEntry e && RowKey(e) == wanted) { NavList.SelectedItem = o; break; }
        _building = false;
        if (offset > 0) RestoreScroll(NavList, offset);
    }

    private ListViewItem AddRow(NavEntry entry, string label, bool dot = false, bool interactive = true)
    {
        ActivationDot? circle = null;
        if (dot)
        {
            circle = new ActivationDot { IsHitTestVisible = interactive };
            if (interactive) circle.Click += (_, _) => _ = ToggleActiveAsync(PathsFor(entry));
        }
        var count = new TextBlock { Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right };
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

        var grid = new Grid { ColumnSpacing = 6, MinHeight = 38 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (circle != null) grid.Children.Add(circle);
        Grid.SetColumn(name, 1); Grid.SetColumn(count, 2);
        grid.Children.Add(name); grid.Children.Add(count);

        var item = new ListViewItem { Content = grid, Tag = entry, Padding = new Thickness(4, 0, 12, 0), MinHeight = 0 };
        NavList.Items.Add(item);
        _navRows[RowKey(entry)] = new NavRow(entry, circle, count);
        return item;
    }

    private void AddHeader(string title)
    {
        bool collapsed = _collapsedSections.Contains(title);
        var chevron = new TextBlock { Text = collapsed ? "▸" : "▾", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Width = 20 };
        var text = new TextBlock { Text = title, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Padding = new Thickness(6, 14, 0, 4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        row.Children.Add(chevron); row.Children.Add(text);
        row.Tapped += (_, _) =>
        {
            if (!_collapsedSections.Remove(title)) _collapsedSections.Add(title);
            RebuildNav();
        };
        NavList.Items.Add(new ListViewHeaderItem { Content = row, Padding = new Thickness(0), MinHeight = 0 });
    }

    /// <summary>Updates the counts and circles in place, without rebuilding the sidebar or losing its scroll position.</summary>
    private void RefreshNavStates()
    {
        _stats = ComputeStats();
        var s = _stats;
        var folderStats = s.Folders.ToDictionary(g => g.Key, g => (Total: g.Items.Sum(i => i.Count), Active: g.Items.Where(i => s.ActivePaths.Contains(i.Path)).Sum(i => i.Count)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in _navRows.Values)
        {
            int count = 0, state = ActivationDot.Off;
            switch (row.Entry.Kind)
            {
                case NavKind.All: count = s.Counts.Total; break;
                case NavKind.Recent: count = s.Counts.Recent; break;
                case NavKind.Favorites: count = s.Counts.Favorites; break;
                case NavKind.Active: count = s.ActiveFonts; state = ActivationDot.On; break;
                case NavKind.Inactive: count = s.Counts.Total - s.ActiveFonts; break;
                case NavKind.AlwaysActive: count = s.Counts.AlwaysActive; break;
                case NavKind.Installed: count = s.Counts.Installed; break;
                case NavKind.Tag: s.TagCounts.TryGetValue(row.Entry.Label, out count); break;
                case NavKind.Folder:
                    if (folderStats.TryGetValue(row.Entry.Key, out var f)) { count = f.Total; state = StateOf(f.Active, f.Total); }
                    break;
                case NavKind.Collection:
                    s.CollectionCounts.TryGetValue(row.Entry.Id, out count);
                    if (s.CollectionPaths.TryGetValue(row.Entry.Id, out var paths))
                        state = StateOf(paths.Count(s.ActivePaths.Contains), paths.Count);
                    break;
            }
            row.Count.Text = count.ToString("N0");
            if (row.Dot != null) row.Dot.State = state;
        }

        static int StateOf(int active, int total) => active == 0 || total == 0 ? ActivationDot.Off : active >= total ? ActivationDot.On : ActivationDot.Some;
    }

    private void OnNavChanged(object s, SelectionChangedEventArgs e)
    {
        if (_building) return;
        if (NavList.SelectedItem is ListViewItem { Tag: string tag } && tag == NewCollectionTag)
        {
            // Not a view: put the highlight back on the current one and ask for a name.
            _building = true;
            foreach (var o in NavList.Items.OfType<ListViewItem>())
                if (o.Tag is NavEntry e2 && RowKey(e2) == RowKey(_nav)) { NavList.SelectedItem = o; break; }
            _building = false;
            OnNewCollection(this, null!);
            return;
        }
        if (NavList.SelectedItem is not ListViewItem { Tag: NavEntry entry }) return;
        if (SettingsOpen) CloseSettings();            // picking a view in the sidebar goes back to the fonts
        _nav = entry;
        Refresh();
    }

    // ------------------------------------------------------------------ memory

    private DispatcherTimer? _trimTimer;

    /// <summary>
    /// A moment after the last big change (a view rebuilt, a scan finished) FontA hands back memory it no longer needs:
    /// a full, compacting garbage collection, then the unused pages are returned to Windows. Windows pages back in
    /// whatever is touched again, so this only costs a brief pause the first time something old is used.
    /// </summary>
    private void ScheduleMemoryTrim()
    {
        _trimTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _trimTimer.Tick -= OnTrimTick;
        _trimTimer.Tick += OnTrimTick;
        _trimTimer.Stop(); _trimTimer.Start();
    }

    private void OnTrimTick(object? s, object e)
    {
        _trimTimer?.Stop();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1); } catch (Exception) { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    // ------------------------------------------------------------------ the mouse wheel

    private WheelHook? _wheelHook;

    /// <summary>
    /// The mouse wheel scrolls the sidebar (the left area) or the font list (the main area, toolbar and gaps included)
    /// according to where the pointer is, every turn, whatever was under the pointer before. The details pane scrolls
    /// itself, and an open dialog or menu keeps the wheel.
    /// </summary>
    private void SetUpWheel()
    {
        _wheelHook = new WheelHook(WinRT.Interop.WindowNative.GetWindowHandle(this), (sx, sy, delta) =>
        {
            var root = (FrameworkElement)Content;
            if (root.XamlRoot is not { } xamlRoot || !IsWindowActive()) return false;
            // A dialog, flyout or suggestion list keeps the wheel for itself. Tooltips don't count (one appears whenever the
            // pointer rests over a button or the search box, and must not stop the wheel working there).
            if (VisualTreeHelper.GetOpenPopups(this).Any(p => p.Child is not ToolTip)) return false;

            var pt = new WheelPoint { X = sx, Y = sy };
            if (!ScreenToClient(WinRT.Interop.WindowNative.GetWindowHandle(this), ref pt)) return false;
            double scale = xamlRoot.RasterizationScale;
            double x = pt.X / scale, y = pt.Y / scale;
            if (x < 0 || y < 0 || y >= root.ActualHeight) return false;

            double side = SidebarColumn.ActualWidth;
            ScrollViewer? target = null;
            double notch;
            if (x < side) { target = Scroller(NavList); notch = 76; }
            else if (SettingsOpen) { target = Scroller(SettingsHost); notch = 110; }     // the settings cover the font area
            else if (x < side + MainColumn.ActualWidth) { target = Scroller(FontGrid); notch = 110; }
            else return false;
            if (target == null) return false;

            ScrollBy(target, -delta / 120.0 * notch);
            return true;
        });
    }

    private readonly Dictionary<ScrollViewer, double> _wheelGoal = new();
    private long _lastWheelTick;

    /// <summary>
    /// Scrolls by an amount, remembering where the scrolling is heading so quick turns of the wheel add up instead of
    /// each starting from wherever the list happens to be mid-move. The move itself is made on the next turn of the
    /// message loop, not inside the mouse hook.
    /// </summary>
    private void ScrollBy(ScrollViewer sv, double pixels)
    {
        long now = Environment.TickCount64;
        if (now - _lastWheelTick > 250 || !_wheelGoal.ContainsKey(sv)) _wheelGoal[sv] = sv.VerticalOffset;
        _lastWheelTick = now;
        ScheduleMemoryTrim();
        double goal = Math.Clamp(_wheelGoal[sv] + pixels, 0, sv.ScrollableHeight);
        _wheelGoal[sv] = goal;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, () => sv.ChangeView(null, goal, null, disableAnimation: true));
    }

    private bool IsWindowActive() => GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(this);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WheelPoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref WheelPoint p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    // ------------------------------------------------------------------ scrolling helpers

    private static ScrollViewer? Scroller(DependencyObject root)
    {
        if (root is ScrollViewer self) return self;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (Scroller(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Puts a list back where it was after its contents were replaced (once the new layout exists).</summary>
    private void RestoreScroll(FrameworkElement list, double offset) =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            list.UpdateLayout();
            Scroller(list)?.ChangeView(null, offset, null, disableAnimation: true);
        });
}
