using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AccessLoop.JourneyDemo.Pages;

public sealed partial class DocumentsPage : UserControl
{
    public DocumentsPage()
    {
        InitializeComponent();
    }

    private void SaveDocument_Click(object sender, RoutedEventArgs e)
    {
        SaveStatus.Text = "Saved: " + DocumentTitleInput.Text;
    }
}
