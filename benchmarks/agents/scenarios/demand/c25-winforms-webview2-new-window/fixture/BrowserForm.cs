using Microsoft.Web.WebView2.WinForms;

namespace Pinegrove.Support;

public partial class BrowserForm : Form
{
    private readonly WebView2 browser = new();

    public BrowserForm()
    {
        InitializeComponent();
    }

    private async void BrowserForm_Load(object sender, EventArgs e)
    {
        browser.Dock = DockStyle.Fill;
        Controls.Add(browser);
        await browser.EnsureCoreWebView2Async();
        browser.Source = new Uri("https://support.pinegrove.example.com/start.html");
    }
}
