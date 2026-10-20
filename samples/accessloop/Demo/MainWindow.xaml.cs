using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace AccessLoop.Demo;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void DocumentTitleInput_Loaded(object sender, RoutedEventArgs e)
    {
        var scale = DocumentTitleInput.XamlRoot.RasterizationScale;
        AppWindow.ResizeClient(new SizeInt32((int)(720 * scale), (int)(460 * scale)));
        DocumentTitleInput.Focus(FocusState.Programmatic);
    }

    private void SaveDocument_Click(object sender, RoutedEventArgs e)
    {
        SaveStatus.Text = "Saved: " + DocumentTitleInput.Text;
    }

    private void ExportDocument_Click(object sender, RoutedEventArgs e)
    {
        SaveStatus.Text = "Exported: " + DocumentTitleInput.Text;
    }
}
