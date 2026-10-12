namespace Ridgeway.Sync;

public partial class SyncForm : Form
{
    private async void SyncButton_Click(object sender, EventArgs e)
    {
        var count = await SyncService.RunAsync();
        statusLabel.Text = $"Synced {count} files";
    }
}
