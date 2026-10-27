using Quotalis.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Quotalis.UI.Expanded;

public sealed partial class ModuleStateView : UserControl
{
    private ModuleViewModel _viewModel = null!;

    public ModuleStateView()
    {
        InitializeComponent();
    }

    public ModuleViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Icon.Module = value.Module;
        }
    }

    private void OnAction(object sender, RoutedEventArgs e) => ViewModel.RequestAction();
}
