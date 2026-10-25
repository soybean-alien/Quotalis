using Claudex.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Claudex.UI.Expanded;

public sealed partial class HermesSummary : UserControl
{
    public HermesSummary()
    {
        InitializeComponent();
    }

    public HermesViewModel ViewModel { get; set; } = null!;

    /// <summary>The usage strip: hides today's token use, keeping the wallet and the model.</summary>
    public bool Condensed
    {
        set => TodayDetails.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSignIn(object sender, RoutedEventArgs e) => ViewModel.RequestSignIn();

    private void OnTopUp(object sender, RoutedEventArgs e) => ViewModel.RequestTopUp();
}
