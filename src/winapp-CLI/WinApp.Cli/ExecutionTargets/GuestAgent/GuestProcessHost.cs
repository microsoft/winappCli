// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

/// <summary>
/// Runs one child process on behalf of a host operation, inside a Job Object, with its standard
/// streams forwarded as raw bytes (spec §"Guest winapp agent mode").
/// </summary>
/// <remarks>
/// Streams are forwarded as bytes rather than decoded lines. A UI command can produce binary output
/// or partial UTF-8 at a chunk boundary, and decoding per chunk would corrupt both; the host
/// reassembles and decodes once.
/// <para>
/// The agent does not implement application semantics. It runs ordinary guest winapp child commands
/// for run, unregister, debugging, and UI Automation, which is what keeps guest behaviour identical
/// to local behaviour.
/// </para>
/// </remarks>
internal sealed class GuestProcessHost : IGuestProcessHost
{
    /// <summary>How long a child gets to exit after a graceful stop before the job is terminated.</summary>
    internal static readonly TimeSpan DefaultGracefulStopTimeout = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly GuestJobObject _job;
    private readonly Task _pumpTask;
    private readonly EventWaitHandle? _released;
    private bool _disposed;

    private GuestProcessHost(Process process, GuestJobObject job, Task pumpTask, EventWaitHandle? released)
    {
        _process = process;
        _job = job;
        _pumpTask = pumpTask;
        _released = released;
    }

    /// <summary>The child's process ID. Meaningful only within the current target epoch.</summary>
    public int ProcessId => _process.Id;

    /// <summary>UTC ticks when the child started, used to detect PID reuse.</summary>
    public long StartTicksUtc => _process.StartTime.ToUniversalTime().Ticks;

    /// <summary>Starts a child process for <paramref name="request"/>.</summary>
    /// <remarks>
    /// The process actually started is <see cref="GuestOperationHost"/> — not the requested command
    /// — whenever a barrier executable is supplied. It waits on an event and so provably cannot
    /// spawn anything before this method assigns it to the job; only then is it released to start
    /// the requested command, which Windows places in the job at creation because its parent is
    /// already a member. That closes the window in which a descendant spawned by a directly started
    /// child could outlive a cancellation of its own operation.
    /// <para>
    /// The barrier path is passed in rather than read from <see cref="Environment.ProcessPath"/>:
    /// only the agent knows that the running binary is winapp and therefore understands the barrier
    /// verb. Callers that do not supply one get a direct start, where containment falls back to
    /// prompt assignment after creation.
    /// </para>
    /// </remarks>
    /// <exception cref="ExecutionTargetException">The process could not be started.</exception>
    public static GuestProcessHost Start(
        GuestExecRequest request,
        Action<GuestStreamId, ReadOnlyMemory<byte>> onOutput,
        string? barrierExecutable = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onOutput);

        // Resolution of which binary to run belongs to the caller, which is the only party that
        // knows whether the request named a guest path or asked for guest winapp itself.
        var executable = request.Executable
            ?? throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                "The request did not name an executable to run inside the guest.");

        var barrierPath = barrierExecutable;
        var readyEventName = barrierPath is null ? null : GuestOperationHost.CreateReleaseEventName();

        EventWaitHandle? released = null;

        var startInfo = new ProcessStartInfo
        {
            FileName = barrierPath ?? executable,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Each argument stays a separate value, so quoting and spacing survive intact and nothing
        // can be reinterpreted as an extra argument.
        var arguments = readyEventName is null
            ? request.Arguments
            : GuestOperationHost.BuildArguments(readyEventName, request);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.Environment is { } environment)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        var job = GuestJobObject.Create();
        Process? process = null;

        try
        {
            if (readyEventName is not null)
            {
                released = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, readyEventName);
            }

            process = Process.Start(startInfo)
                ?? throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.TransportFailed,
                    $"The guest could not start '{request.Executable}'.");

            // Assigned before the barrier is released, which is the whole point: nothing the
            // requested command spawns can exist outside the job.
            job.Assign(process);
            released?.Set();

            var pump = Task.WhenAll(
                PumpAsync(process.StandardOutput.BaseStream, GuestStreamId.StandardOutput, onOutput),
                PumpAsync(process.StandardError.BaseStream, GuestStreamId.StandardError, onOutput));

            return new GuestProcessHost(process, job, pump, released);
        }
        catch (Exception ex) when (ex is not ExecutionTargetException)
        {
            released?.Dispose();
            process?.Dispose();
            job.Dispose();

            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                $"The guest could not start '{request.Executable}'.",
                userAction: "Check that the application deployed successfully, then retry.",
                innerException: ex);
        }
        catch
        {
            released?.Dispose();
            process?.Dispose();
            job.Dispose();
            throw;
        }
    }

    /// <summary>Forwards a chunk of standard input to the child.</summary>
    public async Task WriteStandardInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The child closed its input. That is the child's choice, not a transport failure.
        }
    }

    /// <summary>Signals end of standard input, which many console applications wait for.</summary>
    public void CloseStandardInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Already closed.
        }
    }

    /// <summary>Waits for the child to exit and for its output to be fully drained.</summary>
    /// <remarks>
    /// Draining before returning matters: reporting an exit code while output frames are still in
    /// flight would let a caller observe a completed operation with truncated output.
    /// </remarks>
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await _pumpTask.ConfigureAwait(false);
        return _process.ExitCode;
    }

    /// <summary>
    /// Asks the child to stop, then terminates its whole tree if it does not.
    /// </summary>
    /// <remarks>
    /// Closing standard input is tried first because it is how a well-behaved console application is
    /// told to wind down, and it gives one that is flushing output or finalizing a recording the
    /// chance to finish. Only after the timeout is the job terminated, which kills grandchildren a
    /// process-ID kill would leave behind holding files the next deployment must replace.
    /// </remarks>
    public async Task<int> StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken)
    {
        if (_process.HasExited)
        {
            return _process.ExitCode;
        }

        CloseStandardInput();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(gracefulTimeout);
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _job.TerminateAll();
        }

        try
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up waiting; the job still dies when this host is disposed.
        }

        await _pumpTask.ConfigureAwait(false);
        return _process.HasExited ? _process.ExitCode : -1;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Disposing the job closes the last handle, terminating anything still running. This is what
        // guarantees no guest process outlives the agent that started it.
        _job.Dispose();
        _released?.Dispose();

        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The pipes died with the process.
        }

        _process.Dispose();
    }

    /// <summary>Forwards one stream's bytes until it closes.</summary>
    private static async Task PumpAsync(
        Stream stream,
        GuestStreamId streamId,
        Action<GuestStreamId, ReadOnlyMemory<byte>> onOutput)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                onOutput(streamId, buffer.AsMemory(0, read).ToArray());
            }
        }
        catch (IOException)
        {
            // The pipe broke because the process died. Output already forwarded stays valid.
        }
        catch (ObjectDisposedException)
        {
            // The process was disposed while draining.
        }
    }
}
