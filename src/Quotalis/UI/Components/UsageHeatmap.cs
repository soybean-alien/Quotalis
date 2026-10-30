using System.Globalization;
using Quotalis.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace Quotalis.UI.Components;

/// <summary>
/// Contribution-style usage graph: one column per week, one row per weekday. Each day takes the colour of the tool
/// with the most fresh tokens that day (Claude orange, Codex blue, Hermes yellow); days without use stay dim.
/// The graph fills the width it is given, so the cells grow with the panel (square, from <see cref="MinCellSize"/>
/// up to <see cref="MaxCellSize"/>).
/// Pointer hover and arrow keys inspect a day; a left click or tap (or Enter and Space) pins it, so the readout keeps
/// that day after the pointer leaves, until another day is clicked or the island collapses. A fainter ring marks the
/// pinned day while another one is hovered. The readout (per-tool bars and the total) lives in the panel below the
/// graph, so nothing pops up over it.
/// <para>The cells are not hit-testable; the canvas is, through its transparent background. A
/// UserControl has no template, so a background set on the control itself is never drawn and
/// gives no hit-test area: pointer input would go to the scroll viewer behind the graph instead.</para>
/// </summary>
public sealed class UsageHeatmap : UserControl
{
    public const double MinCellSize = 11;
    public const double MaxCellSize = 26;
    public const double Gap = 4;

