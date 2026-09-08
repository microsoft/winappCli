// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    public async Task Record_Frames_AllowsUnboundedRecordingAndDerivesDirectory()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var outputPath = Path.Join(_tempDirectory.FullName, "unbounded.mp4");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--frames", "--output", outputPath, "--json"]);

        var framesDirectory = Path.Join(_tempDirectory.FullName, "unbounded.frames");
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakeRecording.LastRecordOptions?.DurationSec);
        Assert.AreEqual(1280, _fakeRecording.LastRecordOptions?.MaxEdge);
        Assert.AreEqual(framesDirectory, _fakeRecording.LastRecordOptions?.FramesDirectory);
        Assert.IsTrue(File.Exists(Path.Join(framesDirectory, "manifest.json")));
    }

    [TestMethod]
    public async Task Record_Frames_ValidatesFpsAndMaxEdge()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var outputPath = Path.Join(_tempDirectory.FullName, "limits.mp4");

        var fpsExitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--frames", "--fps", "31", "--output", outputPath, "--json"]);
        Assert.AreEqual(1, fpsExitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "1 through 30");

        foreach (var maxEdge in new[] { "0", "32", "4097" })
        {
            ConsoleStdErr.GetStringBuilder().Clear();
            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command,
                ["-a", "TestApp", "--frames", "--max-edge", maxEdge, "--output", outputPath, "--json"]);
            Assert.AreEqual(1, exitCode);
            StringAssert.Contains(ConsoleStdErr.ToString(), "64 through 4096");
        }
    }

    [TestMethod]
    public async Task Record_Frames_NeverClobbersMp4OrDerivedDirectory()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var outputPath = Path.Join(_tempDirectory.FullName, "existing.mp4");
        var framesDirectory = Path.Join(_tempDirectory.FullName, "existing.frames");
        await File.WriteAllTextAsync(outputPath, "video sentinel");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--frames", "--output", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual("video sentinel", await File.ReadAllTextAsync(outputPath));
        Assert.IsFalse(Directory.Exists(framesDirectory));
        StringAssert.Contains(ConsoleStdErr.ToString(), "output_exists");

        File.Delete(outputPath);
        ConsoleStdErr.GetStringBuilder().Clear();
        Directory.CreateDirectory(framesDirectory);
        await File.WriteAllTextAsync(Path.Join(framesDirectory, "sentinel.txt"), "frame sentinel");

        exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--frames", "--output", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsFalse(File.Exists(outputPath));
        Assert.AreEqual(
            "frame sentinel",
            await File.ReadAllTextAsync(Path.Join(framesDirectory, "sentinel.txt")));
        StringAssert.Contains(ConsoleStdErr.ToString(), "output_exists");
    }

    [TestMethod]
    public void Record_Frames_DerivedPathHandlesFramesExtension()
    {
        var outputPath = Path.Join(_tempDirectory.FullName, "capture.frames");
        Assert.AreEqual(
            outputPath + ".frames",
            UiRecordCommand.Handler.GetFramesDirectory(outputPath));
    }

    [TestMethod]
    public async Task Record_Frames_EmitsAdditiveJsonAndStartedEvent()
    {
        _fakeRecording.RecordResult = new RecordCaptureResult
        {
            Frames = 10,
            Width = 640,
            Height = 480,
            Mode = "wgc",
            ElapsedMs = 1_000,
            AchievedFps = 10,
            CadenceRatio = 1,
            StopReason = "duration_elapsed",
        };
        var command = GetRequiredService<UiRecordCommand>();
        var outputPath = Path.Join(_tempDirectory.FullName, "recording.mp4");
        var framesDirectory = Path.Join(_tempDirectory.FullName, "recording.frames");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--fps", "10",
                "--output", outputPath,
                "--frames",
                "--json",
            ]);

        Assert.AreEqual(0, exitCode);
        var result = JsonSerializer.Deserialize<JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(outputPath, result.GetProperty("path").GetString());
        Assert.AreEqual(framesDirectory, result.GetProperty("frameArtifacts").GetProperty("directory").GetString());

        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "\"recording-started\"");
        StringAssert.Contains(stderr, framesDirectory.Replace("\\", "\\\\"));
        Assert.IsFalse(stderr.Contains("\"recording-progress\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Record_StartedEventOmitsUnavailableFramePaths()
    {
        _fakeRecording.RecordingStartedFrameArtifactsActiveOverride = false;
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--output", Path.Join(_tempDirectory.FullName, "recording.mp4"),
                "--frames",
                "--json",
            ]);

        Assert.AreEqual(0, exitCode);
        var startedLine = ConsoleStdErr.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"recording-started\"", StringComparison.Ordinal));
        var startedEvent = JsonSerializer.Deserialize<JsonElement>(startedLine);
        Assert.IsFalse(startedEvent.TryGetProperty("framesDirectory", out _));
        Assert.IsFalse(startedEvent.TryGetProperty("framesManifest", out _));
        Assert.IsFalse(startedEvent.TryGetProperty("framesIndex", out _));
    }

    [TestMethod]
    public async Task Record_PartialOutput_EmitsStableRecoveryEnvelope()
    {
        var videoPath = Path.Join(_tempDirectory.FullName, "preserved.mp4");
        _fakeRecording.RecordException = new RecordPartialOutputException(
            "Frame output failed.",
            videoPath,
            framesDirectory: null,
            "Retry with --frames and a new output path.",
            new IOException("disk full"));
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--duration-sec", "1", "--json"]);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(stderr));
        using var document = JsonDocument.ParseValue(ref reader);
        var error = document.RootElement.GetProperty("error");
        Assert.AreEqual("partial_output", error.GetProperty("code").GetString());
        Assert.AreEqual(videoPath, error.GetProperty("partialOutput").GetProperty("videoPath").GetString());
        Assert.IsFalse(stderr.Contains("   at ", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Record_PartialOutputAfterStarted_EmitsJsonLines()
    {
        var videoPath = Path.Join(_tempDirectory.FullName, "preserved-after-start.mp4");
        _fakeRecording.RecordExceptionAfterStarted = new RecordPartialOutputException(
            "Frame output failed.",
            videoPath,
            framesDirectory: null,
            "Retry with --frames and a new output path.",
            new IOException("disk full"));
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--duration-sec", "1", "--json"]);

        Assert.AreEqual(1, exitCode);
        var lines = ConsoleStdErr.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith('{'))
            .ToArray();
        Assert.HasCount(2, lines);
        var started = JsonSerializer.Deserialize<JsonElement>(lines[0]);
        var error = JsonSerializer.Deserialize<JsonElement>(lines[1]);
        Assert.AreEqual("recording-started", started.GetProperty("event").GetString());
        Assert.AreEqual("partial_output", error.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Record_FrameOutputFailed_EmitsStableRecoveryEnvelope()
    {
        _fakeRecording.RecordException = new RecordFrameOutputException(
            "No recording artifact could be preserved.",
            "Check disk space and retry with a new --output path.",
            new IOException("simulated frame failure"));
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--frames", "--output", Path.Join(_tempDirectory.FullName, "failed.mp4"), "--json"]);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(stderr));
        using var document = JsonDocument.ParseValue(ref reader);
        var error = document.RootElement.GetProperty("error");
        Assert.AreEqual("frame_output_failed", error.GetProperty("code").GetString());
        StringAssert.Contains(error.GetProperty("recoveryHint").GetString(), "new --output path");
        Assert.IsFalse(stderr.Contains("   at ", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RecordFrameArtifactCoordinator_InitializationFailureLogsSanitizedReason()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var provider = new TextWriterLoggerProvider(stdout, stderr);
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Information).AddProvider(provider));
        RecordFrameBundleWriter.s_create = _ =>
            throw new UnauthorizedAccessException("Access to the frame parent was denied.");
        try
        {
            var coordinator = RecordFrameArtifactCoordinator.Create(
                CreateFrameConfiguration(
                    Path.Join(_tempDirectory.FullName, "unwritable", "recording.frames"),
                    logger: loggerFactory.CreateLogger("recording-test")));

            await coordinator.DisposeAsync();

            Assert.IsNotNull(coordinator.Failure);
            var error = stderr.ToString();
            StringAssert.Contains(error, "Could not initialize frame artifact output");
            StringAssert.Contains(error, "Access to the frame parent was denied.");
            Assert.IsFalse(error.Contains("UnauthorizedAccessException", StringComparison.Ordinal));
            Assert.IsFalse(error.Contains("   at ", StringComparison.Ordinal));
        }
        finally
        {
            RecordFrameBundleWriter.ResetTestSeams();
        }
    }

    [TestMethod]
    public async Task RecordFrameBundleWriter_WritesMinimalTimelineAndDeduplicatesPixels()
    {
        var finalDirectory = Path.Join(_tempDirectory.FullName, "writer.frames");
        var writer = new RecordFrameBundleWriter(CreateFrameConfiguration(finalDirectory));
        var first = Enumerable.Repeat((byte)0x20, 64 * 64 * 4).ToArray();
        var changed = Enumerable.Repeat((byte)0xA0, 64 * 64 * 4).ToArray();

        await writer.WriteAsync(first, Sample(0, 12, 0), CancellationToken.None);
        await writer.WriteAsync(first, Sample(1, 108, 100), CancellationToken.None);
        await writer.WriteAsync(changed, Sample(2, 211, 200), CancellationToken.None);
        var result = await writer.CompleteAsync(Completion());
        await writer.DisposeAsync();

        Assert.AreEqual(3, result.Samples);
        Assert.AreEqual(2, result.Images);
        Assert.AreEqual(1, result.RepeatedSamples);

        var images = Directory.GetFiles(Path.Join(finalDirectory, "frames"), "*.jpg");
        Assert.AreEqual(2, images.Length);
        using var decoded = SKBitmap.Decode(images[0]);
        Assert.IsNotNull(decoded);
        Assert.AreEqual(64, decoded.Width);
        Assert.AreEqual(64, decoded.Height);

        var lines = await File.ReadAllLinesAsync(Path.Join(finalDirectory, "frames.ndjson"));
        Assert.AreEqual(3, lines.Length);
        var entries = lines
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToArray();
        var firstEntry = entries[0];
        var repeatedEntry = entries[1];
        var changedEntry = entries[2];
        Assert.AreEqual(12, firstEntry.GetProperty("elapsedMs").GetInt64());
        Assert.IsTrue(firstEntry.GetProperty("changed").GetBoolean());
        Assert.IsFalse(repeatedEntry.GetProperty("changed").GetBoolean());
        Assert.AreEqual(firstEntry.GetProperty("file").GetString(), repeatedEntry.GetProperty("file").GetString());
        Assert.IsTrue(changedEntry.GetProperty("changed").GetBoolean());
        Assert.AreNotEqual(firstEntry.GetProperty("file").GetString(), changedEntry.GetProperty("file").GetString());
        Assert.IsFalse(firstEntry.TryGetProperty("sha256", out _));
        Assert.IsFalse(firstEntry.TryGetProperty("contentRect", out _));
        foreach (var file in entries.Select(entry => entry.GetProperty("file").GetString()).Distinct())
        {
            using var indexedImage = SKBitmap.Decode(Path.Join(finalDirectory, file!.Replace('/', Path.DirectorySeparatorChar)));
            Assert.IsNotNull(indexedImage);
            Assert.AreEqual(64, indexedImage.Width);
            Assert.AreEqual(64, indexedImage.Height);
        }

        var manifest = JsonSerializer.Deserialize<JsonElement>(
            await File.ReadAllTextAsync(Path.Join(finalDirectory, "manifest.json")));
        Assert.AreEqual("complete", manifest.GetProperty("status").GetString());
        Assert.AreEqual(3, manifest.GetProperty("timing").GetProperty("sampleCount").GetInt32());
        Assert.AreEqual(2, manifest.GetProperty("timing").GetProperty("imageCount").GetInt32());
        Assert.IsFalse(manifest.TryGetProperty("source", out _));
        Assert.IsFalse(manifest.TryGetProperty("crop", out _));
    }

    [TestMethod]
    public async Task RecordFrameBundleWriter_ByteLimitPublishesIndexedPrefix()
    {
        try
        {
            RecordFrameBundleWriter.s_encodeJpeg = (_, _, _) => new byte[800];
            var finalDirectory = Path.Join(_tempDirectory.FullName, "limited.frames");
            await using var writer = new RecordFrameBundleWriter(CreateFrameConfiguration(
                finalDirectory,
                maximumBundleBytes: 1024 * 1024 + 2_000));

            for (var index = 0; index < 20; index++)
            {
                var pixels = Enumerable.Repeat((byte)index, 64 * 64 * 4).ToArray();
                await writer.WriteAsync(pixels, Sample(index, index * 100L, index * 100d), CancellationToken.None);
            }

            var result = await writer.CompleteAsync(Completion(videoFrameCount: 20));
            Assert.IsTrue(result.Truncated);
            Assert.IsGreaterThan(0, result.Samples);
            Assert.IsLessThan(20, result.Samples);
            Assert.IsTrue(result.TotalBytes <= result.ByteLimit);

            var lines = await File.ReadAllLinesAsync(Path.Join(finalDirectory, "frames.ndjson"));
            Assert.AreEqual(result.Samples, lines.Length);
            var indexedFiles = lines
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line).GetProperty("file").GetString())
                .ToHashSet(StringComparer.Ordinal);
            var publishedFiles = Directory.GetFiles(Path.Join(finalDirectory, "frames"), "*.jpg")
                .Select(path => $"frames/{Path.GetFileName(path)}")
                .ToHashSet(StringComparer.Ordinal);
            CollectionAssert.AreEquivalent(indexedFiles.ToArray(), publishedFiles.ToArray());

            var manifest = JsonSerializer.Deserialize<JsonElement>(
                await File.ReadAllTextAsync(Path.Join(finalDirectory, "manifest.json")));
            Assert.AreEqual("truncated", manifest.GetProperty("status").GetString());
            Assert.IsTrue(manifest.GetProperty("frames").GetProperty("truncated").GetBoolean());
            Assert.AreEqual(result.Samples, manifest.GetProperty("timing").GetProperty("sampleCount").GetInt32());
            Assert.AreEqual(result.Images, manifest.GetProperty("timing").GetProperty("imageCount").GetInt32());
            var expectedElapsedMs = (result.Samples - 1) * 100L;
            var expectedAchievedFps = result.Samples * 1000.0 / expectedElapsedMs;
            Assert.AreEqual(expectedElapsedMs, manifest.GetProperty("timing").GetProperty("elapsedMs").GetInt64());
            Assert.AreEqual(expectedAchievedFps, manifest.GetProperty("timing").GetProperty("achievedFps").GetDouble(), 0.001);
            Assert.AreEqual(expectedAchievedFps / 10, manifest.GetProperty("timing").GetProperty("cadenceRatio").GetDouble(), 0.001);
            Assert.AreEqual(20, manifest.GetProperty("video").GetProperty("frameCount").GetInt32());
        }
        finally
        {
            RecordFrameBundleWriter.ResetTestSeams();
        }
    }

    [TestMethod]
    public async Task RecordFrameBundleWriter_FixedQueueAppliesBackpressure()
    {
        using var releaseEncoder = new ManualResetEventSlim();
        var encoderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordFrameBundleWriter.s_encodeJpeg = (_, _, _) =>
        {
            encoderEntered.TrySetResult();
            releaseEncoder.Wait();
            return [1, 2, 3];
        };

        var writer = new RecordFrameBundleWriter(CreateFrameConfiguration(
            Path.Join(_tempDirectory.FullName, "backpressure.frames")));
        try
        {
            var pixels = new byte[64 * 64 * 4];
            await writer.WriteAsync(pixels, Sample(0, 1, 0), CancellationToken.None);
            await encoderEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await writer.WriteAsync(pixels, Sample(1, 100, 100), CancellationToken.None);

            var blockedPixels = new byte[64 * 64 * 4];
            var blockedWrite = writer.WriteAsync(
                blockedPixels,
                Sample(2, 200, 200),
                CancellationToken.None).AsTask();
            await Task.Delay(100);
            Assert.IsFalse(blockedWrite.IsCompleted);

            blockedPixels[0] = 1;
            releaseEncoder.Set();
            await blockedWrite.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await writer.CompleteAsync(Completion());
            Assert.AreEqual(3, result.Samples);
            Assert.AreEqual(2, result.Images);
        }
        finally
        {
            releaseEncoder.Set();
            await writer.DisposeAsync();
            RecordFrameBundleWriter.ResetTestSeams();
        }
    }

    [TestMethod]
    public async Task RecordFrameBundleWriter_PublicationNeverReplacesExistingDirectory()
    {
        var finalDirectory = Path.Join(_tempDirectory.FullName, "race.frames");
        await using var writer = new RecordFrameBundleWriter(CreateFrameConfiguration(finalDirectory));
        await writer.WriteAsync(new byte[64 * 64 * 4], Sample(0, 1, 0), CancellationToken.None);
        Directory.CreateDirectory(finalDirectory);
        await File.WriteAllTextAsync(Path.Join(finalDirectory, "sentinel.txt"), "keep");

        await Assert.ThrowsExactlyAsync<IOException>(() => writer.CompleteAsync(Completion()));
        await writer.AbortAsync();

        Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Join(finalDirectory, "sentinel.txt")));
    }

    private RecordFrameBundleConfiguration CreateFrameConfiguration(
        string finalDirectory,
        long maximumBundleBytes = RecordFrameBundleConfiguration.DefaultMaximumBundleBytes,
        ILogger? logger = null)
        => new()
        {
            FinalDirectory = finalDirectory,
            VideoPath = Path.Join(_tempDirectory.FullName, "video.mp4"),
            StartedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            Width = 64,
            Height = 64,
            MaximumBundleBytes = maximumBundleBytes,
            Requested = new RecordFrameRequestManifest
            {
                DurationSec = 0,
                Fps = 10,
                MaxEdge = 1280,
            },
            Logger = logger ?? NullLogger.Instance,
        };

    private static RecordFrameSample Sample(int index, long elapsedMs, double mediaTimeMs)
        => new()
        {
            SampleIndex = index,
            ElapsedMs = elapsedMs,
            MediaTimeMs = mediaTimeMs,
        };

    private static RecordFrameCompletion Completion(int videoFrameCount = 3)
        => new()
        {
            Status = "complete",
            StopReason = "cancelled",
            ElapsedMs = 300,
            AchievedFps = 10,
            CadenceRatio = 1,
            VideoStatus = "complete",
            VideoFrameCount = videoFrameCount,
            VideoFileSize = 1_024,
        };
}
