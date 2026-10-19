// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO.Pipes;
using System.Text.Json;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Commands;

internal sealed class GuestDevToolsHostCommand : Command, IShortDescription
{
    public string ShortDescription => "Retain one owned guest DevTools launch and host comment connection";
    internal static Option<string> LaunchIdOption { get; } = new("--launch-id") { Required = true };

    public GuestDevToolsHostCommand() : base("guest-devtools-host", "Internal host owner for a guest DevTools launch.")
    {
        Hidden = true;
        Options.Add(LaunchIdOption);
    }

    internal sealed class Handler(
        ExecutionTargetOrchestrator orchestrator, GuestDevToolsHost host,
        TargetDeploymentService deployments, GuestApplicationRunner runner) : AsynchronousCommandLineAction
    {
        internal Func<CancellationToken, Task<GuestDevToolsCapabilities?>> ReadGuestDevToolsCapabilities { get; set; } =
            cancellationToken => GuestDevTools.ReadCapabilitiesAsync(Environment.ProcessPath, cancellationToken);

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var id = parseResult.GetValue(LaunchIdOption)!;
            var directory = host.Resolve(orchestrator.Target, id);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var pipe = new NamedPipeClientStream(".", GuestDevToolsHost.PipeName(id), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var detached = false;
            GuestInspectedAppFrame? application = null;
            Task? parent = null;
            try
            {
                var plan = host.ReadPlan(orchestrator.Target, id);
                await pipe.ConnectAsync(10_000, stop.Token).ConfigureAwait(false);
                GuestCommentPeer.Verify(pipe);
                using var launchLease = orchestrator.AcquireMutationLease(stop.Token);
                var inspection = await orchestrator.InspectAsync(stop.Token).ConfigureAwait(false);
                await using var target = inspection.Target;
                if (target is null || target.Epoch.Value != plan.Epoch)
                {
                    throw new IOException("The selected guest incarnation disconnected or changed before launch.");
                }
                GuestDevTools.RequireMatchingEngines(await ReadGuestDevToolsCapabilities(stop.Token).ConfigureAwait(false),
                    target.Capabilities);
                if (string.IsNullOrWhiteSpace(plan.DeploymentId))
                {
                    throw new IOException("The host launch plan does not identify an owned deployment.");
                }
                var deployment = deployments.ReadCurrent(target.Reference, target.Epoch, plan.DeploymentId);
                TargetDeploymentService.EnsureLaunchable(deployment, target.Epoch);
                if (deployment!.Revision != plan.DeploymentRevision)
                {
                    throw new IOException("The guest deployment changed before its DevTools owner connected. Run the application again.");
                }
                var result = await new GuestDevToolsSession(target, plan.Sources, new CommentStore(), runner, deployment).RunAsync(plan.Request,
                    async (frame, binding) =>
                    {
                        application = frame;
                        var ready = new GuestDevToolsHostMessage("ready", frame, SessionId: id, BindingId: binding.Id);
                        GuestDevToolsHost.WriteState(directory, ready);
                        await GuestCommentFrames.WriteAsync(pipe, ready, GuestCommentsJsonContext.Default.GuestDevToolsHostMessage, stop.Token).ConfigureAwait(false);
                        var control = JsonSerializer.Deserialize(await GuestCommentFrames.ReadAsync(pipe, stop.Token).ConfigureAwait(false),
                            GuestCommentsJsonContext.Default.GuestInspectedAppControl);
                        if (control?.Kind is not ("detach" or "wait"))
                        {
                            throw new IOException("The initiating command did not accept ownership of the launch.");
                        }
                        detached = control.Kind == "detach";
                        await GuestCommentFrames.WriteAsync(pipe, new GuestDevToolsHostMessage("accepted", SessionId: id),
                            GuestCommentsJsonContext.Default.GuestDevToolsHostMessage, stop.Token).ConfigureAwait(false);
                        if (!detached)
                        {
                            parent = MonitorParentAsync();
                        }
                    },
                    bytes =>
                    {
                        var log = new FileInfo(Path.Combine(directory.FullName, "diagnostics.log"));
                        if ((log.Exists ? log.Length : 0) + bytes.Length > 1024 * 1024)
                        {
                            throw new IOException("Guest DevTools diagnostics exceeded the bounded host log.");
                        }
                        using var output = new FileStream(log.FullName, FileMode.Append, FileAccess.Write, FileShare.Read);
                        output.Write(bytes.Span);
                    }, stop.Token, launchLease.Dispose, started =>
                    {
                        application = started;
                        GuestDevToolsHost.WriteState(directory, new("started", started, SessionId: id));
                    }).ConfigureAwait(false);
                var finished = new GuestDevToolsHostMessage("exited", application, ExitCode: result, SessionId: id);
                GuestDevToolsHost.WriteState(directory, finished);
                if (!detached)
                {
                    await GuestCommentFrames.WriteAsync(pipe, finished, GuestCommentsJsonContext.Default.GuestDevToolsHostMessage, stop.Token).ConfigureAwait(false);
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                GuestDevToolsHost.WriteState(directory, new("failed", application, Error: stop.IsCancellationRequested
                    ? "The host owner was cancelled and guest launch cleanup was requested."
                    : "Guest DevTools startup did not become ready before its deadline.", SessionId: id));
                return 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception or JsonException or TimeoutException or ExecutionTargets.Abstractions.ExecutionTargetException)
            {
                var error = new GuestDevToolsHostMessage("failed", application, Error: ex is TimeoutException
                    ? "The retained guest DevTools operation timed out. Owned cleanup was requested."
                    : ex.Message, SessionId: id);
                GuestDevToolsHost.WriteState(directory, error);
                if (pipe.IsConnected && !detached)
                {
                    try
                    {
                        await GuestCommentFrames.WriteAsync(pipe, error, GuestCommentsJsonContext.Default.GuestDevToolsHostMessage, cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // The initiating command may already be gone; status.json remains authoritative.
                    }
                }
                return 1;
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                if (parent is not null)
                {
                    await parent.ConfigureAwait(false);
                }
            }

            async Task MonitorParentAsync()
            {
                try
                {
                    _ = await GuestCommentFrames.ReadAsync(pipe, stop.Token).ConfigureAwait(false);
                    await stop.CancelAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await stop.CancelAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    // The application exited normally and its pending parent read was cancelled.
                }
            }
        }
    }
}
