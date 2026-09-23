// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
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
    private readonly AnonymousPipeServerStream _input;
    private readonly AnonymousPipeServerStream _output;
    private readonly AnonymousPipeServerStream _error;
    private bool _disposed;

    private GuestProcessHost(Process process, GuestJobObject job, Task pumpTask,
        AnonymousPipeServerStream input, AnonymousPipeServerStream output, AnonymousPipeServerStream error)
    {
        _process = process;
        _job = job;
        _pumpTask = pumpTask;
        _input = input;
        _output = output;
        _error = error;
    }

    /// <summary>The child's process ID. Meaningful only within the current target epoch.</summary>
    public int ProcessId => _process.Id;

    /// <summary>UTC ticks when the child started, used to detect PID reuse.</summary>
    public long StartTicksUtc => _process.StartTime.ToUniversalTime().Ticks;

    /// <summary>Starts a child process for <paramref name="request"/>.</summary>
    /// <remarks>
    /// Windows assigns the per-operation job during process creation. Only the three redirected
    /// standard handles are inherited. There is no intermediary process or post-start assignment.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">The process could not be started.</exception>
    public static GuestProcessHost Start(
        GuestExecRequest request,
        Func<GuestStreamId, ReadOnlyMemory<byte>, Task> onOutput)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onOutput);

        // Resolution of which binary to run belongs to the caller, which is the only party that
        // knows whether the request named a guest path or asked for guest winapp itself.
        var executable = request.Executable
            ?? throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                "The request did not name an executable to run inside the guest.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Each argument stays a separate value, so quoting and spacing survive intact and nothing
        // can be reinterpreted as an extra argument.
        foreach (var argument in request.Arguments)
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
        AnonymousPipeServerStream? input = null;
        AnonymousPipeServerStream? output = null;
        AnonymousPipeServerStream? error = null;

        try
        {
            input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            process = CreateProcess(startInfo, job, input, output, error);
            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();

            var pump = Task.WhenAll(
                PumpAsync(output, GuestStreamId.StandardOutput, onOutput),
                PumpAsync(error, GuestStreamId.StandardError, onOutput));

            return new GuestProcessHost(process, job, pump, input, output, error);
        }
        catch (Exception ex) when (ex is not ExecutionTargetException)
        {
            job.Dispose();
            input?.Dispose();
            output?.Dispose();
            error?.Dispose();
            process?.Dispose();

            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                $"The guest could not start '{request.Executable}'.",
                userAction: "Check that the application deployed successfully, then retry.",
                innerException: ex);
        }
        catch
        {
            job.Dispose();
            input?.Dispose();
            output?.Dispose();
            error?.Dispose();
            process?.Dispose();
            throw;
        }
    }

    private static unsafe Process CreateProcess(ProcessStartInfo startInfo, GuestJobObject job,
        AnonymousPipeServerStream input, AnonymousPipeServerStream output, AnonymousPipeServerStream error)
    {
        var executable = ResolveExecutable(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var command = new StringBuilder();
        if (executable.StartsWith('"') && executable.EndsWith('"'))
        {
            command.Append(executable);
        }
        else
        {
            command.Append('"').Append(executable).Append('"');
        }
        foreach (var argument in startInfo.ArgumentList)
        {
            command.Append(' ');
            AppendArgument(command, argument);
        }

        var environment = new StringBuilder();
        foreach (var pair in startInfo.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Value is not null)
            {
                if (pair.Key.Contains('\0') || pair.Value.Contains('\0'))
                {
                    throw new ArgumentException("Environment names and values cannot contain NUL.");
                }
                environment.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
            }
        }
        environment.Append('\0');
        var commandLine = command.ToString().ToCharArray();
        if (commandLine.Contains('\0'))
        {
            throw new ArgumentException("Executable names and arguments cannot contain NUL.");
        }
        // CreateProcessW may modify its command line, including writing the terminating NUL.
        Array.Resize(ref commandLine, commandLine.Length + 1);
        var environmentBlock = environment.ToString();

        nuint size = 0;
        _ = PInvoke.InitializeProcThreadAttributeList(default, 2, 0, &size);
        if (size == 0)
        {
            throw NativeFailure("size the process attribute list");
        }
        var storage = NativeMemory.Alloc(size);
        var attributes = new LPPROC_THREAD_ATTRIBUTE_LIST(storage);
        var initialized = false;
        var jobReference = false;
        PROCESS_INFORMATION information = default;
        Process? process = null;
        try
        {
            if (!PInvoke.InitializeProcThreadAttributeList(attributes, 2, 0, &size))
            {
                throw NativeFailure("initialize the process attribute list");
            }
            initialized = true;
            job.Handle.DangerousAddRef(ref jobReference);
            var jobHandle = new HANDLE(job.Handle.DangerousGetHandle());
            // These pipe instances remain locally owned through creation. Only their child ends
            // appear in the allow-list; unrelated inheritable handles (including other operations)
            // cannot leak. The job handle itself is never inherited.
            HANDLE* handles = stackalloc HANDLE[3]
            {
                new(input.ClientSafePipeHandle.DangerousGetHandle()),
                new(output.ClientSafePipeHandle.DangerousGetHandle()),
                new(error.ClientSafePipeHandle.DangerousGetHandle()),
            };
            if (!PInvoke.UpdateProcThreadAttribute(attributes, 0, PInvoke.PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                    handles, (nuint)(3 * sizeof(HANDLE)), null, null))
            {
                throw NativeFailure("restrict inherited process handles");
            }
            // Supported since Windows 10, below our Windows 10 19041 minimum. Fail closed if the
            // OS or an enclosing job refuses this: never fall back to assigning after creation.
            if (!PInvoke.UpdateProcThreadAttribute(attributes, 0, PInvoke.PROC_THREAD_ATTRIBUTE_JOB_LIST,
                    &jobHandle, (nuint)sizeof(HANDLE), null, null))
            {
                throw NativeFailure("set atomic process job membership");
            }
            var startup = new STARTUPINFOEXW
            {
                StartupInfo = new STARTUPINFOW
                {
                    cb = (uint)sizeof(STARTUPINFOEXW),
                    dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES,
                    hStdInput = handles[0],
                    hStdOutput = handles[1],
                    hStdError = handles[2],
                },
                lpAttributeList = attributes,
            };
            fixed (char* commandPointer = commandLine)
            fixed (char* environmentPointer = environmentBlock)
            fixed (char* directory = string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory)
            {
                if (!PInvoke.CreateProcess(null, new PWSTR(commandPointer), null, null, true,
                        PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT |
                        PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT |
                        PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW |
                        PROCESS_CREATION_FLAGS.CREATE_SUSPENDED,
                        environmentPointer, directory, &startup.StartupInfo, &information))
                {
                    throw NativeFailure($"create '{startInfo.FileName}'");
                }
            }

            // The native process handle pins this PID, and the initial thread is suspended until
            // Process has opened its own handle. Even a no-op cannot exit before that association.
            // Suspension is for managed ownership only: job membership was already atomic.
            process = Process.GetProcessById((int)information.dwProcessId);
            _ = process.SafeHandle;
            if (PInvoke.ResumeThread(information.hThread) == uint.MaxValue)
            {
                throw NativeFailure("resume the guest process");
            }
            return process;
        }
        catch
        {
            // Includes failures after creation but before returning a managed owner.
            job.TerminateAll();
            process?.Dispose();
            throw;
        }
        finally
        {
            if (!information.hThread.IsNull) { _ = PInvoke.CloseHandle(information.hThread); }
            if (!information.hProcess.IsNull) { _ = PInvoke.CloseHandle(information.hProcess); }
            if (initialized) { PInvoke.DeleteProcThreadAttributeList(attributes); }
            NativeMemory.Free(storage);
            if (jobReference) { job.Handle.DangerousRelease(); }
            GC.KeepAlive(input);
            GC.KeepAlive(output);
            GC.KeepAlive(error);
        }
    }

    private static unsafe string ResolveExecutable(ProcessStartInfo startInfo)
    {
        var executable = startInfo.FileName.Trim().Trim('"');
        if (Path.IsPathFullyQualified(executable))
        {
            return executable;
        }

        // The removed helper ran in the requested directory with the requested environment.
        // Resolve there without changing the agent's process-wide directory or PATH.
        var directory = string.IsNullOrEmpty(startInfo.WorkingDirectory)
            ? Environment.CurrentDirectory : Path.GetFullPath(startInfo.WorkingDirectory);
        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return Path.GetFullPath(executable, directory);
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        startInfo.Environment.TryGetValue("PATH", out var path);
        var searchPath = string.Join(';',
            Path.GetDirectoryName(Environment.ProcessPath),
            directory,
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Path.Combine(windows, "System"),
            windows,
            path);
        var buffer = new char[32768];
        fixed (char* search = searchPath)
        fixed (char* file = executable)
        fixed (char* extension = ".exe")
        fixed (char* result = buffer)
        {
            var length = PInvoke.SearchPath(search, file, extension, (uint)buffer.Length, result, null);
            if (length == 0 || length >= buffer.Length)
            {
                throw NativeFailure($"find '{startInfo.FileName}'");
            }
            return new string(result, 0, (int)length);
        }
    }

    /// <summary>Windows CRT quoting, matching ProcessStartInfo.ArgumentList, not shell escaping.</summary>
    private static void AppendArgument(StringBuilder command, string argument)
    {
        var quoted = argument.Length == 0 || argument.Contains(' ') || argument.Contains('\t');
        if (quoted) { command.Append('"'); }
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            command.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            command.Append(character);
            slashes = 0;
        }
        command.Append('\\', quoted ? slashes * 2 : slashes);
        if (quoted) { command.Append('"'); }
    }

    private static ExecutionTargetException NativeFailure(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        var exception = new System.ComponentModel.Win32Exception(error);
        return ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransportFailed,
            $"The guest could not {operation}: {exception.Message}",
            userAction: "Check the executable and working directory, then retry.",
            context: new Dictionary<string, string>
            {
                ["win32Error"] = error.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            innerException: exception);
    }

    /// <summary>Forwards a chunk of standard input to the child.</summary>
    public async Task WriteStandardInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            await _input.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The child closed its input. That is the child's choice, not a transport failure.
        }
    }

    /// <summary>Signals end of standard input, which many console applications wait for.</summary>
    public void CloseStandardInput()
    {
        try
        {
            _input.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
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
        // Process pipes may use synchronous handles: a full stdin write can hold their stream
        // lock even after cancellation. Do not let closing that stream delay the kill deadline.
        var inputClosed = Task.Run(CloseStandardInput, CancellationToken.None);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(gracefulTimeout);
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            // Descendants may still be flushing inherited output after the root exits. Give them
            // the remainder of the same grace period, just as the former relay did.
            await _pumpTask.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The grace period ended, or the caller cancelled it.
        }

        // Even an exited root may have left descendants holding our pipes. The old helper hid
        // that state by remaining alive while relaying output. Stop owns the entire operation,
        // so terminate remaining members before awaiting EOF or an outstanding stdin write.
        _job.TerminateAll();

        try
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up waiting; the job still dies when this host is disposed.
        }

        await inputClosed.ConfigureAwait(false);
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

        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The pipes died with the process.
        }
        finally
        {
            _input.Dispose();
            _output.Dispose();
            _error.Dispose();
            _process.Dispose();
        }
    }

    /// <summary>Forwards one stream's bytes until it closes.</summary>
    private static async Task PumpAsync(
        Stream stream,
        GuestStreamId streamId,
        Func<GuestStreamId, ReadOnlyMemory<byte>, Task> onOutput)
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

                await onOutput(streamId, buffer.AsMemory(0, read)).ConfigureAwait(false);
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
