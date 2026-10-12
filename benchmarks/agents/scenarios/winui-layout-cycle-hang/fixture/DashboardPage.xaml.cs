using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Harborlight;

public sealed partial class DashboardPage : Page
{
    public DashboardPage() => InitializeComponent();

    private void MainGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep tiles square: grow the grid to fit the tiles.
        MainGrid.Height = e.NewSize.Width / 3 + 1;
    }
}
