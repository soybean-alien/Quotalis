using Claudex.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Claudex.UI.Expanded;

public sealed partial class UsagePanel : UserControl
{
    private UsageViewModel _viewModel = null!;

    public UsagePanel()
    {
        InitializeComponent();
    }

    public UsageViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Heatmap.ViewModel = value;
            value.HourProfileChanged += () => Hours.Set(value.HourFractions, value.PeakHourMask);
            Hours.HourHovered += value.HoverHour;
            Hours.Set(value.HourFractions, value.PeakHourMask);
            value.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(UsageViewModel.ColumnCount) or nameof(UsageViewModel.ShowBoth) or "" or null)
                {
                    ApplyColumns();
                }

                if (e.PropertyName is nameof(UsageViewModel.IsCondensed) or nameof(UsageViewModel.IsInspecting) or "" or null)
                {
                    ApplyCondensed();
                }
            };
            ApplyColumns();
            ApplyCondensed();
        }
    }

    /// <summary>
    /// The usage strip keeps plan usage, the gold totals and the heatmap. A day's readout appears only while a day is
    /// hovered or pinned. The island's spring animates the height change, and its shape clips the content.
    /// </summary>
    private void ApplyCondensed()
    {
        if (_viewModel is null)
        {
            return;
        }

        var condensed = _viewModel.IsCondensed;
        ClaudeSummary.Condensed = condensed;
        CodexSummary.Condensed = condensed;
        HermesSummary.Condensed = condensed;
        RestDetails.Visibility = condensed ? Visibility.Collapsed : Visibility.Visible;
        Footnote.Visibility = condensed ? Visibility.Collapsed : Visibility.Visible;
        ReadoutArea.Visibility = !condensed || _viewModel.IsInspecting ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The shown columns (Claude, Codex, Hermes, in that order) share the width; hidden ones take none.</summary>
    private void ApplyColumns()
    {
        if (_viewModel is null)
        {
            return;
        }

        var shown = new List<FrameworkElement>();
        if (_viewModel.ShowClaude) shown.Add(ClaudeSummary);
        if (_viewModel.ShowCodex) shown.Add(CodexSummary);
        if (_viewModel.ShowHermes) shown.Add(HermesSummary);
        // Exactly one definition per shown column: Grid spaces every definition, even an empty one.
        ProvidersGrid.ColumnDefinitions.Clear();
        for (var i = 0; i < Math.Max(1, shown.Count); i++)
        {
            ProvidersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var i = 0; i < shown.Count; i++)
        {
            Grid.SetColumn(shown[i], i);
        }

        ProvidersGrid.ColumnSpacing = shown.Count > 1 ? 24 : 0;
    }
}
