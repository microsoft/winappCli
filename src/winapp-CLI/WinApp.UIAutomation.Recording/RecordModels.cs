// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

/// <summary>Options controlling an MP4 window/element recording.</summary>
public sealed class RecordOptions
{
    /// <summary>Absolute output path for the .mp4 file.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Recording duration in seconds. 0 = record until cancellation (Ctrl+C).</summary>
    public int DurationSec { get; init; }

    /// <summary>Target frames per second.</summary>
    public int Fps { get; init; } = 15;

    /// <summary>
    /// Downscale so the longest edge is at most this many pixels. 0 = no downscale. A nonzero value
    /// must be at least 64, the smallest frame the H.264 encoder accepts.
    /// </summary>
    public int MaxEdge { get; init; }

    /// <summary>Capture from the screen DC (BitBlt) so overlays/popups are included.</summary>
    public bool CaptureScreen { get; init; }

    /// <summary>
    /// Directory to write timestamped frame images and their manifest into, alongside the video.
    /// <see langword="null"/> records video only.
    /// </summary>
    public string? FramesDirectory { get; init; }

    /// <summary>
    /// Record the window exactly where it stands, never restoring, activating, or foregrounding it.
    /// </summary>
    /// <remarks>
    /// This is a promise to the user, not a performance hint, which is why it changes what counts as
    /// a failure rather than just which code path runs. An ordinary recording may raise a minimized
    /// window and may recover a blank <c>PrintWindow</c> frame by bringing the window to the front;
    /// both are reasonable when the user is watching the app they asked to record, and both are
    /// unacceptable for a caller who advertised that recording takes no focus — a background job that
    /// yanked a Sandbox window onto the user's screen mid-take would be a worse outcome than not
    /// recording at all.
    /// <para>
    /// So under this option nothing is moved: frame capture reads the window's own frames wherever it
    /// is, the fallback is a single non-activating <c>PrintWindow</c>, and a window that can only be
    /// captured by activating it is reported as uncapturable. Recording still publishes whatever it
    /// had already captured when that happens partway through a take.
    /// </para>
    /// <para>
    /// Incompatible with <see cref="CaptureScreen"/>, which reads the screen the window would have to
    /// be visible on, and with an element selector, which crops within a window this policy is only
    /// defined for as a whole.
    /// </para>
    /// </remarks>
    public bool NoActivation { get; init; }
}

/// <summary>Result of an MP4 recording.</summary>
public sealed class RecordCaptureResult
{
    /// <summary>Number of frames written to the video.</summary>
    public int Frames { get; init; }

    /// <summary>Width in pixels of the encoded video.</summary>
    public int Width { get; init; }

    /// <summary>Height in pixels of the encoded video.</summary>
    public int Height { get; init; }

    /// <summary>Size of the finished .mp4 in bytes.</summary>
    public long FileSize { get; init; }

    /// <summary>Capture mode actually used: "wgc" (Windows Graphics Capture), "screen" (explicit --capture-screen), or "printwindow".</summary>
    public string Mode { get; init; } = "wgc";

    /// <summary>Wall-clock duration of the recording, in milliseconds.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>Frames per second actually achieved, which can fall below the requested rate on a busy machine.</summary>
    public double AchievedFps { get; init; }

    /// <summary><see cref="AchievedFps"/> as a fraction of the requested rate. 1.0 means the cadence was met.</summary>
    public double CadenceRatio { get; init; }

    /// <summary>
    /// Why recording stopped: "duration_elapsed", "cancelled", "target_closed" when the recorded
    /// window went away, "capture_unavailable" when the window could no longer be captured under
    /// <see cref="RecordOptions.NoActivation"/>, or "mp4_failed" when the encoder failed partway.
    /// </summary>
    public string StopReason { get; init; } = "duration_elapsed";

    /// <summary>
    /// Frame image output, or <see langword="null"/> when <see cref="RecordOptions.FramesDirectory"/> was not set.
    /// </summary>
    public RecordFrameArtifactResult? FrameArtifacts { get; init; }

