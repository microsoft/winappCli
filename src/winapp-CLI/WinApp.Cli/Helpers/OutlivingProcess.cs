// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Starts a child that outlives winapp without handing it any handle winapp itself inherited.
/// </summary>
/// <remarks>
/// <para>
/// When a script captures winapp's output, the script reaches end of stream only when every handle
/// to the write end of that pipe has closed — not when winapp exits. A Sandbox run deliberately
/// leaves processes running afterwards: the client window, and the <c>wsb exec</c> that hosts the
/// persistent guest agent. If either holds a copy of the caller's pipe, the caller waits until the
/// whole Sandbox goes away.
/// </para>
/// <para>
/// <see cref="Process.Start(ProcessStartInfo)"/> cannot prevent that. It always lets the child
/// inherit every inheritable handle in this process, and callers hand winapp more than its three
/// standard handles: a nested PowerShell pipeline, for example, leaves inheritable duplicates of
/// its pipes in winapp. Clearing inheritance on the standard handles alone therefore still leaks.
/// This launcher passes an explicit inheritance list instead — only the child's own redirected
/// pipes, or nothing at all — so no other handle can reach the child.
/// </para>
/// </remarks>
internal static class OutlivingProcess
{
    private const ushort ShowHidden = 0;
    private const ushort ShowMinimized = 2;
    private const ushort ShowMaximized = 3;

    /// <summary>Starts a child that inherits no handles.</summary>
    /// <exception cref="Win32Exception">The process could not be created.</exception>
    public static Process Start(ProcessStartInfo startInfo) => Create(startInfo, null);

    /// <summary>
    /// Starts a child whose standard output and error are captured, and whose standard input is
    /// already at end of stream. Those three pipes are the only handles it inherits.
    /// </summary>
    /// <exception cref="Win32Exception">The process could not be created.</exception>
    public static RedirectedProcess StartRedirected(ProcessStartInfo startInfo)
    {
        AnonymousPipeServerStream? input = null;
        AnonymousPipeServerStream? output = null;
        AnonymousPipeServerStream? error = null;

        try
        {
            input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

            var process = Create(
                startInfo,
                (input.ClientSafePipeHandle, output.ClientSafePipeHandle, error.ClientSafePipeHandle));

            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();

            // Nothing is ever written to the child, so closing our end now gives it end of input
            // rather than a handle to whatever standard input winapp was given.
            input.Dispose();

            var encoding = Console.OutputEncoding;
            return new RedirectedProcess(
                process,
                new StreamReader(output, encoding),
                new StreamReader(error, encoding));
        }
        catch
        {
            input?.Dispose();
            output?.Dispose();
            error?.Dispose();
            throw;
        }
    }

    private static unsafe Process Create(
        ProcessStartInfo startInfo,
        (SafeHandle Input, SafeHandle Output, SafeHandle Error)? standardHandles)
    {
        if (!Path.IsPathFullyQualified(startInfo.FileName))
        {
            throw new ArgumentException("An outliving process must be started from an absolute path.", nameof(startInfo));
        }

        var command = new StringBuilder().Append('"').Append(startInfo.FileName).Append('"');
        foreach (var argument in startInfo.ArgumentList)
        {
            command.Append(' ').Append(WindowsCommandLine.EscapeArgument(argument));
        }

        // CreateProcessW may write to its command line, including the terminating NUL.
        var commandLine = command.Append('\0').ToString().ToCharArray();

        var environment = new StringBuilder();
        foreach (var pair in startInfo.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Value is not null)
            {
                environment.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
            }
        }
        var environmentBlock = environment.Append('\0').ToString();

        var flags = PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED;
        if (startInfo.CreateNoWindow)
        {
            flags |= PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW;
        }

