# Threading and Windowing

Use this reference when migrating `Dispatcher`, top-level windows, dialogs, file pickers, sizing/positioning, HWND interop, or custom process entry points.

## Dispatcher Is a Semantic Migration

WPF `Dispatcher.Invoke` can synchronously execute a callback and propagate its result/exception. WinUI `DispatcherQueue.TryEnqueue` queues work, returns whether enqueueing succeeded, and does not directly return the callback result.

| WPF | WinUI 3 |
|---|---|
| `Dispatcher.CheckAccess()` | `DispatcherQueue.HasThreadAccess` |
| `Dispatcher.BeginInvoke(...)` | `DispatcherQueue.TryEnqueue(...)` |
| `Dispatcher.Invoke(...)` | Run directly when already on the owning thread, or enqueue and explicitly await/propagate completion when required. |
| `DispatcherPriority` | `DispatcherQueuePriority` (`High`, `Normal`, `Low`); map intent, not enum names. |

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

Use this `TryPost` shape only for work that is intentionally fire-and-forget and covered by the application's UI-thread exception policy. Check its `false` result during shutdown. Use a `HasThreadAccess` fast path only for invoke-and-observe behavior, not for posting.

Prefer passing the queue or an application-owned UI scheduler abstraction to code that needs it. Avoid turning a queue into a global static merely to reproduce `Application.Current.Dispatcher`.

## Awaiting Enqueued Work

Only wrap dispatch in a task when the caller truly needs completion, a return value, or exception propagation:

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

Review cancellation and shutdown explicitly. A queue can reject work during teardown. If cancellation is required, define whether it may cancel only before enqueue, while waiting, or also suppress an already queued callback; do not add a token whose semantics the helper cannot honor.

When the WPF call returned a value, use the same pattern with `Func<T>` and `TaskCompletionSource<T>`; do not emulate synchronous `Invoke` by blocking the worker or UI thread.

## View Models and Services

- UI-bound collection/property updates must occur on the owning UI thread unless the binding source explicitly supports another model.
- Capture the queue when constructing the view model on the UI thread, inject a scheduler abstraction, or dispatch in the view layer.
- Keep CPU-bound and I/O-bound work off the UI thread; dispatch only the minimal state update.
- Do not use `Task.Run` as a mechanical replacement for `Task.Factory.StartNew` or synchronous WPF code. Preserve cancellation, long-running semantics, ordering, and exception observation.
- Recheck timers: WPF `DispatcherTimer`, `System.Threading.Timer`, `PeriodicTimer`, and composition/UI timers have different ownership and suspension behavior.

## WinUI Window Model

WinUI `Window` is not a `DependencyObject` or `FrameworkElement`.

| WPF `Window` feature | WinUI migration |
|---|---|
| `Resources`, `DataContext`, visual states | Put them on a hosted `Page`, `UserControl`, or root `FrameworkElement`. |
| `Application.Current.MainWindow` / `Window.Current` | Track one or more windows explicitly in the application/window service. |
| `Width`, `Height`, `Left`, `Top`, `WindowState`, `ResizeMode` | Use `AppWindow`/presenter APIs or an already-approved helper library. |
| `SizeToContent` | Measure content and size the `AppWindow`, or redesign the layout with an explicit/responsive size. |
| `WindowStartupLocation` | Position through `AppWindow` and `DisplayArea`, respecting monitor work areas and DPI. |
| `Closing` / cancellation | Use the supported WinUI/`AppWindow` closing event and preserve hide-to-tray or cancellation behavior. |

A useful structure is a thin top-level window that hosts the actual view:

```xml
<Window
    x:Class="MyApp.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:views="using:MyApp.Views">
    <views:MainPage />
</Window>
```

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

For multi-window applications, use a collection or window service, remove closed windows, and decide which window owns dialogs, pickers, commands, and process lifetime. Avoid a global `MainWindow` shortcut that silently targets the wrong window.

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

Keep HWND/`WindowId` conversion at the platform boundary instead of spreading it through view models.

## Size and Position

`AppWindow` sizing APIs use physical pixels while XAML layout uses effective pixels/DIPs. Account for the target monitor's scale and for changes after moving between monitors.

- Size after enough content exists to measure; constructor-time `XamlRoot` information may be unavailable.
- Avoid hard-coded title-bar/chrome heights.
- Respect the display work area and minimum/maximum sizes.
- Recalculate when DPI, display, content, localization, or text scale changes if the window is content-sized.
- Test long localized strings and accessibility text scaling instead of matching one WPF pixel size.

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

## Optional WinUIEx Window Base

When the application already uses WinUIEx, or its window requirements have led the project to select that library, `WindowEx` exposes common window settings directly. Keep the native `Window`/`AppWindow` approach above when it fits the project; adopting WinUIEx does not require changing the deployment model.

Change both the XAML root and its code-behind base class:

```xml
<winuiex:WindowEx
    x:Class="MyApp.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:views="using:MyApp.Views"
    xmlns:winuiex="using:WinUIEx"
    MinWidth="480"
    MinHeight="320"
    IsResizable="True"
    IsMinimizable="True"
    IsMaximizable="True"
    IsAlwaysOnTop="False"
    IsTitleBarVisible="True">
    <views:MainPage />
</winuiex:WindowEx>
```

```csharp
using WinUIEx;

namespace MyApp;

public sealed partial class MainWindow : WindowEx
{
    public MainWindow()
    {
        InitializeComponent();
        this.CenterOnScreen(width: 800, height: 600);
    }
}
```

