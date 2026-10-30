using Quotalis.Core.Modules;
using Quotalis.UI.Animations;
using Quotalis.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Quotalis.UI.Island;

public sealed partial class CompactView : UserControl
{
    private IslandViewModel _viewModel = null!;

    public CompactView()
    {
        InitializeComponent();
    }

    public IslandViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            value.CompactModuleChanged += OnFeaturedChanged;
            ShowFeaturedIcon();
        }
    }

    /// <summary>A different module is featured: swap its icon and cross-fade the content.</summary>
    private void OnFeaturedChanged()
    {
        ShowFeaturedIcon();
        if (_viewModel.IsFeaturing && IsLoaded)
        {
            Motion.FadeIn(Featured, delay: TimeSpan.Zero, fromY: 0, fromScale: 0.98f);
        }
    }

    private void ShowFeaturedIcon()
    {
        var module = _viewModel.CompactModule;
        var isProvider = module is ModuleId.Claude or ModuleId.Codex;
        var isHermes = module == ModuleId.Hermes;
        var isTotal = _viewModel.CompactShowsTotal;
        FeaturedMark.Visibility = isProvider ? Visibility.Visible : Visibility.Collapsed;
        FeaturedHermesDot.Visibility = isHermes ? Visibility.Visible : Visibility.Collapsed;
        FeaturedTotalDots.Visibility = isTotal ? Visibility.Visible : Visibility.Collapsed;
        FeaturedModuleIcon.Visibility = isProvider || isHermes || isTotal ? Visibility.Collapsed : Visibility.Visible;
        if (isProvider)
        {
            FeaturedMark.Kind = module == ModuleId.Claude ? "Claude" : "Codex";
        }
        else if (module is { } m && !isHermes)
        {
            FeaturedModuleIcon.Module = m;
        }
    }
}
