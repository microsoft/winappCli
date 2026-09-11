// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

internal sealed partial class UiRecordingService
{
    public Task<RecordCaptureResult> RecordAsync(
        UiTarget uiTarget, string? elementId, RecordOptions options, CancellationToken ct,
        Action<bool>? onRecordingStarted = null)
    {
        ArgumentNullException.ThrowIfNull(uiTarget);
        return RecordWithPublicationAsync(uiTarget, elementId, options, onRecordingStarted, ct);
    }

    public Task<RecordCaptureResult> RecordDesktopAsync(
        RecordOptions options, CancellationToken ct, Action<bool>? onRecordingStarted = null)
    {
        if (options.CaptureScreen || options.NoActivation)
        {
            throw new ArgumentException("Desktop recording does not accept window capture policy flags.", nameof(options));
        }
        return RecordWithPublicationAsync(null, null, options, onRecordingStarted, ct);
    }

    private async Task<RecordCaptureResult> RecordWithPublicationAsync(
        UiTarget? uiTarget, string? elementId, RecordOptions options,
        Action<bool>? onRecordingStarted, CancellationToken ct)
    {
        var videoPath = Path.GetFullPath(options.OutputPath);
        var framesDirectory = options.FramesDirectory is { } frames ? Path.GetFullPath(frames) : null;
        // Replacing a video-only take must not leave the previous take's canonical frame bundle.
        var pairedDirectory = framesDirectory ?? RecordingArtifactPublisher.GetFramesDirectory(videoPath);
        RecordingArtifactPublisher.ValidateDestination(videoPath, pairedDirectory, options.Overwrite);

        if (!options.Overwrite)
        {
            return await RecordCoreAsync(uiTarget, elementId, options, ct, onRecordingStarted).ConfigureAwait(false);
        }

        var staging = Path.Join(Path.GetDirectoryName(videoPath)!, $".recording-{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(staging);
        var stagedVideo = Path.Join(staging, Path.GetFileName(videoPath));
        var stagedFrames = framesDirectory is null ? null : Path.Join(staging, "frames");
        try
        {
            var result = await RecordCoreAsync(uiTarget, elementId, new RecordOptions
            {
                OutputPath = stagedVideo,
                FramesDirectory = stagedFrames,
                DurationSec = options.DurationSec,
                Fps = options.Fps,
                MaxEdge = options.MaxEdge,
                CaptureScreen = options.CaptureScreen,
                NoActivation = options.NoActivation,
            }, ct, onRecordingStarted).ConfigureAwait(false);

            // Cancellation stops sampling, not publication of the finalized evidence.
            using var completion = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            if (stagedFrames is not null)
            {
                await RecordingArtifactPublisher.RewriteManifestVideoAsync(stagedFrames, videoPath, completion.Token)
                    .ConfigureAwait(false);
            }
            var previousFrames = RecordingArtifactPublisher.Publish(
                stagedVideo, videoPath, stagedFrames, pairedDirectory, options.Overwrite);
            return new RecordCaptureResult
            {
                Frames = result.Frames,
                Width = result.Width,
                Height = result.Height,
                FileSize = result.FileSize,
                Mode = result.Mode,
                ElapsedMs = result.ElapsedMs,
                AchievedFps = result.AchievedFps,
                CadenceRatio = result.CadenceRatio,
                StopReason = result.StopReason,
                FrameArtifacts = result.FrameArtifacts is { } bundle
                    ? RecordingArtifactPublisher.RelocateFrames(bundle, framesDirectory!)
                    : null,
                Coordinates = result.Coordinates,
                Warnings = previousFrames is null ? result.Warnings :
                    [.. result.Warnings ?? [], $"Previous frame artifacts were retained at {previousFrames}."],
            };
        }
        catch (RecordPartialOutputException ex)
        {
            throw new RecordPartialOutputException(
                ex.Message, ex.VideoPath, ex.FramesDirectory,
                $"The previous output was not replaced. Inspect the preserved artifacts at '{staging}'. {ex.RecoveryHint}", ex);
        }
        catch (Exception ex) when (File.Exists(stagedVideo))
        {
            if (stagedFrames is not null && File.Exists(Path.Join(stagedFrames, "manifest.json")))
            {
                try
                {
                    using var repair = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await RecordingArtifactPublisher.RewriteManifestVideoAsync(stagedFrames, stagedVideo, repair.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception repairError) when (repairError is IOException or OperationCanceledException)
                {
                    System.Diagnostics.Trace.TraceWarning("Could not update recovery manifest: {0}", repairError.Message);
                }
            }
            throw new RecordPartialOutputException(
                "The recording was finalized but could not be published.",
                stagedVideo, stagedFrames is not null && Directory.Exists(stagedFrames) ? stagedFrames : null,
                $"The previous output was not replaced. Recover the new recording from '{staging}'.", ex);
        }
        finally
        {
            // Only remove our own empty directory; evidence is never cleanup.
            try
            {
                if (Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any())
                {
                    Directory.Delete(staging);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("Could not remove empty recording staging: {0}", ex.Message);
            }
        }
    }
}