Keep `DataContext`, local resources, and visual states on the hosted view. `WindowEx` remains a `Window`, not a `FrameworkElement`.

| WPF requirement | WinUIEx option |
|---|---|
| Window size and minimum/maximum bounds | `Width`/`Height`, `MinWidth`/`MinHeight`, `MaxWidth`/`MaxHeight`, in device-independent pixels (DIPs). |
| `ResizeMode` and caption-button restrictions | Set `IsResizable`, `IsMinimizable`, and `IsMaximizable` separately to preserve the intended behavior. |
| `Topmost` | `IsAlwaysOnTop`. |
| Standard title-bar visibility | `IsTitleBarVisible`; custom chrome still needs its drag regions and caption behavior. |
| Center on the current monitor | `CenterOnScreen()`; optional width/height arguments use DIPs, unlike the physical pixels accepted by `AppWindow`. |
| Restore window placement across sessions | A stable `PersistenceId`, set before first activation, with persistence storage appropriate to the deployment model. |

For example, set `PersistenceId = "MainWindow";` before the first `Activate()` only when restoring placement is part of the application's behavior. Center the initial window before activation as above; do not center it again after saved placement is restored. The built-in restoration depends on the monitor layout remaining unchanged.

Packaged applications can use WinUIEx's default `ApplicationData` storage. For an unpackaged application, initialize `WindowManager.PersistenceStorage` from application-owned persisted data before any participating window opens, and save the updated dictionary across sessions. Preserve existing settings contracts when adding this storage; do not change packaging to enable persistence.

## Title Bars and Presenters

- Preserve standard title-bar behavior unless custom chrome is a requirement.
- For a custom title bar, use the supported `Window`/`AppWindowTitleBar` APIs and call `SetTitleBar` with a XAML element when appropriate.
- Preserve drag regions, caption buttons, system menu, keyboard behavior, snap layouts, High Contrast, and right-to-left layout.
- Map minimize/maximize/resize restrictions through the current `AppWindow` presenter rather than copying WPF properties into XAML that WinUI `Window` does not expose.

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

Choose the API from the target Windows App SDK version.

### Windows App SDK 1.8 and later

The `Microsoft.Windows.Storage.Pickers` APIs accept a `WindowId` in their constructor and do not require `InitializeWithWindow`:

```csharp
var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(
    window.AppWindow.Id);
picker.FileTypeFilter.Add(".txt");

var result = await picker.PickSingleFileAsync();
```

Use the returned result type defined by that API; do not assume it is the same as the legacy `StorageFile` result without checking the target SDK.

### Older Windows App SDK targets / legacy WinRT pickers

`Windows.Storage.Pickers` in a desktop app must be initialized with the owning HWND:

```csharp
var picker = new Windows.Storage.Pickers.FileOpenPicker();
picker.FileTypeFilter.Add(".txt");

IntPtr hwnd = WindowNative.GetWindowHandle(window);
InitializeWithWindow.Initialize(picker, hwnd);

StorageFile? file = await picker.PickSingleFileAsync();
```

Do not cargo-cult the HWND initialization into the newer picker API, and do not omit ownership from the legacy API.

## Closing and Process Lifetime

Port the intent behind WPF close handling:

- Cancel-and-hide behavior for tray applications.
- Save/discard prompts and async cleanup.
- Last-window shutdown versus background service lifetime.
- Multiple-window ownership and app-instance redirection.
- Cleanup that must run even when activation or initialization fails.

Avoid `async void` close handlers that let the window disappear before a prompt or save finishes unless the event contract requires it and reentrancy is controlled.

## Custom Entry Points

Keep the generated WinUI entry point unless the process genuinely needs work before XAML application startup, such as a dual CLI/GUI mode, custom app-instance redirection, or native initialization.

If a custom entry point is required:

- Follow the target Windows App SDK template/version for COM wrappers, `Application.Start`, dispatcher synchronization context, and apartment initialization.
- Keep UI creation on the correct STA/UI thread.
- Separate CLI/shared logic into UI-neutral libraries rather than constructing the WinUI `Application` for non-UI execution.
- Validate packaged and unpackaged activation paths separately.

Do not copy a `Program.cs` from another application without matching its Windows App SDK version and deployment model.

## Official References

- [`DispatcherQueue.TryEnqueue`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.dispatching.dispatcherqueue.tryenqueue)
- [WPF `Dispatcher.BeginInvoke`](https://learn.microsoft.com/dotnet/api/system.windows.threading.dispatcher.begininvoke)
- [Task-based asynchronous pattern exception handling](https://learn.microsoft.com/dotnet/standard/asynchronous-programming-patterns/task-based-asynchronous-pattern-tap#exceptions)
- [Async return types and `async void`](https://learn.microsoft.com/dotnet/csharp/asynchronous-programming/async-return-types#void-return-type)
- [Windows App SDK 1.8 release notes](https://learn.microsoft.com/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-8)
- [FileOpenPicker(WindowId) constructor](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.windows.storage.pickers.fileopenpicker.-ctor)
- [WinUIEx window base and XAML setup](https://dotmorten.github.io/WinUIEx/concepts/WindowEx.html)
- [WinUIEx `CenterOnScreen`](https://dotmorten.github.io/WinUIEx/api/WinUIEx.WindowExtensions.CenterOnScreen.html)
- [WinUIEx `PersistenceId`](https://dotmorten.github.io/WinUIEx/api/WinUIEx.WindowManager.PersistenceId.html) and [`PersistenceStorage`](https://dotmorten.github.io/WinUIEx/api/WinUIEx.WindowManager.PersistenceStorage.html)
