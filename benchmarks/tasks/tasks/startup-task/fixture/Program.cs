namespace SyncTray;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var icon = new NotifyIcon { Icon = SystemIcons.Information, Text = "SyncTray - up to date", Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Sync now", null, (_, _) => icon.ShowBalloonTip(2000, "SyncTray", "Sync complete.", ToolTipIcon.Info));
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        icon.ContextMenuStrip = menu;
        Application.Run();
    }
}
