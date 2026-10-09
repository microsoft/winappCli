using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace MapArchive;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(AppWindow.Id);
        var result = await picker.PickSingleFolderAsync();
        FolderPathTextBlock.Text = result?.Path ?? "(cancelled)";
    }
}
