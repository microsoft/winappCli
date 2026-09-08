// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

/// <summary>
/// Records a window or element region to H.264 MP4, driving the UI Automation library's capture
/// primitives and encoding the frames it samples.
/// </summary>
public interface IUiRecordingService
{
    /// <summary>Records a window or element region to H.264 MP4.</summary>
    /// <param name="uiTarget">The app or window to record.</param>
    /// <param name="elementId">Selector of the element to crop to, or <see langword="null"/> to record the whole window.</param>
    /// <param name="options">Output path, duration, frame rate, and downscale limit.</param>
    /// <param name="ct">Stops the recording early. Whatever was captured is still finalized.</param>
    /// <param name="onRecordingStarted">Invoked after the first frame; reports whether frame output is active.</param>
    /// <returns>Frame counts, timing, and the paths that were written.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="RecordOptions.DurationSec"/> is negative, <see cref="RecordOptions.Fps"/> is not
    /// positive, or <see cref="RecordOptions.MaxEdge"/> is negative or between 1 and 63.
    /// </exception>
    /// <exception cref="Mp4EncoderInitializationException">The H.264 encoder could not be created.</exception>
    /// <exception cref="RecordPartialOutputException">Recording failed partway, but some output survived.</exception>
    /// <exception cref="RecordFrameOutputException">The frame bundle could not be written.</exception>
    Task<RecordCaptureResult> RecordAsync(
        UiTarget uiTarget,
        string? elementId,
        RecordOptions options,
        CancellationToken ct,
        Action<bool>? onRecordingStarted = null);
}
