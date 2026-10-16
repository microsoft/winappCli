// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Threading.Channels;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

// Retains one guest launch and comment relay on the existing authenticated channel.
internal sealed class GuestDevToolsSession(
    PreparedTarget target, GuestSourceManifest sources, CommentStore store,
    GuestApplicationRunner runner, DeploymentState deployment)
{
    internal async Task<int> RunAsync(
        GuestExecRequest request, Func<GuestInspectedAppFrame, GuestCommentBinding, Task> publishReady,
        Action<ReadOnlyMemory<byte>> diagnostic, CancellationToken cancellationToken,
        Action? operationTracked = null, Action<GuestInspectedAppFrame>? publishStarted = null)
    {
        TargetDeploymentService.EnsureLaunchable(deployment, target.Epoch);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var executionStop = new CancellationTokenSource();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(120));
        var frames = Channel.CreateBounded<GuestInspectedAppFrame>(2);
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var journalReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = deployment;
        GuestProcessStart? announcedProcess = null;
        var reader = new GuestCommentFrames.Reader(bytes =>
        {
            var frame = JsonSerializer.Deserialize(bytes, GuestCommentsJsonContext.Default.GuestInspectedAppFrame)
                ?? throw new InvalidDataException("Guest launch returned an empty startup frame.");
            if (frame.Phase == "failed")
            {
                if (frame.Process != announcedProcess || string.IsNullOrWhiteSpace(frame.Error) ||
                    frame.Error.Length > GuestInspectedAppFrame.MaximumErrorLength)
                {
                    throw new InvalidDataException("Guest launch returned an invalid failure frame.");
                }
                fault.TrySetException(new IOException(frame.Error));
                return;
            }
            if (frame.Phase == "started")
            {
                announcedProcess = frame.Process;
            }
            if (!frames.Writer.TryWrite(frame))
            {
                throw new InvalidDataException("Guest launch exceeded its bounded startup queue.");
            }
        });
        var execution = runner.RunAsync(target, deployment, request, new GuestExecCallbacks(
            OnOperationId: id => operation.TrySetResult(id),
            OnStarted: process =>
            {
                if (current.TrackedOperationProcessId != process.ProcessId ||
                    current.TrackedOperationProcessStartTicksUtc != process.StartTicksUtc ||
                    current.Revision <= deployment.Revision)
                {
                    fault.TrySetException(new IOException("The guest launch could not retain its deployment ownership. Retry after the competing run finishes."));
                    return;
                }
                operationTracked?.Invoke();
                journalReady.TrySetResult();
            },
            OnStandardOutput: bytes =>
            {
                try
                {
                    reader.Append(bytes.Span);
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException)
                {
                    fault.TrySetException(ex);
                }
            }, OnStandardError: diagnostic), executionStop.Token, state => current = state);
        Task? relay = null;
        Task? setup = null;
        Exception? firstFailure = null;
        try
        {
            setup = StartAsync();
            var first = await Task.WhenAny(setup, execution, fault.Task).ConfigureAwait(false);
            // Output callbacks precede execution completion; do not lose an already-delivered failure to exit.
            if (fault.Task.IsCompleted)
            {
                await fault.Task.ConfigureAwait(false);
            }
            await first.ConfigureAwait(false);
            if (first != setup)
            {
                throw new IOException($"Guest DevTools launch ended before application and host-comment readiness (exit {(await execution.ConfigureAwait(false)).ExitCode}).");
            }
            var ended = await Task.WhenAny(execution, relay!, fault.Task).ConfigureAwait(false);
            if (fault.Task.IsCompleted)
            {
                await fault.Task.ConfigureAwait(false);
            }
            await ended.ConfigureAwait(false);
            if (ended == relay)
            {
                // Relay success means its exact app-lifetime monitor observed exit, not merely EOF.
                // Wake the launcher's real console input read before joining its operation.
                await target.Operations.SendStandardInputAsync(operation.Task.Result,
                    GuestCommentFrames.Encode(new GuestInspectedAppControl("stop"),
                        GuestCommentsJsonContext.Default.GuestInspectedAppControl), stop.Token).ConfigureAwait(false);
                return (await execution.WaitAsync(TimeSpan.FromSeconds(10), stop.Token).ConfigureAwait(false)).ExitCode;
            }
            if (ended != execution)
            {
                throw new IOException("Host-backed guest inspection lost its comment relay. Cleanup of this owned launch was requested.");
            }
            return (await execution.ConfigureAwait(false)).ExitCode;
        }
        catch (Exception ex)
        {
            firstFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                if (!execution.IsCompleted && operation.Task.IsCompletedSuccessfully)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await target.Operations.SendStandardInputAsync(operation.Task.Result,
                            GuestCommentFrames.Encode(new GuestInspectedAppControl("stop"),
                                GuestCommentsJsonContext.Default.GuestInspectedAppControl), cleanup.Token).ConfigureAwait(false);
                        await execution.WaitAsync(cleanup.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ExecutionTargetException or OperationCanceledException or ObjectDisposedException)
                    {
                        diagnostic(System.Text.Encoding.UTF8.GetBytes(
                            $"Graceful guest stop was not acknowledged; requesting owned-operation cancellation: {ex.Message}\n"));
                    }
                }
            }
            finally
            {
                await executionStop.CancelAsync().ConfigureAwait(false);
                await stop.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(execution, setup ?? Task.CompletedTask).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    // The execution channel acknowledges cancellation before its job is released.
                }
                catch (Exception ex) when (firstFailure is not null && ex is IOException or ExecutionTargetException)
                {
                    diagnostic(System.Text.Encoding.UTF8.GetBytes(
                        $"Guest DevTools launch cleanup also reported {ex.GetType().Name}; the first failure is retained.\n"));
                }
                finally
                {
                    if (relay is not null)
                    {
                        try
                        {
                            await relay.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested)
                        {
                            // Setup has finished assigning the relay before this final join.
                        }
                        catch (Exception ex) when (firstFailure is not null && ex is IOException or ExecutionTargetException)
                        {
                            diagnostic(System.Text.Encoding.UTF8.GetBytes(
                                $"Guest DevTools relay cleanup also reported {ex.GetType().Name}; the first failure is retained.\n"));
                        }
                    }
                }
            }
        }

        async Task StartAsync()
        {
            await journalReady.Task.WaitAsync(startup.Token).ConfigureAwait(false);
            var started = await frames.Reader.ReadAsync(startup.Token).ConfigureAwait(false);
            if (started is not { Phase: "started", Process: { ProcessId: > 0, StartTicksUtc: > 0 } identity } ||
                !await target.Operations.IsTrackedProcessRunningAsync(identity.ProcessId, identity.StartTicksUtc, startup.Token).ConfigureAwait(false))
            {
                throw new IOException("Guest launch did not prove its actual application process lifetime.");
            }
            publishStarted?.Invoke(started);
            var binding = new GuestCommentBinding(target.Reference, target.Epoch, identity,
                new FileInfo(sources.ProjectPath), sources.Files.Select(file => file.RelativePath), sources.GuestRoot);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            relay = new GuestCommentRelay(binding, new(binding, store), target.Operations)
                .RunAsync(diagnostic, stop.Token, () => ready.TrySetResult());
            var relayStart = await Task.WhenAny(ready.Task, relay).WaitAsync(startup.Token).ConfigureAwait(false);
            await relayStart.ConfigureAwait(false);
            if (relayStart != ready.Task)
            {
                throw new IOException("The guest comment endpoint exited before becoming ready.");
            }
            await target.Operations.SendStandardInputAsync(await operation.Task.WaitAsync(startup.Token).ConfigureAwait(false),
                GuestCommentFrames.Encode(new GuestInspectedAppControl("owner-ready"),
                    GuestCommentsJsonContext.Default.GuestInspectedAppControl), startup.Token).ConfigureAwait(false);
            var connected = await frames.Reader.ReadAsync(startup.Token).ConfigureAwait(false);
            if (connected.Phase != "ready" || connected.Process != identity || connected.NodeCount is null or < 0)
            {
                throw new IOException("Guest DevTools did not acknowledge inspection of the launched application.");
            }
            await publishReady(connected, binding).ConfigureAwait(false);
        }
    }
}
