// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

/// <summary>
/// The barrier that makes per-operation job containment race-free.
/// </summary>
/// <remarks>
/// <see cref="Process.Start(ProcessStartInfo)"/> cannot create a process that is already a job
/// member, and assigning one afterwards leaves a window in which it can spawn descendants outside
/// the job. Those descendants then survive a cancellation of their own operation, which the
/// specification requires to terminate the whole process tree.
/// <para>
/// Rather than reimplementing process creation to pass <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>, the
/// agent starts <em>this</em> host instead of the requested command. It does nothing but wait on an
/// event, so it provably cannot spawn anything before the agent has assigned it to the job. Once
/// released it starts the real command as its own child, which Windows places in the job at
/// creation because its parent is already a member. There is no window at any point.
/// </para>
/// <para>
/// Standard streams are inherited rather than redirected again, so the requested command writes
/// directly into the pipes the agent created. The extra process costs one handle and no copying,
/// and it does not change what the host observes: the agent already tracks an intermediate — guest
/// <c>winapp</c> — rather than the application itself, and an application's own process ID still
/// comes from that command's output.
/// </para>
/// </remarks>
internal static class GuestOperationHost
{
    /// <summary>Hidden flag that runs winapp as the containment barrier.</summary>
    public const string OperationHostOption = "--operation-host";

    /// <summary>Hidden option naming the event the agent signals once the job is assigned.</summary>
    public const string ReadyEventOption = "--ready-event";

    /// <summary>How long the barrier waits to be released before giving up.</summary>
    internal static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Exit code used when the barrier itself could not do its job.</summary>
    /// <remarks>
    /// Distinct from anything the requested command is likely to return, so a containment failure
    /// is not mistaken for an application result.
    /// </remarks>
    internal const int BarrierFailedExitCode = 64;

    /// <summary>
    /// Builds a per-operation release-event name.
    /// </summary>
    /// <remarks>
    /// The <c>Local\</c> prefix scopes the name to the guest's own Terminal Services session, so it
    /// is not visible to another session on the same machine. The random suffix keeps it unique per
    /// operation and unguessable to something that has to guess.
    /// <para>
    /// <strong>It is not a secret and must not be treated as one.</strong> The name is passed on
    /// this process's command line, which any same-user process can read, and everything in the
    /// Sandbox runs as the same user. A co-resident process with inspection rights can therefore
    /// learn it and signal the event early. Randomness raises the bar against blind guessing and
    /// nothing more.
    /// </para>
    /// <para>
    /// That is accepted under the specification's threat model, which states that co-resident guest
    /// applications are mutually trusted and can observe or interfere with one another. A process
    /// able to do this can already terminate the agent outright. The ordering that matters in
    /// normal operation comes from the agent signalling only after assignment, not from the name.
    /// </para>
    /// </remarks>
    public static string CreateReleaseEventName() =>
        $@"Local\winapp-op-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";

    /// <summary>Builds the arguments that run winapp as the barrier for one request.</summary>
    public static List<string> BuildArguments(string readyEventName, GuestExecRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var executable = request.Executable
            ?? throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                "The request did not name an executable to run inside the guest.");

        var arguments = new List<string>
        {
            GuestAgentCommandNames.Verb,
            OperationHostOption,
            ReadyEventOption,
            readyEventName,

            // Everything after the separator is the requested command, still as discrete values, so
            // argument boundaries survive this extra hop exactly as they survive the transport.
            "--",
            executable,
        };

        arguments.AddRange(request.Arguments);
        return arguments;
    }

    /// <summary>Waits to be released, then runs the requested command as its own child.</summary>
    /// <returns>The requested command's exit code.</returns>
    public static async Task<int> RunAsync(
        string readyEventName,
        IReadOnlyList<string> command,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readyEventName);
        ArgumentNullException.ThrowIfNull(command);

        if (command.Count == 0)
        {
            return BarrierFailedExitCode;
        }

        if (!await WaitForReleaseAsync(readyEventName, cancellationToken).ConfigureAwait(false))
        {
            // Never released, so this process may not be a job member. Starting the command now
            // would create exactly the unconstrained process this barrier exists to prevent.
            return BarrierFailedExitCode;
        }

        // A minimum-containment backstop, and worth being precise about what it proves. Because the
        // agent places itself in a job, this process inherits that membership at creation -- so
        // this check does not by itself prove the *per-operation* job was assigned. What actually
        // orders the two is that the agent signals only after assigning; the unguessable event name
        // is what stops another guest process from forging that signal. This check catches the
        // remaining case: agent-level containment having failed and the barrier being released
        // anyway, where starting user code would leave it in no job at all.
        if (!GuestJobObject.IsCurrentProcessInJob())
        {
            Console.Error.WriteLine(
                "The guest operation host is not contained by a job object and refused to start the command.");
            return BarrierFailedExitCode;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command[0],
            WorkingDirectory = workingDirectory ?? string.Empty,

            // Explicitly bridge the barrier's redirected handles. Process.Start does not reliably
            // inherit redirected parent pipes when the child itself requests no redirection, which
            // made real guest commands return the right exit code with empty stdout/stderr.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in command.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return BarrierFailedExitCode;
            }

            using var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var barrierInput = Console.OpenStandardInput();
            var childInput = process.StandardInput.BaseStream;

            var standardOutput = process.StandardOutput.BaseStream.CopyToAsync(
                Console.OpenStandardOutput(),
                pumpCancellation.Token);
            var standardError = process.StandardError.BaseStream.CopyToAsync(
                Console.OpenStandardError(),
                pumpCancellation.Token);
            var standardInput = CopyInputAsync(
                barrierInput,
                childInput,
                pumpCancellation.Token);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // Output reaches EOF when the child exits and must be fully drained before the barrier
            // reports completion. Input may still be waiting on the host pipe, so cancel that pump
            // once the child can no longer consume it.
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            barrierInput.Dispose();
            childInput.Dispose();
            await pumpCancellation.CancelAsync().ConfigureAwait(false);
            ObserveInBackground(standardInput);

            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"The guest could not start '{command[0]}': {ex.Message}");
            return BarrierFailedExitCode;
        }
    }

    private static async Task CopyInputAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Close();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The child exited or the operation was cancelled while input was idle.
        }
    }

    private static void ObserveInBackground(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Waits for the agent to signal that this process is inside the job.</summary>
    private static async Task<bool> WaitForReleaseAsync(string readyEventName, CancellationToken cancellationToken)
    {
        try
        {
            using var released = EventWaitHandle.OpenExisting(readyEventName);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReleaseTimeout);

            await released.WaitOneAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The agent died between starting this process and creating the event.
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Awaits a <see cref="WaitHandle"/> without blocking a thread-pool thread.</summary>
internal static class WaitHandleExtensions
{
    /// <summary>Completes when <paramref name="handle"/> is signalled or the token fires.</summary>
    public static async Task WaitOneAsync(this WaitHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var registration = ThreadPool.RegisterWaitForSingleObject(
            handle,
            static (state, timedOut) => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            System.Threading.Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true);

        await using var cancellationRegistration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(),
            completion);

        try
        {
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(waitObject: null);
        }
    }
}
