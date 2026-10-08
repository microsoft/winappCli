using System.Windows;
namespace Tidewell;
public partial class IntakeDialog : Window {
    public IntakeDialog() { InitializeComponent(); }
    private void SubmitClicked(object sender, RoutedEventArgs e) {
        Dispatcher.BeginInvoke(new System.Action(() => Receipt.Text = "Queued"));
    }
}
