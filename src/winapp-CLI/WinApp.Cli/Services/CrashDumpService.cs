// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

#pragma warning disable CA1416

using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.Utilities;
using Microsoft.Diagnostics.Runtime.Utilities.DbgEng;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;
using Windows.Win32.System.Threading;

namespace WinApp.Cli.Services;

/// <summary>
/// Writes minidumps for crashed processes and analyzes them using ClrMD
/// to produce human-readable crash reports with managed exception details and stack traces.
/// </summary>
internal sealed class CrashDumpService(IAnsiConsole console, ILogger<CrashDumpService> logger, IXamlTriageService xamlTriageService) : ICrashDumpService
{
    private static readonly string DumpDirectory = Path.Combine(Path.GetTempPath(), "winapp-dumps");

    // For testing only — overrides the ClrMD analysis boundary so the AnalyzeDumpAsync orchestration
    // (managed vs native fallback, WinUI-triage branch, error handling) can be exercised without a
    // real dump. Null means use the real ClrMD analyzer.
    internal Func<string, IReadOnlyList<string>?, (string Summary, string Details, bool IsWinUi)>? ClrMdAnalyzerOverride { get; set; }

    // For testing only — overrides the DbgEng native-fallback boundary. Null means use the real engine.
    internal Func<string, bool, (string Summary, string Details)>? DbgEngAnalyzerOverride { get; set; }

    // For testing only — the symbol-file download boundary used by DownloadSymbolsForModules.
    // Given a symbol-server URL and a destination path, streams the PDB straight to that path
    // (constant memory) and returns true, or returns false when the file is unavailable. Defaults to
    // a real HTTPS GET from the Microsoft Symbol Server.
    internal static Func<string, string, bool> SymbolFileDownloader { get; set; } = DefaultDownloadSymbolFile;

