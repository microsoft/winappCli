using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AccessLoop.StateDemo;

public sealed partial class NameChecksPage : UserControl
{
    private int refreshCount;

    public NameChecksPage()
    {
        InitializeComponent();
        CorrectNames.Checked += CorrectNames_Changed;
        CorrectNames.Unchecked += CorrectNames_Changed;
    }

    public void Reset()
    {
        CorrectNames.IsChecked = false;
        UpdateNames();
        refreshCount = 0;
        ActionStatus.Text = "Preview idle";
    }

    private void CorrectNames_Changed(object sender, RoutedEventArgs e) => UpdateNames();

    private void UpdateNames()
    {
        if (CorrectNames.IsChecked == true)
        {
            AutomationProperties.SetName(CaseB, "Refresh preview");
            AutomationProperties.SetName(CaseF, "Refresh preview");
            NameState.Text = "Corrected names";
        }
        else
        {
            CaseB.ClearValue(AutomationProperties.NameProperty);
            AutomationProperties.SetName(CaseF, "Delete layout");
            NameState.Text = "Original names";
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        refreshCount++;
        ActionStatus.Text = $"Preview refreshed {refreshCount} time(s)";
    }
}
