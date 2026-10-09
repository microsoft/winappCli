using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace TidePlanner.ViewModels;

public partial class StatusViewModel : ObservableObject
{
    [ObservableProperty]
    private string status = "Idle";

    public async Task RefreshAsync()
    {
        await Task.Run(() =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            queue?.TryEnqueue(() => Status = "Complete");
        });
    }
}
