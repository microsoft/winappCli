// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>
/// Ends a Sandbox winapp started when the user closes the window winapp opened for it.
/// </summary>
/// <remarks>
/// <para>
/// <c>wsb start</c> creates a Sandbox with no window, and only <c>wsb stop</c> ends it. The window
/// <c>wsb connect</c> opens is just a viewer, so closing it left the Sandbox running out of sight,
/// and opening Windows Sandbox from Start then failed with "Only one running instance of Windows
/// Sandbox is allowed". <c>wsb</c> has no option to tie the two together, so a small hidden winapp
/// process does it: it waits for the window's process to exit, then stops the Sandbox, which is
/// what closing a Sandbox window opened from Start does.
/// </para>
/// <para>
/// Only a Sandbox winapp started is ever stopped, and only for the exact window winapp recorded
/// opening for it. A Sandbox winapp adopted belongs to someone else, a newer recorded window means
/// another winapp command reconnected it, and any other Sandbox window still open means someone is
/// still looking at it.
/// </para>
/// </remarks>
internal sealed class SandboxClientWatcher(
    IWindowsSandboxCli cli,
    ITargetStateStore stateStore,
    ITargetMutationLock mutationLock,
    ITargetConnectionLock connectionLock)
{
    /// <summary>The hidden verb that runs a watcher.</summary>
    internal const string Verb = "__sandbox-window-watch";

    /// <summary>
    /// How long to wait for a winapp command that is changing or connecting to the Sandbox before
    /// stopping it.
    /// </summary>
    internal static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(15);

    private static readonly ExecutionTargetRef s_target = WindowsSandboxTarget.Default;

    /// <summary>
    /// How often to check whether the window is still shown.
    /// </summary>
    /// <remarks>
    /// Measured: closing the window hides it within 0.1 seconds, but its process can stay alive,
    /// hidden, for 15 seconds to well over a minute. Waiting for the process alone would leave the
    /// Sandbox running for that long, so a hidden window counts as closed.
    /// </remarks>
    internal static readonly TimeSpan WindowPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Waits for the watched window to close; seamed for tests.</summary>
    internal Func<SandboxClientWindow, CancellationToken, Task> WaitForClientExitAsync { get; set; } = WaitForCloseAsync;

    /// <summary>Whether any other Sandbox window is shown; seamed for tests.</summary>
    internal Func<int, bool> IsAnotherClientRunning { get; set; } = AnyOtherClientWindow;

    /// <summary>Ends the closed window's lingering process; seamed for tests.</summary>
    internal Action<SandboxClientWindow> EndClosedClient { get; set; } = EndClientProcess;

    /// <summary>The arguments that start a watcher for <paramref name="client"/>.</summary>
    internal static IReadOnlyList<string> Arguments(string instanceId, SandboxClientWindow client) =>
    [
        Verb,
        "--instance-id", instanceId,
        "--client-window", ((long)client.Handle).ToString(CultureInfo.InvariantCulture),
        "--client-pid", client.ProcessId.ToString(CultureInfo.InvariantCulture),
        "--client-start-ticks", client.StartTicksUtc.ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>
    /// Whether the persisted state still says this window is winapp's view of a Sandbox it started.
    /// </summary>
    internal static bool OwnsWindow(TargetState? state, string instanceId, SandboxClientWindow client) =>
        state is { ClientOwnedByWinapp: true } &&
        string.Equals(state.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase) &&
        state.InstanceOrigin is nameof(SandboxInstanceOrigin.Created) or nameof(SandboxInstanceOrigin.RecoveredStart) &&
        state.ClientWindowHandle == (long)client.Handle &&
        state.ClientProcessId == client.ProcessId &&
        state.ClientProcessStartTicksUtc == client.StartTicksUtc;

    /// <summary>Waits for the window to close, then stops the Sandbox when it is still winapp's to stop.</summary>
    /// <returns>Whether the Sandbox was stopped.</returns>
    public async Task<bool> RunAsync(
        string instanceId,
        SandboxClientWindow client,
        CancellationToken cancellationToken)
    {
        if (client.StartTicksUtc == 0 || !OwnsWindow(stateStore.Read(s_target), instanceId, client))
        {
            return false;
        }

        await WaitForClientExitAsync(client, cancellationToken).ConfigureAwait(false);

        // Waits for a winapp command that is changing the Sandbox, then for one that is connecting a
        // window to it, so neither has the Sandbox stopped underneath it. The connection lock comes
        // second because commands hold it only briefly, and never while waiting for the other one.
        using var mutation = mutationLock.TryAcquire(s_target, LockTimeout, cancellationToken);
        using var connection = mutation is null
            ? null
            : connectionLock.TryAcquire(s_target, LockTimeout, cancellationToken);
        if (connection is null ||
            !OwnsWindow(stateStore.Read(s_target), instanceId, client) ||
            IsAnotherClientRunning(client.ProcessId))
        {
            return false;
        }

        // Ended first: a client still connected when the Sandbox stops shows "The connection to the
        // Windows Sandbox environment was lost", which is exactly the leftover window this avoids.
        EndClosedClient(client);
        await cli.StopAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Starts a hidden watcher for the window winapp just opened, best effort.
    /// </summary>
    /// <remarks>
    /// Runs the same winapp executable, detached and without the caller's standard handles, so it
    /// outlives this command without holding its output open. Skipped when this process is not
    /// <c>winapp.exe</c>, such as under a test host.
    /// </remarks>
    internal static void Launch(string instanceId, SandboxClientWindow client)
    {
        var executable = Environment.ProcessPath;
        if (client.StartTicksUtc == 0 ||
            executable is null ||
            !string.Equals(Path.GetFileName(executable), GuestAgentCommandNames.BinaryName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,

            // Not the caller's directory, so a long-lived watcher never keeps it from being deleted.
            WorkingDirectory = Environment.SystemDirectory,
        };

        foreach (var argument in Arguments(instanceId, client))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using (StandardHandleInheritance.Suppress())
            {
                Process.Start(startInfo)?.Dispose();
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // Without a watcher, closing the window leaves the Sandbox running, as before.
            Trace.TraceWarning("Could not start the Windows Sandbox window watcher: {0}", ex.Message);
        }
    }

    private static async Task WaitForCloseAsync(SandboxClientWindow client, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(client.ProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            try
            {
                // A recycled process ID belongs to some other program, which means the window
                // being watched has already closed.
                if (process.StartTime.ToUniversalTime().Ticks != client.StartTicksUtc)
                {
                    return;
                }

                while (IsWindowOf(client) && !process.HasExited)
                {
                    await Task.Delay(WindowPollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // The process exited while it was being inspected.
            }
        }
    }

    /// <summary>Whether the window still exists, belongs to the client, and is shown (minimized counts).</summary>
    private static unsafe bool IsWindowOf(SandboxClientWindow client)
    {
        var window = new HWND(client.Handle);
        uint owner = 0;
        return PInvoke.IsWindow(window) &&
            PInvoke.IsWindowVisible(window) &&
            PInvoke.GetWindowThreadProcessId(window, &owner) != 0 &&
            owner == (uint)client.ProcessId;
    }

    private static void EndClientProcess(SandboxClientWindow client)
    {
        try
        {
            using var process = Process.GetProcessById(client.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks == client.StartTicksUtc)
            {
                process.Kill();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>Whether another Sandbox window is shown. A closed window's lingering process is not.</summary>
    private static bool AnyOtherClientWindow(int closedProcessId)
    {
        var shown = false;
        foreach (var process in Process.GetProcessesByName(WindowsSandboxWindowController.RemoteSessionProcessName))
        {
            using (process)
            {
                shown |= process.Id != closedProcessId && process.MainWindowHandle != 0;
            }
        }

        return shown;
    }
}
