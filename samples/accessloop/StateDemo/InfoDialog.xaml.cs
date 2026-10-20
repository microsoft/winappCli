using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AccessLoop.StateDemo;

public sealed partial class InfoDialog : ContentDialog
{
    public InfoDialog()
    {
        InitializeComponent();
        Opened += InfoDialog_Opened;
    }

    private void InfoDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
        {
            if (ReferenceEquals(popup.Child, this))
            {
                // WinUI copies the dialog ID to this wrapper; give the two UIA nodes distinct IDs.
                AutomationProperties.SetAutomationId(popup, "InfoDialogPopup");
                return;
            }
        }
    }

    private void DialogIconButton_Click(object sender, RoutedEventArgs e) =>
        DetailStatus.Text = "Detail reviewed in this preview.";

    private void CloseInfoDialog_Click(object sender, RoutedEventArgs e) => Hide();
}
