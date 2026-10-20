using AccessLoop.JourneyDemo.Pages;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace AccessLoop.JourneyDemo;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PageHost.Content = new HomePage();
    }

    private void WindowRoot_Loaded(object sender, RoutedEventArgs e)
    {
        WindowRoot.Loaded -= WindowRoot_Loaded;
        var scale = WindowRoot.XamlRoot.RasterizationScale;
        AppWindow.ResizeClient(new SizeInt32((int)(960 * scale), (int)(640 * scale)));
    }

    private void NavHome_Click(object sender, RoutedEventArgs e)
    {
        PageHost.Content = new HomePage();
    }

    private void NavDocuments_Click(object sender, RoutedEventArgs e)
    {
        PageHost.Content = new DocumentsPage();
    }

    private void NavExport_Click(object sender, RoutedEventArgs e)
    {
        PageHost.Content = new ExportPage();
    }
}