    /// <summary>
    /// Non-fatal problems worth surfacing, such as the capture cadence falling short of the requested rate.
    /// </summary>
    public string[]? Warnings { get; init; }
}

/// <summary>Frame images written alongside a recording, and the counts describing them.</summary>
public sealed class RecordFrameArtifactResult
{
    /// <summary>Directory holding the images, the manifest, and the index.</summary>
    public string Directory { get; init; } = "";

    /// <summary>Path to the manifest describing the bundle.</summary>
    public string Manifest { get; init; } = "";

    /// <summary>Path to the newline-delimited JSON index, one <see cref="RecordFrameIndexEntry"/> per sample.</summary>
    public string Index { get; init; } = "";

    /// <summary>Image encoding, currently always "jpeg".</summary>
    public string Format { get; init; } = "jpeg";

    /// <summary>JPEG quality the images were encoded at.</summary>
    public int Quality { get; init; } = 85;

    /// <summary>Frames sampled, including those identical to the previous frame.</summary>
    public int Samples { get; init; }

    /// <summary>Image files written. Lower than <see cref="Samples"/> because unchanged frames reuse the previous image.</summary>
    public int Images { get; init; }

    /// <summary>Samples whose pixels matched the previous frame, so no new image was written.</summary>
    public int RepeatedSamples { get; init; }

    /// <summary>Combined size of the written images, in bytes.</summary>
    public long TotalBytes { get; init; }

    /// <summary><see langword="true"/> when <see cref="ByteLimit"/> was reached and later frames were dropped.</summary>
    public bool Truncated { get; init; }

    /// <summary>Byte budget for the bundle.</summary>
    public long ByteLimit { get; init; }
}

internal sealed class RecordFrameSample
{
    public int SampleIndex { get; init; }
    public long ElapsedMs { get; init; }
    public double MediaTimeMs { get; init; }
}

/// <summary>One line of the frame index, describing a single sampled frame.</summary>
public sealed class RecordFrameIndexEntry
{
    /// <summary>Zero-based position of this sample in the recording.</summary>
    public int SampleIndex { get; init; }

    /// <summary>Milliseconds from the start of the recording to this sample.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>Presentation time of the matching video frame, in milliseconds.</summary>
    public double MediaTimeMs { get; init; }

    /// <summary>Index of the image file this sample resolves to. Repeated frames point back at an earlier image.</summary>
    public int ImageIndex { get; init; }

    /// <summary>File name of the image, relative to the bundle directory.</summary>
    public string File { get; init; } = "";

    /// <summary><see langword="true"/> when this sample's pixels differ from the previous sample.</summary>
    public bool Changed { get; init; }
}

/// <summary>Manifest describing a frame bundle: what was requested, and what was produced.</summary>
public sealed class RecordFrameBundleManifest
{
    /// <summary>Version of this manifest's shape, so readers can detect an unfamiliar layout.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>"complete" when the bundle finished, or "partial" when recording ended early.</summary>
    public string Status { get; set; } = "complete";

    /// <summary>When recording started.</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>When recording finished.</summary>
    public DateTimeOffset CompletedUtc { get; set; }

    /// <summary>Why recording stopped, matching <see cref="RecordCaptureResult.StopReason"/>.</summary>
    public string StopReason { get; set; } = "duration_elapsed";

    /// <summary>The options the recording was asked for.</summary>
    public RecordFrameRequestManifest Requested { get; init; } = new();

    /// <summary>What the recording actually achieved.</summary>
    public RecordFrameTimingManifest Timing { get; set; } = new();

    /// <summary>The video that accompanies this bundle.</summary>
    public RecordFrameVideoManifest Video { get; set; } = new();

    /// <summary>The images in this bundle.</summary>
    public RecordFrameImagesManifest Frames { get; set; } = new();
}

/// <summary>The recording options as requested, echoed for comparison against what was achieved.</summary>
public sealed class RecordFrameRequestManifest
{
    /// <summary>Requested duration in seconds. 0 means until cancellation.</summary>
    public int DurationSec { get; init; }

    /// <summary>Requested frames per second.</summary>
    public int Fps { get; init; }

