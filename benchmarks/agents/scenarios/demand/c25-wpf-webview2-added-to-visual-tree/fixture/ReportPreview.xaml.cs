using Microsoft.Web.WebView2.Wpf;
using System.Windows.Controls;

namespace WillowBank;

public partial class ReportPreview : UserControl
{
    public ReportPreview()
    {
        InitializeComponent();
        _ = ShowHtmlAsync("<html><body><h1>Quarterly report</h1></body></html>");
    }

    private async Task ShowHtmlAsync(string html)
    {
        var webView = new WebView2();
        await webView.EnsureCoreWebView2Async(null);
        webView.NavigateToString(html);
    }
}
