using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace FontA.App;

/// <summary>
/// The round activation switch used throughout FontA. Hollow ring: nothing active. Solid green: everything active.
/// Ring with a small dot: some of a group's fonts are active. Clicking raises <see cref="ButtonBase.Click"/>; the
/// owner decides what the click means (and updates <see cref="State"/>).
/// </summary>
public sealed class ActivationDot : Button
{
    public const int Off = 0, Some = 1, On = 2;

    private static readonly SolidColorBrush Green = new(Color.FromArgb(255, 34, 197, 94));
    private readonly Ellipse _ring = new() { Width = 16, Height = 16, StrokeThickness = 1.75, Fill = new SolidColorBrush(Colors.Transparent) };
    private readonly Ellipse _fill = new() { Width = 16, Height = 16 };

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(int), typeof(ActivationDot), new PropertyMetadata(Off, (d, _) => ((ActivationDot)d).UpdateVisual()));

    /// <summary><see cref="Off"/>, <see cref="Some"/> or <see cref="On"/>.</summary>
    public int State
    {
        get => (int)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public ActivationDot()
    {
        Width = 30; Height = 30; MinWidth = 0; MinHeight = 0;
        Padding = new Thickness(0);
        Background = new SolidColorBrush(Colors.Transparent);
        BorderThickness = new Thickness(0);
        CornerRadius = new CornerRadius(15);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var ring) && ring is Brush b) _ring.Stroke = b;
        else _ring.Stroke = new SolidColorBrush(Colors.Gray);

        var grid = new Grid { Width = 16, Height = 16 };
        grid.Children.Add(_fill);
        grid.Children.Add(_ring);
        Content = grid;
        UpdateVisual();
    }

    private void UpdateVisual()
    {
        switch (State)
        {
            case On:
                _fill.Fill = Green; _fill.Width = _fill.Height = 16; _fill.Visibility = Visibility.Visible;
                _ring.Stroke = Green;
                ToolTipService.SetToolTip(this, "Active. Click to deactivate");
                break;
            case Some:
                _fill.Fill = Green; _fill.Width = _fill.Height = 7; _fill.Visibility = Visibility.Visible;
                _ring.Stroke = Green;
                ToolTipService.SetToolTip(this, "Some are active. Click to activate all");
                break;
            default:
                _fill.Visibility = Visibility.Collapsed;
                if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var ring) && ring is Brush rb) _ring.Stroke = rb;
                ToolTipService.SetToolTip(this, "Inactive. Click to activate");
                break;
        }
    }
}