    // For testing only — the on-disk PDB symbol cache directory. Defaults to the shared %TEMP%\symbols
    // cache; tests point it at an isolated temp dir so they never read or mutate the real cache.
    internal static string SymbolCacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "symbols");

    /// <inheritdoc/>
    public unsafe string? WriteMiniDump(uint processId,
        byte[]? savedContext, uint savedThreadId,
        int savedExceptionCode, nuint savedExceptionAddress,
        int crashExceptionCode = 0, nuint crashExceptionAddress = 0,
        nuint[]? crashExceptionParameters = null)
    {
        try
        {
            Directory.CreateDirectory(DumpDirectory);
            var dumpPath = Path.Combine(DumpDirectory, $"crash-{processId}-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");

            using var processHandle = PInvoke.OpenProcess_SafeHandle(
                PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ | PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE,
                false, processId);

            if (processHandle.IsInvalid)
            {
                logger.LogError("Failed to open process {PID} for dump capture.", processId);
                return null;
            }

            using var fileHandle = File.Create(dumpPath);

            var dumpType =
                MINIDUMP_TYPE.MiniDumpWithFullMemory |
                MINIDUMP_TYPE.MiniDumpWithHandleData |
                MINIDUMP_TYPE.MiniDumpWithUnloadedModules |
                MINIDUMP_TYPE.MiniDumpWithFullMemoryInfo |
                MINIDUMP_TYPE.MiniDumpWithThreadInfo;

            BOOL success;

            if (savedContext != null)
            {
                // Prefer the terminating stowed exception (0xC000027B) for the dump's exception
                // record: its parameters point at the stowed-exception array that WinUI triage
                // (!xamlstowed) reads via $exr_param0/$exr_param1. The thread CONTEXT is whatever
                // the caller supplied: the crashing thread's first-chance context when that frame is
                // still live (so ClrMD recovers the managed user frames that originally threw), or
                // the crash's own context when it is not.
                var (recordCode, recordAddress, useCrashParameters) = SelectExceptionRecord(
                    savedExceptionCode, savedExceptionAddress,
                    crashExceptionCode, crashExceptionAddress, crashExceptionParameters);

                logger.LogDebug("Writing dump (thread {ThreadId}); exception record code 0x{Code:X8}{Params}.",
                    savedThreadId, recordCode, useCrashParameters ? " (with crash parameters)" : string.Empty);

                // CONTEXT must be 16-byte aligned on x64/ARM64. The saved byte[] from a managed
                // array doesn't guarantee this, so copy into an aligned native buffer.
                var pContext = (CONTEXT*)NativeMemory.AlignedAlloc((nuint)savedContext.Length, 16);
                try
                {
                    fixed (byte* pSaved = savedContext)
                    {
                        Buffer.MemoryCopy(pSaved, pContext, savedContext.Length, savedContext.Length);
                    }

                    var exRecord = new EXCEPTION_RECORD
                    {
                        ExceptionCode = new NTSTATUS(recordCode),
                        ExceptionAddress = (void*)recordAddress,
                        ExceptionRecord = null,
                    };

                    if (useCrashParameters)
                    {
                        // Copy the crash's parameters (e.g., the stowed-exception array pointer + count,
                        // or a fail-fast code) so the dump's record mirrors the live exception.
                        var n = Math.Min(crashExceptionParameters!.Length, exRecord.ExceptionInformation.Length);
                        var slot = exRecord.ExceptionInformation.AsSpan();
                        for (var i = 0; i < n; i++)
                        {
                            slot[i] = crashExceptionParameters[i];
                        }
                        exRecord.NumberParameters = (uint)n;
                    }

                    var exPtrs = new EXCEPTION_POINTERS
                    {
                        ExceptionRecord = &exRecord,
                        ContextRecord = pContext,
                    };

                    var exInfo = new MINIDUMP_EXCEPTION_INFORMATION
                    {
                        ThreadId = savedThreadId,
                        ExceptionPointers = &exPtrs,
                        ClientPointers = false,
                    };

                    success = PInvoke.MiniDumpWriteDump(
                        processHandle,
                        processId,
                        fileHandle.SafeFileHandle,
                        dumpType,
                        exInfo,
                        UserStreamParam: null,
                        CallbackParam: null);
                }
                finally
                {
                    NativeMemory.AlignedFree(pContext);
                }
            }
            else
            {
                success = PInvoke.MiniDumpWriteDump(
                    processHandle,
                    processId,
                    fileHandle.SafeFileHandle,
                    dumpType,
                    ExceptionParam: null,
                    UserStreamParam: null,
                    CallbackParam: null);
            }

            if (!success)
            {
                var error = Marshal.GetLastWin32Error();
                logger.LogError("MiniDumpWriteDump failed with error code {Error}.", error);
                return null;
            }

            logger.LogDebug("Crash dump written to {DumpPath} ({Size} bytes).", dumpPath, fileHandle.Length);
            return dumpPath;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write crash dump for process {PID}.", processId);
            return null;
        }
    }

    /// <summary>
    /// Chooses which exception the dump's exception record should describe. A terminating stowed
    /// exception (<c>0xC000027B</c>) carrying parameters is preferred so WinUI triage can locate the
    /// stowed-exception array; otherwise the saved exception is used. The crash's parameters are
    /// copied whenever the chosen record is the crash itself (stowed, or the saved exception is the
    /// crash). Exposed internally for testing the selection logic without writing a real dump.
    /// </summary>
    internal static (int Code, nuint Address, bool UseCrashParameters) SelectExceptionRecord(
        int savedExceptionCode, nuint savedExceptionAddress,
        int crashExceptionCode, nuint crashExceptionAddress, nuint[]? crashExceptionParameters)
    {
        const int statusStowedException = unchecked((int)0xC000027B);
        if (crashExceptionParameters is not { Length: > 0 })
        {
            return (savedExceptionCode, savedExceptionAddress, false);
        }

        if (crashExceptionCode == statusStowedException)
        {
            return (crashExceptionCode, crashExceptionAddress, true);
        }

        var savedIsCrash = savedExceptionCode == crashExceptionCode && savedExceptionAddress == crashExceptionAddress;
        return (savedExceptionCode, savedExceptionAddress, savedIsCrash);
    }

    /// <inheritdoc/>
    public async Task AnalyzeDumpAsync(string dumpPath, string logPath, bool useSymbols = false, IReadOnlyList<string>? symbolSearchPaths = null)
    {
        console.MarkupLine("[dim]Analyzing crash dump...[/]");

        try
        {
            var (summary, details, isWinUi) = await Task.Run(() => (ClrMdAnalyzerOverride ?? AnalyzeWithClrMD)(dumpPath, symbolSearchPaths));

            // WinUI triage pass — auto-enabled when Microsoft.UI.Xaml.dll is in the dump's
            // module list. Recovers the stowed exception (0xC000027B) and the XAML dispatch
            // chain that the ClrMD/DbgEng passes alone cannot surface.
            string? xamlTriage = null;
            XamlTriageResult? xamlTriageResult = null;
            if (isWinUi)
            {
                console.MarkupLine(useSymbols
                    ? "[dim]Running WinUI stowed-exception triage (first run may download debugger components and symbols; this can take a few minutes)...[/]"
                    : "[dim]Running WinUI stowed-exception triage (first run may download debugger components)...[/]");
                xamlTriageResult = await RunXamlTriageGuardedAsync(dumpPath, useSymbols);
                xamlTriage = xamlTriageResult.LogText;
            }

            // ClrMD found managed exception — no need for native fallback
            if (!string.IsNullOrWhiteSpace(summary))
            {
                var managedLog = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(details))
                {
                    managedLog.AppendLine(details);
                }

                if (!string.IsNullOrWhiteSpace(xamlTriage))
                {
                    managedLog.AppendLine();
                    managedLog.AppendLine(xamlTriage);
                }

                if (managedLog.Length > 0)
                {
                    await File.AppendAllTextAsync(logPath,
                        $"\n\n=== Crash Analysis ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===\n{managedLog}\n");
                }

                console.WriteLine();
                console.MarkupLine("[red]========== CRASH DETECTED ==========[/]");
                console.WriteLine();
                console.MarkupLine("[red][[CRASH ANALYSIS]][/]");
                console.WriteLine(summary);
                console.MarkupLine("[red]=====================================[/]");
                WriteXamlTriageConsole(xamlTriageResult);

                console.MarkupLine($"[dim]Crash dump:[/] {dumpPath.EscapeMarkup()}");
                console.MarkupLine($"[dim]Full debug log:[/] {logPath.EscapeMarkup()}");
                return;
            }

            // No managed info — fallback to DbgEng for native stack trace
            if (useSymbols)
            {
                console.MarkupLine("[dim]Downloading symbols (first run may take a few minutes)...[/]");
            }

            var (nativeSummary, nativeDetails) = await Task.Run(() => (DbgEngAnalyzerOverride ?? AnalyzeWithDbgEng)(dumpPath, useSymbols));

            var allDetails = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(details))
            {
                allDetails.AppendLine(details);
            }

            if (!string.IsNullOrWhiteSpace(nativeDetails))
            {
                allDetails.AppendLine(nativeDetails);
            }

            if (!string.IsNullOrWhiteSpace(xamlTriage))
            {
                allDetails.AppendLine();
                allDetails.AppendLine(xamlTriage);
            }

            if (allDetails.Length > 0)
            {
                await File.AppendAllTextAsync(logPath,
                    $"\n\n=== Crash Analysis ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===\n{allDetails}\n");
            }

            console.WriteLine();
            console.MarkupLine("[red]========== CRASH DETECTED ==========[/]");

            if (!string.IsNullOrWhiteSpace(nativeSummary))
            {
                console.WriteLine();
                console.MarkupLine("[red][[CRASH ANALYSIS (native)]][/]");
                console.WriteLine(nativeSummary);
                console.MarkupLine("[red]=====================================[/]");
            }

            WriteXamlTriageConsole(xamlTriageResult);

            console.MarkupLine($"[dim]Crash dump:[/] {dumpPath.EscapeMarkup()}");
            console.MarkupLine($"[dim]Full debug log:[/] {logPath.EscapeMarkup()}");
            if (!useSymbols && !string.IsNullOrWhiteSpace(nativeSummary))
            {
                console.MarkupLine("[dim]Tip: Re-run with [bold]--symbols[/] for resolved function names in native stacks.[/]");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Crash analysis failed.");
            console.MarkupLine($"\n[red]Crash dump:[/] {dumpPath.EscapeMarkup()}");
            console.MarkupLine($"[dim]Analysis failed. Open in WinDbg:[/] [blue]windbg -z \"{dumpPath.EscapeMarkup()}\"[/]");
            console.MarkupLine($"[dim]Full debug log:[/] {logPath.EscapeMarkup()}");
        }
    }

    /// <summary>
    /// Invokes the WinUI triage service, guaranteeing the crash-analysis flow is never derailed by a
    /// triage failure. <see cref="IXamlTriageService.TryAnalyzeAsync"/> is contracted to fail open, but
    /// this local guard is defense-in-depth: any escaping exception (e.g. an internal <c>HttpClient</c>
    /// timeout surfacing as <see cref="OperationCanceledException"/> on a slow first-run download) is
    /// logged and swallowed so the already-computed managed/native crash stack is still emitted rather
    /// than discarded by the outer handler as "Analysis failed."
    /// </summary>
    internal async Task<XamlTriageResult> RunXamlTriageGuardedAsync(string dumpPath, bool useSymbols)
    {
        try
        {
            return await xamlTriageService.TryAnalyzeAsync(dumpPath, useSymbols);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WinUI triage pass failed; continuing with the standard crash analysis.");
            return XamlTriageResult.None;
        }
    }

    /// <summary>
    /// Surfaces an accurate WinUI-triage line in the console based on the outcome: the headline verdict
    /// (or a "written to the debug log" pointer) on success, a distinct "skipped" note when tooling was
    /// unavailable, and nothing when triage was not applicable. Avoids the previous behavior of always
    /// claiming triage was written to the log even when it was skipped.
    /// </summary>
    private void WriteXamlTriageConsole(XamlTriageResult? result)
    {
        if (result == null)
        {
            return;
        }

        switch (result.Outcome)
        {
            case XamlTriageOutcome.Succeeded:
                if (!string.IsNullOrWhiteSpace(result.Verdict))
                {
                    console.MarkupLine($"[yellow]WinUI stowed exception:[/] {result.Verdict.EscapeMarkup()}");
                }

                console.MarkupLine("[dim]WinUI stowed-exception triage written to the debug log.[/]");
                break;
            case XamlTriageOutcome.Skipped:
                console.MarkupLine("[dim]WinUI stowed-exception triage was skipped (see the debug log for details).[/]");
                break;
            case XamlTriageOutcome.None:
            default:
                break;
        }
    }

    private (string Summary, string Details, bool IsWinUi) AnalyzeWithClrMD(string dumpPath, IReadOnlyList<string>? symbolSearchPaths)
    {
        using var dt = DataTarget.LoadDump(dumpPath);

        // Detect WinUI so the triage pass can be auto-enabled. Only meaningful when the dump
        // matches the host architecture (the triage engine/extension are host-arch native).
        var archMatches = dt.DataReader.Architecture == RuntimeInformation.ProcessArchitecture;
        var isWinUi = archMatches && DumpHasWinUiModule(dt);

        // Cross-architecture analysis is not supported (e.g., ARM64 winapp analyzing x64 dump).
        // ClrMD requires a matching-architecture DAC DLL that cannot be loaded cross-arch.
        if (dt.DataReader.Architecture != RuntimeInformation.ProcessArchitecture)
        {
            return (
                $"Cross-architecture crash dump (target: {dt.DataReader.Architecture}, host: {RuntimeInformation.ProcessArchitecture}).\n" +
                "Automatic analysis is not supported for cross-architecture dumps.\n" +
                "Open the dump in WinDbg for full analysis.",
                $"Skipped analysis: dump architecture ({dt.DataReader.Architecture}) does not match host ({RuntimeInformation.ProcessArchitecture}).",
                isWinUi);
        }

        if (dt.ClrVersions.Length == 0)
        {
            return (string.Empty, "No CLR runtime found in dump (native-only crash).", isWinUi);
        }

        ClrRuntime runtime;
        try
        {
            runtime = dt.ClrVersions[0].CreateRuntime();
        }
        catch (ClrDiagnosticsException ex) when (ex.InnerException is BadImageFormatException)
        {
            // ClrMD cannot load the DAC DLL — typically a cross-architecture dump
            // (e.g., x64 .NET app running under emulation on ARM64 Windows).
            return (
                "Unable to analyze crash dump: the .NET runtime in the dump does not match " +
                $"the host architecture ({RuntimeInformation.ProcessArchitecture}).\n" +
                "This typically happens when debugging an x64 app under ARM64 emulation.\n" +
                "Open the dump in WinDbg for full analysis.",
                $"ClrMD DAC load failed: {ex.Message}",
                isWinUi);
        }

        using var _ = runtime;
        using var pdbResolver = new PdbSourceResolver(symbolSearchPaths, runtime, logger);

        var summary = new StringBuilder();
        var details = new StringBuilder();

        details.AppendLine($"CLR Version: {dt.ClrVersions[0].Version}");
        details.AppendLine($"Target Architecture: {dt.DataReader.Architecture}");

        // 1. Check threads for CurrentException
        ClrException? exception = null;
        foreach (var thread in runtime.Threads)
        {
            if (thread.CurrentException != null)
            {
                exception = thread.CurrentException;
                break;
            }
        }

        // 2. WinUI's FailFast clears the thread exception — scan the heap as fallback.
        //    Skip pre-allocated singletons (OOM, SOE, EEE) that have no stack trace.
        //    Take the last match — Gen0 (most recently allocated) objects appear later
        //    in the enumeration, so the crash-causing exception is more likely at the end.
        //    Note: full-memory dumps can be hundreds of MB; heap enumeration is O(heap size)
        //    but typically completes in a few seconds even for large dumps.
        if (exception == null)
        {
            foreach (var seg in runtime.Heap.Segments)
            {
                foreach (var obj in seg.EnumerateObjects())
                {
                    if (obj.Type is not { IsException: true })
                    {
                        continue;
                    }

                    var candidate = obj.AsException();
                    if (candidate?.StackTrace.Length > 0)
                    {
                        exception = candidate;
                    }
                }
            }
        }

        if (exception != null)
        {
            FormatException(exception, summary, details, pdbResolver);
        }

        // 3. No exception found — check for Stack Overflow by finding a thread with
        //    a very deep stack (hundreds of repeated frames from infinite recursion).
        //    Materialize frames once per thread to avoid double enumeration.
        if (exception == null)
        {
            List<ClrStackFrame>? deepestFrames = null;
            ClrThread? deepest = null;

            foreach (var thread in runtime.Threads)
            {
                var frames = thread.EnumerateStackTrace().Where(f => f.Method != null).ToList();
                if (frames.Count > (deepestFrames?.Count ?? 0))
                {
                    deepestFrames = frames;
                    deepest = thread;
                }
            }

            if (deepest != null && deepestFrames != null && deepestFrames.Count > 100)
            {
                AppendStackOverflowSummary(
                    deepest.OSThreadId,
                    deepestFrames,
                    f => $"{f.Method!.Type?.Name}.{f.Method!.Name}",
                    f => pdbResolver.GetSourceLocation(f),
                    summary);
            }
        }

        // All threads in detailed log
        details.AppendLine("\n=== All Threads ===");
        foreach (var thread in runtime.Threads)
        {
            var frames = thread.EnumerateStackTrace().ToList();
            if (frames.Count == 0)
            {
                continue;
            }

            details.AppendLine($"\nThread {thread.OSThreadId} (Managed ID: {thread.ManagedThreadId}):");
            if (thread.CurrentException != null)
            {
                details.AppendLine($"  ** Exception: {thread.CurrentException.Type?.Name} **");
            }

            foreach (var frame in frames)
            {
                if (frame.Method != null)
                {
                    var sourceInfo = pdbResolver.GetSourceLocation(frame);
                    var frameLine = $"  {frame.Method.Type?.Module?.Name}!{frame.Method.Type?.Name}.{frame.Method.Name}";
                    if (sourceInfo != null)
                    {
                        frameLine += $" in {sourceInfo}";
                    }
                    details.AppendLine(frameLine);
                }
            }
        }

        return (summary.ToString().Trim(), details.ToString().Trim(), isWinUi);
    }

    /// <summary>
    /// Formats the "stack overflow (deep recursion)" summary from a thread's frames, collapsing runs of
    /// identical frames and capping the displayed count at 15. The frame name and source location are
    /// resolved lazily — via <paramref name="nameSelector"/> and <paramref name="sourceResolver"/> — as
    /// each frame is visited, so resolution stops once the 15-frame cap is reached rather than eagerly
    /// resolving every frame of a (potentially many-thousand-frame) overflow. This matches the original
    /// in-situ loop and keeps a corrupt frame beyond the cap from ever being touched. Generic over the
    /// frame type so it is unit-testable without a live stack-overflow dump.
    /// </summary>
    internal static void AppendStackOverflowSummary<TFrame>(
        ulong osThreadId,
        IReadOnlyList<TFrame> frames,
        Func<TFrame, string> nameSelector,
        Func<TFrame, string?> sourceResolver,
        StringBuilder summary)
    {
        summary.AppendLine("Exception: Stack Overflow (deep recursion detected)");
        summary.AppendLine($"Thread: {osThreadId} ({frames.Count} managed frames)");
        summary.AppendLine();
        summary.AppendLine("Stack:");
        string? lastFrame = null;
        var repeatCount = 0;
        var displayed = 0;

        foreach (var frame in frames)
        {
            if (displayed >= 15)
            {
                break;
            }

            var name = nameSelector(frame);
            var sourceInfo = sourceResolver(frame);
            var displayName = sourceInfo != null ? $"{name} in {sourceInfo}" : name;

            if (name == lastFrame)
            {
                repeatCount++;
                continue;
            }

            if (repeatCount > 0)
            {
                summary.AppendLine($"  ... (repeated {repeatCount} more times)");
                displayed++;
            }

            if (displayed >= 15)
            {
                break;
            }

            summary.AppendLine($"  {displayName}");
            displayed++;
            repeatCount = 0;
            lastFrame = name;
        }

        if (repeatCount > 0 && displayed < 15)
        {
            summary.AppendLine($"  ... (repeated {repeatCount} more times)");
        }
    }

    /// <summary>
    /// Returns true when the dump's loaded module list contains Microsoft.UI.Xaml.dll,
    /// indicating a WinUI (Windows App SDK) app whose crashes benefit from the triage pass.
    /// </summary>
    private static bool DumpHasWinUiModule(DataTarget dt)
    {
        try
        {
            return EnumerateHasWinUiModule(dt.EnumerateModules().Select(m => m.FileName));
        }
        catch (Exception)
        {
            // Module enumeration is best-effort; absence simply disables the triage pass.
        }

        return false;
    }

    /// <summary>Returns true if the file name is Microsoft.UI.Xaml.dll (case-insensitive).</summary>
    internal static bool IsWinUiModuleFileName(string? fileName) =>
        string.Equals(Path.GetFileName(fileName), "Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true when any module file name in the sequence is Microsoft.UI.Xaml.dll.</summary>
    internal static bool EnumerateHasWinUiModule(IEnumerable<string?> moduleFileNames)
    {
        foreach (var fileName in moduleFileNames)
        {
            if (IsWinUiModuleFileName(fileName))
            {
                return true;
            }
        }

        return false;
    }

    private static void FormatException(ClrException ex, StringBuilder summary, StringBuilder details, PdbSourceResolver pdbResolver)
    {
        // Console summary
        summary.AppendLine($"Exception: {ex.Type?.Name}");
        if (!string.IsNullOrEmpty(ex.Message))
        {
            summary.AppendLine($"Message: {ex.Message}");
        }

        var inner = ex.Inner;
        while (inner != null)
        {
            summary.AppendLine($"Inner: {inner.Type?.Name}: {inner.Message}");
            inner = inner.Inner;
        }

        if (ex.StackTrace.Length > 0)
        {
            summary.AppendLine();
            summary.AppendLine("Stack:");
            var limit = Math.Min(ex.StackTrace.Length, 15);
            for (var i = 0; i < limit; i++)
            {
                var method = ex.StackTrace[i].Method;
                if (method != null)
                {
                    var sourceInfo = pdbResolver.GetSourceLocation(ex.StackTrace[i]);
                    var line = sourceInfo != null
                        ? $"  {method.Type?.Name}.{method.Name} in {sourceInfo}"
                        : $"  {method.Type?.Name}.{method.Name}";
                    summary.AppendLine(line);
                }
            }

            if (ex.StackTrace.Length > 15)
            {
                summary.AppendLine($"  ... ({ex.StackTrace.Length - 15} more frames in log)");
            }
        }

        // Detailed log
        details.AppendLine($"\nException Type: {ex.Type?.Name}");
        details.AppendLine($"Message: {ex.Message}");
        details.AppendLine($"HResult: 0x{ex.HResult:X8}");

        inner = ex.Inner;
        var depth = 1;
        while (inner != null)
        {
            details.AppendLine($"\nInner Exception [{depth}]: {inner.Type?.Name}");
            details.AppendLine($"  Message: {inner.Message}");
            details.AppendLine($"  HResult: 0x{inner.HResult:X8}");
            inner = inner.Inner;
            depth++;
        }

        details.AppendLine("\nException Stack Trace:");
        foreach (var frame in ex.StackTrace)
        {
            var method = frame.Method;
            if (method != null)
            {
                var sourceInfo = pdbResolver.GetSourceLocation(frame);
                var frameLine = $"  {method.Type?.Module?.Name}!{method.Type?.Name}.{method.Name}";
                if (sourceInfo != null)
                {
                    frameLine += $" in {sourceInfo}";
                }
                details.AppendLine(frameLine);
            }
        }
    }

    internal static (string Summary, string Details) AnalyzeWithDbgEng(string dumpPath, bool useSymbols)
    {
        // Use system32's dbgeng.dll — available on every Windows machine.
        var dbgengPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
        using IDisposable dbgeng = IDebugClient.Create(dbgengPath);

        IDebugClient client = (IDebugClient)dbgeng;
        IDebugControl control = (IDebugControl)dbgeng;

        var hr = client.OpenDumpFile(dumpPath);
        if (hr < 0)
        {
            return (string.Empty, $"DbgEng failed to open dump: HRESULT 0x{(uint)hr:X8}");
        }

        hr = control.WaitForEvent(TimeSpan.FromSeconds(30));
        if (hr < 0)
        {
            return (string.Empty, $"DbgEng WaitForEvent failed: HRESULT 0x{(uint)hr:X8}");
        }

        var output = new StringBuilder();
        void CaptureOutput(Action action)
        {
            output.Clear();
            using var holder = new DbgEngOutputHolder(client, DEBUG_OUTPUT.ALL);
            holder.OutputReceived += (text, _) => output.Append(text);
            action();
        }

        // First pass: get stack trace without symbols
        CaptureOutput(() =>
        {
            control.Execute(DEBUG_OUTCTL.THIS_CLIENT, ".ecxr", DEBUG_EXECUTE.DEFAULT);
            control.Execute(DEBUG_OUTCTL.THIS_CLIENT, "kp 20", DEBUG_EXECUTE.DEFAULT);
        });

        var stackOutput = output.ToString();

        // If --symbols, download PDBs for modules on the stack, then re-run
        if (useSymbols)
        {
            var symbolCachePath = SymbolCacheDirectory;
            var downloaded = DownloadSymbolsForStack(stackOutput, control, client, symbolCachePath);
            if (downloaded > 0)
            {
                IDebugSymbols symbols = (IDebugSymbols)dbgeng;
                // Prepend local cache path — system32's dbgeng can load PDBs from
                // local directories but doesn't support srv*/cache* (requires symsrv.dll).
                // Preserve existing path so dbgeng can still find PDBs next to the DLLs.
                var existingPath = symbols.SymbolPath ?? "";
                symbols.SymbolPath = existingPath.Length > 0
                    ? $"{symbolCachePath};{existingPath}"
                    : symbolCachePath;

                CaptureOutput(() =>
                {
                    control.Execute(DEBUG_OUTCTL.THIS_CLIENT, ".reload /f", DEBUG_EXECUTE.DEFAULT);
                    control.Execute(DEBUG_OUTCTL.THIS_CLIENT, ".ecxr", DEBUG_EXECUTE.DEFAULT);
                    control.Execute(DEBUG_OUTCTL.THIS_CLIENT, "kp 20", DEBUG_EXECUTE.DEFAULT);
                });
                stackOutput = output.ToString();
            }
        }

        var summary = ExtractNativeStackSummary(stackOutput);
        return (summary, $"Native Stack (DbgEng):\n{stackOutput}");
    }

    /// <summary>
    /// Downloads PDB symbols for modules that appear in the native stack trace.
    /// Reads PE headers from the original DLL on disk to get the PDB GUID,
    /// then downloads from Microsoft Symbol Server.
    /// </summary>
    private static int DownloadSymbolsForStack(string stackOutput, IDebugControl control, IDebugClient client, string cachePath)
    {
        // Extract unique module names from stack (e.g., "Microsoft_UI_Xaml" from "Microsoft_UI_Xaml+0x3e503")
        var modules = ParseStackModuleNames(stackOutput);
        return DownloadSymbolsForModules(modules, name => GetModuleImagePath(name, control, client), cachePath);
    }

    /// <summary>
    /// Asks the live DbgEng engine (<c>lmvm</c>) for a module's on-disk image path. This is the
    /// native-engine boundary, kept separate from the offline-testable download/PE-parse core in
    /// <see cref="DownloadSymbolsForModules"/>.
    /// </summary>
    private static string? GetModuleImagePath(string moduleName, IDebugControl control, IDebugClient client)
    {
        // Get module file path from DbgEng
        var modOutput = new StringBuilder();
        using (var holder = new DbgEngOutputHolder(client, DEBUG_OUTPUT.ALL))
        {
            holder.OutputReceived += (text, _) => modOutput.Append(text);
            control.Execute(DEBUG_OUTCTL.THIS_CLIENT, $"lmvm {moduleName}", DEBUG_EXECUTE.DEFAULT);
        }

        // Parse "Image path: C:\...\Module.dll" from lmvm output
        return ExtractImagePath(modOutput.ToString());
    }

    /// <summary>
    /// Downloads PDB symbols for the given modules. For each module the on-disk DLL path is obtained via
    /// <paramref name="imagePathProvider"/> (the DbgEng <c>lmvm</c> boundary in production, a stub in
    /// tests), the CodeView PDB signature is read from the DLL's PE header, and the matching PDB is fetched
    /// from the Microsoft Symbol Server (via the <see cref="SymbolFileDownloader"/> seam) unless already
    /// cached. Behavior-preserving extraction of the per-module body so the empty / missing-DLL / cached /
    /// not-found / download-and-write branches are unit-testable offline. Returns the number of modules
    /// whose PDB is present after the pass.
    /// </summary>
    internal static int DownloadSymbolsForModules(ICollection<string> modules, Func<string, string?> imagePathProvider, string cachePath)
    {
        if (modules.Count == 0)
        {
            return 0;
        }

        // For each module, get its DLL path, then read PE header from disk
        var downloaded = 0;

        foreach (var moduleName in modules)
        {
            try
            {
                var dllPath = imagePathProvider(moduleName);
                if (dllPath == null || !File.Exists(dllPath))
                {
                    continue;
                }

                // Read PE CodeView entry to get PDB GUID
                using var stream = File.OpenRead(dllPath);
                using var peReader = new System.Reflection.PortableExecutable.PEReader(stream);
                var debugEntries = peReader.ReadDebugDirectory();

                foreach (var entry in debugEntries)
                {
                    if (entry.Type != System.Reflection.PortableExecutable.DebugDirectoryEntryType.CodeView)
                    {
                        continue;
                    }

                    var cv = peReader.ReadCodeViewDebugDirectoryData(entry);
                    var pdbName = Path.GetFileName(cv.Path);
                    var sig = cv.Guid.ToString("N").ToUpperInvariant() + cv.Age;
                    var localPdb = Path.Combine(cachePath, pdbName, sig, pdbName);

                    // Already cached?
                    if (File.Exists(localPdb))
                    {
                        downloaded++;
                        break;
                    }

                    // Download from Microsoft Symbol Server, streamed straight to the cache file
                    // (constant memory; seamed for tests).
                    var url = $"https://msdl.microsoft.com/download/symbols/{pdbName}/{sig}/{pdbName}";
                    if (!SymbolFileDownloader(url, localPdb))
                    {
                        break;
                    }

                    downloaded++;
                    break;
                }
            }
            catch
            {
                // Skip modules we can't process
            }
        }

        return downloaded;
    }

    /// <summary>
    /// Parses unique module names from DbgEng <c>kp</c> stack output. A frame such as
    /// <c>Module!Function+0x1a</c> or <c>Module+0x1a</c> yields <c>Module</c>; hex addresses and
    /// mangled (backtick-containing) names are skipped. Behavior-preserving extraction for testing.
    /// </summary>
    internal static ISet<string> ParseStackModuleNames(string stackOutput)
    {
        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in stackOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            var bangIdx = trimmed.IndexOf('!');
            var plusIdx = trimmed.IndexOf('+');

            // "Module!Function+0x..." or "Module+0x..."
            if (plusIdx > 0)
            {
                var start = trimmed.LastIndexOf(' ', bangIdx > 0 ? bangIdx : plusIdx) + 1;
                var end = bangIdx > 0 ? bangIdx : plusIdx;
                if (end > start)
                {
                    var name = trimmed[start..end];
                    // Skip addresses (hex strings) and empty names
                    if (name.Length > 0 && !name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                        && !name.Contains('`'))
                    {
                        modules.Add(name);
                    }
                }
            }
        }

        return modules;
    }

    /// <summary>
    /// Real symbol-file download boundary: HTTPS GET from the Microsoft Symbol Server, streamed
    /// directly to <paramref name="destPath"/> with constant memory (no full-PDB buffering). Returns
    /// <c>true</c> when the file was downloaded, <c>false</c> when unavailable. Seamed via
    /// <see cref="SymbolFileDownloader"/> so tests never hit the network.
    /// </summary>
    private static bool DefaultDownloadSymbolFile(string url, string destPath)
    {
        using var http = new HttpClient();
        using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, url));
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var pdbStream = response.Content.ReadAsStream();
        using var fileStream = File.Create(destPath);
        pdbStream.CopyTo(fileStream);
        return true;
    }

    internal static string? ExtractImagePath(string lmvmOutput)
    {
        foreach (var line in lmvmOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Image path:", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["Image path:".Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts a concise stack summary from DbgEng kp output for terminal display.
    /// </summary>
    internal static string ExtractNativeStackSummary(string output)
    {
        var result = new StringBuilder();
        var lines = output.Split('\n');
        var inStack = false;
        var frameCount = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // kp output starts with "Child-SP" header
            if (line.Contains("Child-SP"))
            {
                inStack = true;
                result.AppendLine("Stack:");
                continue;
            }

            if (!inStack || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (frameCount >= 15)
            {
                result.AppendLine("  ... (more frames in log)");
                break;
            }

            // Find call site by looking for Module!Function or Module+offset pattern
            var parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            var callSite = parts.FirstOrDefault(p => p.Contains('!') || p.Contains('+'));
            if (callSite != null)
            {
                // Strip parameters: cut at first '('
                var parenIdx = callSite.IndexOf('(');
                if (parenIdx > 0)
                {
                    callSite = callSite[..parenIdx];
                }

                // Truncate long C++ template names for terminal readability
                if (callSite.Length > 100)
                {
                    var templateStart = callSite.IndexOf('<');
                    if (templateStart > 20)
                    {
                        callSite = callSite[..templateStart] + "<...>";
                    }
                    else
                    {
                        callSite = callSite[..100] + "...";
                    }
                }

                result.AppendLine($"  {callSite}");
                frameCount++;
            }
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Validates that the PDB matches the DLL by comparing the CodeView debug directory GUID.
    /// Returns true if the PDB matches or if validation cannot be performed (e.g., file missing/locked,
    /// no CodeView entry). Extracted to the outer class so it can be unit-tested directly.
    /// </summary>
    internal static bool ValidatePdbMatchesDll(string dllPath, string pdbPath)
    {
        try
        {
            if (!File.Exists(dllPath))
            {
                return true; // Can't validate, accept by name
            }

            using var dllStream = File.OpenRead(dllPath);
            using var peReader = new PEReader(dllStream);
            var debugEntries = peReader.ReadDebugDirectory();

            Guid? peGuid = null;
            foreach (var entry in debugEntries)
            {
                if (entry.Type == DebugDirectoryEntryType.CodeView)
                {
                    peGuid = peReader.ReadCodeViewDebugDirectoryData(entry).Guid;
                    break;
                }
            }

            if (peGuid == null)
            {
                return true; // No CodeView entry, accept by name
            }

            using var pdbStream = File.OpenRead(pdbPath);
            using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream, MetadataStreamOptions.LeaveOpen);
            var pdbReader = pdbProvider.GetMetadataReader();
            var pdbId = new BlobContentId(pdbReader.DebugMetadataHeader!.Id);

            return pdbId.Guid == peGuid.Value;
        }
        catch
        {
            return true; // Can't validate, accept by name
        }
    }

    /// <summary>
    /// Resolves source file and line numbers from portable PDB files for ClrMD stack frames.
    /// Searches for PDBs in the provided search paths and next to the module's image on disk.
    /// Caches loaded PDB readers to avoid re-opening the same PDB for every frame.
    /// </summary>
    private sealed class PdbSourceResolver : IDisposable
    {
        private readonly IReadOnlyList<string> _searchPaths;
        private readonly ClrRuntime _runtime;
        private readonly ILogger _logger;
        // Cache: module name → (MetadataReaderProvider, MetadataReader), or null if not found
        private readonly Dictionary<string, (MetadataReaderProvider Provider, MetadataReader Reader)?> _pdbCache = new(StringComparer.OrdinalIgnoreCase);

        public PdbSourceResolver(IReadOnlyList<string>? searchPaths, ClrRuntime runtime, ILogger logger)
        {
            _searchPaths = searchPaths ?? [];
            _runtime = runtime;
            _logger = logger;
        }

        /// <summary>
        /// Returns "file:line" for the given stack frame, or null if PDB info is unavailable.
        /// </summary>
        public string? GetSourceLocation(ClrStackFrame frame)
        {
            var method = frame.Method;
            if (method == null)
            {
                return null;
            }

            var ilOffset = method.GetILOffset(frame.InstructionPointer);
            if (ilOffset < 0)
            {
                return null;
            }

            return GetSourceLocation(method, ilOffset);
        }

        /// <summary>
        /// Returns "file:line" for a method at the given IL offset, or null if PDB info is unavailable.
        /// </summary>
        public string? GetSourceLocation(ClrMethod method, int ilOffset)
        {
            var module = method.Type?.Module;
            if (module == null)
            {
                return null;
            }

            var reader = GetOrLoadPdbReader(module);
            if (reader == null)
            {
                return null;
            }

            try
            {
                var methodToken = (int)method.MetadataToken;
                var handle = MetadataTokens.MethodDefinitionHandle(methodToken);
                var debugInfo = reader.GetMethodDebugInformation(handle);

                // Walk sequence points to find the one at or just before the IL offset.
                // Skip hidden sequence points (line 0xFEEFEE).
                string? bestFile = null;
                int bestLine = -1;
                int bestOffset = -1;

                foreach (var sp in debugInfo.GetSequencePoints())
                {
                    if (sp.IsHidden)
                    {
                        continue;
                    }

                    if (sp.Offset <= ilOffset && sp.Offset > bestOffset)
                    {
                        bestOffset = sp.Offset;
                        bestLine = sp.StartLine;
                        bestFile = reader.GetString(reader.GetDocument(sp.Document).Name);
                    }
                }

                if (bestFile != null && bestLine > 0)
                {
                    // Show just the filename for terminal readability
                    return $"{Path.GetFileName(bestFile)}:{bestLine}";
                }
            }
            catch (BadImageFormatException)
            {
                // Corrupted or non-portable PDB — skip silently
            }

            return null;
        }

        private MetadataReader? GetOrLoadPdbReader(ClrModule module)
        {
            var moduleName = module.Name ?? "";
            if (_pdbCache.TryGetValue(moduleName, out var cached))
            {
                return cached?.Reader;
            }

            var reader = TryLoadPdb(module);
            _pdbCache[moduleName] = reader;
            return reader?.Reader;
        }

        private (MetadataReaderProvider Provider, MetadataReader Reader)? TryLoadPdb(ClrModule module)
        {
            var modulePath = module.Name;
            if (string.IsNullOrEmpty(modulePath))
            {
                return null;
            }

            var moduleDllName = Path.GetFileNameWithoutExtension(modulePath);

            // Build candidate PDB paths:
            // 1. Provided search paths (e.g., build output folder)
            // 2. Next to the module DLL on disk (may be in AppX folder)
            // 3. Parent of the module directory (build output is often parent of AppX)
            var candidates = new List<string>();

            foreach (var searchPath in _searchPaths)
            {
                candidates.Add(Path.Combine(searchPath, $"{moduleDllName}.pdb"));
            }

            var moduleDir = Path.GetDirectoryName(modulePath);
            if (moduleDir != null)
            {
                candidates.Add(Path.Combine(moduleDir, $"{moduleDllName}.pdb"));
                var parentDir = Path.GetDirectoryName(moduleDir);
                if (parentDir != null)
                {
                    candidates.Add(Path.Combine(parentDir, $"{moduleDllName}.pdb"));
                }
            }

            foreach (var pdbPath in candidates)
            {
                if (!File.Exists(pdbPath))
                {
                    continue;
                }

                try
                {
                    // Validate this is the matching PDB by checking the PDB ID from the PE debug directory.
                    // If we can't read the PE (e.g., file locked), fall back to accepting the PDB by name.
                    if (!ValidatePdbMatchesDll(modulePath, pdbPath))
                    {
                        continue;
                    }

                    var stream = File.OpenRead(pdbPath);
                    var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
                    var reader = provider.GetMetadataReader();
                    _logger.LogDebug("Loaded PDB for {Module} from {Path}", moduleDllName, pdbPath);
                    return (provider, reader);
                }
                catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException)
                {
                    // Not a valid portable PDB or I/O error — try next candidate
                }
            }

            return null;
        }

        public void Dispose()
        {
            foreach (var entry in _pdbCache.Values)
            {
                entry?.Provider.Dispose();
            }
            _pdbCache.Clear();
        }
    }
}
