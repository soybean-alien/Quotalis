using Claudex.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Claudex.UI.Expanded;

public sealed partial class CalendarPanel : UserControl
{
    private CalendarViewModel _viewModel = null!;

    public CalendarPanel()
    {
        InitializeComponent();
    }

    public CalendarViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            StateView.ViewModel = value;
        }
    }
}
