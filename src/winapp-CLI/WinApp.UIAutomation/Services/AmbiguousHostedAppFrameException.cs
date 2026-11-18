// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Thrown when a minimized packaged app could be hosted by more than one minimized
/// ApplicationFrameHost frame. A minimized frame can only be linked to its app by
/// AppUserModelID, so two minimized windows of the same app cannot be told apart.
/// </summary>
internal sealed class AmbiguousHostedAppFrameException : InvalidOperationException
{
    /// <summary>Creates an exception listing the candidate frames.</summary>
    /// <param name="frames">Window handles of the minimized frames that match the app.</param>
    public AmbiguousHostedAppFrameException(IReadOnlyList<long> frames)
        : base($"Multiple minimized windows of this app are open ({string.Join(", ", frames.Select(f => $"HWND {f}"))}), " +
            "and a minimized window can't be matched to its process. Pass --window <HWND> to choose one.")
    {
        Frames = frames;
    }

    /// <summary>Window handles of the minimized frames that match the app.</summary>
    public IReadOnlyList<long> Frames { get; }
}
