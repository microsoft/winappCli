// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


namespace WinApp.Cli.Tests;

/// <summary>
/// Fake <see cref="IUiRecordingService"/> — writes a stub video (and, when requested, a frame
/// bundle) and returns a configurable result, so the <c>ui record</c> command can be exercised
/// without a live capture session or the Media Foundation encoder.
/// </summary>
internal sealed class FakeUiRecordingService : IUiRecordingService
{
    public RecordCaptureResult RecordResult { get; set; } = new() { Frames = 3, Width = 2, Height = 2, FileSize = 0, Mode = "wgc" };

    public RecordOptions? LastRecordOptions { get; private set; }

    public Exception? RecordException { get; set; }

    public Exception? RecordExceptionAfterStarted { get; set; }

    public bool RecordShouldWaitForCancellation { get; set; }

    public bool? RecordingStartedFrameArtifactsActiveOverride { get; set; }

    public Action? BeforeRecordingStarted { get; set; }

    public Action? AfterRecordingStarted { get; set; }

    public bool InvokeRecordingStartedTwice { get; set; }

    public Task? WaitAfterRecordingStarted { get; set; }

    public async Task<RecordCaptureResult> RecordAsync(UiTarget uiTarget, string? elementId, RecordOptions options, CancellationToken ct, Action<bool>? onRecordingStarted = null)
    {
        LastRecordOptions = options;
        if (RecordException is not null)
        {
            throw RecordException;
        }
        await File.WriteAllBytesAsync(options.OutputPath, new byte[16], CancellationToken.None);
        RecordFrameArtifactResult? frameArtifacts = RecordResult.FrameArtifacts;
        if (options.FramesDirectory is not null)
        {
            Directory.CreateDirectory(Path.Join(options.FramesDirectory, "frames"));
            await File.WriteAllTextAsync(
                Path.Join(options.FramesDirectory, "frames.ndjson"),
                "{\"sampleIndex\":0}\n",
                CancellationToken.None);
            await File.WriteAllTextAsync(
                Path.Join(options.FramesDirectory, "manifest.json"),
                "{\"schemaVersion\":1,\"status\":\"complete\"}",
                CancellationToken.None);
            frameArtifacts = new RecordFrameArtifactResult
            {
                Directory = options.FramesDirectory,
                Manifest = Path.Join(options.FramesDirectory, "manifest.json"),
                Index = Path.Join(options.FramesDirectory, "frames.ndjson"),
                Samples = RecordResult.Frames,
                Images = 1,
                RepeatedSamples = Math.Max(0, RecordResult.Frames - 1),
                TotalBytes = 64,
            };
        }
        BeforeRecordingStarted?.Invoke();
        var frameArtifactsActive = RecordingStartedFrameArtifactsActiveOverride
            ?? (options.FramesDirectory is not null);
        onRecordingStarted?.Invoke(frameArtifactsActive);
        if (InvokeRecordingStartedTwice)
        {
            onRecordingStarted?.Invoke(frameArtifactsActive);
        }
        AfterRecordingStarted?.Invoke();
        if (WaitAfterRecordingStarted is { } wait)
        {
            await wait.ConfigureAwait(false);
        }
        if (RecordExceptionAfterStarted is not null)
        {
            throw RecordExceptionAfterStarted;
        }
        if (RecordShouldWaitForCancellation)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation is the expected stop signal.
            }
        }
        var size = new FileInfo(options.OutputPath).Length;
        return new RecordCaptureResult
        {
            Frames = RecordResult.Frames,
            Width = RecordResult.Width,
            Height = RecordResult.Height,
            FileSize = size,
            Mode = RecordResult.Mode,
            ElapsedMs = RecordResult.ElapsedMs,
            AchievedFps = RecordResult.AchievedFps,
            CadenceRatio = RecordResult.CadenceRatio,
            StopReason = RecordResult.StopReason,
            FrameArtifacts = frameArtifacts,
            Warnings = RecordResult.Warnings,
        };
    }
}
