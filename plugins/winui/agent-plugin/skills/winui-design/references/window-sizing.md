# Window sizing

## Window sizing (WinUI 3 specifics)

> **WinUI 3 has no `SizeToContent`.** Without an explicit size, Windows defaults the main window to ~1024×768 — oversized for most utilities. Size it in `MainWindow`'s constructor.

**Rubric.** Width = widest row + 48 padding, rounded up to nearest 20. Height = 32 (titlebar) + Σ(row heights) + Σ(spacing) + 48 padding, rounded up to 20. Round up — clipped content is a worse failure than a slightly-wide window. Sanity ranges (derive yours from the rubric):

- Single-purpose utility → ~440–560 wide
- Form / single-page tool → ~600–800 wide, ~640–800 tall
- Multi-pane (nav + content) → ~1100–1300 wide, ~720–840 tall
- Document / canvas / media editor → 1280+ wide

`AppWindow.Resize` takes **physical pixels**, not DIPs — multiply by the monitor's DPI scale. `XamlRoot.RasterizationScale` is null in the constructor and stale after `AppWindow.Move`, so `[DllImport] GetDpiForWindow` is the cleanest path:

```csharp
using Microsoft.UI;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using Windows.Graphics;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();
        var hwnd  = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        // widthDip / heightDip come from the rubric above — derive, don't copy.
        AppWindow.Resize(new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale)));
    }
}
```

Don't size the window by setting `Width`/`Height` on the root `Grid` — that clips content, not the window.
