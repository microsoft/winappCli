// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

// The CLI requires Windows 10+; suppress platform compat warnings for Debug APIs.
#pragma warning disable CA1416
// _logWriter lifetime is managed in RunDebugLoopAsync try/finally, not via IDisposable.
#pragma warning disable CA1001

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;
using Windows.Win32.System.Threading;

namespace WinApp.Cli.Services;

/// <summary>
/// Attaches to a running process via the Win32 Debug API and streams
/// <c>OutputDebugString</c> messages and first-chance exceptions to the console.
/// Only one debugger can attach to a process at a time — using this service
/// prevents other debuggers (Visual Studio, VS Code) from attaching.
/// The debugged process is terminated when the debug session ends (e.g., Ctrl+C)
/// or if the winapp process exits unexpectedly.
/// </summary>
internal sealed class DebugOutputService(IAnsiConsole console, ICrashDumpService crashDumpService, ILogger<DebugOutputService> logger) : IDebugOutputService
{
    // Well-known NTSTATUS / exception codes
    private const uint STATUS_BREAKPOINT = 0x80000003;
    private const uint STATUS_SINGLE_STEP = 0x80000004;
    private const uint STATUS_WX86_BREAKPOINT = 0x4000001F;
    private const uint THREAD_NAME_EXCEPTION = 0x406D1388;

    // Set by the debug loop when a crash dump is captured.
    private string? _crashDumpPath;

    // Log writer for verbose debug output (OutputDebugString, first-chance exceptions).
    private StreamWriter? _logWriter;
    private string? _logPath;

    // Saved first-chance exception contexts, keyed by thread — at first-chance time the thread
    // context still points to user code. By second-chance, XAML's FailFast has replaced the stack.
    // Keyed per thread so a handled exception on one thread never stands in for a crash on another.
    private readonly Dictionary<uint, SavedExceptionContext> _savedFirstChanceContexts = [];

    internal sealed record SavedExceptionContext(
        byte[] Context, uint ThreadId, int ExceptionCode, nuint ExceptionAddress,
        ulong StackPointer, byte[]? StackFingerprint);

    // Bytes read at a saved exception's stack pointer. If they are unchanged (and the thread has not
    // popped above that stack pointer), the throwing frame is still live on the stack.
    internal const int StackFingerprintSize = 64;

    // Offsets of the stack pointer inside CONTEXT: Rsp on x64, Sp on ARM64.
    private const int ContextRspOffsetX64 = 0x98;
    private const int ContextSpOffsetArm64 = 0x100;

    /// <inheritdoc/>
    public async Task<int> RunDebugLoopAsync(uint processId, CancellationToken cancellationToken, bool useSymbols = false, IReadOnlyList<string>? symbolSearchPaths = null)
    {
        // Create a log file alongside the dump directory for verbose debug output.
        var logDir = Path.Combine(Path.GetTempPath(), "winapp-dumps");
        Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, $"debug-{processId}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _logWriter = new StreamWriter(_logPath, append: false, Encoding.UTF8) { AutoFlush = true };

        try
        {
            // DebugActiveProcess + WaitForDebugEventEx must be called from the same thread,
            // so spin up a dedicated thread via Task.Run.
            var exitCode = await Task.Run(() => RunDebugLoop(processId, cancellationToken), cancellationToken);

            // Close the log writer before analysis appends to the same file.
            _logWriter.Dispose();
            _logWriter = null;

            // After the debug loop ends, analyze the crash dump if one was captured.
            if (_crashDumpPath != null)
            {
                await crashDumpService.AnalyzeDumpAsync(_crashDumpPath, _logPath!, useSymbols, symbolSearchPaths);
            }
            else
            {
                // No crash — show log path so users can find captured debug output.
                console.MarkupLine($"[dim]Full debug log:[/] {_logPath!.EscapeMarkup()}");
            }

            return exitCode;
        }
        finally
        {
            _logWriter?.Dispose();
            _logWriter = null;
        }
    }

