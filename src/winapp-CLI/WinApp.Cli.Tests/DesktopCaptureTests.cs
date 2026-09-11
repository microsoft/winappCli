// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.InteractiveDesktop;
using RecordingArtifactPublisher = Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording.RecordingArtifactPublisher;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DesktopCaptureTests
{
    private Func<PointerRect> _readBounds = null!;
    private readonly Func<string, int, int, int, uint, IVideoEncoder> _originalEncoder = Mp4SinkWriterEncoder.s_createNoClobber;
    private string _root = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = TestPaths.TempRoot(nameof(DesktopCaptureTests));
        Directory.CreateDirectory(_root);
        _readBounds = () => new PointerRect(-101, -75, 0, 0);
        UiRecordingService.s_restoreWindow = _ => Assert.Fail("Desktop capture must not restore a window.");
        UiRecordingService.s_bringToForeground = _ => Assert.Fail("Desktop capture must not foreground a window.");
        UiRecordingService.s_isWindowMinimized = _ => throw new AssertFailedException("A desktop is not an application HWND.");
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => new Encoder(path, width, height);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Mp4SinkWriterEncoder.s_createNoClobber = _originalEncoder;
        UiRecordingService.ResetWindowStateSeams();
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [TestMethod]
    public void NativeCoordinates_PreserveNegativeOriginAndRejectPadding()
    {
        var mapping = new CaptureCoordinates
        {
            SourceBounds = new PointerRect(-100, -50, 0, 0),
            ContentRect = new PointerRect(0, 0, 100, 50),
        };
        Assert.AreEqual(new PointerPoint(-100, -50), mapping.ToScreenPoint(new PointerPoint(0, 0)));
        Assert.AreEqual(new PointerPoint(-1, -1), mapping.ToScreenPoint(new PointerPoint(99, 49)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => mapping.ToScreenPoint(new PointerPoint(100, 0)));
    }

    [TestMethod]
    [DataRow(101, 75, 0)]
    [DataRow(1824, 1178, 1280)]
    [DataRow(32, 24, 0)]
    [DataRow(301, 31, 99)]
    public void RecordingMapping_UsesActualFittedContentIncludingOddSizesAndPadding(int width, int height, int maxEdge)
    {
        var (ew, eh, dw, dh) = UiRecordingService.ComputeTargetSize(width, height, maxEdge);
        var sourceBounds = new PointerRect(-width, -height, 0, 0);
        var mapping = UiRecordingService.DescribeCoordinates(sourceBounds, ew, eh, dw, dh);
        var (x, y, fw, fh) = CaptureGeometry.ComputeFittedContentRect(width, height, ew, eh, dw, dh);
        Assert.AreEqual(sourceBounds, mapping.SourceBounds);
        Assert.AreEqual(new PointerRect(x, y, x + fw, y + fh), mapping.ContentRect);
        var point = mapping.ToScreenPoint(new PointerPoint(x + fw / 2, y + fh / 2));
        Assert.IsTrue(sourceBounds.Contains(point));
        if (x > 0 || y > 0)
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => mapping.ToScreenPoint(new PointerPoint(0, 0)));
        }
        var nativePixels = new byte[width * height * 4];
        for (var i = 0; i < nativePixels.Length; i += 4) { nativePixels[i + 1] = 255; }
        var processed = UiRecordingService.ProcessFrame(nativePixels, width, height, 0, 0, width, height, ew, eh, dw, dh);
        Assert.AreEqual((byte)255, processed[((y + fh / 2) * ew + x + fw / 2) * 4 + 1]);
        if (x > 0 || y > 0) { Assert.AreEqual((byte)0, processed[1]); }
    }

    [TestMethod]
    public async Task Screenshot_UsesNativeDesktopPixelsWithoutAnyWindowCapture()
    {
        var capture = Capture((x, y, width, height, ew, eh, dw, dh) =>
        {
            Assert.AreEqual((-101, -75, 101, 75, 101, 75, 101, 75), (x, y, width, height, ew, eh, dw, dh));
            return Pixels(ew, eh);
        });
        using var console = new TestConsole();
        var coordinator = new FakeInteractiveDesktopLock();
        var path = Path.Join(_root, "desktop.png");
        var exit = await Screenshot(capture, coordinator, console, path, TestContext.CancellationToken);
        Assert.AreEqual(0, exit);
        var result = JsonSerializer.Deserialize(console.Output, UiJsonContext.Default.UiScreenshotResult)!;
        Assert.AreEqual((101, 75), (result.Width, result.Height));
        Assert.AreEqual(new PointerRect(-101, -75, 0, 0), result.Coordinates!.SourceBounds);
        Assert.AreEqual(new PointerRect(0, 0, 101, 75), result.Coordinates.ContentRect);
        Assert.AreEqual(0L, result.Hwnd);
        Assert.AreEqual("test-epoch", result.ExecutionTarget!.Epoch);
        Assert.AreEqual(UiTurnMode.Observe, coordinator.Runs.Single().Mode);
        var bytes = await File.ReadAllBytesAsync(path, TestContext.CancellationToken);
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71 }, bytes.Take(4).ToArray());
        Assert.AreEqual(0, capture.CapturedWithBlankRetry.Count);
    }

    [TestMethod]
    public async Task Screenshot_DisplayChangePreservesExistingFile()
    {
        var path = Path.Join(_root, "desktop.png");
        await File.WriteAllTextAsync(path, "prior evidence", TestContext.CancellationToken);
        var capture = Capture((_, _, _, _, ew, eh, _, _) =>
        {
            _readBounds = () => new PointerRect(0, 0, 200, 100);
            return Pixels(ew, eh);
        });
        using var console = new TestConsole();
        Assert.AreEqual(1, await Screenshot(capture, new(), console, path, TestContext.CancellationToken));
        Assert.AreEqual("prior evidence", await File.ReadAllTextAsync(path, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Screenshot_CancelledBeforePublicationPreservesExistingFile()
    {
        var path = Path.Join(_root, "desktop.png");
        await File.WriteAllTextAsync(path, "prior evidence", TestContext.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var capture = Capture((_, _, _, _, ew, eh, _, _) =>
        {
            cancellation.Cancel();
            return Pixels(ew, eh);
        });
        using var console = new TestConsole();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Screenshot(capture, new(), console, path, cancellation.Token));
        Assert.AreEqual("prior evidence", await File.ReadAllTextAsync(path, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Record_WholeDesktopCompletesWithoutResolvingOrActivatingAWindow()
    {
        var capture = Capture((_, _, _, _, ew, eh, _, _) => Pixels(ew, eh));
        var result = await Recorder(capture).RecordDesktopAsync(new RecordOptions
        {
            OutputPath = Path.Join(_root, "desktop.mp4"),
            FramesDirectory = Path.Join(_root, "desktop.frames"),
            DurationSec = 1,
            Fps = 1,
        }, TestContext.CancellationToken);

        Assert.AreEqual("screen", result.Mode);
        Assert.AreEqual("duration_elapsed", result.StopReason);
        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(new PointerRect(-101, -75, 0, 0), result.Coordinates!.SourceBounds);
        Assert.IsNotNull(result.FrameArtifacts);
        Assert.AreEqual(0, capture.CapturedWithBlankRetry.Count);
    }

    [TestMethod]
    public async Task Record_DesktopRejectsWindowScreenCaptureOptionBeforeCreatingOutput()
    {
        var capture = Capture((_, _, _, _, _, _, _, _) => throw new AssertFailedException("No capture is allowed."));
        var output = Path.Join(_root, "invalid.mp4");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Recorder(capture).RecordDesktopAsync(
            new RecordOptions { OutputPath = output, CaptureScreen = true }, TestContext.CancellationToken));
        Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    public async Task Record_DesktopChangeFinalizesOnlyOldMappingAndMarksFramesPartial()
    {
        var calls = 0;
        var capture = Capture((_, _, _, _, ew, eh, _, _) =>
        {
            calls++;
            if (calls == 2) { _readBounds = () => new PointerRect(0, 0, 200, 100); }
            return Pixels(ew, eh);
        });
        Encoder? encoder = null;
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => encoder = new Encoder(path, width, height);
        var options = Options("resize.mp4", frames: true);
        var result = await Recorder(capture).RecordDesktopAsync(options, TestContext.CancellationToken);
        Assert.AreEqual("display_changed", result.StopReason);
        Assert.AreEqual(1, result.Frames, "The frame sampled during the bounds change must be discarded.");
        Assert.IsTrue(encoder!.Completed);
        var manifest = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(result.FrameArtifacts!.Manifest, TestContext.CancellationToken),
            RecordingJsonContext.Default.RecordFrameBundleManifest)!;
        Assert.AreEqual("partial", manifest.Status);
        Assert.AreEqual("display_changed", manifest.StopReason);
        Assert.AreEqual(result.Coordinates!.SourceBounds, manifest.Coordinates!.SourceBounds);
        Assert.AreEqual(result.Coordinates.ContentRect, manifest.Coordinates.ContentRect);
        Assert.AreEqual((result.Width, result.Height), (manifest.Frames.Width, manifest.Frames.Height));
        Assert.AreEqual(1, manifest.Timing.SampleCount);
        Assert.AreEqual(0, capture.CapturedWithBlankRetry.Count);
    }

    [TestMethod]
    public async Task Record_LostDesktopFinalizesPartialWithoutActivation()
    {
        var capture = Capture((_, _, _, _, ew, eh, _, _) => Pixels(ew, eh));
        var result = await Recorder(capture).RecordDesktopAsync(Options("lost.mp4", frames: true),
            TestContext.CancellationToken, _ =>
                _readBounds = () => throw new InvalidOperationException("No input desktop."));
        Assert.AreEqual("capture_unavailable", result.StopReason);
        Assert.AreEqual(1, result.Frames);
        var manifest = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(result.FrameArtifacts!.Manifest, TestContext.CancellationToken),
            RecordingJsonContext.Default.RecordFrameBundleManifest)!;
        Assert.AreEqual("partial", manifest.Status);
    }

    [TestMethod]
    public async Task Record_CancelAfterStartedFinalizesAndPreservesMapping()
    {
        using var cancellation = new CancellationTokenSource();
        var capture = Capture((_, _, _, _, ew, eh, _, _) => Pixels(ew, eh));
        var result = await Recorder(capture).RecordDesktopAsync(Options("cancelled.mp4", frames: true),
            cancellation.Token, _ => cancellation.Cancel());
        Assert.AreEqual("cancelled", result.StopReason);
        Assert.AreEqual(1, result.Frames);
        Assert.IsTrue(File.Exists(result.FrameArtifacts!.Manifest));
        Assert.AreEqual(new PointerRect(-101, -75, 0, 0), result.Coordinates!.SourceBounds);
    }

    [TestMethod]
    public async Task Record_FirstCaptureFailureCreatesNoVideoAndDoesNotOverwrite()
    {
        var capture = Capture((_, _, _, _, _, _, _, _) => throw new System.ComponentModel.Win32Exception(5));
        var options = Options("failure.mp4", frames: false);
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(
            () => Recorder(capture).RecordDesktopAsync(options, TestContext.CancellationToken));
        Assert.IsFalse(File.Exists(options.OutputPath));
        Assert.IsFalse(Directory.Exists(RecordingArtifactPublisher.GetFramesDirectory(options.OutputPath)));
    }

    [TestMethod]
    public async Task Record_HiddenCommandPinsSharedTurnButReleasesSectionAfterFirstFrame()
    {
        using var console = new TestConsole();
        var coordinator = new FakeInteractiveDesktopLock();
        var recording = new FakeUiRecordingService { RecordResult = new RecordCaptureResult { Mode = "screen" } };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recording.WaitAfterRecordingStarted = release.Task;
        recording.AfterRecordingStarted = () => started.TrySetResult();
        var command = new GuestDesktopRecordCommand();
        var handler = new GuestDesktopRecordCommand.Handler(new FakeUiTargetResolver(), recording,
            Capture((_, _, _, _, ew, eh, _, _) => Pixels(ew, eh)), new FakeSystemUiQuery(),
            console, coordinator, NullLogger<UiRecordCommand>.Instance);
        var task = handler.InvokeAsync(Parse(command, Path.Join(_root, "shared.mp4"), "--duration-sec", "1"), TestContext.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        for (var i = 0; i < 100 && coordinator.OpenDesktopSections != 0; i++) { await Task.Delay(10, TestContext.CancellationToken); }
        try
        {
            Assert.AreEqual(UiTurnMode.TurnShared, coordinator.Runs.Single().Mode);
            Assert.AreEqual(0, coordinator.OpenDesktopSections);
            Assert.IsNull(recording.LastTarget, "Desktop recording must not manufacture an app target.");
            Assert.IsFalse(recording.LastRecordOptions!.CaptureScreen);
        }
        finally { release.TrySetResult(); }
        Assert.AreEqual(0, await task);
    }

    private RecordOptions Options(string name, bool frames) => new()
    {
        OutputPath = Path.Join(_root, name),
        FramesDirectory = frames ? Path.Join(_root, name + ".frames") : null,
        DurationSec = 2,
        Fps = 30,
        MaxEdge = 64,
    };

    private static UiRecordingService Recorder(FakeWindowCapture capture) =>
        new(new FakeUiAutomationService(), capture, new UnusedSelectorParser(), NullLogger<UiRecordingService>.Instance);

    private FakeWindowCapture Capture(Func<int, int, int, int, int, int, int, int, byte[]> capture) => new()
    {
        DesktopBoundsOverride = () => _readBounds(),
        CaptureScreenOverride = capture,
        CaptureWindowOverride = (_, _, _) => throw new AssertFailedException("No window capture is allowed."),
        StartGrabberCallback = (_, _) => throw new AssertFailedException("No window grabber is allowed."),
    };

    private static byte[] Pixels(int width, int height)
    {
        var bytes = new byte[width * height * 4];
        Array.Fill(bytes, (byte)127);
        return bytes;
    }

    private static Task<int> Screenshot(FakeWindowCapture capture, FakeInteractiveDesktopLock coordinator,
        TestConsole console, string path, CancellationToken cancellationToken)
    {
        var command = new GuestDesktopScreenshotCommand();
        return new GuestDesktopScreenshotCommand.Handler(capture, console, coordinator,
            NullLogger<GuestDesktopScreenshotCommand>.Instance).InvokeAsync(Parse(command, path), cancellationToken);
    }

    private static ParseResult Parse(Command command, string path, params string[] extra)
    {
        var root = new RootCommand();
        root.Options.Add(WinAppRootCommand.QuietOption);
        root.Subcommands.Add(command);
        var result = root.Parse([command.Name, "--output", path, "--json",
            "--target-kind", "sandbox", "--target-name", "default", "--target-epoch", "test-epoch", .. extra]);
        Assert.AreEqual(0, result.Errors.Count, string.Join("; ", result.Errors.Select(error => error.Message)));
        return result;
    }

    private sealed class UnusedSelectorParser : IUiSelectorParser
    {
        public UiSelector Parse(string selector) => throw new AssertFailedException("Desktop capture must not resolve elements.");
    }

    private sealed class Encoder(string path, int width, int height) : IVideoEncoder
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public bool Completed { get; private set; }
        public void WriteFrame(ReadOnlySpan<byte> bgra, long sampleTimeHns, long sampleDurationHns) =>
            Assert.AreEqual(Width * Height * 4, bgra.Length);
        public void Complete()
        {
            Completed = true;
            File.WriteAllBytes(path, [1, 2, 3, 4]);
        }
        public void Dispose() { }
    }
}
