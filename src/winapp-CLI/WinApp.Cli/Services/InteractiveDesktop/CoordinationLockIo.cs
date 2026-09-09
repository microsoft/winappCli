// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// Classifies <see cref="IOException"/>s raised while acquiring the coordination file locks.
/// </summary>
internal static class CoordinationLockIo
{
    /// <summary>ERROR_SHARING_VIOLATION — another process has the file open without sharing.</summary>
    private const int ErrorSharingViolation = 32;

    /// <summary>ERROR_LOCK_VIOLATION — a byte-range lock is held on the file.</summary>
    private const int ErrorLockViolation = 33;

    /// <summary>
    /// Whether <paramref name="exception"/> means "another process holds this file right now", which is
    /// the only I/O condition a lock acquisition may retry.
    /// </summary>
    /// <remarks>
    /// Everything else — a bad or vanished path, a failing volume, exhausted handles, a disconnected
    /// network share — is a real failure. Retrying it forever would be indistinguishable from waiting on
    /// a genuinely held lock: the command would hang instead of reporting
    /// <c>desktop_coordination_unavailable</c>. Win32 codes surface in the low word of
    /// <see cref="Exception.HResult"/> as <c>0x8007xxxx</c>.
    /// </remarks>
    internal static bool IsContention(IOException exception)
        => (exception.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    /// <summary>The failure reported when a lock cannot be opened for a non-contention reason.</summary>
    internal static UiCoordinationException CannotOpen(string path, IOException exception)
        => new(
            UiCoordinationErrorCodes.Unavailable,
            $"The UI coordination lock '{path}' could not be opened: {exception.Message}",
            "Check that the coordination directory is on a healthy, reachable volume, then retry.");
}
