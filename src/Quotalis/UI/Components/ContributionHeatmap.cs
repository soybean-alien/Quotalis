using Quotalis.Core.Git;
using Quotalis.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Quotalis.UI.Components;

/// <summary>
/// soybean-alien's GitHub contribution calendar, laid out like the usage heatmap at its smallest cell size: one column per week,
/// Sunday on top as on GitHub, in GitHub's dark-mode greens for its five levels. Hover a day to read it, click to pin it.
/// </summary>
public sealed class ContributionHeatmap : UserControl
{
    private const double CellSize = 11;
    private const double Gap = 3;
    private const double Pitch = CellSize + Gap;

    /// <summary>GitHub's dark theme: no contributions, then the four quartiles.</summary>
    public static readonly Color[] Palette =
    [
        Color.FromArgb(0xFF, 0x16, 0x1B, 0x22),
        Color.FromArgb(0xFF, 0x0E, 0x44, 0x29),
        Color.FromArgb(0xFF, 0x00, 0x6D, 0x32),
        Color.FromArgb(0xFF, 0x26, 0xA6, 0x41),
        Color.FromArgb(0xFF, 0x39, 0xD3, 0x53),
    ];

    private static readonly SolidColorBrush[] Brushes = Palette.Select(c => new SolidColorBrush(c)).ToArray();

    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Rectangle _marker = new()
    {
        Width = CellSize + 4,
        Height = CellSize + 4,
        RadiusX = 3,
        RadiusY = 3,
        StrokeThickness = 1.5,
        Stroke = new SolidColorBrush(Colors.White),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };

    private readonly List<(int Column, int Row)> _positions = [];
    private GitViewModel? _viewModel;

    public ContributionHeatmap()
    {
        Content = _canvas;
        _canvas.PointerMoved += (_, e) => _viewModel?.InspectContribution(IndexAt(e.GetCurrentPoint(_canvas).Position));
        _canvas.PointerExited += (_, _) => _viewModel?.InspectContribution(-1);
        _canvas.PointerPressed += (_, e) =>
        {
            var index = IndexAt(e.GetCurrentPoint(_canvas).Position);
            if (index >= 0)
            {
                _viewModel?.PinContribution(index);
                PlaceMarker();
            }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "GitHub contributions, last 26 weeks");
    }

    public GitViewModel ViewModel
    {
        set
        {
            _viewModel = value;
            value.ContributionsChanged += Build;
            Build();
        }
    }

    private void Build()
    {
        _canvas.Children.Clear();
        _positions.Clear();
        var days = _viewModel?.Contributions ?? [];
        if (days.Count == 0)
        {
            _canvas.Width = _canvas.Height = 0;
            return;
        }

        var first = days[0].Date;
        var firstSunday = first.AddDays(-(int)first.DayOfWeek);
        var columns = 0;
        foreach (var day in days)
        {
            var column = (day.Date.DayNumber - firstSunday.DayNumber) / 7;
            var row = (int)day.Date.DayOfWeek;
            _positions.Add((column, row));
            columns = Math.Max(columns, column + 1);
            var cell = new Rectangle
            {
                Width = CellSize,
                Height = CellSize,
                RadiusX = 2,
                RadiusY = 2,
                Fill = Brushes[Math.Clamp(day.Level, 0, Brushes.Length - 1)],
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(cell, column * Pitch);
            Canvas.SetTop(cell, row * Pitch);
            _canvas.Children.Add(cell);
        }

        _canvas.Children.Add(_marker);
        _canvas.Width = columns * Pitch - Gap;
        _canvas.Height = 7 * Pitch - Gap;
        PlaceMarker();
    }

    private int IndexAt(Windows.Foundation.Point p)
    {
        var column = (int)(p.X / Pitch);
        var row = (int)(p.Y / Pitch);
        return _positions.FindIndex(x => x.Column == column && x.Row == row);
    }

    private void PlaceMarker()
    {
        var index = _viewModel?.PinnedContribution ?? -1;
        if (index < 0 || index >= _positions.Count)
        {
            _marker.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(_marker, _positions[index].Column * Pitch - 2);
        Canvas.SetTop(_marker, _positions[index].Row * Pitch - 2);
        _marker.Visibility = Visibility.Visible;
    }
}
