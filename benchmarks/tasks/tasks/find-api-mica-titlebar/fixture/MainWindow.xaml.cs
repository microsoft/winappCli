using Microsoft.UI.Xaml;

namespace PhotoNotes;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Notes.ItemsSource = new[] { "Beach, July", "Kitchen remodel", "Garden progress" };
    }
}
