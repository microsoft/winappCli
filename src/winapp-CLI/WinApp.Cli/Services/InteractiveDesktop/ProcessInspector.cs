// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// Process facts the coordinator needs: this process's reuse-proof identity, its Windows session, and
/// whether a recorded participant is still alive (for pruning, spec §10.1).
/// </summary>
/// <remarks>
/// Extracted behind an interface because liveness answers change under a test while it runs. The
/// production implementation is <see cref="ProcessInspector"/>.
/// </remarks>
internal interface IProcessInspector
{
    /// <summary>This process's id.</summary>
    int CurrentProcessId { get; }

    /// <summary>
    /// This process's <c>Process.StartTime.ToUniversalTime().Ticks</c>, which pairs with the PID to form
    /// a reuse-proof participant identity.
    /// </summary>
    long CurrentProcessStartTicksUtc { get; }

    /// <summary>The Windows session this process runs in. Coordination is scoped per session.</summary>
    int CurrentSessionId { get; }

    /// <summary>
    /// Whether <paramref name="processId"/> is running <em>and</em> started at
    /// <paramref name="startTicksUtc"/>. Returns <see langword="null"/> when liveness cannot be
    /// determined, which callers must treat as "assume alive" rather than as death.
    /// </summary>
    bool? IsProcessAlive(int processId, long startTicksUtc);
}

/// <summary>Production <see cref="IProcessInspector"/>.</summary>
internal sealed class ProcessInspector : IProcessInspector
{
    private readonly int _currentProcessId;
    private readonly long _currentStartTicks;
    private readonly int _sessionId;

    public ProcessInspector()
    {
        using var current = Process.GetCurrentProcess();
        _currentProcessId = current.Id;
        _currentStartTicks = current.StartTime.ToUniversalTime().Ticks;
        _sessionId = current.SessionId;
    }

    public int CurrentProcessId => _currentProcessId;

    public long CurrentProcessStartTicksUtc => _currentStartTicks;

    public int CurrentSessionId => _sessionId;

    public bool? IsProcessAlive(int processId, long startTicksUtc)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            // A matching start time proves this is the same process, not a recycled PID.
            return process.StartTime.ToUniversalTime().Ticks == startTicksUtc;
        }
        catch (ArgumentException)
        {
            // No process with that id is running — definitively dead.
            return false;
        }
        catch (InvalidOperationException)
        {
            // The process exited between lookup and property read — definitively dead.
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process exists but its start time is unreadable. Spec §5.2/§10.1: an unreadable
            // liveness answer must not be treated as death, or a live owner could be evicted.
            return null;
        }
    }

    /// <summary>
    /// Formats process start ticks the way lease filenames and every start-time comparison require:
    /// invariant-culture signed 64-bit decimal with no sign for positives, no grouping and no
    /// locale-specific digits (spec §8).
    /// </summary>
    public static string FormatStartTicks(long startTicksUtc)
        => startTicksUtc.ToString(CultureInfo.InvariantCulture);
}