    private int RunDebugLoop(uint processId, CancellationToken cancellationToken)
    {
        // If winapp crashes without cleanup, the OS terminates the debuggee.
        PInvoke.DebugSetProcessKillOnExit(true);

        if (!PInvoke.DebugActiveProcess(processId))
        {
            logger.LogError(
                "Failed to attach debugger to process {PID}. The process may have exited before the debugger could attach. " +
                "For short-lived apps, consider using --with-alias instead.",
                processId);
            return -1;
        }

        logger.LogDebug("Attached debugger to process {PID}.", processId);

        try
        {
            return DebugEventLoop(processId, cancellationToken);
        }
        finally
        {
            PInvoke.DebugActiveProcessStop(processId);
            logger.LogDebug("Detached debugger from process {PID}.", processId);
        }
    }

    private int DebugEventLoop(uint processId, CancellationToken cancellationToken)
    {
        int exitCode = -1;
        bool initialBreakpointSeen = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Poll with a short timeout so we can check the cancellation token.
            if (!PInvoke.WaitForDebugEventEx(out var debugEvent, 100))
            {
                continue;
            }

            var continueStatus = NTSTATUS.DBG_CONTINUE;

            switch (debugEvent.dwDebugEventCode)
            {
                case DEBUG_EVENT_CODE.OUTPUT_DEBUG_STRING_EVENT:
                    HandleOutputDebugString(in debugEvent);
                    break;

                case DEBUG_EVENT_CODE.EXCEPTION_DEBUG_EVENT:
                    HandleException(in debugEvent, ref initialBreakpointSeen, ref continueStatus);
                    break;

                case DEBUG_EVENT_CODE.EXIT_PROCESS_DEBUG_EVENT:
                    exitCode = unchecked((int)debugEvent.u.ExitProcess.dwExitCode);
                    PInvoke.ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, continueStatus);
                    return exitCode;

                case DEBUG_EVENT_CODE.EXIT_THREAD_DEBUG_EVENT:
                    HandleThreadExit(debugEvent.dwThreadId);
                    break;

                case DEBUG_EVENT_CODE.CREATE_PROCESS_DEBUG_EVENT:
                    CloseHandleSafe(debugEvent.u.CreateProcessInfo.hFile);
                    break;

                case DEBUG_EVENT_CODE.LOAD_DLL_DEBUG_EVENT:
                    CloseHandleSafe(debugEvent.u.LoadDll.hFile);
                    break;
            }

