using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AccessLoop.JourneyDemo.Pages;

public sealed partial class ExportPage : UserControl
{
    public ExportPage()
    {
        InitializeComponent();
    }

    private void ExportDocument_Click(object sender, RoutedEventArgs e)
    {
        ExportStatus.Text = "Exported: " + ExportTitleInput.Text;
    }
}
