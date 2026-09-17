// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

/// <summary>
/// Reads the real Windows session, window station, and input desktop of the current process.
/// </summary>
/// <remarks>
/// The agent is started as <c>ExistingLogin</c> after <c>wsb connect</c> establishes the interactive
/// Sandbox session, but it verifies rather than assumes: an agent that advertised itself ready from
/// session 0 would accept UI commands it cannot perform and report input that was never delivered.
/// <para>
/// <see cref="PInvoke.OpenInputDesktop"/> is the load-bearing check. In Windows Sandbox a closed
/// client leaves the guest user session, its processes, and UI Automation fully working while real
/// input and Windows Graphics Capture stop — and opening the input desktop is what distinguishes
/// those two states. Every probe re-opens it rather than caching, because the user can disconnect at
/// any moment.
/// </para>
/// </remarks>
internal sealed class GuestSessionProbe : IGuestSessionProbe
{
    /// <inheritdoc/>
    public GuestSessionInfo Probe() => new(
        ReadSessionId(),
        ReadWindowStationName(),
        HasInputDesktop());

    /// <summary>Reads the session this process belongs to; session 0 is services-only.</summary>
    private static int ReadSessionId()
    {
        var processId = (uint)Environment.ProcessId;

        // A failure here cannot be distinguished from "session 0" safely, so it is reported as the
        // non-interactive session: refusing a command winapp cannot prove it can perform is always
        // preferable to claiming input was delivered.
        return PInvoke.ProcessIdToSessionId(processId, out var sessionId) ? (int)sessionId : 0;
    }

    /// <summary>Reads the process window station name, or null when it cannot be read.</summary>
    private static unsafe string? ReadWindowStationName()
    {
        var station = PInvoke.GetProcessWindowStation();
        if (station.IsNull)
        {
            return null;
        }

        // Names are short; one fixed buffer avoids a two-call length dance for a value that is
        // "WinSta0" in every case that matters.
        const int BufferChars = 256;
        var buffer = stackalloc char[BufferChars];
        uint lengthNeeded;

        if (!PInvoke.GetUserObjectInformation(
                new HANDLE(station.Value),
                USER_OBJECT_INFORMATION_INDEX.UOI_NAME,
                buffer,
                sizeof(char) * BufferChars,
                &lengthNeeded))
        {
            return null;
        }

        // The returned length counts bytes including the terminator.
        var chars = (int)(lengthNeeded / sizeof(char));
        if (chars <= 1)
        {
            return null;
        }

        return new string(buffer, 0, chars - 1);
    }

    /// <summary>
    /// Whether the input desktop can be opened, which is what real input actually requires.
    /// </summary>
    private static bool HasInputDesktop()
    {
        var desktop = PInvoke.OpenInputDesktop_SafeHandle(
            0,
            fInherit: false,
            DESKTOP_ACCESS_FLAGS.DESKTOP_READOBJECTS);

        if (desktop.IsInvalid)
        {
            desktop.Dispose();
            return false;
        }

        desktop.Dispose();
        return true;
    }
}

/// <summary>
/// A probe that reports a fixed observation, used where a real session cannot be inspected.
/// </summary>
/// <remarks>
/// Registered on non-Windows build/test hosts and used by tests that need a specific session shape.
/// Kept beside the real probe so the readiness rules always run against the same contract.
/// </remarks>
internal sealed class StaticGuestSessionProbe(GuestSessionInfo session) : IGuestSessionProbe
{
    /// <inheritdoc/>
    public GuestSessionInfo Probe() => session;
}

/// <summary>Marshalling helpers kept out of the probe body.</summary>
internal static class GuestSessionProbeErrors
{
    /// <summary>The last Win32 error, for diagnostics that need it.</summary>
    public static int LastError => Marshal.GetLastWin32Error();
}
