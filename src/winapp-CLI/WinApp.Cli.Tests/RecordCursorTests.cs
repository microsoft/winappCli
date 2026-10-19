// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinApp.Cli.Tests;

/// <summary>Covers <c>ui record --cursor</c>: pointer placement, click rings, and the frame index.</summary>
[TestClass]
[DoNotParallelize]
public sealed class RecordCursorTests
{
    private static readonly PointerRect DesktopBounds = new(-101, -75, 0, 0);
    private readonly Func<string, int, int, int, uint, IVideoEncoder> _originalEncoder = Mp4SinkWriterEncoder.s_createNoClobber;
    private readonly Func<bool, Microsoft.Extensions.Logging.ILogger, IPointerTracker> _originalTracker = RecordCursorOverlay.s_createPointerTracker;
    private string _root = null!;
    private readonly List<byte[]> _frames = [];

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = TestPaths.TempRoot(nameof(RecordCursorTests));
        Directory.CreateDirectory(_root);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => new FrameCollectingEncoder(path, width, height, _frames);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Mp4SinkWriterEncoder.s_createNoClobber = _originalEncoder;
        RecordCursorOverlay.s_createPointerTracker = _originalTracker;
        UiRecordingService.ResetWindowStateSeams();
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [TestMethod]
    public void Mapping_PlacesScreenPointsThroughCropAndScale()
    {
        // A 200x100 area at screen (1000, 500), scaled to half size in a 100x50 frame.
        var mapping = CursorFrameMapping.Create(1000, 500, 200, 100, 100, 50, 100, 50);

        Assert.AreEqual((0, 0), mapping.Map(1000, 500));
        Assert.AreEqual((50, 25), mapping.Map(1100, 550));
        Assert.AreEqual(0.5, mapping.Scale, 1e-9);
        Assert.IsTrue(mapping.Contains(1199, 599));
        Assert.IsFalse(mapping.Contains(1200, 550), "the right edge is outside the recorded area");
        Assert.AreEqual((-5, -5), mapping.Map(990, 490), "points outside the area still map, outside the image");
    }

    [TestMethod]
    public void Mapping_AccountsForPaddingAroundContent()
    {
        // A 10x10 area padded to the 64x64 encoder minimum is centered in the frame.
        var (encoderW, encoderH, displayW, displayH) = UiRecordingService.ComputeTargetSize(10, 10, 0);
        var mapping = CursorFrameMapping.Create(0, 0, 10, 10, encoderW, encoderH, displayW, displayH);

        Assert.IsTrue(mapping.ContentX > 0 && mapping.ContentY > 0, "content must be padded");
        Assert.AreEqual((mapping.ContentX, mapping.ContentY), mapping.Map(0, 0));
    }

    [TestMethod]
    public void RingProgress_ShowsEveryClickAtLeastOnceThenFades()
    {
        Assert.AreEqual(0.0, RecordCursorOverlay.RingProgress(0, shownBefore: false));
        Assert.AreEqual(0.5, RecordCursorOverlay.RingProgress(RecordCursorOverlay.RingDurationMs / 2, shownBefore: true));

        // A click older than the fade that has not been drawn yet is still drawn once.
        Assert.AreEqual(RecordCursorOverlay.MaxFirstShownProgress, RecordCursorOverlay.RingProgress(2_000, shownBefore: false));
        Assert.IsNull(RecordCursorOverlay.RingProgress(RecordCursorOverlay.RingDurationMs, shownBefore: true));
    }

    [TestMethod]
    [DataRow(0x0201u, 0u, "left", true)]
    [DataRow(0x0202u, 0u, "left", false)]
    [DataRow(0x0204u, 0u, "right", true)]
    [DataRow(0x0208u, 0u, "middle", false)]
    [DataRow(0x020Bu, 0x00010000u, "x1", true)]
    [DataRow(0x020Cu, 0x00020000u, "x2", false)]
    public void ClassifyButtonMessage_NamesButtonAndDirection(uint message, uint mouseData, string button, bool down)
        => Assert.AreEqual((button, down), MousePointerTracker.ClassifyButtonMessage(message, mouseData));

