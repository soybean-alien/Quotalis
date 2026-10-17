using Claudex.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Claudex.UI.Island;

public sealed partial class ActivityView : UserControl
{
    public ActivityView()
    {
        InitializeComponent();
    }

    public ActivityViewModel ViewModel { get; set; } = null!;
}
