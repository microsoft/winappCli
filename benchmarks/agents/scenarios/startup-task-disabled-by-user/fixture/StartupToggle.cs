using Windows.ApplicationModel;

namespace Saltmarsh.Tray;

internal static class StartupToggle
{
    public static async Task<bool> EnableAsync()
    {
        var task = await StartupTask.GetAsync("SaltmarshStartup");
        var state = await task.RequestEnableAsync();
        return state == StartupTaskState.Enabled;   // always DisabledByUser on my PC
    }
}