    [TestMethod]
    public void ClassifyButtonMessage_IgnoresMovesAndWheel()
    {
        Assert.IsNull(MousePointerTracker.ClassifyButtonMessage(0x0200, 0));
        Assert.IsNull(MousePointerTracker.ClassifyButtonMessage(0x020A, 0x00780000));
    }

    [TestMethod]
    public async Task Record_WithCursor_WritesPointerAndClicksToFrameIndexAndDrawsRing()
    {
        var tracker = InstallTracker(tracksClicks: true);
        tracker.State = new PointerState(-50, -40, Visible: true, Cursor: 0);
        tracker.QueuedEvents.Add(("left", true, -50, -40));
        tracker.QueuedEvents.Add(("left", false, -50, -40));

        var result = await RecordDesktopAsync(cursor: true, clickHighlight: true);

        var entry = await ReadFirstIndexEntryAsync(result);
        var cursor = entry.GetProperty("cursor");
        Assert.IsTrue(cursor.GetProperty("visible").GetBoolean());
        Assert.IsTrue(cursor.GetProperty("inFrame").GetBoolean());
        var expected = CursorFrameMapping.Create(-101, -75, 101, 75, result.Width, result.Height, result.Width, result.Height).Map(-50, -40);
        Assert.AreEqual(expected, (cursor.GetProperty("x").GetInt32(), cursor.GetProperty("y").GetInt32()));

        var clicks = entry.GetProperty("clicks").EnumerateArray().ToArray();
        Assert.HasCount(2, clicks);
        Assert.AreEqual("left", clicks[0].GetProperty("button").GetString());
        Assert.AreEqual("down", clicks[0].GetProperty("kind").GetString());
        Assert.AreEqual("up", clicks[1].GetProperty("kind").GetString());
        Assert.AreEqual(expected.X, clicks[0].GetProperty("x").GetInt32());

        var requested = await ReadRequestedAsync(result);
        Assert.IsTrue(requested.GetProperty("cursor").GetBoolean());
        Assert.IsTrue(requested.GetProperty("clickHighlight").GetBoolean());

        Assert.IsTrue(_frames[0].Any(value => value != 127), "the click ring must be drawn into the video");
        Assert.IsTrue(tracker.Disposed, "the pointer tracker must be released when recording ends");
        Assert.IsNull(result.Warnings);
    }

    [TestMethod]
    public async Task Record_WithOverwrite_KeepsCursorAndClickHighlight()
    {
        var tracker = InstallTracker(tracksClicks: true);
        tracker.State = new PointerState(-50, -40, Visible: true, Cursor: 0);
        tracker.QueuedEvents.Add(("left", true, -50, -40));

        var result = await RecordDesktopAsync(cursor: true, clickHighlight: true, overwrite: true);

        var entry = await ReadFirstIndexEntryAsync(result);
        Assert.IsTrue(entry.TryGetProperty("cursor", out _), "--overwrite must keep the pointer in the frame index");
        Assert.AreEqual("left", entry.GetProperty("clicks")[0].GetProperty("button").GetString());
        Assert.IsTrue((await ReadRequestedAsync(result)).GetProperty("clickHighlight").GetBoolean());
        Assert.IsTrue(_frames[0].Any(value => value != 127), "--overwrite must keep the click ring");
    }

    [TestMethod]
    public async Task Record_WithoutClickHighlight_LogsClicksButDrawsNoRing()
    {
        var tracker = InstallTracker(tracksClicks: true);
        tracker.State = new PointerState(-50, -40, Visible: true, Cursor: 0);
        tracker.QueuedEvents.Add(("right", true, -50, -40));

        var result = await RecordDesktopAsync(cursor: true, clickHighlight: false);

        var entry = await ReadFirstIndexEntryAsync(result);
        Assert.AreEqual("right", entry.GetProperty("clicks")[0].GetProperty("button").GetString());
        Assert.IsFalse((await ReadRequestedAsync(result)).GetProperty("clickHighlight").GetBoolean());
        Assert.IsTrue(_frames[0].All(value => value == 127), "no ring may be drawn");
    }

