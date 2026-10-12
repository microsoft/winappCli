using Windows.ApplicationModel;

namespace ContosoAlerts;

public partial class SettingsForm : Form
{
    private async void StartWithWindows_CheckedChanged(object sender, EventArgs e)
    {
        var task = await StartupTask.GetAsync("ContosoAlertsStartup");
        if (((CheckBox)sender).Checked) await task.RequestEnableAsync(); else task.Disable();
    }
}