        var startup = new STARTUPINFOEXW { StartupInfo = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW) } };
        if (startInfo.WindowStyle != ProcessWindowStyle.Normal)
        {
            startup.StartupInfo.dwFlags |= STARTUPINFOW_FLAGS.STARTF_USESHOWWINDOW;
            startup.StartupInfo.wShowWindow = startInfo.WindowStyle switch
            {
                ProcessWindowStyle.Hidden => ShowHidden,
                ProcessWindowStyle.Minimized => ShowMinimized,
                _ => ShowMaximized,
            };
        }

        HANDLE* handles = stackalloc HANDLE[3];
        var references = new bool[3];
        void* storage = null;
        var attributes = default(LPPROC_THREAD_ATTRIBUTE_LIST);
        var initialized = false;
        PROCESS_INFORMATION information = default;
        Process? process = null;

        try
        {
            if (standardHandles is var (input, output, error))
            {
                input.DangerousAddRef(ref references[0]);
                output.DangerousAddRef(ref references[1]);
                error.DangerousAddRef(ref references[2]);
                handles[0] = new HANDLE(input.DangerousGetHandle());
                handles[1] = new HANDLE(output.DangerousGetHandle());
                handles[2] = new HANDLE(error.DangerousGetHandle());

                nuint size = 0;
                _ = PInvoke.InitializeProcThreadAttributeList(default, 1, 0, &size);
                if (size == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                storage = NativeMemory.Alloc(size);
                attributes = new LPPROC_THREAD_ATTRIBUTE_LIST(storage);
                if (!PInvoke.InitializeProcThreadAttributeList(attributes, 1, 0, &size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                initialized = true;

                if (!PInvoke.UpdateProcThreadAttribute(attributes, 0, PInvoke.PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                        handles, (nuint)(3 * sizeof(HANDLE)), null, null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
                startup.StartupInfo.dwFlags |= STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES;
                startup.StartupInfo.hStdInput = handles[0];
                startup.StartupInfo.hStdOutput = handles[1];
                startup.StartupInfo.hStdError = handles[2];
                startup.lpAttributeList = attributes;
                flags |= PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT;
            }

            fixed (char* commandPointer = commandLine)
            fixed (char* environmentPointer = environmentBlock)
            fixed (char* directory = string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory)
            {
                // Inheritance is enabled only together with the list above, which limits it to the
                // child's own pipes. Without redirection the child inherits nothing.
                if (!PInvoke.CreateProcess(null, new PWSTR(commandPointer), null, null, standardHandles is not null,
                        flags, environmentPointer, directory, &startup.StartupInfo, &information))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }

            // The child stays suspended until a managed owner holds its own handle, so even a child
            // that fails instantly still reports its exit code instead of disappearing first.
            process = Process.GetProcessById((int)information.dwProcessId);
            _ = process.SafeHandle;

            if (PInvoke.ResumeThread(information.hThread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return process;
        }
        catch
        {
            if (!information.hProcess.IsNull)
            {
                _ = PInvoke.TerminateProcess(information.hProcess, 1);
            }

            process?.Dispose();
            throw;
        }
        finally
        {
            if (!information.hThread.IsNull)
            {
                _ = PInvoke.CloseHandle(information.hThread);
            }

            if (!information.hProcess.IsNull)
            {
                _ = PInvoke.CloseHandle(information.hProcess);
            }

            if (initialized)
            {
                PInvoke.DeleteProcThreadAttributeList(attributes);
            }

            NativeMemory.Free(storage);

            if (standardHandles is var (input, output, error))
            {
                if (references[0]) { input.DangerousRelease(); }
                if (references[1]) { output.DangerousRelease(); }
                if (references[2]) { error.DangerousRelease(); }
            }
        }
    }
}

/// <summary>A started child and readers for its captured output.</summary>
internal sealed record RedirectedProcess(Process Process, StreamReader StandardOutput, StreamReader StandardError)
    : IDisposable
{
    public void Dispose()
    {
        StandardOutput.Dispose();
        StandardError.Dispose();
        Process.Dispose();
    }
}
