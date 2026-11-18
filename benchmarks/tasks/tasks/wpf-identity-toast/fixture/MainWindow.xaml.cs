using System.Windows;

namespace PingNotifier;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void OnTestClick(object sender, RoutedEventArgs e)
    {
        try
        {
            App.ShowToast("Host down", $"{HostBox.Text} is not responding.");
            Status.Text = "Notification sent.";
        }
        catch (Exception ex)
        {
            Status.Text = $"Notification failed: {ex.Message}";
        }
    }
}
