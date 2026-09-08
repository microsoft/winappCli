// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    public void WgcFallback_WithoutCaptureScreen_Throws()
    {
        // Without --capture-screen, a WGC init failure must throw rather than silently
        // falling back to screen capture (which would leak unrelated windows).
        var inner = new InvalidOperationException("Simulated WGC init failure");
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<UiRecordingService>.Instance;
        var thrown = false;
        try
        {
            UiRecordingService.EnsureWgcFallbackConsented(inner, captureScreenRequested: false, logger);
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }
        Assert.IsTrue(thrown, "EnsureWgcFallbackConsented without --capture-screen must throw InvalidOperationException");
    }

    [TestMethod]
    public void WgcFallback_WithCaptureScreen_DoesNotThrow()
    {
        // With --capture-screen, the user consented to screen-DC capture, so the fallback is allowed.
        var inner = new InvalidOperationException("Simulated WGC init failure");
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<UiRecordingService>.Instance;
        // Must not throw — screen capture was explicitly requested.
        UiRecordingService.EnsureWgcFallbackConsented(inner, captureScreenRequested: true, logger);
    }

    [TestMethod]
    public void WgcFallback_ThrowMessage_MentionsCaptureScreenOption()
    {
        var inner = new InvalidOperationException("Simulated WGC init failure");
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<UiRecordingService>.Instance;
        try
        {
            UiRecordingService.EnsureWgcFallbackConsented(inner, captureScreenRequested: false, logger);
            Assert.Fail("expected InvalidOperationException");
        }
        catch (InvalidOperationException ex)
        {
            StringAssert.Contains(ex.Message, "--capture-screen",
                "error must mention --capture-screen so users know how to opt in to screen capture");
        }
    }

    [TestMethod]
    public void Record_ShouldEncodeClosedDrainFrame_SkipsAlreadyEncodedVersion()
    {
        Assert.IsFalse(UiRecordingService.ShouldEncodeClosedDrainFrame(cachedVersion: 7, lastEncodedVersion: 7));
    }

    [TestMethod]
    public void Record_ShouldEncodeClosedDrainFrame_EncodesNewerVersion()
    {
        Assert.IsTrue(UiRecordingService.ShouldEncodeClosedDrainFrame(cachedVersion: 8, lastEncodedVersion: 7));
    }

    [TestMethod]
    public async Task Record_WindowClosedMidRecording_FinalizesGracefullyWithPartialVideo()
    {
        // M5: when the capture item closes mid-recording, the recording loop breaks
        // and finalizes the frames already captured rather than encoding stale data
        // to the deadline. Simulated via the fake returning fewer frames than duration.
        _fakeRecording.RecordResult = new RecordCaptureResult { Frames = 3, Width = 640, Height = 480, Mode = "wgc" };

        var outputPath = Path.Combine(_tempDirectory.FullName, "partial-close.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        // Duration of 60s; fake returns 3 frames (simulating early close).
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "60", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode, "window-closed mid-recording must finalize gracefully (exit 0)");
        Assert.IsTrue(File.Exists(outputPath), "partial video must be written");
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(3, result.GetProperty("frames").GetInt32(), "partial frame count must be reported");
        Assert.AreEqual("wgc", result.GetProperty("mode").GetString());
    }
}
