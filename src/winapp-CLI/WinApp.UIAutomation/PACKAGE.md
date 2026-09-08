# Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation

Inspect and drive any running Windows desktop app from code — the UI Automation engine behind the
`winapp ui` commands, packaged as a library.

```console
dotnet add package Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation
dotnet add package Microsoft.Extensions.DependencyInjection
dotnet add package Microsoft.Extensions.Logging
```

```csharp
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection()
    .AddLogging()
    .AddWinAppUiAutomation()
    .BuildServiceProvider();

var ui = services.GetRequiredService<IUiAutomation>();
var resolver = services.GetRequiredService<IUiTargetResolver>();

var target = await resolver.ResolveAsync("notepad", hwnd: null, CancellationToken.None);
var button = await ui.FindSingleElementAsync(target, new UiSelector { Query = "Save" }, CancellationToken.None);
await ui.InvokeAsync(target, button!, CancellationToken.None);
```

## What you get

- **Element inspection and search** — walk the UIA tree, or find one element by a stable semantic
  slug (`btn-save-a1b2`) or a plain-text query.
- **Interaction through UIA patterns** — invoke, set value, focus, scroll, scroll-into-view, read
  text.
- **Input injection** — keyboard, mouse, touch and pen, including gestures.
- **Window capture** — screenshots as raw BGRA pixels.

Video recording is a separate package,
`Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording`, so that projects which only
inspect and drive UI do not take a dependency on SkiaSharp.

## Choosing a target framework

The package ships two targets with the same public API:

| Target framework | Screenshots | Continuous frame capture | Added to your app |
|---|---|---|---|
| `net10.0-windows` | `PrintWindow` (GDI) | not available | ~1.5 MB |
| `net10.0-windows10.0.19041.0` | Windows Graphics Capture, falling back to `PrintWindow` | available | ~1.5 MB + ~24 MB Windows SDK projection |

Prefer `net10.0-windows` unless you need Graphics Capture. Graphics Capture matters when you have
to screenshot a window that is **occluded or in the background**, or one whose content is
GPU-composited (WinUI 3, DirectX, video), where `PrintWindow` can return a blank frame.

`IWindowCapture.IsFrameCaptureSupported` reports which implementation you got at runtime.

## Using it with MSTest

The package pairs with `MSTest.Windows.UIAutomation`, which launches the app and hands you the main
window as a UIA2 `AutomationElement`. Bridge into this library through the window handle:

```csharp
[STATestClass]
public class CalculatorTests : WindowTest
{
    protected override ProcessStartInfo CreateProcessStartInfo() => new("calc.exe");

    [TestMethod]
    public async Task Clicking_Seven_ShowsSeven()
    {
        var target = UiTarget.FromWindowHandle(MainWindow.Current.NativeWindowHandle);
        var seven = await _ui.FindSingleElementAsync(target, new UiSelector { Query = "Seven" }, default);
        await _ui.InvokeAsync(target, seven!, default);
    }
}
```

Your test project must target a framework this package supports — for example `net10.0-windows`.

## Input injection drives the real mouse and keyboard

`IKeyboardInput`, `IMouseInput`, and `IPointerInput` send system-wide input, exactly as a person at
the machine would: clicks land wherever the cursor is moved, and keystrokes go to whichever window
holds focus at that instant. If a popup, a UAC prompt, or a screen lock steals focus mid-test, the
input goes there instead of your app.

Run this on a dedicated interactive desktop rather than the one you are working on, and note that
injection does nothing useful over a disconnected RDP session, where there is no live desktop to
receive it.

## Requirements

Windows 10 version 1809 or later for synthetic pen and touch injection; other features work on
earlier Windows 10 releases.
