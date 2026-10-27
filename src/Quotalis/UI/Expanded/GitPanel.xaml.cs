using Quotalis.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Quotalis.UI.Expanded;

public sealed partial class GitPanel : UserControl
{
    private GitViewModel _viewModel = null!;

    public GitPanel()
    {
        InitializeComponent();
    }

    public GitViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Contributions.ViewModel = value;
            StateView.ViewModel = value;
        }
    }

    private void OnSelectRepo(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string root })
        {
            ViewModel.Select(root);
        }
    }
}
