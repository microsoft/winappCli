using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Maestro.ViewModels;

public partial class LibraryViewModel
{
    [RelayCommand]
    private async Task AddNewSong()
    {
        ContentDialog dialog = new();
        dialog.XamlRoot = null;
        dialog.Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style;
        dialog.Title = "Add a new song";
        dialog.PrimaryButtonText = "Add";
        dialog.CloseButtonText = "Cancel";
        dialog.Content = App.GetService<Views.Dialogs.LibraryAddSongControl>();

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            App.GetService<LibraryAddSongViewModel>().AddSong();
        }
    }
}
