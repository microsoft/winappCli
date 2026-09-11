// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

public partial class RealRecordingTests
{
    [TestMethod]
    public async Task RecordAsync_OrdinaryRecording_StillUsesTheBlankRetryCapture()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture { Supported = false };
        var recording = NewRecordingService(NewAutomation(), capture);
        var output = ScratchOutput("ordinary.mp4");
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
        }, CancellationToken.None);

        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(1, capture.CapturedWithBlankRetry.Count);
    }

    [TestMethod]
    public async Task RecordAsync_OrdinaryRecording_MinimizedWindow_IsStillRestored()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture { Supported = false };
        var recording = NewRecordingService(NewAutomation(), capture);
        var output = ScratchOutput("minimized-ordinary.mp4");
        var restores = 0;
        UiRecordingService.s_isWindowMinimized = _ => true;
        UiRecordingService.s_restoreWindow = _ => restores++;
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
        }, CancellationToken.None);

        Assert.AreEqual(1, restores);
        Assert.AreEqual(1, result.Frames);
    }

    [TestMethod]
    public async Task RecordAsync_OrdinaryRecording_BlankFrameCapture_IsRecordedAsBefore()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture
        {
            Supported = true,
            StartGrabberCallback = (_, _) => new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64),
        };
        var recording = NewRecordingService(NewAutomation(), capture);
        var output = ScratchOutput("wgc-blank-ordinary.mp4");
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 2,
            MaxEdge = 64,
        }, CancellationToken.None);

        Assert.AreEqual(2, result.Frames);
        Assert.AreEqual("duration_elapsed", result.StopReason);
    }

    [TestMethod]
    public async Task RecordAsync_OrdinaryRecording_SessionClosesWithBlankCached_StillDrainsIt()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture
        {
            Supported = true,
            StartGrabberCallback = (_, _) => new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64) { IsClosed = true },
        };
        var recording = NewRecordingService(NewAutomation(), capture);
        var output = ScratchOutput("closed-blank-ordinary.mp4");
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 4,
            Fps = 2,
            MaxEdge = 64,
        }, CancellationToken.None);

        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual("target_closed", result.StopReason);
    }

    private static string ScratchOutput(string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
