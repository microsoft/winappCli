using Microsoft.UI.Xaml;

namespace TodoBench;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Nav.Navigate(typeof(ContactsPage));
    }
}
