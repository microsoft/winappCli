# Threading and Windowing

Use this reference when migrating `Dispatcher`, top-level windows, dialogs, file pickers, sizing/positioning, HWND interop, or custom process entry points.

## Dispatcher Is a Semantic Migration

WPF `Dispatcher.Invoke` can synchronously execute a callback and propagate its result/exception. WinUI `DispatcherQueue.TryEnqueue` queues work, returns whether enqueueing succeeded, and does not directly return the callback result.

| WPF | WinUI 3 |
|---|---|
| `Dispatcher.CheckAccess()` | `DispatcherQueue.HasThreadAccess` |
| `Dispatcher.VerifyAccess()` | Check the owning queue and throw when access is invalid. |
| `Dispatcher.BeginInvoke(...)` | `DispatcherQueue.TryEnqueue(...)` |
| `Dispatcher.Invoke(...)` | Preserve completion, exceptions, and the original overload's queue order; see [Awaiting Enqueued Work](#awaiting-enqueued-work). |
| `DispatcherPriority` | `DispatcherQueuePriority` (`High`, `Normal`, `Low`); map intent, not enum names. |

Preserve `VerifyAccess` as a guard on the captured owning queue; enqueueing instead would change its failure behavior:

```csharp
if (!dispatcherQueue.HasThreadAccess)
    throw new InvalidOperationException("This operation requires the owning UI thread.");
```

## Capture the Owning Queue

Capture the queue from the UI object or UI thread that owns the state. Do not call `DispatcherQueue.GetForCurrentThread()` on an arbitrary worker and assume that proves UI access.

```csharp
public sealed class UiUpdater
{
    private readonly DispatcherQueue _dispatcherQueue;

    public UiUpdater(FrameworkElement owner)
    {
        _dispatcherQueue = owner.DispatcherQueue;
    }

    public bool TryPost(Action action) =>
        _dispatcherQueue.TryEnqueue(() => action());
}
```

`Action` and `DispatcherQueueHandler` are distinct delegate types, so an `Action` value cannot be passed directly even though both signatures are `void ()`; the target-typed lambda adapts the callback. This helper always enqueues, including when called from the owning thread. WPF `Dispatcher.BeginInvoke` also adds work to the dispatcher event queue and returns immediately; running inline would change ordering, reentrancy, exception timing, and the meaning of the shutdown result.

Check whether `TryPost` returns `false` during shutdown. Use a `HasThreadAccess` fast path only for invoke-and-observe behavior, not for posting.

## Awaiting Enqueued Work

Use a task when the caller needs completion, a return value, or exception propagation. The helper below runs inline on the owning thread; check the original WPF `Invoke` overload and its effective priority first, because non-`Send` calls queue even on that thread.

```csharp
public static Task RunOnUiThreadAsync(
    DispatcherQueue queue,
    Action action)
{
    if (queue.HasThreadAccess)
    {
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    var completion = new TaskCompletionSource<object?>(
        TaskCreationOptions.RunContinuationsAsynchronously);

    if (!queue.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }))
    {
        completion.TrySetException(
            new InvalidOperationException("The UI dispatcher rejected the work item."));
    }

    return completion.Task;
}
```

This overload is only for synchronous `Action` callbacks. Do not pass an `async` lambda, which would become `async void` and make the returned task complete before the asynchronous work while losing exception propagation. Provide a separate `Func<Task>` overload that awaits the callback when dispatched work is asynchronous.

When the WPF call returned a value, use the same pattern with `Func<T>` and `TaskCompletionSource<T>`.

## View Models and Services

- UI-bound collection/property updates must occur on the owning UI thread unless the binding source explicitly supports another model.
- Keep CPU-bound and I/O-bound work off the UI thread; dispatch only the minimal state update.

For an existing C++/WinRT component, replace `CoreDispatcher.RunAsync` with `DispatcherQueue.TryEnqueue`, and use WIL to resume a coroutine on a WinUI queue:

```cpp
#include <winrt/Microsoft.UI.Dispatching.h>
#include <wil/cppwinrt_helpers.h> // Include after the queue's projected header.

// Inside the coroutine, using the owning UI queue:
co_await wil::resume_foreground(dispatcherQueue);
```

Use the project's `Microsoft.Windows.ImplementationLibrary` dependency/version for WIL. This replaces `winrt::resume_foreground(CoreDispatcher)`; C# code does not need this dependency.

## Choose the UI Surface

Choose the surface according to its role in the application:

| UI role | WinUI surface |
|---|---|
| An in-app confirmation, prompt, or dialog | `ContentDialog` attached to the owning content's `XamlRoot`. |
| A child page or navigation destination | A `Page` hosted by a `Frame` in the current window. |
| An independent top-level interface | A window with its own lifetime, activation, and ownership. |

