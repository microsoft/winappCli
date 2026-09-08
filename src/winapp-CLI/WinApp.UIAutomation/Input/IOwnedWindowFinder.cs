// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Abstraction over the live enumeration of top-level windows owned by a set of application windows.
/// Extracted purely as an OS-boundary seam so the screenshot command's multi-window / owned-dialog
/// discovery can be exercised deterministically with a fake — the real implementation
/// (<see cref="RealOwnedWindowFinder"/>) issues the actual Win32 window walk
/// (<c>FindWindowEx</c>/<c>GetWindow</c>/<c>GetWindowText</c>), which can't run in a headless test.
/// Behavior is unchanged: production is wired to the real finder.
/// </summary>
public interface IOwnedWindowFinder
{
    /// <summary>
    /// Returns the visible top-level windows owned (via <c>GW_OWNER</c>) by one of
    /// <paramref name="appWindows"/>, excluding the app windows themselves.
    /// </summary>
    List<(nint Hwnd, int Pid, string Title)> FindOwnedWindows(List<(nint Hwnd, int Pid, string Title)> appWindows);
}