    private static readonly SolidColorBrush EmptyBrush = new(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));

    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Rectangle _ring;
    private readonly Rectangle _pinRing;
    private readonly List<Rectangle> _cells = [];
    private readonly List<(int Column, int Row)> _positions = [];
    private Quotalis.Core.Usage.HeatmapLayout _layout = Quotalis.Core.Usage.HeatmapLayout.For([], DayOfWeek.Monday);
    private UsageViewModel? _viewModel;
    private int _columns;
    private DateOnly _firstDay;
    private double _pitch = MinCellSize + Gap;

    public UsageHeatmap()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        FocusVisualMargin = new Thickness(-4);
        _ring = new Rectangle
        {
            Stroke = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            StrokeThickness = 1.5,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _pinRing = new Rectangle
        {
            Stroke = (Brush)Application.Current.Resources["TextTertiaryBrush"],
            StrokeThickness = 1.5,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        Content = _canvas;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);

        PointerMoved += (_, e) => InspectAt(e.GetCurrentPoint(_canvas).Position);
        PointerPressed += OnPointerPressed;
        PointerExited += (_, _) =>
        {
            // Arrow-key navigation keeps its day; a hovered day ends with the pointer, back to the pinned one.
            if (FocusState != FocusState.Keyboard)
            {
                _viewModel?.EndHover();
            }
        };
        GotFocus += (_, _) =>
        {
            if (_viewModel is { } vm && !vm.IsInspecting && vm.Days.Count > 0)
            {
                vm.Inspect(vm.PinnedIndex >= 0 ? vm.PinnedIndex : vm.Days.Count - 1);
            }
        };
        LostFocus += (_, _) => _viewModel?.EndHover();
    }

    public UsageViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.HistoryChanged -= Rebuild;
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = value;
            if (_viewModel is not null)
            {
                _viewModel.HistoryChanged += Rebuild;
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }

            Rebuild();
        }
    }

    private double CellSize => _pitch - Gap;

    /// <summary>Fills the offered width: square cells, so the height follows from the width.</summary>
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        _canvas.Measure(availableSize);
        if (_columns == 0)
        {
            return new Windows.Foundation.Size(0, 0);
        }

        var pitch = double.IsInfinity(availableSize.Width)
            ? MinCellSize + Gap
            : Math.Clamp((availableSize.Width + Gap) / _columns, MinCellSize + Gap, MaxCellSize + Gap);
        if (Math.Abs(pitch - _pitch) > 0.01)
        {
            _pitch = pitch;
            Place();
        }

        return new Windows.Foundation.Size(_columns * _pitch - Gap, 7 * _pitch - Gap);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        _canvas.Arrange(new Windows.Foundation.Rect(0, 0, finalSize.Width, finalSize.Height));
        return finalSize;
    }

    /// <summary>Sizes and positions every cell and the rings for the current pitch.</summary>
    private void Place()
    {
        var cell = CellSize;
        var radius = Math.Round(cell * 0.22, 1);
        for (var i = 0; i < _cells.Count; i++)
        {
            var (column, row) = _positions[i];
            _cells[i].Width = _cells[i].Height = cell;
            _cells[i].RadiusX = _cells[i].RadiusY = radius;
            Canvas.SetLeft(_cells[i], column * _pitch);
            Canvas.SetTop(_cells[i], row * _pitch);
        }

        foreach (var ring in new[] { _ring, _pinRing })
        {
            ring.Width = ring.Height = cell + 4;
            ring.RadiusX = ring.RadiusY = radius + 1.5;
        }

        UpdateRing();
    }

    private static Brush CellBrush(Quotalis.Core.Models.UsageDay day) => day.Leader switch
    {
        Quotalis.Core.Models.UsageTool.Claude => (Brush)Application.Current.Resources["ClaudeBrush"],
        Quotalis.Core.Models.UsageTool.Codex => (Brush)Application.Current.Resources["CodexBrush"],
        Quotalis.Core.Models.UsageTool.Hermes => (Brush)Application.Current.Resources["HermesBrush"],
        _ => EmptyBrush,
    };

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (_viewModel is not { Days.Count: > 0 } vm)
        {
            base.OnKeyDown(e);
            return;
        }

        var index = vm.IsInspecting ? vm.InspectedIndex : vm.Days.Count - 1;
        if (e.Key is VirtualKey.Enter or VirtualKey.Space)
        {
            vm.Pin(index);
            e.Handled = true;
            return;
        }

        var next = e.Key switch
        {
            VirtualKey.Left => index - 7,
            VirtualKey.Right => index + 7,
            VirtualKey.Up => index - 1,
            VirtualKey.Down => index + 1,
            VirtualKey.Home => 0,
            VirtualKey.End => vm.Days.Count - 1,
            _ => int.MinValue,
        };

        if (next == int.MinValue)
        {
            base.OnKeyDown(e);
            return;
        }

        vm.Inspect(Math.Clamp(next, 0, vm.Days.Count - 1));
        e.Handled = true;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UsageViewModel.InspectedIndex) or nameof(UsageViewModel.PinnedIndex) or "" or null)
        {
            UpdateRing();
        }
    }

    private void Rebuild()
    {
        // Same range as before (the common case: today got more tokens): recolour in place.
        if (_viewModel is { } current && current.Days.Count == _cells.Count && _cells.Count > 0 && current.Days[0].Day == _firstDay)
        {
            for (var i = 0; i < _cells.Count; i++)
            {
                _cells[i].Fill = CellBrush(current.Days[i]);
            }

            UpdateRing();
            return;
        }

        _canvas.Children.Clear();
        _cells.Clear();
        _positions.Clear();
        if (_viewModel is not { } vm || vm.Days.Count == 0)
        {
            _columns = 0;
            InvalidateMeasure();
            return;
        }

        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        _layout = Quotalis.Core.Usage.HeatmapLayout.For(vm.Days.Select(d => d.Day).ToList(), firstDay);

        for (var i = 0; i < vm.Days.Count; i++)
        {
            var cell = new Rectangle { Fill = CellBrush(vm.Days[i]), IsHitTestVisible = false };
            _canvas.Children.Add(cell);
            _cells.Add(cell);
            _positions.Add(_layout.Positions[i]);
        }

        _columns = _layout.Columns;
        _firstDay = vm.Days[0].Day;
        _canvas.Children.Add(_pinRing);
        _canvas.Children.Add(_ring);
        Place();
        InvalidateMeasure();
    }

    /// <summary>
    /// A left click or tap pins the day under it (touch and pen have no hover) and takes keyboard
    /// focus, so the arrow keys continue from that day.
    /// </summary>
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        if (_viewModel is null || (point.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed))
        {
            return;
        }

        var index = _layout.IndexAt(point.Position.X, point.Position.Y, _pitch);
        if (index >= 0 && index < _viewModel.Days.Count)
        {
            _viewModel.Pin(index);
            Focus(FocusState.Pointer);
        }

        e.Handled = true;
    }

    private void InspectAt(Windows.Foundation.Point point)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.Inspect(_layout.IndexAt(point.X, point.Y, _pitch));
    }

    private void UpdateRing()
    {
        UpdatePinRing();
        if (_viewModel is not { IsInspecting: true } vm || vm.InspectedIndex >= _positions.Count)
        {
            _ring.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(this, _viewModel?.InspectedAccessibleText ?? "Usage history");
            return;
        }

        var (column, row) = _positions[vm.InspectedIndex];
        Canvas.SetLeft(_ring, column * _pitch - 2);
        Canvas.SetTop(_ring, row * _pitch - 2);
        _ring.Visibility = Visibility.Visible;
        AutomationProperties.SetName(this, vm.InspectedAccessibleText);
    }

    /// <summary>The fainter ring on the pinned day, shown while another day is inspected.</summary>
    private void UpdatePinRing()
    {
        var pinned = _viewModel?.PinnedIndex ?? -1;
        if (pinned < 0 || pinned >= _positions.Count || pinned == _viewModel!.InspectedIndex)
        {
            _pinRing.Visibility = Visibility.Collapsed;
            return;
        }

        var (column, row) = _positions[pinned];
        Canvas.SetLeft(_pinRing, column * _pitch - 2);
        Canvas.SetTop(_pinRing, row * _pitch - 2);
        _pinRing.Visibility = Visibility.Visible;
    }
}
