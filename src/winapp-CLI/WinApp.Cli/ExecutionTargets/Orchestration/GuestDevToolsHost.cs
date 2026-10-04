// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

internal sealed record GuestDevToolsHostPlan(
    string Epoch, GuestSourceManifest Sources, GuestExecRequest Request, string DeploymentId, long DeploymentRevision);
internal sealed record GuestDevToolsHostMessage(
    string Phase, GuestInspectedAppFrame? App = null, int? ExitCode = null, string? Error = null, string? SessionId = null,
    string? BindingId = null);
internal sealed record GuestDevToolsApplication(
    string Selector, GuestProcessStart Process, string Epoch, string? BindingId, GuestSourceManifest? Sources);

internal sealed class GuestDevToolsHost(ITargetStateDirectoryProvider directories, IAppLauncherService launcher)
{
    internal DirectoryInfo Create(ExecutionTargetRef target, string id)
    {
        var directory = Resolve(target, id);
        if (directory.Exists)
        {
            throw new IOException("The host DevTools launch identity already exists.");
        }
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("The host launch owner is unavailable.");
        var acl = new DirectorySecurity();
        acl.SetOwner(user);
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.Create(acl);
        directory.Refresh();
        return directory;
    }

    internal DirectoryInfo Resolve(ExecutionTargetRef target, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new InvalidOperationException("Invalid host DevTools launch identity.");
        }
        var root = directories.GetTargetRoot(target, create: false).FullName;
        var path = TargetPathSafety.CombineInsideRoot(root, "devtools", id);
        if (PathSafety.HasReparsePointOnPath(path, root))
        {
            throw new IOException("The host DevTools launch directory is redirected or inaccessible.");
        }
        return new DirectoryInfo(path);
    }

    internal GuestDevToolsHostPlan ReadPlan(ExecutionTargetRef target, string id)
    {
        var directory = Resolve(target, id);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("The launch owner is unavailable.");
        var file = new FileInfo(Path.Combine(directory.FullName, "launch.json"));
        if (!directory.Exists || !InteractiveDesktopPaths.IsCurrentUserOnly(directory.GetAccessControl(), user) ||
            !file.Exists || file.Length > 4 * 1024 * 1024 ||
            PathSafety.HasReparsePointOnPath(file.FullName, directory.FullName) ||
            !InteractiveDesktopPaths.IsCurrentUserOnly(file.GetAccessControl(), user))
        {
            throw new UnauthorizedAccessException("The host DevTools launch plan is missing or not exclusively owned by this user.");
        }
        return JsonSerializer.Deserialize(File.ReadAllBytes(file.FullName), GuestCommentsJsonContext.Default.GuestDevToolsHostPlan)
            ?? throw new InvalidDataException("The host DevTools launch plan is empty.");
    }

    internal GuestDevToolsHostMessage ReadState(ExecutionTargetRef target, string id)
    {
        var directory = Resolve(target, id);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("The launch owner is unavailable.");
        var file = new FileInfo(Path.Combine(directory.FullName, "status.json"));
        if (!directory.Exists || !InteractiveDesktopPaths.IsCurrentUserOnly(directory.GetAccessControl(), user) ||
            !file.Exists || file.Length > GuestCommentFrames.MaximumLength ||
            PathSafety.HasReparsePointOnPath(file.FullName, directory.FullName) ||
            !InteractiveDesktopPaths.IsCurrentUserOnly(file.GetAccessControl(), user))
        {
            throw new UnauthorizedAccessException("The host DevTools launch receipt is missing or not exclusively owned by this user.");
        }
        using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var state = JsonSerializer.Deserialize(input, GuestCommentsJsonContext.Default.GuestDevToolsHostMessage);
        return state?.SessionId == id ? state :
            throw new InvalidDataException("The host DevTools receipt belongs to another launch.");
    }

    internal async Task<GuestDevToolsApplication> ResolveApplicationAsync(
        PreparedTarget target, string value, GuestDevToolsCapabilities? expected, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Supply --app with a guest PID or name from 'winapp devtools list --on sandbox'.");
        }
        if (!value.StartsWith("guest:", StringComparison.Ordinal) &&
            !value.StartsWith("guest-process:", StringComparison.Ordinal))
        {
            var discovered = await GuestDevToolsCommandRequest.DiscoverAsync(
                target, this, expected, includeAvailable: true, cancellationToken).ConfigureAwait(false);
            var apps = discovered.Apps.Where(app => app.AppSelector is not null).ToList();
            List<DevToolsAppInfo> matches;
            if (int.TryParse(value, out var pid))
            {
                matches = apps.FindAll(app => app.Pid == pid);
            }
            else
            {
                matches = apps.FindAll(app => string.Equals(app.ProcessName, value, StringComparison.OrdinalIgnoreCase));
                if (matches.Count == 0)
                {
                    matches = apps.FindAll(app => app.ProcessName?.Contains(value, StringComparison.OrdinalIgnoreCase) == true);
                }
                if (matches.Count == 0)
                {
                    matches = apps.FindAll(app => app.WindowTitle?.Contains(value, StringComparison.OrdinalIgnoreCase) == true);
                }
            }
            if (matches.Count != 1)
            {
                throw new InvalidOperationException(matches.Count == 0
                    ? $"No current guest WinUI app matches '{value}'. Run 'winapp devtools list --on sandbox --include-available'."
                    : $"Multiple guest apps match '{value}' (PIDs: {string.Join(", ", matches.Select(app => app.Pid))}). Use --app with one PID.");
            }
            value = matches[0].AppSelector!;
        }
        var selector = GuestDevToolsSelector.Parse(value, target.Reference);
        GuestDevTools.RequireMatchingEngines(expected,
            await target.Operations.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false));
        GuestDevToolsApplication application;
        if (selector.LaunchId is { } id)
        {
            var plan = ReadPlan(target.Reference, id);
            var state = ReadState(target.Reference, id);
            if (state.Phase != "ready" || state.App?.Process is not { ProcessId: > 0, StartTicksUtc: > 0 } process ||
                !Guid.TryParseExact(state.BindingId, "N", out _))
            {
                throw new InvalidOperationException("The host-owned guest launch is no longer ready. Run 'winapp devtools list --on sandbox' for a current selector.");
            }
            application = new(value, process, plan.Epoch, state.BindingId, plan.Sources);
        }
        else
        {
            application = new(value, selector.Process!, selector.Epoch!, null, null);
        }
        await GuestDevToolsSelector.VerifyProcessAsync(target, application.Epoch, application.Process, cancellationToken).ConfigureAwait(false);
        return application;
    }

    internal string FindSelector(PreparedTarget target, GuestProcessStart process)
    {
        var targetRoot = directories.GetTargetRoot(target.Reference, create: false).FullName;
        var root = Path.Combine(targetRoot, "devtools");
        if (PathSafety.HasReparsePointOnPath(root, targetRoot))
        {
            throw new IOException("The host DevTools receipt directory is redirected or inaccessible.");
        }
        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var id = Path.GetFileName(directory);
                if (!Guid.TryParseExact(id, "N", out _) || !File.Exists(Path.Combine(directory, "status.json")))
                {
                    continue;
                }
                var state = ReadState(target.Reference, id);
                if (state.Phase == "ready" && state.App?.Process == process &&
                    Guid.TryParseExact(state.BindingId, "N", out _) && ReadPlan(target.Reference, id).Epoch == target.Epoch.Value)
                {
                    return GuestDevToolsSelector.ForLaunch(id);
                }
            }
        }
        return GuestDevToolsSelector.ForProcess(target.Reference, target.Epoch, process);
    }

    internal static void WriteState(DirectoryInfo directory, GuestDevToolsHostMessage message)
    {
        var temporary = Path.Combine(directory.FullName, "status-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (var output = CreatePrivateFile(directory, temporary))
        {
            JsonSerializer.Serialize(output, message, GuestCommentsJsonContext.Default.GuestDevToolsHostMessage);
        }
        var destination = Path.Combine(directory.FullName, "status.json");
        if (File.Exists(destination))
        {
            File.Replace(temporary, destination, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporary, destination);
        }
    }

    internal static string PipeName(string id) => "WinApp.DevTools.Host." + id;

    internal void WritePlan(ExecutionTargetRef target, string id, GuestDevToolsHostPlan plan)
    {
        var directory = Resolve(target, id);
        using var file = CreatePrivateFile(directory, Path.Combine(directory.FullName, "launch.json"));
        JsonSerializer.Serialize(file, plan, GuestCommentsJsonContext.Default.GuestDevToolsHostPlan);
    }

    private static FileStream CreatePrivateFile(DirectoryInfo directory, string path)
    {
        directory.Refresh();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("The launch owner is unavailable.");
        if (!directory.Exists || !InteractiveDesktopPaths.IsCurrentUserOnly(directory.GetAccessControl(), user))
        {
            throw new UnauthorizedAccessException("The host DevTools launch directory is not exclusively owned by this user.");
        }
        var acl = new FileSecurity();
        acl.SetOwner(user);
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(
            FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, acl);
    }

    internal async Task<GuestDevToolsHostMessage> StartAsync(
        ExecutionTargetRef target, string id, GuestDevToolsHostPlan plan, bool detach, CancellationToken cancellationToken,
        Action<GuestDevToolsHostMessage>? onReady = null)
    {
        var directory = Resolve(target, id);
        WritePlan(target, id, plan);
        using var pipe = new NamedPipeServerStream(PipeName(id), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        using var helper = launcher.LaunchExecutable(
            Environment.ProcessPath ?? throw new IOException("The host winapp executable is unavailable."),
            WindowsCommandLine.JoinArguments(["guest-devtools-host", "--launch-id", id]),
            directory.FullName, LaunchStdioMode.Suppress);
        var handedOff = false;
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(150));
        try
        {
            var connect = pipe.WaitForConnectionAsync(startup.Token);
            var helperExit = helper.WaitForExitAsync(startup.Token);
            if (await Task.WhenAny(connect, helperExit).ConfigureAwait(false) != connect)
            {
                await helperExit.ConfigureAwait(false);
                throw new IOException("The host DevTools owner exited before connecting. See the launch status.json for its failure.");
            }
            await connect.ConfigureAwait(false);
            GuestCommentPeer.Verify(pipe);
            var ready = JsonSerializer.Deserialize(await GuestCommentFrames.ReadAsync(pipe, startup.Token).ConfigureAwait(false),
                GuestCommentsJsonContext.Default.GuestDevToolsHostMessage)
                ?? throw new IOException("The host DevTools owner did not acknowledge readiness.");
            if (ready.Phase != "ready" || ready.SessionId != id ||
                ready.App is not { Process: { ProcessId: > 0, StartTicksUtc: > 0 }, NodeCount: >= 0 } ||
                !Guid.TryParseExact(ready.BindingId, "N", out _))
            {
                throw new IOException((ready.Error ?? "The guest application did not become ready.") +
                    $" Launch diagnostics: {Path.Combine(directory.FullName, "diagnostics.log")}");
            }
            await GuestCommentFrames.WriteAsync(pipe, new GuestInspectedAppControl(detach ? "detach" : "wait"),
                GuestCommentsJsonContext.Default.GuestInspectedAppControl, startup.Token).ConfigureAwait(false);
            var accepted = JsonSerializer.Deserialize(await GuestCommentFrames.ReadAsync(pipe, startup.Token).ConfigureAwait(false),
                GuestCommentsJsonContext.Default.GuestDevToolsHostMessage);
            if (accepted is not { Phase: "accepted" } || accepted.SessionId != id)
            {
                throw new IOException(accepted?.Error ?? "The host DevTools owner did not accept the launch handoff.");
            }
            onReady?.Invoke(ready);
            handedOff = detach;
            if (detach)
            {
                return ready;
            }
            var finished = JsonSerializer.Deserialize(await GuestCommentFrames.ReadAsync(pipe, cancellationToken).ConfigureAwait(false),
                GuestCommentsJsonContext.Default.GuestDevToolsHostMessage)
                ?? throw new IOException("The host DevTools owner ended without an exit result.");
            if (finished.Phase != "exited" || finished.ExitCode is null || finished.SessionId != id)
            {
                throw new IOException(finished.Error ?? "The host DevTools owner did not return an application exit result.");
            }
            await helper.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return finished;
        }
        finally
        {
            await startup.CancelAsync().ConfigureAwait(false);
            if (!handedOff && !helper.HasExited)
            {
                pipe.Dispose();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await helper.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                {
                    helper.Kill();
                    await helper.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }
}
