using Claudex.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Claudex.UI.Expanded;

public sealed partial class GitHubPanel : UserControl
{
    private GitHubViewModel _viewModel = null!;

    public GitHubPanel()
    {
        InitializeComponent();
    }

    public GitHubViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            StateView.ViewModel = value;
        }
    }
}