    [TestMethod]
    public async Task Record_WithoutClickHighlightOrFrames_DoesNotWatchClicks()
    {
        bool? requestedTracking = null;
        RecordCursorOverlay.s_createPointerTracker = (trackClicks, _) =>
        {
            requestedTracking = trackClicks;
            return new FakePointerTracker(trackClicks);
        };

        await RecordDesktopAsync(cursor: true, clickHighlight: false, frames: false);

        Assert.IsFalse(requestedTracking, "the mouse hook is only needed for rings or the frame index");
    }

    [TestMethod]
    public async Task Record_WhenClickTrackingFails_WarnsAndStillRecords()
    {
        InstallTracker(tracksClicks: false);

        var result = await RecordDesktopAsync(cursor: true, clickHighlight: true);

        Assert.AreEqual(1, result.Frames);
        StringAssert.Contains(result.Warnings!.Single(), "Mouse clicks could not be tracked");
    }

    [TestMethod]
    public async Task Record_WithoutCursor_LeavesFrameIndexAndManifestUnchanged()
    {
        RecordCursorOverlay.s_createPointerTracker = (_, _) => throw new AssertFailedException("No pointer tracking without --cursor.");

        var result = await RecordDesktopAsync(cursor: false, clickHighlight: true);

        var entry = await ReadFirstIndexEntryAsync(result);
        Assert.IsFalse(entry.TryGetProperty("cursor", out _));
        Assert.IsFalse(entry.TryGetProperty("clicks", out _));
        var requested = await ReadRequestedAsync(result);
        Assert.IsFalse(requested.TryGetProperty("cursor", out _));
        Assert.IsFalse(requested.TryGetProperty("clickHighlight", out _));
    }

    private static FakePointerTracker InstallTracker(bool tracksClicks)
    {
        var tracker = new FakePointerTracker(tracksClicks);
        RecordCursorOverlay.s_createPointerTracker = (_, _) => tracker;
        return tracker;
    }

    private Task<RecordCaptureResult> RecordDesktopAsync(bool cursor, bool clickHighlight, bool frames = true, bool overwrite = false)
    {
        var capture = new FakeWindowCapture
        {
            DesktopBoundsOverride = () => DesktopBounds,
            CaptureScreenOverride = (_, _, _, _, ew, eh, _, _) =>
            {
                var bytes = new byte[ew * eh * 4];
                Array.Fill(bytes, (byte)127);
                return bytes;
            },
        };
        var recorder = new UiRecordingService(
            new FakeUiAutomationService(), capture, new ThrowingSelectorParser(), NullLogger<UiRecordingService>.Instance);
        var name = Guid.NewGuid().ToString("N");
        return recorder.RecordDesktopAsync(new RecordOptions
        {
            OutputPath = Path.Join(_root, name + ".mp4"),
            FramesDirectory = frames ? Path.Join(_root, name + ".frames") : null,
            DurationSec = 1,
            Fps = 1,
            Cursor = cursor,
            ClickHighlight = clickHighlight,
            Overwrite = overwrite,
        }, TestContext.CancellationToken);
    }

    private async Task<JsonElement> ReadFirstIndexEntryAsync(RecordCaptureResult result)
    {
        var lines = await File.ReadAllLinesAsync(result.FrameArtifacts!.Index, TestContext.CancellationToken);
        return JsonDocument.Parse(lines[0]).RootElement;
    }

    private async Task<JsonElement> ReadRequestedAsync(RecordCaptureResult result)
    {
        var manifest = await File.ReadAllTextAsync(result.FrameArtifacts!.Manifest, TestContext.CancellationToken);
        return JsonDocument.Parse(manifest).RootElement.GetProperty("requested");
    }

    private sealed class ThrowingSelectorParser : IUiSelectorParser
    {
        public UiSelector Parse(string selector) => throw new AssertFailedException("Desktop capture must not resolve elements.");
    }

    private sealed class FrameCollectingEncoder(string path, int width, int height, List<byte[]> frames) : IVideoEncoder
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public void WriteFrame(ReadOnlySpan<byte> bgra, long sampleTimeHns, long sampleDurationHns) => frames.Add(bgra.ToArray());
        public void Complete() => File.WriteAllBytes(path, [1, 2, 3, 4]);
        public void Dispose() { }
    }
}