            PInvoke.ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, continueStatus);
        }

        return exitCode;
    }

    private unsafe void HandleOutputDebugString(in DEBUG_EVENT debugEvent)
    {
        var info = debugEvent.u.DebugString;
        int length = Math.Min((int)info.nDebugStringLength, 65534);
        if (length == 0)
        {
            return;
        }

        using var processHandle = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, debugEvent.dwProcessId);

        if (processHandle.IsInvalid)
        {
            return;
        }

        Span<byte> buffer = length <= 4096 ? stackalloc byte[length] : new byte[length];

        if (!PInvoke.ReadProcessMemory(processHandle, info.lpDebugStringData, buffer, out var bytesRead) || bytesRead == 0)
        {
            return;
        }

        var usable = buffer[..(int)bytesRead];
        string message = info.fUnicode != 0
            ? Encoding.Unicode.GetString(usable)
            : Encoding.Default.GetString(usable);

        message = message.TrimEnd('\0');

        if (!string.IsNullOrWhiteSpace(message))
        {
            // Trim trailing newline so log doesn't double-space the output.
            message = message.TrimEnd('\r', '\n');

            // Log file gets everything for detailed investigation.
            _logWriter?.WriteLine($"[Debug] {message}");

            // Console only shows app-specific messages — filter out OS/framework
            // noise from WinUI, COM, DirectX, and other system DLLs.
            if (!IsFrameworkNoise(message))
            {
                console.MarkupLine($"[dim][[Debug]][/] {message.EscapeMarkup()}");
            }
        }
    }

    internal unsafe void HandleException(
        in DEBUG_EVENT debugEvent,
        ref bool initialBreakpointSeen,
        ref NTSTATUS continueStatus)
    {
        var exInfo = debugEvent.u.Exception;
        uint code = unchecked((uint)exInfo.ExceptionRecord.ExceptionCode.Value);
        bool firstChance = exInfo.dwFirstChance != 0;

        // Suppress the initial breakpoint that the OS sends when we attach.
        if (!initialBreakpointSeen && (code is STATUS_BREAKPOINT or STATUS_WX86_BREAKPOINT))
        {
            initialBreakpointSeen = true;
            continueStatus = NTSTATUS.DBG_CONTINUE;
            return;
        }

        // Suppress single-step and thread-name exceptions — they are noise.
        if (code is STATUS_SINGLE_STEP or THREAD_NAME_EXCEPTION)
        {
            continueStatus = NTSTATUS.DBG_CONTINUE;
            return;
        }

        if (firstChance)
        {
            var name = GetExceptionName(code);
            var address = (nuint)exInfo.ExceptionRecord.ExceptionAddress;

            // Log file gets all first-chance exceptions.
            _logWriter?.WriteLine($"First-chance exception: {name} (0x{code:X8}) at 0x{address:X}");

            // Console only shows exceptions meaningful for crash diagnosis.
            // Skip WinUI/COM internal exceptions (0x40080201, 0x04242420, etc.)
            // that are caught and handled during normal framework operation.
            if (code is 0xE0434352 or 0xC0000005 or 0xC00000FD)
            {
                console.MarkupLine($"[yellow]First-chance exception:[/] {name} (0x{code:X8}) at 0x{address:X}");
            }

            // Save each thread's context for its critical exception — at first-chance time, the
            // context still points to user code. A later exception nested inside that one (CLR
            // wrapping the AV) keeps the original; one raised after the thread returned past the
            // original frame (it was handled) replaces it.
            if (code is 0xC0000005 or 0xC00000FD or 0xE0434352 or 0xE06D7363)
            {
                SaveFirstChanceContext(debugEvent.dwProcessId, debugEvent.dwThreadId, code, address);
            }

            // Stack Overflow is always fatal in .NET — no second-chance will follow.
            // Capture the dump immediately on first-chance, describing this exception.
            if (code is 0xC00000FD && _crashDumpPath == null)
            {
                console.MarkupLine($"[red]Crash:[/] {name} (0x{code:X8}) at 0x{address:X}");
                WriteCrashDump(debugEvent, code, address, ReadExceptionParameters(exInfo.ExceptionRecord),
                    useEarlierFirstChanceContext: false);
            }
        }
        else
        {
            // Second-chance exception — the process is about to crash.
            // Only capture if we don't already have a dump (e.g., Stack Overflow
            // already captured at first-chance time).
            var name = GetExceptionName(code);
            var address = (nuint)exInfo.ExceptionRecord.ExceptionAddress;
            _logWriter?.WriteLine($"Second-chance exception (crash): {name} (0x{code:X8}) at 0x{address:X}");
            console.MarkupLine($"[red]Crash:[/] {name} (0x{code:X8}) at 0x{address:X}");

            if (_crashDumpPath == null)
            {
                // Forward the terminating exception's parameters. For a stowed exception
                // (0xC000027B) these point at the stowed-exception array that WinUI triage reads.
                WriteCrashDump(debugEvent, code, address, ReadExceptionParameters(exInfo.ExceptionRecord),
                    useEarlierFirstChanceContext: true);
            }
        }

        // Let the target's own exception handling run. For second-chance exceptions
        // (firstChance == false), this causes the OS to terminate the process — correct
        // behavior for a passive listener that doesn't handle exceptions itself.
        continueStatus = NTSTATUS.DBG_EXCEPTION_NOT_HANDLED;
    }

    private static unsafe void CloseHandleSafe(HANDLE handle)
    {
        if (!handle.IsNull && handle.Value != (void*)-1)
        {
            PInvoke.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Reads the parameters (<c>ExceptionInformation</c>) from a debug-event exception record. For a
    /// stowed exception (<c>0xC000027B</c>) element 0 is the stowed-exception array pointer and element
    /// 1 is the count; these are copied verbatim into the dump so WinUI triage can locate them.
    /// </summary>
    internal static unsafe nuint[]? ReadExceptionParameters(in EXCEPTION_RECORD record)
    {
        var count = (int)Math.Min(record.NumberParameters, (uint)record.ExceptionInformation.Length);
        if (count <= 0)
        {
            return null;
        }

        var parameters = new nuint[count];
        var source = record.ExceptionInformation.AsReadOnlySpan();
        for (var i = 0; i < count; i++)
        {
            parameters[i] = source[i];
        }
        return parameters;
    }

    /// <summary>
    /// Maps the host CPU architecture to the <c>CONTEXT</c> flags requested from
    /// <c>GetThreadContext</c>. Extracted as a pure function so both arms are unit-testable without a
    /// live thread handle.
    /// </summary>
    internal static CONTEXT_FLAGS GetContextFlags(Architecture architecture) => architecture switch
    {
        Architecture.Arm64 => CONTEXT_FLAGS.CONTEXT_FULL_ARM64,
        _ => CONTEXT_FLAGS.CONTEXT_FULL_AMD64,
    };

    /// <summary>
    /// Writes the crash dump using the crashing thread as the primary crash context. When
    /// <paramref name="useEarlierFirstChanceContext"/> is set and that thread saved a first-chance
    /// context whose frame is still live on the stack, that context is used so ClrMD still recovers
    /// the managed user frames; otherwise the crashing thread's current context and this exception's
    /// own record are used. Other saved first-chance contexts are only logged as supplemental.
    /// </summary>
    private void WriteCrashDump(in DEBUG_EVENT debugEvent, uint code, nuint address, nuint[]? parameters, bool useEarlierFirstChanceContext)
    {
        var processId = debugEvent.dwProcessId;
        var crashThreadId = debugEvent.dwThreadId;
        var current = CaptureExceptionContext(processId, crashThreadId, code, address);

        SavedExceptionContext? primary = current;
        if (useEarlierFirstChanceContext && _savedFirstChanceContexts.TryGetValue(crashThreadId, out var saved))
        {
            if (IsSavedFrameLive(processId, saved, current))
            {
                primary = saved;
                _logWriter?.WriteLine($"[CrashDump] Using first-chance context saved on crashing thread {crashThreadId} (0x{(uint)saved.ExceptionCode:X8}) at 0x{saved.ExceptionAddress:X}");
            }
            else
            {
                _logWriter?.WriteLine($"[CrashDump] Supplemental (not the crash): earlier first-chance 0x{(uint)saved.ExceptionCode:X8} on crashing thread {crashThreadId} at 0x{saved.ExceptionAddress:X} was already unwound");
            }
        }

        if (ReferenceEquals(primary, current) && current != null)
        {
            _logWriter?.WriteLine($"[CrashDump] Using crashing thread {crashThreadId} context for 0x{code:X8} at 0x{address:X}");
        }

        foreach (var other in _savedFirstChanceContexts.Values)
        {
            if (other.ThreadId != crashThreadId)
            {
                _logWriter?.WriteLine($"[CrashDump] Supplemental (not the crash): earlier first-chance 0x{(uint)other.ExceptionCode:X8} on thread {other.ThreadId} at 0x{other.ExceptionAddress:X}");
            }
        }

        _crashDumpPath = crashDumpService.WriteMiniDump(
            processId,
            primary?.Context, primary?.ThreadId ?? 0,
            primary?.ExceptionCode ?? 0, primary?.ExceptionAddress ?? 0,
            unchecked((int)code), address, parameters);
    }

    /// <summary>
    /// Records a thread's critical first-chance exception. An already-saved exception on the same
    /// thread is kept while its frame is still live (the new one is nested inside it), and replaced
    /// once the thread has returned past it (it was handled).
    /// </summary>
    private void SaveFirstChanceContext(uint processId, uint threadId, uint code, nuint address)
    {
        var current = CaptureExceptionContext(processId, threadId, code, address);
        if (current == null)
        {
            return;
        }

        if (_savedFirstChanceContexts.TryGetValue(threadId, out var saved))
        {
            if (IsSavedFrameLive(processId, saved, current))
            {
                return;
            }

            _logWriter?.WriteLine($"[CrashDump] Earlier first-chance 0x{(uint)saved.ExceptionCode:X8} on thread {threadId} was unwound; replacing it");
        }

        _savedFirstChanceContexts[threadId] = current;
        _logWriter?.WriteLine($"[CrashDump] Saved first-chance context for thread {threadId} (0x{code:X8}) at 0x{address:X}");
    }

    // Windows can reuse a thread id, so an exited thread's saved context must not outlive it.
    internal void HandleThreadExit(uint threadId) => _savedFirstChanceContexts.Remove(threadId);

    private SavedExceptionContext? CaptureExceptionContext(uint processId, uint threadId, uint code, nuint address)
    {
        if (CaptureThreadContext(threadId) is not { } context)
        {
            return null;
        }

        var stackPointer = GetStackPointer(context, RuntimeInformation.ProcessArchitecture);
        return new SavedExceptionContext(context, threadId, unchecked((int)code), address,
            stackPointer, ReadStackBytes(processId, stackPointer));
    }

    private bool IsSavedFrameLive(uint processId, SavedExceptionContext saved, SavedExceptionContext? current) =>
        current != null &&
        IsFrameLive(saved.StackPointer, saved.StackFingerprint, current.StackPointer, ReadStackBytes(processId, saved.StackPointer));

    /// <summary>
    /// Returns true when the frame that raised a saved exception is still on the thread's stack: the
    /// thread is at or below the saved stack pointer (stacks grow down) and the bytes at that stack
    /// pointer are unchanged. When either cannot be measured, the frame is treated as gone so the
    /// crash's own context is preferred.
    /// </summary>
    internal static bool IsFrameLive(ulong savedStackPointer, byte[]? savedFingerprint, ulong currentStackPointer, byte[]? currentBytesAtSavedStackPointer)
    {
        if (savedStackPointer == 0 || currentStackPointer == 0 || currentStackPointer > savedStackPointer)
        {
            return false;
        }

        return savedFingerprint != null && currentBytesAtSavedStackPointer != null &&
            savedFingerprint.AsSpan().SequenceEqual(currentBytesAtSavedStackPointer);
    }

    /// <summary>Reads the stack pointer from raw <c>CONTEXT</c> bytes for the given architecture.</summary>
    internal static ulong GetStackPointer(ReadOnlySpan<byte> context, Architecture architecture)
    {
        var offset = architecture == Architecture.Arm64 ? ContextSpOffsetArm64 : ContextRspOffsetX64;
        return context.Length >= offset + sizeof(ulong)
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(context[offset..])
            : 0;
    }

    private byte[]? ReadStackBytes(uint processId, ulong address)
    {
        if (address == 0)
        {
            return null;
        }

        using var processHandle = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, processId);
        if (processHandle.IsInvalid)
        {
            return null;
        }

        var buffer = new byte[StackFingerprintSize];
        unsafe
        {
            if (!PInvoke.ReadProcessMemory(processHandle, (void*)address, buffer, out var bytesRead) || bytesRead != (nuint)buffer.Length)
            {
                _logWriter?.WriteLine($"[CrashDump] Could not read stack at 0x{address:X} in process {processId}");
                return null;
            }
        }
        return buffer;
    }

    /// <summary>
    /// Reads a debuggee thread's current <c>CONTEXT</c>. The thread is suspended while its debug
    /// event is being handled, so the context reflects the exception site.
    /// </summary>
    private unsafe byte[]? CaptureThreadContext(uint threadId)
    {
        using var threadHandle = PInvoke.OpenThread_SafeHandle(
            THREAD_ACCESS_RIGHTS.THREAD_GET_CONTEXT | THREAD_ACCESS_RIGHTS.THREAD_QUERY_INFORMATION,
            false, threadId);

        if (threadHandle.IsInvalid)
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            _logWriter?.WriteLine($"[CrashDump] OpenThread failed for thread {threadId}: error {err}");
            return null;
        }

        // CONTEXT must be 16-byte aligned on x64. Allocate on native heap to guarantee alignment.
        var contextSize = sizeof(CONTEXT);
        var pContext = (CONTEXT*)NativeMemory.AlignedAlloc((nuint)contextSize, 16);
        try
        {
            NativeMemory.Clear(pContext, (nuint)contextSize);
            pContext->ContextFlags = GetContextFlags(RuntimeInformation.ProcessArchitecture);

            if (PInvoke.GetThreadContext(new HANDLE(threadHandle.DangerousGetHandle()), pContext))
            {
                var context = new byte[contextSize];
                fixed (byte* p = context)
                {
                    Buffer.MemoryCopy(pContext, p, contextSize, contextSize);
                }
                return context;
            }

            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            _logWriter?.WriteLine($"[CrashDump] GetThreadContext failed for thread {threadId}: error {error}");
            return null;
        }
        finally
        {
            NativeMemory.AlignedFree(pContext);
        }
    }

    internal static string GetExceptionName(uint code) => code switch
    {
        0xC0000005 => "Access Violation",
        0xC00000FD => "Stack Overflow",
        0xC0000094 => "Integer Division By Zero",
        0xC0000017 => "No Memory",
        0xC000001D => "Illegal Instruction",
        0xC0000025 => "Non-Continuable Exception",
        0xC000008C => "Array Bounds Exceeded",
        0xC0000135 => "DLL Not Found",
        0xC0000142 => "DLL Initialization Failed",
        STATUS_BREAKPOINT => "Breakpoint",
        STATUS_SINGLE_STEP => "Single Step",
        0xE06D7363 => "C++ Exception",
        0xE0434352 => "CLR Exception",
        _ => "Exception",
    };

    /// <summary>
    /// Returns true if the debug message is internal OS/framework noise
    /// rather than an app-specific debug message worth showing on the console.
    /// </summary>
    internal static bool IsFrameworkNoise(string message)
    {
        // Windows OS source paths (onecore, onecoreuap, minkernel, etc.)
        if (message.StartsWith("onecore\\", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("onecoreuap\\", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("minkernel\\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // WinRT/COM internal trace markers
        if (message.Contains("ReturnHr(", StringComparison.Ordinal) ||
            message.Contains("LogHr(", StringComparison.Ordinal) ||
            message.Contains("ReturnNt(", StringComparison.Ordinal))
        {
            return true;
        }

        // Windows SDK build paths (Azure DevOps build agent)
        if (message.StartsWith("C:\\__w\\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Framework DLL WIL/HRESULT trace format: "DllName.dll!0x..." or "DllName.dll!FuncName"
        if (IsFrameworkDllTrace(message))
        {
            return true;
        }

        // Common framework HRESULT noise
        if (message.StartsWith("E_INVALIDARG", StringComparison.Ordinal) ||
            message.StartsWith("E_FAIL", StringComparison.Ordinal) ||
            message.StartsWith("HRESULT:", StringComparison.Ordinal) ||
            message.StartsWith("hr = ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the message looks like a framework DLL debug trace
    /// (e.g., "Microsoft.UI.Xaml.dll!0x..." or "twinapi.appcore.dll!SomeFunc").
    /// </summary>
    internal static bool IsFrameworkDllTrace(string message)
    {
        var bangIndex = message.IndexOf('!');
        if (bangIndex < 5)
        {
            return false;
        }

        var beforeBang = message.AsSpan(0, bangIndex);
        if (!beforeBang.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (beforeBang.StartsWith("Microsoft.UI.", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("Microsoft.Web.", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("Microsoft.WinUI.", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("twinapi", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("dxgi", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("d3d", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("d2d", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("combase", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("oleaut32", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("ntdll", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("kernelbase", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("kernel32", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("WinAppRuntime", StringComparison.OrdinalIgnoreCase) ||
            beforeBang.StartsWith("MRM", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
