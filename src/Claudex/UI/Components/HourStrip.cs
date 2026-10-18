using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Claudex.UI.Components;

/// <summary>
/// Usage by local hour of day: 24 bars (estimated API equivalent, scaled to the busiest hour) in the usage graph's
/// purple, over a bright yellow band for the hours that fall in Anthropic's peak window. Hovering a bar brightens it and
/// raises <see cref="HourHovered"/> (-1 when the pointer leaves), so the panel can show that hour's tokens.
/// </summary>
public sealed class HourStrip : UserControl
{
    private const double Gap = 2;

    private static readonly SolidColorBrush BarBrush = new(Color.FromArgb(0xFF, 0x6A, 0x52, 0xC4));
    private static readonly SolidColorBrush EmptyBrush = new(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush PeakBrush = new(Color.FromArgb(0x8C, 0xFF, 0xD6, 0x0A)); // bright yellow, clearly visible behind the bars
    private static readonly SolidColorBrush HoverBrush = new(Color.FromArgb(0xFF, 0xA9, 0x96, 0xFF));

    // The canvas is hit-testable through its transparent background; the bars themselves are not.
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private int _hovered = -1;
    private IReadOnlyList<double> _values = new double[24];
    private IReadOnlyList<bool> _peak = new bool[24];

    public HourStrip()
    {
        Content = _canvas;
        Height = 24;
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        SizeChanged += (_, _) => Draw();
        _canvas.PointerMoved += (_, e) => Hover(HourAt(e.GetCurrentPoint(_canvas).Position.X));
        _canvas.PointerExited += (_, _) => Hover(-1);
    }

    /// <summary>The hour under the pointer (0 to 23), or -1 when it left the strip.</summary>
    public event Action<int>? HourHovered;

    private int HourAt(double x)
    {
        var width = ActualWidth;
        return width <= 0 ? -1 : Math.Clamp((int)(x / (width / 24)), 0, 23);
    }

    private void Hover(int hour)
    {
        if (hour == _hovered)
        {
            return;
        }

        _hovered = hour;
        Draw();
        HourHovered?.Invoke(hour);
    }

    /// <summary>24 values from 0 to 1 (local hours 0 to 23) and which of those hours are peak hours.</summary>
    public void Set(IReadOnlyList<double> values, IReadOnlyList<bool> peak)
    {
        _values = values;
        _peak = peak;
        Draw();
    }

    private void Draw()
    {
        _canvas.Children.Clear();
        var width = ActualWidth;
        var height = Height;
        if (width <= 0 || _values.Count != 24)
        {
            return;
        }

        var bar = (width - 23 * Gap) / 24;
        for (var h = 0; h < 24; h++)
        {
            var x = h * (bar + Gap);
            if (h < _peak.Count && _peak[h])
            {
                var band = new Rectangle { Width = bar + Gap, Height = height, Fill = PeakBrush, IsHitTestVisible = false };
                Canvas.SetLeft(band, x - Gap / 2);
                _canvas.Children.Add(band);
            }

            var value = Math.Clamp(_values[h], 0, 1);
            var filled = value > 0 ? Math.Max(2, value * height) : 2;
            var fill = h == _hovered ? HoverBrush : value > 0 ? BarBrush : EmptyBrush;
            var rect = new Rectangle { Width = bar, Height = filled, RadiusX = 1.5, RadiusY = 1.5, Fill = fill, IsHitTestVisible = false };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, height - filled);
            _canvas.Children.Add(rect);
        }
    }
}