    /// <summary>Requested longest-edge limit in pixels. 0 means no downscale.</summary>
    public int MaxEdge { get; init; }
}

/// <summary>What the recording achieved, against what it was asked for.</summary>
public sealed class RecordFrameTimingManifest
{
    /// <summary>Wall-clock duration of the recording, in milliseconds.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>Frames sampled, including repeats.</summary>
    public int SampleCount { get; init; }

    /// <summary>Image files written.</summary>
    public int ImageCount { get; init; }

    /// <summary>Samples that matched the previous frame and reused its image.</summary>
    public int RepeatedSampleCount { get; init; }

    /// <summary>Frames per second actually achieved.</summary>
    public double AchievedFps { get; init; }

    /// <summary><see cref="AchievedFps"/> as a fraction of the requested rate.</summary>
    public double CadenceRatio { get; init; }
}

/// <summary>The video file that accompanies a frame bundle.</summary>
public sealed class RecordFrameVideoManifest
{
    /// <summary>Path to the .mp4.</summary>
    public string Path { get; init; } = "";

    /// <summary>"complete" when the video was finalized, or "partial" when it was not.</summary>
    public string Status { get; init; } = "complete";

    /// <summary>Video codec, currently always "h264".</summary>
    public string Codec { get; init; } = "h264";

    /// <summary>Frames written to the video.</summary>
    public int FrameCount { get; init; }

    /// <summary>Size of the video in bytes, or <see langword="null"/> when it could not be finalized.</summary>
    public long? FileSize { get; init; }
}

/// <summary>The images in a frame bundle.</summary>
public sealed class RecordFrameImagesManifest
{
    /// <summary>Image encoding, currently always "jpeg".</summary>
    public string Format { get; init; } = "jpeg";

    /// <summary>JPEG quality the images were encoded at.</summary>
    public int Quality { get; init; } = 85;

    /// <summary>File name of the newline-delimited JSON index.</summary>
    public string Index { get; init; } = "frames.ndjson";

    /// <summary>Width of each image in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Height of each image in pixels.</summary>
    public int Height { get; init; }

    /// <summary><see langword="true"/> when <see cref="ByteLimit"/> was reached and later frames were dropped.</summary>
    public bool Truncated { get; init; }

    /// <summary>Byte budget for the bundle.</summary>
    public long ByteLimit { get; init; }
}

/// <summary>
/// Recording failed partway through, but some output survived on disk. Inspect
/// <see cref="VideoPath"/> and <see cref="FramesDirectory"/> for what was kept.
/// </summary>
/// <param name="message">Describes what failed.</param>
/// <param name="videoPath">Path to the surviving video, or <see langword="null"/> if none was kept.</param>
/// <param name="framesDirectory">Path to the surviving frame bundle, or <see langword="null"/> if none was kept.</param>
/// <param name="recoveryHint">What the caller can do about it.</param>
/// <param name="innerException">The underlying failure.</param>
public sealed class RecordPartialOutputException(
    string message,
    string? videoPath,
    string? framesDirectory,
    string recoveryHint,
    Exception innerException)
    : IOException(message, innerException)
{
    /// <summary>The video that survived, or <see langword="null"/> when none was kept.</summary>
    public string? VideoPath { get; } = videoPath;

    /// <summary>The frame bundle that survived, or <see langword="null"/> when none was kept.</summary>
    public string? FramesDirectory { get; } = framesDirectory;

    /// <summary>What the caller can do about the failure.</summary>
    public string RecoveryHint { get; } = recoveryHint;
}

/// <summary>Writing the frame bundle failed, so no usable frame output was produced.</summary>
/// <param name="message">Describes what failed.</param>
/// <param name="recoveryHint">What the caller can do about it.</param>
/// <param name="innerException">The underlying failure.</param>
public sealed class RecordFrameOutputException(
    string message,
    string recoveryHint,
    Exception innerException)
    : IOException(message, innerException)
{
    /// <summary>What the caller can do about the failure.</summary>
    public string RecoveryHint { get; } = recoveryHint;
}
