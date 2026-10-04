// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using WinApp.Cli.Services;

namespace WinApp.Cli.Services.DevTools;

internal interface IDevToolsTargetResolver
{
    /// <summary>
    /// Resolves a target from the shared <c>-a/--app</c> and <c>-w/--window</c> options, attaching the agent
    /// only when explicitly authorized. Never throws for an ordinary targeting failure — the reason is returned so the caller can
    /// render it in its own human/JSON shape.
    /// </summary>
    /// <param name="attachIfNeeded">
    /// Whether an unattached target may be injected into. <c>false</c> makes injection an explicit caller
    /// decision: the resolve fails naming the pid, and nothing is loaded into that process.
    /// </param>
    Task<DevToolsTarget> ResolveAsync(
        string? app,
        long? window,
        CancellationToken cancellationToken,
        bool attachIfNeeded = false);
}

internal sealed record DevToolsTarget
{
    private DevToolsTarget(bool ok, int pid, string? processName, string? error, bool justAttached, long? window = null, string? root = null)
    {
        Ok = ok;
        Pid = pid;
        ProcessName = processName;
        Error = error;
        JustAttached = justAttached;
        Window = window;
        Root = root;
        Tap = ok ? new VisualTreeTap(unchecked((uint)pid), window: window, root: root) : null;
    }

    public bool Ok { get; }

    public int Pid { get; }

    public string? ProcessName { get; }

    public long? Window { get; }

    public string? Root { get; }

    public string? Error { get; }

    public bool JustAttached { get; }

    public VisualTreeTap? Tap { get; }

    public string Describe() => ProcessName is null ? $"PID {Pid}" : $"{ProcessName} (PID {Pid})";

    public static DevToolsTarget Ready(int pid, string? processName, bool justAttached, long? window = null, string? root = null) =>
        new(true, pid, processName, null, justAttached, window, root);

    public static DevToolsTarget Fail(string error) => new(false, 0, null, error, false);

    public static DevToolsTarget FailNotAttached(int pid, string? processName, string error) =>
        new(false, pid, processName, error, false) { NotAttached = true };

    public bool NotAttached { get; private init; }
}

internal sealed class DevToolsTargetResolver(
    IUiTargetResolver sessionService,
    IDevToolsService devToolsService,
    Func<IReadOnlyList<int>>? attachedPidLister = null,
    ExecutionTargets.GuestAgent.GuestCommentContext? guestComments = null) : IDevToolsTargetResolver
{
    private readonly Func<IReadOnlyList<int>> _attachedPids =
        attachedPidLister ?? (() => DevToolsPipeDiscovery.EnumerateInjectedPids());

    public async Task<DevToolsTarget> ResolveAsync(
        string? app,
        long? window,
        CancellationToken cancellationToken,
        bool attachIfNeeded = false)
    {
        if (guestComments is { ScopedInspection: true, Process: { } scoped })
        {
            try
            {
                guestComments.VerifyInspectionTarget(app, window);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                return DevToolsTarget.Fail(ex.Message);
            }
            var attached = _attachedPids().Contains(scoped.ProcessId);
            if (!attached && (!attachIfNeeded || !guestComments.AllowInspectionAttach))
            {
                return DevToolsTarget.FailNotAttached(scoped.ProcessId, null,
                    "The scoped guest application is not attached. Use 'winapp devtools attach --on sandbox --app <appSelector>' " +
                    "with the selector from discovery; no implicit attachment or stale-handle reuse was attempted.");
            }
            var scopedConnection = await devToolsService.ConnectAsync((uint)scoped.ProcessId, false,
                DevToolsAccess.Mutation, cancellationToken).ConfigureAwait(false);
            return scopedConnection.Connected
                ? DevToolsTarget.Ready(scoped.ProcessId, null, !attached)
                : DevToolsTarget.Fail(scopedConnection.Error ?? "The guest inspector did not prove this application scope.");
        }
        var hasExplicitTarget = window is not null || !string.IsNullOrWhiteSpace(app);
        int pid;
        string? processName = null;

        if (hasExplicitTarget)
        {
            // -w resolves the HWND to its owning process and takes precedence; -a accepts process name,
            // window title, or PID. Same resolution `winapp ui` uses, so -a/-w mean one thing across the CLI.
            try
            {
                var session = window is null
                    ? await sessionService.ResolveProcessAsync(app!, cancellationToken)
                    : await sessionService.ResolveAsync(app, window, cancellationToken);
                pid = session.ProcessId;
                processName = session.ProcessName;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return DevToolsTarget.Fail(ex.Message);
            }
        }
        else
        {
            var attached = _attachedPids();
            if (attached.Count != 1)
            {
                return DevToolsTarget.Fail(DescribeAmbiguousTarget(attached));
            }

            pid = attached[0];
            processName = TryGetProcessName(pid);
        }

        if (pid <= 0)
        {
            return DevToolsTarget.Fail("Could not resolve a target process. Pass --app/-a <name|title|pid>.");
        }

        if (_attachedPids().Contains(pid))
        {
            return DevToolsTarget.Ready(pid, processName, justAttached: false, window);
        }

        // Injection loads a DLL into someone else's process and is irreversible for that process's lifetime.
        // A caller that did not ask for it gets told, and nothing is loaded.
        if (!attachIfNeeded)
        {
            var who = processName is null ? $"PID {pid}" : $"{processName} (PID {pid})";
            return DevToolsTarget.FailNotAttached(
                pid,
                processName,
                $"{who} does not have DevTools attached. " +
                "Pass --attach to inject the agent into it first, or attach it explicitly with " +
                "`winapp devtools attach`. Nothing was loaded into that process.");
        }

        // An explicitly authorized target is attached now. Request mutation,
        // because a posture is fixed for the process lifetime by the FIRST injection — attaching read-only here
        // would silently make `set-property` impossible until the app restarts.
        var connection = await devToolsService.ConnectAsync(
            unchecked((uint)pid),
            showOverlay: false,
            DevToolsAccess.Mutation,
            cancellationToken);
        if (!connection.Connected)
        {
            return DevToolsTarget.Fail(
                $"Could not attach the DevTools agent to {(processName is null ? $"PID {pid}" : $"{processName} (PID {pid})")}: " +
                (connection.Error ?? "the agent did not come up.") +
                " Is it a WinUI 3 app?");
        }

        return DevToolsTarget.Ready(pid, processName, justAttached: true, window);
    }

    /// <summary>
    /// Explains what to pass when "no target" cannot pick one: nothing is attached, or several apps are. Names
    /// the candidates so the next command is a copy-paste away rather than a second discovery step.
    /// </summary>
    private static string DescribeAmbiguousTarget(IReadOnlyList<int> attached)
    {
        if (attached.Count > 1)
        {
            var list = string.Join(", ", attached.Select(p => TryGetProcessName(p) is string n ? $"{n} ({p})" : p.ToString()));
            return $"{attached.Count} apps have DevTools attached ({list}). " +
                   "Pass --app/-a <name|title|pid> to choose one.";
        }

        return "No app has DevTools attached. Launch one with `winapp run --devtools`, or pass " +
               "--attach with --app/-a <name|title|pid> to authorize attachment to a running WinUI app. " +
               "`winapp devtools list --include-available` shows attach candidates.";
    }

    private static string? TryGetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}
