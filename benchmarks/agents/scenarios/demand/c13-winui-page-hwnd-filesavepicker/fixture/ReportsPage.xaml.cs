using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;

namespace HarborMetrics.Pages;

public sealed partial class ReportsPage : Page
{
    public ReportsPage()
    {
        InitializeComponent();
    }

    private async void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        FileSavePicker savePicker = new();
        var window = (MainWindow)Application.Current.MainWindow;
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);

        savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        savePicker.FileTypeChoices.Add("Text report", new List<string> { ".txt" });
        savePicker.SuggestedFileName = "InspectionReport";

        StorageFile file = await savePicker.PickSaveFileAsync();
        if (file is not null)
        {
            CachedFileManager.DeferUpdates(file);
            await FileIO.WriteTextAsync(file, "report contents");
            await CachedFileManager.CompleteUpdatesAsync(file);
        }
    }
}
