# Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording

Record a Windows app window — or one element's region — to an H.264 MP4, with optional timestamped
JPEG frames for evidence. This is the recording engine behind `winapp ui record`.

> **This package does not coordinate with other automation on the desktop.** The `winapp` CLI layers
> cooperative desktop turns on top of this engine — holding the desktop while a recording starts, and
> for the whole recording when the host falls back to PrintWindow capture. That arbitration lives in
> the CLI, not here. Code calling these APIs directly does not participate in it: it is outside that
> guarantee and is responsible for serializing itself against any other automation running at the
> same time, or for running on a desktop nothing else is driving. This matters most for
> `RecordOptions.CaptureScreen`, where anything another workflow does lands in the video.

```console
dotnet add package Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording
dotnet add package Microsoft.Extensions.DependencyInjection
dotnet add package Microsoft.Extensions.Logging
```

```csharp
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection()
    .AddLogging()
    .AddWinAppUiAutomation()
    .AddWinAppUiRecording()
    .BuildServiceProvider();

var resolver = services.GetRequiredService<IUiTargetResolver>();
var recorder = services.GetRequiredService<IUiRecordingService>();

var target = await resolver.ResolveAsync("notepad", hwnd: null, CancellationToken.None);

var result = await recorder.RecordAsync(target, elementId: null, new RecordOptions
{
    OutputPath = "session.mp4",
    DurationSec = 10,
    Fps = 10,
    MaxEdge = 1280,
}, CancellationToken.None);

Console.WriteLine($"{result.Frames} frames, {result.Width}x{result.Height}, mode {result.Mode}");
```

## Why this is a separate package

Recording needs SkiaSharp for frame scaling and JPEG output, whose native binary adds roughly 9 MB
per architecture. Splitting it out keeps
`Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation` small for the many projects that only need to
inspect and drive UI. Reference this package only when you actually want video.

## Capture modes

`RecordAsync` picks a capture path and reports it as `RecordCaptureResult.Mode`:

- **`wgc`** — Windows Graphics Capture. The default, and the only one that reliably captures
  occluded or GPU-composited windows.
- **`printwindow`** — GDI fallback when Graphics Capture is unavailable.
- **`screen`** — screen-DC capture, used when you set `RecordOptions.CaptureScreen`. This is the
  only mode that includes popups and overlays drawn outside the window, and it captures whatever is
  on screen — including other windows on top.

Set `RecordOptions.FramesDirectory` to also write a frame bundle: numbered JPEGs, a `frames.ndjson`
index, and a `manifest.json` describing the run.

## Requirements

Windows 10 version 2004 (build 19041) or later, and a target framework of
`net10.0-windows10.0.19041.0`. Both Graphics Capture and the Media Foundation H.264 encoder need the
Windows 10 SDK projection, so this package does not offer a leaner target.
