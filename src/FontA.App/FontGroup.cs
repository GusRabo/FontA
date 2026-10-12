using System.Collections.ObjectModel;
using System.ComponentModel;

namespace FontA.App;

/// <summary>The fonts from one folder, shown under a heading in the grid. The heading has its own activation circle.</summary>
public sealed class FontGroup : ObservableCollection<FontItem>
{
    private int _state;

    public FontGroup(string key, string display, IEnumerable<FontItem> items) : base(items)
    {
        Key = key;
        Display = display;
        _state = ComputeState();
    }

    /// <summary>The folder's path (empty for the "recent" group).</summary>
    public string Key { get; }

    public string Display { get; }

    /// <summary>What the heading shows when hovered: where the folder is.</summary>
    public string Tooltip => Key == FontA.Core.Library.FolderGrouping.SystemKey ? "Fonts installed in Windows" : Key;

    public string CountText => Count == 1 ? "1 font" : $"{Count:N0} fonts";

    /// <summary><see cref="ActivationDot.Off"/>, <see cref="ActivationDot.Some"/> or <see cref="ActivationDot.On"/>.</summary>
    public int ActiveState => _state;

    /// <summary>Recomputes the heading's circle from the fonts' current state.</summary>
    public void RefreshState()
    {
        var now = ComputeState();
        if (now == _state) return;
        _state = now;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(ActiveState)));
    }

    private int ComputeState()
    {
        if (Count == 0) return ActivationDot.Off;
        int on = 0;
        foreach (var item in this) if (item.IsActive) on++;
        return on == 0 ? ActivationDot.Off : on == Count ? ActivationDot.On : ActivationDot.Some;
    }
}
