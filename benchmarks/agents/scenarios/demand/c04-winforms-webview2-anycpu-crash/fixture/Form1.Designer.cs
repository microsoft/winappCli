namespace HarborDesk
{
    partial class Form1
    {
        private Microsoft.Web.WebView2.WinForms.WebView2 webView21;

        private void InitializeComponent()
        {
            this.webView21 = new Microsoft.Web.WebView2.WinForms.WebView2();
            this.webView21.Location = new System.Drawing.Point(153, 66);
            this.webView21.Name = "webView21";
            this.webView21.Size = new System.Drawing.Size(492, 253);
            this.webView21.Source = new System.Uri("about:blank", System.UriKind.Absolute);
            this.webView21.TabIndex = 0;
            this.webView21.Text = "webView21";
            this.webView21.ZoomFactor = 1D;
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.Controls.Add(this.webView21);
        }
    }
}
