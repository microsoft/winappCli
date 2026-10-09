using Microsoft.Web.WebView2.Core;
using System.Windows.Controls;

namespace LedgerView;

public partial class BrowserPane : UserControl
{
    public BrowserPane()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.Navigate("https://portal.intra.example");
        };
    }
}