Use a separate window when the interface needs that independent top-level behavior. Keep dialogs and child pages within their owning window.

## WinUI Window Model

Start with `Microsoft.UI.Xaml.Window` for a top-level window. It hosts the view but is not a `FrameworkElement`.

| WPF `Window` feature | WinUI migration |
|---|---|
| `Resources`, `DataContext` | Put them on the hosted view or its root element. For visual state groups, use the [content or template root](xaml-migration.md#triggers-to-visual-states). |
| `Application.Current.MainWindow` / `Window.Current` | Track window references explicitly. |
| `Width`, `Height` | Use `AppWindow.Resize` for the whole window or `ResizeClient` for its content area; convert the original WPF size to physical pixels at the target monitor's scale. |
| `MinWidth`, `MinHeight`, `MaxWidth`, `MaxHeight` | On Windows App SDK 1.7 or later, set the matching [OverlappedPresenter](https://learn.microsoft.com/windows/apps/develop/ui-input/manage-app-windows#modify-an-overlappedpresenter) property: `PreferredMinimumWidth`, `PreferredMinimumHeight`, `PreferredMaximumWidth`, or `PreferredMaximumHeight`. Convert each WPF limit from device-independent pixels to physical pixels for the whole window, including its frame. Update the limits when the window's DPI changes. |
| `WindowState`, `ResizeMode`, `Topmost` | Use `OverlappedPresenter` for minimize/maximize/restore, `IsResizable`, `IsMinimizable`, `IsMaximizable`, and `IsAlwaysOnTop`. |
| `Left`, `Top` | Position the `AppWindow` with `Move`. |
| `SizeToContent` | Measure content and size the `AppWindow`. |
| `WindowStartupLocation="Manual"` (default) | Keep each specified `Left` or `Top` coordinate. Let Windows choose any unset coordinate; do not center the window. |
| `WindowStartupLocation="CenterScreen"` | Center in the work area of the owner's monitor if the app has set an owner window with a handle. Otherwise, use the monitor containing the pointer. |
| `WindowStartupLocation="CenterOwner"` | For a minimized or maximized WPF owner, use `CenterScreen`. Otherwise, center over the owner and keep the window within its work area when possible. If no owner bounds are available, keep `Manual` placement. |
| `Closing` / cancellation | Use the supported WinUI/`AppWindow` closing event and preserve hide-to-tray or cancellation behavior. |
| `Loaded` / `Unloaded` | Attach these handlers to the hosted `FrameworkElement`, not the WinUI `Window`. |
| `Title` | Use `Window.Title` or `AppWindow.Title`. |
| `Icon` | Use `AppWindow.SetIcon(path)` with the deployed icon file. |
| `ShowInTaskbar` | `AppWindow.IsShownInSwitchers` also affects Alt-Tab, so verify both behaviors. |

### Projects already using WinUIEx

Keep the project's `WindowEx` base in both XAML and code-behind. Its `MinWidth`/`MinHeight` are in device-independent pixels, and its presenter properties such as `IsResizable` can be set on the window itself. For a window that was already centered on startup, `this.CenterOnScreen(width: 800, height: 600)` also takes device-independent pixels.

If the app already restores window placement, set its stable `PersistenceId` before first activation and do not center it again after restoration. WinUIEx uses `ApplicationData` by default for packaged apps; unpackaged apps must load and save their own `WindowManager.PersistenceStorage`. Restoration depends on the monitor layout remaining unchanged.

## Track Windows Explicitly

For a single-window application:

```csharp
public partial class App : Application
{
    public Window? MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
```

For applications with multiple windows, use the correct owner for dialogs, pickers, and commands, and remove closed window references. Preserve which window controls process lifetime.

## Access AppWindow and HWND

Use the `Window.AppWindow` property when it is available in the target Windows App SDK. When an older target or an API requires HWND interop, derive the identifiers explicitly:

```csharp
using Microsoft.UI;
using Microsoft.UI.Windowing;
using WinRT.Interop;

IntPtr hwnd = WindowNative.GetWindowHandle(window);
WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
```

## Size and Position

`AppWindow` sizing APIs use physical pixels while XAML layout uses effective pixels/DIPs. Account for the target monitor's scale and for changes after moving between monitors.

For a normal overlapped window, measure after the hosted view loads. Bound the available client area using the monitor's work area and the current non-client size. `ResizeClient` calculates the required window chrome; do not add a fixed title-bar height.

This example sizes both axes (`SizeToContent.WidthAndHeight`). For `Height` or `Width`, keep the other client dimension unchanged in `ResizeClient` and constrain `Measure` using that dimension divided by `RasterizationScale`; skip this content-based resize for `Manual`.

```csharp
if (window.Content is FrameworkElement content && content.XamlRoot is { } root)
{
    DisplayArea display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
    double scale = root.RasterizationScale;
    int chromeWidth = Math.Max(0, appWindow.Size.Width - appWindow.ClientSize.Width);
    int chromeHeight = Math.Max(0, appWindow.Size.Height - appWindow.ClientSize.Height);
    int maxWidth = Math.Max(1, display.WorkArea.Width - chromeWidth);
    int maxHeight = Math.Max(1, display.WorkArea.Height - chromeHeight);
    content.Measure(new Windows.Foundation.Size(maxWidth / scale, maxHeight / scale));
    int width = (int)Math.Ceiling(Math.Clamp(content.DesiredSize.Width * scale, 1, maxWidth));
    int height = (int)Math.Ceiling(Math.Clamp(content.DesiredSize.Height * scale, 1, maxHeight));
    appWindow.ResizeClient(new Windows.Graphics.SizeInt32(width, height));
}
```

Apply the application's minimum/maximum sizes too; scroll overflowing content. Recalculate after navigation, content/localization/text-scale changes, and monitor/DPI changes. On targets without `ResizeClient`, calculate the outer rectangle with the target DPI's window styles before calling `Resize`.

Detach old page handlers before attaching the new page; `OnPageLoaded` can run the measurement above:

```csharp
if (previousPage is not null)
    previousPage.Loaded -= OnPageLoaded;
currentPage.Loaded += OnPageLoaded;
```

If the new page is already loaded, measure immediately. Detach the current page and any `XamlRoot.Changed` handlers when the window closes. Avoid unconditional resizing from `LayoutUpdated`/`SizeChanged`, which can trigger another layout/resize cycle.

Example center calculation once the window and display area are known:

```csharp
DisplayArea display = DisplayArea.GetFromWindowId(
    appWindow.Id,
    DisplayAreaFallback.Nearest);

int x = display.WorkArea.X +
    Math.Max(0, (display.WorkArea.Width - appWindow.Size.Width) / 2);
int y = display.WorkArea.Y +
    Math.Max(0, (display.WorkArea.Height - appWindow.Size.Height) / 2);

appWindow.Move(new Windows.Graphics.PointInt32(x, y));
```

## Title Bars and Presenters

On Windows App SDK 1.3 and later, use `Window.SystemBackdrop` when the intended design calls for a system material. For example, this child of the `Window` XAML root applies Mica:

```xml
<Window.SystemBackdrop>
    <MicaBackdrop />
</Window.SystemBackdrop>
```

`MicaBackdrop` falls back to a solid color where Mica is unsupported. Verify the material and fallback against the existing design and the target SDK/OS range.

Map window-state commands to the overlapped presenter:

```csharp
if (appWindow.Presenter is OverlappedPresenter presenter)
{
    presenter.Maximize(); // In the maximize command.
    // Use presenter.Minimize() or presenter.Restore() in their respective commands.
}
appWindow.SetIcon(iconPath); // An icon file available in the deployed application.
```

For a custom XAML title bar on a supported target:

```csharp
window.ExtendsContentIntoTitleBar = true;
window.SetTitleBar(appTitleBar);
```

On Windows App SDK 1.4 and later, the XAML window and `AppWindow` share the title-bar object. When the intended custom title bar needs transparent caption-button backgrounds, apply the native settings alongside the XAML setup above:

```csharp
if (AppWindowTitleBar.IsCustomizationSupported())
{
    AppWindowTitleBar titleBar = appWindow.TitleBar;
    titleBar.ExtendsContentIntoTitleBar = true;
    titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
    titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
}
```

Transparent caption-button backgrounds require content extended into the title bar. Preserve drag regions, caption-button contrast, and active/inactive states. For older targets, follow the title-bar APIs supported by that SDK/OS combination. Set presenter resize/minimize/maximize restrictions separately.

## ContentDialog Ownership

Desktop WinUI dialogs need the `XamlRoot` of the owning content:

```csharp
var dialog = new ContentDialog
{
    Title = "Discard changes?",
    Content = "Unsaved changes will be lost.",
    PrimaryButtonText = "Discard",
    CloseButtonText = "Cancel",
    XamlRoot = owner.XamlRoot,
};

ContentDialogResult result = await dialog.ShowAsync();
```

The owner must already be loaded into the XAML tree and have a non-null `XamlRoot` before `ShowAsync`. Do not take `XamlRoot` from an unrelated or closed window. Coordinate concurrent dialogs per root and preserve the original modality/owner behavior.

## File and Folder Pickers

Choose the API using the target Windows App SDK version and whether the application must support running as administrator.

### Windows App SDK 1.8 and later

The `Microsoft.Windows.Storage.Pickers` APIs support both normal and elevated processes. They accept a `WindowId` in their constructor and do not require `InitializeWithWindow`:

```csharp
var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(
    window.AppWindow.Id);
picker.FileTypeFilter.Add(".txt");

var result = await picker.PickSingleFileAsync();
```

This picker returns a `PickFileResult`, not the legacy `StorageFile`. To check the method on the restored project, use the fully qualified picker name in the [migration rules' `find-api` example](../SKILL.md#migration-rules).

### Older Windows App SDK targets / legacy WinRT pickers

`Windows.Storage.Pickers` supports only non-elevated processes; initializing the owning HWND does not remove that restriction. If the application must run as administrator on an older SDK, retain or use a native Win32 dialog such as `IFileDialog`. Preserve the application's execution-level policy. See the [official guidance for elevated desktop apps](https://learn.microsoft.com/uwp/api/windows.storage.pickers.filesavepicker#in-a-desktop-app-that-requires-elevation).

For a non-elevated desktop app, initialize the picker with the owning HWND:

```csharp
var picker = new Windows.Storage.Pickers.FileOpenPicker();
picker.FileTypeFilter.Add(".txt");

IntPtr hwnd = WindowNative.GetWindowHandle(window);
InitializeWithWindow.Initialize(picker, hwnd);

StorageFile? file = await picker.PickSingleFileAsync();
```

## Closing and Process Lifetime

For a tray application whose close button hides the window, set `exitRequested` in its explicit Exit command:

```csharp
appWindow.Closing += (_, args) =>
{
    if (exitRequested) return; // Explicit Exit must still close the application.
    args.Cancel = true;
    appWindow.Hide();
};
```

For an async save/discard prompt, cancel closing synchronously, prevent a second prompt while the first is pending, then request close again only after confirmation using a bypass flag. Preserve last-window/background lifetime. Do not let an `async void` handler close the window before the awaited prompt finishes.

## Custom Entry Points

Keep the generated WinUI entry point unless the process genuinely needs work before XAML application startup, such as a dual CLI/GUI mode or native initialization.

If required, start from the installed target SDK's generated `Main` and retain its apartment/runtime initialization. Append the symbol without replacing existing constants:

```xml
<DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>
```

Keep the GUI initialization order used by that template. A C# template with these APIs has this sequence inside its `[STAThread]` entry point:

```csharp
WinRT.ComWrappersSupport.InitializeComWrappers();
Microsoft.UI.Xaml.Application.Start(p =>
{
    var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(queue);
    System.Threading.SynchronizationContext.SetSynchronizationContext(context);
    _ = new App();
});
```

Preserve any additional initialization in the target template. Route a recognized non-UI command before XAML startup, initializing any WinRT services that command needs; do not create a WPF `Application` to obtain a dispatcher. Keep GUI creation on its STA thread and avoid an awaited entry point that resumes GUI startup on a worker. Arbitrary arguments may be activation data, not a request for CLI mode.

## Official References

- [`DispatcherQueue.TryEnqueue`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.dispatching.dispatcherqueue.tryenqueue)
- [WPF `Dispatcher.BeginInvoke`](https://learn.microsoft.com/dotnet/api/system.windows.threading.dispatcher.begininvoke)
- [WPF `Dispatcher.Invoke` overloads](https://learn.microsoft.com/dotnet/api/system.windows.threading.dispatcher.invoke)
- [Task-based asynchronous pattern exception handling](https://learn.microsoft.com/dotnet/standard/asynchronous-programming-patterns/task-based-asynchronous-pattern-tap#exceptions)
- [Async return types and `async void`](https://learn.microsoft.com/dotnet/csharp/asynchronous-programming/async-return-types#void-return-type)
- [Windows App SDK 1.8 release notes](https://learn.microsoft.com/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-8)
- [FileOpenPicker(WindowId) constructor](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.windows.storage.pickers.fileopenpicker.-ctor)
- [WinUIEx window base and XAML setup](https://dotmorten.github.io/WinUIEx/concepts/WindowEx.html)
- [WinUIEx `CenterOnScreen`](https://dotmorten.github.io/WinUIEx/api/WinUIEx.WindowExtensions.CenterOnScreen.html)
- [WinUIEx `PersistenceId`](https://dotmorten.github.io/WinUIEx/api/WinUIEx.WindowManager.PersistenceId.html) and [`PersistenceStorage`](https://dotmorten.github.io/WinUIEx/api/WinUIEx.WindowManager.PersistenceStorage.html)
- [WIL coroutine helpers](https://github.com/microsoft/wil/blob/master/include/wil/cppwinrt_helpers.h)
- [`AppWindow.ResizeClient`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindow.resizeclient), [`IsShownInSwitchers`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindow.isshowninswitchers), and [`Closing`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindow.closing)
- [Title bar customization](https://learn.microsoft.com/windows/apps/develop/title-bar) and [`MicaBackdrop`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.media.micabackdrop)
