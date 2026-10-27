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
/// Contribution-style usage graph: one column per week, one row per weekday. Claude and Codex are
/// pooled; the purple intensity (dim, brighter, brightest) follows thirds of the active days.
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
    public const double CellSize = 11;
    public const double Gap = 3;
    private const double Pitch = CellSize + Gap;

    private static readonly SolidColorBrush[] LevelBrushes =
    [
        new(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
        new(Color.FromArgb(0xFF, 0x3B, 0x2A, 0x6B)),
        new(Color.FromArgb(0xFF, 0x6A, 0x52, 0xC4)),
        new(Color.FromArgb(0xFF, 0xA9, 0x96, 0xFF)),
    ];

    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Rectangle _ring;
    private readonly Rectangle _pinRing;
    private readonly List<Rectangle> _cells = [];
    private readonly List<(int Column, int Row)> _positions = [];
    private Quotalis.Core.Usage.HeatmapLayout _layout = Quotalis.Core.Usage.HeatmapLayout.For([], DayOfWeek.Monday);
    private UsageViewModel? _viewModel;
    private int _columns;
    private DateOnly _firstDay;

    public UsageHeatmap()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        FocusVisualMargin = new Thickness(-4);
        _ring = new Rectangle
        {
            Width = CellSize + 4,
            Height = CellSize + 4,
            RadiusX = 4,
            RadiusY = 4,
            Stroke = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            StrokeThickness = 1.5,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _pinRing = new Rectangle
        {
            Width = CellSize + 4,
            Height = CellSize + 4,
            RadiusX = 4,
            RadiusY = 4,
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
                var level = i < current.Levels.Count ? current.Levels[i] : 0;
                _cells[i].Fill = LevelBrushes[Math.Clamp(level, 0, LevelBrushes.Length - 1)];
            }

            UpdateRing();
            return;
        }

        _canvas.Children.Clear();
        _cells.Clear();
        _positions.Clear();
        if (_viewModel is not { } vm || vm.Days.Count == 0)
        {
            Width = Height = 0;
            return;
        }

        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        _layout = Quotalis.Core.Usage.HeatmapLayout.For(vm.Days.Select(d => d.Day).ToList(), firstDay);

        for (var i = 0; i < vm.Days.Count; i++)
        {
            var (column, row) = _layout.Positions[i];
            var level = i < vm.Levels.Count ? vm.Levels[i] : 0;
            var cell = new Rectangle
            {
                Width = CellSize,
                Height = CellSize,
                RadiusX = 2.5,
                RadiusY = 2.5,
                Fill = LevelBrushes[Math.Clamp(level, 0, LevelBrushes.Length - 1)],
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(cell, column * Pitch);
            Canvas.SetTop(cell, row * Pitch);
            _canvas.Children.Add(cell);
            _cells.Add(cell);
            _positions.Add((column, row));
        }

        _columns = _layout.Columns;
        _firstDay = vm.Days[0].Day;
        _canvas.Children.Add(_pinRing);
        _canvas.Children.Add(_ring);
        Width = _columns * Pitch - Gap;
        Height = 7 * Pitch - Gap;
        UpdateRing();
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

        var index = _layout.IndexAt(point.Position.X, point.Position.Y, Pitch);
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

        _viewModel.Inspect(_layout.IndexAt(point.X, point.Y, Pitch));
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
        Canvas.SetLeft(_ring, column * Pitch - 2);
        Canvas.SetTop(_ring, row * Pitch - 2);
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
        Canvas.SetLeft(_pinRing, column * Pitch - 2);
        Canvas.SetTop(_pinRing, row * Pitch - 2);
        _pinRing.Visibility = Visibility.Visible;
    }
}
