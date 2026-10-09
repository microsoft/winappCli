using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Windows.Controls;

namespace HarborAudit.Views;

public partial class BrowserView : UserControl
{
    public BrowserView()
    {
        InitializeComponent();
        InitializeAsync();
    }

    private async void InitializeAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HarborAudit",
            "WebView2");

        var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await WebView.EnsureCoreWebView2Async(environment);
        WebView.Source = new Uri("https://reports.harbor-audit.example.com/");
    }
}
