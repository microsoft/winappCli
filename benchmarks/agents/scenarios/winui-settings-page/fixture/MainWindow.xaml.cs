using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ContosoReader;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ContentFrame.Navigate(typeof(LibraryPage));
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is "Library")
        {
            ContentFrame.Navigate(typeof(LibraryPage));
        }
    }
}
