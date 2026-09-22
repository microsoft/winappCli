using Microsoft.UI;
using Microsoft.UI.Xaml;
using PerformanceDiagnosticsLab.Services;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace PerformanceDiagnosticsLab;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    public MainWindow(AppLaunchContext launchContext)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        var windowHandle = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(windowHandle) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(780 * scale)));

        RootFrame.Navigate(typeof(MainPage), launchContext);
    }
}
