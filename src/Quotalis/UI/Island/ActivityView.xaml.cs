using Quotalis.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Quotalis.UI.Island;

public sealed partial class ActivityView : UserControl
{
    public ActivityView()
    {
        InitializeComponent();
    }

    public ActivityViewModel ViewModel { get; set; } = null!;
}
