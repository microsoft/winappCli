// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Console;
using Windows.Win32.System.Diagnostics.Debug;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Threading;

namespace WinApp.Cli.Services.Performance;

internal static class PerfStartupGate
{
    internal const string Coverage = "providers enabled before executable entry point; earlier DLL and TLS initialization not recorded";

    internal static string? HostArchitectureError(Architecture osArchitecture, Architecture processArchitecture) =>
        osArchitecture == Architecture.Arm64 && processArchitecture != Architecture.Arm64
            ? "Startup profiling on ARM64 Windows requires the ARM64 winapp CLI. Use the ARM64 CLI for run --profile, or launch normally and use perf start to record an existing app."
            : null;

    internal static void ValidateHostArchitecture()
    {
        if (HostArchitectureError(RuntimeInformation.OSArchitecture, RuntimeInformation.ProcessArchitecture) is { } error)
        {
            throw new NotSupportedException(error);
        }
    }

    internal static string CreateEnvironmentBlock(IEnumerable<KeyValuePair<string, string?>> inheritedEnvironment)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in inheritedEnvironment)
        {
            environment[pair.Key] = pair.Value;
        }
        // Heap checks are chosen during initialization and survive the startup debugger's detach.
        environment["_NO_DEBUG_HEAP"] = "1";
        return string.Join('\0', environment.Where(pair => pair.Value is not null)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
    }

    public static async Task<ILaunchedProcess> LaunchAsync(string executable, string? arguments,
        string? workingDirectory, LaunchStdioMode stdio, Func<uint, Task> ready, CancellationToken token)
    {
        ValidateHostArchitecture();
        using var paused = await Task.Factory.StartNew(
            () => CreatePaused(executable, arguments, workingDirectory, stdio, token),
            token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await ready(checked((uint)paused.Process.Id));
        token.ThrowIfCancellationRequested();
        paused.Resume();
        return new LaunchedProcess(paused.ReleaseProcess());
    }

    public static async Task AttachAsync(int pid, uint threadId, Func<Process, Task> ready, CancellationToken token)
    {
        ValidateHostArchitecture();
        using var paused = await Task.Factory.StartNew(
            () => AttachPaused(pid, threadId, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await ready(paused.Process);
        token.ThrowIfCancellationRequested();
        paused.Resume();
    }

    private static unsafe PausedProcess CreatePaused(string executable, string? arguments,
        string? workingDirectory, LaunchStdioMode stdio, CancellationToken token)
    {
        using var input = InheritedStandardHandle(STD_HANDLE.STD_INPUT_HANDLE, stdio);
        using var output = InheritedStandardHandle(STD_HANDLE.STD_OUTPUT_HANDLE, stdio);
        using var error = InheritedStandardHandle(STD_HANDLE.STD_ERROR_HANDLE, stdio);
        HANDLE* handles = stackalloc HANDLE[3]
        {
            new(input.DangerousGetHandle()), new(output.DangerousGetHandle()), new(error.DangerousGetHandle()),
        };
        nuint size = 0;
        _ = PInvoke.InitializeProcThreadAttributeList(default, 1, 0, &size);
        if (size == 0) { throw NativeFailure("Size process attributes"); }
        var storage = NativeMemory.Alloc(size);
        var attributes = new LPPROC_THREAD_ATTRIBUTE_LIST(storage);
        var initialized = false;
        var handedOff = false;
        Process? process = null;
        PROCESS_INFORMATION information = default;
        try
        {
            Check(PInvoke.InitializeProcThreadAttributeList(attributes, 1, 0, &size), "Initialize process attributes");
            initialized = true;
            Check(PInvoke.UpdateProcThreadAttribute(attributes, 0, PInvoke.PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                handles, (nuint)(3 * sizeof(HANDLE)), null, null), "Restrict inherited handles");
            var startup = new STARTUPINFOEXW
            {
                StartupInfo = new()
                {
                    cb = (uint)sizeof(STARTUPINFOEXW), dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES,
                    hStdInput = handles[0], hStdOutput = handles[1], hStdError = handles[2],
                },
                lpAttributeList = attributes,
            };
            var command = (Helpers.WindowsCommandLine.EscapeArgument(executable) +
                (string.IsNullOrEmpty(arguments) ? "" : " " + arguments) + '\0').ToCharArray();
            var environment = CreateEnvironmentBlock(new ProcessStartInfo().Environment);
            var flags = PROCESS_CREATION_FLAGS.DEBUG_ONLY_THIS_PROCESS |
                PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT | PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT;
            if (stdio == LaunchStdioMode.Suppress) { flags |= PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW; }
            fixed (char* application = executable)
            fixed (char* commandLine = command)
            fixed (char* environmentBlock = environment)
            fixed (char* directory = string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory)
            {
                Check(PInvoke.CreateProcess(application, commandLine, null, null, true, flags, environmentBlock,
                    directory, &startup.StartupInfo, &information), "Create profiled process");
            }
            process = Process.GetProcessById(checked((int)information.dwProcessId));
            _ = process.Handle;
            var thread = new SafeFileHandle((nint)information.hThread, ownsHandle: true);
            information.hThread = default;
            handedOff = true;
            return PauseAtEntryPoint(process, thread, information.dwThreadId, token);
        }
        catch (Exception failure) when (!handedOff && information.dwProcessId != 0)
        {
            try
            {
                Check(PInvoke.TerminateProcess(information.hProcess, 1), "Terminate uninitialized profiled process");
                Check(PInvoke.DebugActiveProcessStop(information.dwProcessId), "Detach uninitialized startup debugger");
            }
            catch (Exception cleanup)
            {
                throw new AggregateException("Startup recording failed and process cleanup failed.", failure, cleanup);
            }
            throw;
        }
        finally
        {
            if (!handedOff) { process?.Dispose(); }
            if (!information.hProcess.IsNull) { PInvoke.CloseHandle(information.hProcess); }
            if (!information.hThread.IsNull) { PInvoke.CloseHandle(information.hThread); }
            if (initialized) { PInvoke.DeleteProcThreadAttributeList(attributes); }
            NativeMemory.Free(storage);
        }
    }

    private static PausedProcess AttachPaused(int pid, uint threadId, CancellationToken token)
    {
        var process = Process.GetProcessById(pid);
        _ = process.Handle;
        var thread = PInvoke.OpenThread_SafeHandle(
            THREAD_ACCESS_RIGHTS.THREAD_GET_CONTEXT | THREAD_ACCESS_RIGHTS.THREAD_SET_CONTEXT |
            THREAD_ACCESS_RIGHTS.THREAD_SUSPEND_RESUME | THREAD_ACCESS_RIGHTS.THREAD_QUERY_LIMITED_INFORMATION,
            false, threadId);
        var attached = false;
        try
        {
            if (thread.IsInvalid) { throw NativeFailure("Open activation thread"); }
            if (PInvoke.GetProcessIdOfThread(thread) != checked((uint)pid))
            {
                throw new InvalidOperationException("The activation thread does not belong to the profiled process.");
            }
            Check(PInvoke.DebugActiveProcess(checked((uint)pid)), "Debug package activation");
            attached = true;
            // Release the activation hold once; pending debug events can contribute another suspension.
            Check(PInvoke.ResumeThread(thread) != uint.MaxValue, "Initialize package process");
        }
        catch (Exception failure)
        {
            try { TerminateStartupProcess(process, attached); }
            catch (Exception cleanup)
            {
                throw new AggregateException("Startup recording failed and activation cleanup failed.", failure, cleanup);
            }
            finally { process.Dispose(); thread.Dispose(); }
            throw;
        }
        return PauseAtEntryPoint(process, thread, threadId, token);
    }

    private static unsafe PausedProcess PauseAtEntryPoint(Process process, SafeFileHandle thread,
        uint threadId, CancellationToken token)
    {
        var attached = true;
        DEBUG_EVENT? pending = null;
        nuint entryPoint = 0;
        byte[]? original = null;
        Machine machine = default;
        var timer = Stopwatch.StartNew();
        try
        {
            while (timer.Elapsed < TimeSpan.FromSeconds(30))
            {
                token.ThrowIfCancellationRequested();
                if (!PInvoke.WaitForDebugEventEx(out var e, 100))
                {
                    if (Marshal.GetLastPInvokeError() != 121) { throw NativeFailure("Wait for startup debug event"); }
                    continue;
                }
                pending = e;
                var status = NTSTATUS.DBG_CONTINUE;
                switch (e.dwDebugEventCode)
                {
                    case DEBUG_EVENT_CODE.CREATE_PROCESS_DEBUG_EVENT:
                        try
                        {
                            using var file = new SafeFileHandle((nint)e.u.CreateProcessInfo.hFile, ownsHandle: false);
                            using var stream = new FileStream(file, FileAccess.Read);
                            using var image = new PEReader(stream);
                            machine = image.PEHeaders.CoffHeader.Machine;
                            var breakpoint = Breakpoint(machine);
                            var header = image.PEHeaders.PEHeader ??
                                throw new InvalidDataException("The profiled executable has no PE header.");
                            if (header.AddressOfEntryPoint <= 0 || header.AddressOfEntryPoint >= header.SizeOfImage)
                            {
                                throw new InvalidDataException("The profiled executable has no valid entry point.");
                            }
                            entryPoint = (nuint)e.u.CreateProcessInfo.lpBaseOfImage + checked((uint)header.AddressOfEntryPoint);
                            original = new byte[breakpoint.Length];
                            Check(PInvoke.ReadProcessMemory(process.SafeHandle, (void*)entryPoint,
                                original, out var read) && read == (nuint)original.Length, "Read executable entry point");
                            WriteCode(process, entryPoint, breakpoint);
                        }
                        finally
                        {
                            if (!e.u.CreateProcessInfo.hFile.IsNull) { PInvoke.CloseHandle(e.u.CreateProcessInfo.hFile); }
                        }
                        break;
                    case DEBUG_EVENT_CODE.LOAD_DLL_DEBUG_EVENT:
                        if (!e.u.LoadDll.hFile.IsNull) { PInvoke.CloseHandle(e.u.LoadDll.hFile); }
                        break;
                    case DEBUG_EVENT_CODE.EXCEPTION_DEBUG_EVENT:
                        var code = unchecked((uint)e.u.Exception.ExceptionRecord.ExceptionCode.Value);
                        if (code is 0x80000003 or 0x4000001f)
                        {
                            if (original is not null && e.dwThreadId == threadId &&
                                (nuint)e.u.Exception.ExceptionRecord.ExceptionAddress == entryPoint)
                            {
                                WriteCode(process, entryPoint, original);
                                RewindEntryPoint(thread, machine, entryPoint, original.Length);
                                var suspendCount = PInvoke.SuspendThread(thread);
                                Check(suspendCount != uint.MaxValue, "Hold application entry point");
                                if (suspendCount != 0) { throw new InvalidOperationException("The entry-point thread was already suspended."); }
                                Check(PInvoke.ContinueDebugEvent(e.dwProcessId, e.dwThreadId, status), "Continue entry-point event");
                                pending = null;
                                Check(PInvoke.DebugActiveProcessStop(checked((uint)process.Id)), "Detach startup debugger");
                                attached = false;
                                return new(process, thread);
                            }
                        }
                        else
                        {
                            status = NTSTATUS.DBG_EXCEPTION_NOT_HANDLED;
                        }
                        break;
                    case DEBUG_EVENT_CODE.EXIT_PROCESS_DEBUG_EVENT:
                        Check(PInvoke.ContinueDebugEvent(e.dwProcessId, e.dwThreadId, status), "Continue process exit");
                        pending = null;
                        throw new InvalidOperationException($"The app exited before startup recording was ready (code {e.u.ExitProcess.dwExitCode}).");
                }
                Check(PInvoke.ContinueDebugEvent(e.dwProcessId, e.dwThreadId, status), "Continue startup event");
                pending = null;
            }
            throw new TimeoutException("The app did not reach its executable entry point within 30 seconds.");
        }
        catch (Exception failure)
        {
            try { TerminateStartupProcess(process, attached, pending); }
            catch (Exception cleanup)
            {
                throw new AggregateException("Startup recording failed and debugger cleanup failed.", failure, cleanup);
            }
            finally { process.Dispose(); thread.Dispose(); }
            throw;
        }
    }

    private static void TerminateStartupProcess(Process process, bool attached, DEBUG_EVENT? pending = null)
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); }
        if (attached)
        {
            if (pending is { } e)
            {
                Check(PInvoke.ContinueDebugEvent(e.dwProcessId, e.dwThreadId, NTSTATUS.DBG_CONTINUE),
                    "Release failed startup event");
            }
            if (!PInvoke.DebugActiveProcessStop(checked((uint)process.Id)))
            {
                var failure = NativeFailure("Detach failed startup debugger");
                if (!(failure.NativeErrorCode == 87 && process.HasExited)) { throw failure; }
            }
        }
        if (!process.WaitForExit(5000)) { throw new TimeoutException("The failed startup process did not terminate."); }
    }

    internal static byte[] Breakpoint(Machine machine) => machine switch
    {
        Machine.Arm64 => [0, 0, 0x3e, 0xd4],
        Machine.Amd64 or Machine.I386 => [0xcc],
        _ => throw new NotSupportedException($"Startup recording does not support executable architecture {machine}."),
    };

    private static unsafe void RewindEntryPoint(SafeFileHandle thread, Machine machine, nuint entryPoint, int width)
    {
        // Native CONTEXT follows the debugger architecture, even for x64 emulation on ARM64.
        // 32-bit targets instead use the separate WOW64_CONTEXT layout.
        var wow64 = machine == Machine.I386;
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64))
        {
            throw new NotSupportedException("Startup recording requires a 64-bit winapp process.");
        }
        var armHost = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        var size = wow64 ? 716 : armHost ? 912 : 1232;
        var flagsOffset = wow64 || armHost ? 0 : 48;
        var pcOffset = wow64 ? 184 : armHost ? 264 : 248;
        var context = (byte*)NativeMemory.AlignedAlloc((nuint)size, 16);
        try
        {
            NativeMemory.Clear(context, (nuint)size);
            *(uint*)(context + flagsOffset) = wow64 ? 0x10001u : armHost ? 0x400001u : 0x100001u;
            var handle = new HANDLE(thread.DangerousGetHandle());
            Check(wow64 ? PInvoke.Wow64GetThreadContext(handle, (WOW64_CONTEXT*)context) :
                PInvoke.GetThreadContext(handle, (CONTEXT*)context), "Read startup thread context");
            var pc = wow64 ? *(uint*)(context + pcOffset) : *(ulong*)(context + pcOffset);
            if (pc != (ulong)entryPoint + (uint)width)
            {
                throw new InvalidDataException("The startup thread's program counter does not match the entry-point breakpoint.");
            }
            if (wow64) { *(uint*)(context + pcOffset) = checked((uint)entryPoint); }
            else { *(ulong*)(context + pcOffset) = (ulong)entryPoint; }
            Check(wow64 ? PInvoke.Wow64SetThreadContext(handle, (WOW64_CONTEXT*)context) :
                PInvoke.SetThreadContext(handle, (CONTEXT*)context), "Restore startup program counter");
        }
        finally
        {
            NativeMemory.AlignedFree(context);
        }
    }

    private static unsafe void WriteCode(Process process, nuint address, byte[] bytes)
    {
        var handle = new HANDLE(process.Handle);
        Check(PInvoke.VirtualProtectEx(process.SafeHandle, (void*)address, (nuint)bytes.Length,
            PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_READWRITE, out var protection), "Unprotect entry point");
        try
        {
            Check(PInvoke.WriteProcessMemory(process.SafeHandle, (void*)address, bytes, out var written) &&
                written == (nuint)bytes.Length, "Write entry-point breakpoint");
            Check(PInvoke.FlushInstructionCache(handle, (void*)address, (nuint)bytes.Length), "Flush entry-point instructions");
        }
        finally
        {
            Check(PInvoke.VirtualProtectEx(process.SafeHandle, (void*)address, (nuint)bytes.Length,
                protection, out _), "Restore entry-point protection");
        }
    }

    private static SafeFileHandle InheritedStandardHandle(STD_HANDLE id, LaunchStdioMode stdio)
    {
        var source = stdio == LaunchStdioMode.Inherit ? PInvoke.GetStdHandle(id) : default;
        using var nul = source.IsNull || (nint)source == -1
            ? File.OpenHandle("NUL", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)
            : null;
        if (nul is not null) { source = new(nul.DangerousGetHandle()); }
        using var self = new SafeFileHandle((nint)PInvoke.GetCurrentProcess(), ownsHandle: false);
        using var borrowed = new SafeFileHandle((nint)source, ownsHandle: false);
        Check(PInvoke.DuplicateHandle(self, borrowed, self, out var inherited, 0, true,
            DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS), "Duplicate app standard handle");
        return inherited;
    }

    private static void Check(BOOL success, string operation)
    {
        if (!success) { throw NativeFailure(operation); }
    }

    private static Win32Exception NativeFailure(string operation) =>
        new(Marshal.GetLastPInvokeError(), operation + " failed.");

    private sealed class PausedProcess(Process process, SafeFileHandle thread) : IDisposable
    {
        private Process? owned = process;
        public Process Process => owned ?? throw new ObjectDisposedException(nameof(PausedProcess));
        private bool resumed;

        public void Resume()
        {
            var previous = PInvoke.ResumeThread(thread);
            Check(previous != uint.MaxValue, "Resume profiled application");
            if (previous != 1) { throw new InvalidOperationException("The profiled thread did not have exactly one suspend count."); }
            resumed = true;
        }

        public Process ReleaseProcess()
        {
            var result = Process;
            owned = null;
            return result;
        }

        public void Dispose()
        {
            try
            {
                if (owned is { } target)
                {
                    try { if (!resumed) { TerminateStartupProcess(target, attached: false); } }
                    finally { owned?.Dispose(); owned = null; }
                }
            }
            finally { thread.Dispose(); }
        }
    }
}
