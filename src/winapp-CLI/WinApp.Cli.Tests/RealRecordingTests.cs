// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using Windows.Win32.Foundation;
using WinApp.Cli.Services;

using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// Real-recording coverage: drives the CLI recording service against a live in-process window,
/// with frame capture and the video encoder swapped for deterministic fakes.
/// </summary>
/// <remarks>
/// Not parallelized: these tests drive a live desktop window and mutate the process-wide encoder
/// and frame-writer seams, so concurrent cases would interfere with each other.
/// </remarks>
[TestClass]
[DoNotParallelize]
public partial class RealRecordingTests
{
    /// <summary>
    /// Restores the process-wide encoder and frame-writer seams after every test. These are static,
    /// so a test that leaves a fake installed corrupts every later test in the assembly — including
    /// ones in other classes that expect the real encoder.
    /// </summary>
    [TestCleanup]
    public void ResetRecordingSeams()
    {
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, fps, bitrate)
            => new Mp4SinkWriterEncoder(path, width, height, fps, bitrate, overwriteExisting: false);
        RecordFrameBundleWriter.ResetTestSeams();
        UiRecordingService.ResetWindowStateSeams();
    }

    [TestMethod]
    [DataRow(-1, 15, 0, nameof(RecordOptions.DurationSec))]
    [DataRow(10, 0, 0, nameof(RecordOptions.Fps))]
    [DataRow(10, -1, 0, nameof(RecordOptions.Fps))]
    [DataRow(10, 15, -1, nameof(RecordOptions.MaxEdge))]
    [DataRow(10, 15, 1, nameof(RecordOptions.MaxEdge))]
    [DataRow(10, 15, 63, nameof(RecordOptions.MaxEdge))]
    public async Task RecordAsync_InvalidOptions_ThrowsBeforeTouchingTheWindow(
        int durationSec, int fps, int maxEdge, string expectedParameter)
    {
        // Direct library calls skip the CLI's command-layer validation. Without this guard fps 0
        // divided by zero when computing the frame interval, and a negative duration recorded
        // without end.
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var recording = NewRecordingService(NewAutomation(), capture);
        var output = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "invalid.mp4");

        var exception = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => recording.RecordAsync(SessionFor(fx), null, new RecordOptions
            {
                OutputPath = output,
                DurationSec = durationSec,
                Fps = fps,
                MaxEdge = maxEdge,
            }, CancellationToken.None));

        StringAssert.Contains(exception.ParamName, expectedParameter);
        Assert.IsFalse(File.Exists(output), "Validation must run before any output file is created.");
    }

    [TestMethod]
    public async Task RecordAsync_ExistingOutput_IsNotOverwritten()
    {
        // Video-only recording used to take the clobbering encoder, so running the readme sample
        // twice at the same OutputPath destroyed the first take. The CLI refuses up front, but that
        // guard does not travel with the package.
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var recording = NewRecordingService(NewAutomation(), capture);
        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "already-there.mp4");
        File.WriteAllText(output, "the first recording");

        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) => new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);

        await Assert.ThrowsExactlyAsync<IOException>(
            () => recording.RecordAsync(SessionFor(fx), null, new RecordOptions
            {
                OutputPath = output,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.AreEqual("the first recording", File.ReadAllText(output), "the existing recording must survive");
    }

    [TestMethod]
    public async Task RecordAsync_WgcSeams_EncodesTimedFramesAndReportsResult()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var output = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "record.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var frame = Enumerable.Repeat((byte)0x44, 80 * 60 * 4).ToArray();
        var grabber = new FakeFrameGrabber(frame, 80, 60);
        FakeVideoEncoder? encoder = null;
        var started = 0;

        capture.Supported = true;
        capture.StartGrabberCallback = (hwnd, fps) =>
        {
            Assert.AreEqual(fx.Hwnd, (nint)hwnd);
            Assert.AreEqual(2, fps);
            return grabber;
        };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, fps, bitrate) =>
        {
            encoder = new FakeVideoEncoder(path, width, height);
            return encoder;
        };

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 2,
            MaxEdge = 64,
            CaptureScreen = false,
        }, CancellationToken.None, _ => started++);

        Assert.AreEqual("wgc", result.Mode);
        Assert.AreEqual(2, result.Frames);
        Assert.AreEqual(64, result.Width);
        Assert.AreEqual(64, result.Height);
        Assert.AreEqual(1, started, "recording should signal readiness exactly once after the first frame");
        Assert.IsNotNull(encoder);
        Assert.AreEqual(2, encoder!.FramesWritten);
        Assert.IsTrue(encoder.Completed);
        Assert.IsTrue(grabber.Disposed);
        Assert.IsTrue(new FileInfo(output).Length > 0);
    }

    [TestMethod]
    public async Task RecordAsync_WgcClosedDrainsLatestFrameBeforeStopping()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var output = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "closed.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var grabber = new FakeFrameGrabber(Enumerable.Repeat((byte)0x55, 64 * 64 * 4).ToArray(), 64, 64)
        {
            IsClosed = true,
        };

        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) => grabber;
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 10,
            Fps = 5,
            MaxEdge = 0,
            CaptureScreen = false,
        }, CancellationToken.None);

        Assert.AreEqual(1, result.Frames, "closed WGC items should drain the cached frame once before finalizing");
        Assert.AreEqual("wgc", result.Mode);
        Assert.IsTrue(grabber.Disposed);
    }

    [TestMethod]
    public async Task RecordAsync_CaptureScreen_UsesConsentedScreenPath()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        // A screen-DC recording captures whatever is genuinely in front, so the engine now refuses to
        // start one unless the target actually reached the foreground. That is a real precondition of
        // this capture mode, not test scaffolding — satisfy it the same way a user would.
        DesktopTestHelpers.ForceForeground(fx.Hwnd);
        if (!ForegroundBelongsToFixture(fx))
        {
            Assert.Inconclusive(
                "The fixture window could not be brought to the foreground on this session, so the "
                + "consented screen path cannot be exercised. Foreground refusal itself is covered by "
                + "CaptureForegroundSafetyTests, which drives the check directly.");
        }

        var output = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "screen.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var screenCalls = 0;
        capture.CaptureScreenOverride = (_, _, _, _, tw, th, _, _) =>
        {
            screenCalls++;
            return Enumerable.Repeat((byte)0x66, tw * th * 4).ToArray();
        };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
            CaptureScreen = true,
        }, CancellationToken.None);

        Assert.AreEqual("screen", result.Mode);
        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(1, screenCalls);
    }

    [TestMethod]
    public async Task RecordAsync_PrintWindowFallback_UsesBlankRetryCapture()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var output = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "printwindow.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var windowCalls = 0;
        capture.Supported = false;
        capture.CaptureWindowOverride = (_, width, height) =>
        {
            windowCalls++;
            return Enumerable.Repeat((byte)0x77, width * height * 4).ToArray();
        };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
            CaptureScreen = false,
        }, CancellationToken.None);

        Assert.AreEqual("printwindow", result.Mode);
        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(1, windowCalls);
    }

    [TestMethod]
    public async Task RecordAsync_FrameArtifacts_UseTheProcessedMp4FrameStream()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "record.mp4");
        var framesDirectory = Path.Join(root, "record.frames");
        var frame = Enumerable.Repeat((byte)0x88, 80 * 60 * 4).ToArray();

        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) => new FakeFrameGrabber(frame, 80, 60);
        Mp4SinkWriterEncoder.s_createNoClobber =
            (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            FramesDirectory = framesDirectory,
            DurationSec = 1,
            Fps = 2,
            MaxEdge = 64,
            CaptureScreen = false,
        }, CancellationToken.None);

        Assert.AreEqual(2, result.Frames);
        Assert.IsNotNull(result.FrameArtifacts);
        Assert.AreEqual(2, result.FrameArtifacts.Samples);
        Assert.AreEqual(1, result.FrameArtifacts.Images, "identical processed BGRA frames should share one JPEG");
        Assert.AreEqual(1, result.FrameArtifacts.RepeatedSamples);
        Assert.IsTrue(File.Exists(Path.Join(framesDirectory, "manifest.json")));
        Assert.IsTrue(File.Exists(Path.Join(framesDirectory, "frames.ndjson")));
        Assert.AreEqual(1, Directory.GetFiles(Path.Join(framesDirectory, "frames"), "*.jpg").Length);
    }

    [TestMethod]
    public async Task RecordAsync_FirstMp4WriteFailure_PreservesAcceptedFrameArtifact()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var framesDirectory = Path.Join(root, "partial.frames");
        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height)
            {
                WriteFrameException = new IOException("simulated encoder failure"),
            };

        var exception = await Assert.ThrowsExactlyAsync<RecordPartialOutputException>(
            () => recording.RecordAsync(uiTarget, null, new RecordOptions
            {
                OutputPath = Path.Join(root, "partial.mp4"),
                FramesDirectory = framesDirectory,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.IsNotNull(exception.FramesDirectory);
        StringAssert.StartsWith(exception.FramesDirectory, framesDirectory + ".partial-", exception.RecoveryHint);
        var partialDirectory = exception.FramesDirectory;
        var manifest = JsonSerializer.Deserialize<JsonElement>(
            await File.ReadAllTextAsync(Path.Join(partialDirectory, "manifest.json")));
        Assert.AreEqual("partial", manifest.GetProperty("status").GetString());
        Assert.AreEqual(1, manifest.GetProperty("timing").GetProperty("sampleCount").GetInt32());
        Assert.AreEqual(0, manifest.GetProperty("video").GetProperty("frameCount").GetInt32());
        Assert.IsFalse(Directory.Exists(framesDirectory));
    }

    [TestMethod]
    public async Task RecordAsync_Mp4PublicationRaceDoesNotPublishMismatchedFrames()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "winner.mp4");
        var framesDirectory = Path.Join(root, "loser.frames");
        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height)
            {
                OnComplete = () =>
                {
                    File.WriteAllText(output, "winning recording");
                    throw new IOException("simulated publication race");
                },
            };

        var exception = await Assert.ThrowsExactlyAsync<RecordPartialOutputException>(
            () => recording.RecordAsync(uiTarget, null, new RecordOptions
            {
                OutputPath = output,
                FramesDirectory = framesDirectory,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.AreEqual("winning recording", await File.ReadAllTextAsync(output));
        Assert.IsFalse(Directory.Exists(framesDirectory));
        Assert.IsNotNull(exception.FramesDirectory);
        StringAssert.StartsWith(exception.FramesDirectory, framesDirectory + ".partial-", exception.RecoveryHint);
        Assert.IsTrue(Directory.Exists(exception.FramesDirectory));
    }

    [TestMethod]
    public async Task RecordAsync_FrameAndMp4FailurePreservesNeitherArtifact()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height)
            {
                WriteFrameException = new IOException("simulated MP4 failure"),
            };
        RecordFrameBundleWriter.s_create = configuration => new FakeFrameSink
        {
            Configuration = configuration,
            WriteException = new IOException("simulated frame failure"),
        };

        var exception = await Assert.ThrowsExactlyAsync<RecordFrameOutputException>(
            () => recording.RecordAsync(uiTarget, null, new RecordOptions
            {
                OutputPath = Path.Join(root, "failed.mp4"),
                FramesDirectory = Path.Join(root, "failed.frames"),
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        StringAssert.Contains(exception.InnerException!.Message, "frame failure");
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(root).Any());
    }

    [TestMethod]
    public async Task RecordAsync_CancellationAfterProcessedFrameCommitsSampleBeforeStopping()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "drained.mp4");
        using var cts = new CancellationTokenSource();
        var frameSink = new FakeFrameSink
        {
            OnWrite = cancellationToken =>
            {
                cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            },
        };
        FakeVideoEncoder? encoder = null;
        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            encoder = new FakeVideoEncoder(path, width, height);
        RecordFrameBundleWriter.s_create = configuration =>
        {
            frameSink.Configuration = configuration;
            return frameSink;
        };

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            FramesDirectory = Path.Join(root, "drained.frames"),
            DurationSec = 10,
            Fps = 1,
            MaxEdge = 64,
        }, cts.Token);

        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(1, frameSink.SampleCount);
        Assert.AreEqual("cancelled", result.StopReason);
        Assert.IsNotNull(encoder);
        Assert.AreEqual(1, encoder!.FramesWritten);
        Assert.IsTrue(encoder.Completed);
    }

    [TestMethod]
    public async Task RecordAsync_JpegWorkerFailurePreservesMp4AndRemovesFrameStaging()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "jpeg-failure.mp4");
        var framesDirectory = Path.Join(root, "jpeg-failure.frames");
        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);
        RecordFrameBundleWriter.s_encodeJpeg = (_, _, _) =>
            throw new IOException("simulated JPEG worker failure");

        var exception = await Assert.ThrowsExactlyAsync<RecordPartialOutputException>(
            () => recording.RecordAsync(uiTarget, null, new RecordOptions
            {
                OutputPath = output,
                FramesDirectory = framesDirectory,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.AreEqual(output, exception.VideoPath);
        Assert.IsTrue(File.Exists(output));
        Assert.IsFalse(Directory.Exists(framesDirectory));
        Assert.IsFalse(Directory.EnumerateDirectories(
            root,
            ".*.staging",
            SearchOption.TopDirectoryOnly).Any());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecordAsync_NonIoFrameFailurePreservesMp4(bool failOnComplete)
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "non-io-frame-failure.mp4");
        var framesDirectory = Path.Join(root, "non-io-frame-failure.frames");
        var frameFailure = new TypeInitializationException(
            "SkiaSharp",
            new DllNotFoundException("simulated native dependency failure"));

        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);
        RecordFrameBundleWriter.s_create = configuration => new FakeFrameSink
        {
            Configuration = configuration,
            WriteException = failOnComplete ? null : frameFailure,
            CompleteException = failOnComplete ? frameFailure : null,
        };

        var exception = await Assert.ThrowsExactlyAsync<RecordPartialOutputException>(
            () => recording.RecordAsync(uiTarget, null, new RecordOptions
            {
                OutputPath = output,
                FramesDirectory = framesDirectory,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.AreEqual(output, exception.VideoPath);
        Assert.IsTrue(File.Exists(output));
        Assert.IsFalse(Directory.Exists(framesDirectory));
    }

    [TestMethod]
    public async Task RecordAsync_TruncatedFrameBundleReturnsActionableWarning()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);
        RecordFrameBundleWriter.s_create = configuration => new FakeFrameSink
        {
            Configuration = configuration,
            IsTruncated = true,
            ByteLimit = RecordFrameBundleConfiguration.DefaultMaximumBundleBytes,
        };

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = Path.Join(root, "truncated.mp4"),
            FramesDirectory = Path.Join(root, "truncated.frames"),
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
        }, CancellationToken.None);

        Assert.IsNotNull(result.FrameArtifacts);
        Assert.IsTrue(result.FrameArtifacts.Truncated);
        Assert.IsNotNull(result.Warnings);
        Assert.IsTrue(result.Warnings.Any(
            warning => warning.Contains("only the indexed frame prefix was retained", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RecordAsync_FirstFrameArrivesAfterTheRequestedDuration_StillCapturesAFrame()
    {
        // The elapsed-duration check used to run before any frame had been committed, so a capture
        // source that took longer than --duration to deliver its first frame into the capture loop
        // ended the recording with zero frames: an empty MP4 and, with --frames-dir, a zero-frame
        // manifest. Elapsed time may only end a recording that has already captured something.
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "slow-first-frame.mp4");
        var frame = Enumerable.Repeat((byte)0x33, 64 * 64 * 4).ToArray();

        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new SlowStartingFrameGrabber(frame, 64, 64, TimeSpan.FromMilliseconds(1_500));
        Mp4SinkWriterEncoder.s_createNoClobber =
            (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 2,
            MaxEdge = 64,
        }, CancellationToken.None);

        Assert.IsTrue(
            result.Frames >= 1,
            $"a recording whose capture source starts late must still capture a frame, got {result.Frames}");
    }

    [TestMethod]
    public async Task RecordAsync_SlowFrameArtifactSetup_IsNotChargedToTheRequestedDuration()
    {
        // Frame artifact setup creates a staging directory and opens the manifest and index writers.
        // The capture clock used to start before that work, so on a loaded machine a short recording
        // could spend its whole --duration budget on filesystem setup and capture almost nothing.
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "slow-setup.mp4");
        var setupDelay = TimeSpan.FromMilliseconds(1_500);

        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber =
            (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);
        RecordFrameBundleWriter.s_create = configuration =>
        {
            Thread.Sleep(setupDelay);
            return new FakeFrameSink { Configuration = configuration };
        };

        var wallClock = Stopwatch.StartNew();
        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            FramesDirectory = Path.Join(root, "slow-setup.frames"),
            DurationSec = 1,
            Fps = 4,
            MaxEdge = 64,
        }, CancellationToken.None);
        wallClock.Stop();

        Assert.IsTrue(result.Frames >= 1, "the recording must capture something");

        // Wall time covers setup and capture; the reported elapsed time must cover capture only, so
        // the setup delay has to show up as the difference. Comparing the two rather than asserting
        // an absolute elapsed time keeps this immune to a slow machine stretching the capture loop.
        var excluded = wallClock.ElapsedMilliseconds - result.ElapsedMs;
        Assert.IsTrue(
            excluded >= setupDelay.TotalMilliseconds * 0.8,
            $"reported elapsed time must measure capture, not setup: only {excluded}ms of the " +
            $"{setupDelay.TotalMilliseconds}ms setup was excluded from {result.ElapsedMs}ms elapsed");
    }

    private sealed class FakeFrameGrabber(byte[] pixels, int width, int height) : IFrameGrabber
    {
        private long _version;

        public bool IsClosed { get; init; }

        public bool Disposed { get; private set; }

        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
            => (pixels, width, height, Interlocked.Increment(ref _version));

        public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Reports one frame to the recorder's setup read, then yields nothing for the given silence
    /// window before delivering frames again — the shape of a capture source that is slow to start
    /// feeding the capture loop.
    /// </summary>
    private sealed class SlowStartingFrameGrabber(
        byte[] pixels, int width, int height, TimeSpan silence) : IFrameGrabber
    {
        private readonly Stopwatch _silenceSince = new();
        private int _reads;
        private long _version;

        public bool IsClosed => false;

        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
        {
            // Recorder setup dereferences its read without a null check, so it always gets a frame.
            if (Interlocked.Increment(ref _reads) == 1)
            {
                return Latest();
            }

            // Time the silence from the capture loop's first read, so the window the recorder sees
            // does not shrink by however long the recorder spent between the two.
            if (!_silenceSince.IsRunning)
            {
                _silenceSince.Start();
            }

            return _silenceSince.Elapsed < silence ? null : Latest();
        }

        public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);

        public void Dispose()
        {
        }

        private (byte[] Pixels, int Width, int Height, long Version) Latest()
            => (pixels, width, height, Interlocked.Increment(ref _version));
    }

    private sealed class FakeVideoEncoder(string path, int width, int height) : IVideoEncoder
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public int FramesWritten { get; private set; }

        public bool Completed { get; private set; }

        public bool Disposed { get; private set; }

        public Exception? WriteFrameException { get; init; }

        public Action? OnComplete { get; init; }

        public void WriteFrame(ReadOnlySpan<byte> bgra, long sampleTimeHns, long sampleDurationHns)
        {
            if (WriteFrameException is not null)
            {
                throw WriteFrameException;
            }

            Assert.AreEqual(Width * Height * 4, bgra.Length);
            Assert.IsTrue(sampleDurationHns > 0);
            FramesWritten++;
        }

        public void Complete()
        {
            Completed = true;
            OnComplete?.Invoke();
            File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class FakeFrameSink : IRecordFrameSink
    {
        public RecordFrameBundleConfiguration? Configuration { get; set; }

        public int SampleCount { get; private set; }

        public int ImageCount => SampleCount;
        public bool IsTruncated { get; init; }

        public long ByteLimit { get; init; }

        public bool Aborted { get; private set; }

        public Action<CancellationToken>? OnWrite { get; init; }

        public Exception? WriteException { get; init; }

        public Exception? CompleteException { get; init; }

        public Exception? AbortException { get; init; }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> bgra,
            RecordFrameSample sample,
            CancellationToken cancellationToken)
        {
            OnWrite?.Invoke(cancellationToken);
            if (WriteException is not null)
            {
                throw WriteException;
            }
            SampleCount++;
            return ValueTask.CompletedTask;
        }

        public Task<RecordFrameArtifactResult> CompleteAsync(RecordFrameCompletion completion)
        {
            if (CompleteException is not null)
            {
                throw CompleteException;
            }

            var configuration = Configuration ?? throw new InvalidOperationException("Frame sink was not configured.");
            var directory = completion.PublicationDirectory ?? configuration.FinalDirectory;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Join(directory, "frames.ndjson"), "");
            File.WriteAllText(Path.Join(directory, "manifest.json"),
                JsonSerializer.Serialize(new RecordFrameBundleManifest
                {
                    Video = new RecordFrameVideoManifest { Path = configuration.VideoPath },
                }, RecordingJsonContext.Default.RecordFrameBundleManifest));
            return Task.FromResult(new RecordFrameArtifactResult
            {
                Directory = directory,
                Manifest = Path.Join(directory, "manifest.json"),
                Index = Path.Join(directory, "frames.ndjson"),
                Samples = SampleCount,
                Images = ImageCount,
                Truncated = IsTruncated,
                ByteLimit = ByteLimit,
            });
        }

        public Task AbortAsync()
        {
            Aborted = true;
            if (AbortException is not null)
            {
                throw AbortException;
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
