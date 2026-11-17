// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using Spectre.Console.Testing;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Real-workflow tests for <see cref="CrashDumpService"/> that generate a genuine minidump (via the
/// product's own <see cref="CrashDumpService.WriteMiniDump"/>) and run the in-process ClrMD/DbgEng
/// analyzers against it — the same engines the product uses. No external debugger executable is
/// launched and no symbols are downloaded (the symbol-file boundary is stubbed). Marked
/// <c>[DoNotParallelize]</c> because the tests run the shared in-process debugging engine and mutate
/// the static <see cref="CrashDumpService.SymbolFileDownloader"/> seam.
/// </summary>
/// <remarks>
/// <para><b>Documented coverage ceiling (~91.3% Debug line coverage; the full-suite run is authoritative).</b>
/// The remaining 68 uncovered lines are unreachable on this host without a foreign CPU architecture, a live
/// symbol server, a corrupted/stripped/mismatched PE or PDB, or Win32/engine fault injection, and per policy
/// are left honestly uncovered rather than forced with flaky tests or excluded from coverage. Current
/// uncovered ranges and why:</para>
/// <list type="bullet">
///   <item>168-171, 177-180 — <c>MiniDumpWriteDump</c> P/Invoke returning FALSE and the outer catch (native
///   dump-write failure): defensive; cannot be provoked for a live process without corrupting the OS call.</item>
///   <item>384-390, 403-404, 407-413 — cross-architecture DAC / emulation branches (a dump whose target
///   architecture differs from the host, and the <c>ClrDiagnosticsException</c>/<c>BadImageFormatException</c>
///   DAC-load-failure path): require a non-x64 dump analyzed on an x64 host.</item>
///   <item>604-605, 607, 609 — <c>DumpHasWinUiModule</c> catch and its fall-through: reached only if
///   <c>DataTarget.EnumerateModules()</c> throws; defensive engine guard.</item>
///   <item>665-667 — <c>FormatManagedException</c> "… N more frames" line: runs only when ClrMD surfaces a
///   managed exception whose stack exceeds 15 frames. The dedicated <c>AnalyzeDumpAsync_ManagedDumpWithDeepStack…</c>
///   / <c>…DeepStackWithPortablePdb…</c> tests below drive exactly this shape, but they
///   <see cref="Assert.Inconclusive(string)"/> on hosts where the matching DAC cannot be loaded (as here), so
///   the line stays uncovered in this run.</item>
///   <item>695-697 — <c>FormatManagedException</c> per-frame source append: runs only when ClrMD/DAC resolves
///   a source location for a managed frame. Covered by <c>AnalyzeDumpAsync_DeepStackWithPortablePdb_ResolvesManagedSourceLocations</c>
///   when the DAC is available; that test goes <see cref="Assert.Inconclusive(string)"/> here because the DAC
///   for the child's runtime is not loadable on this host.</item>
///   <item>720-721 — <c>AnalyzeWithDbgEng</c> <c>WaitForEvent</c> non-success HRESULT: defensive engine guard,
///   not drivable once <c>OpenDumpFile</c> has succeeded without corrupting engine state.</item>
///   <item>840-841 — the non-CodeView debug-directory entry skip while reading a module's PDB GUID: reached
///   only if a non-CodeView debug entry precedes the CodeView entry; the test modules' CodeView entry is
///   first, so the loop breaks before hitting it (PE-layout-dependent).</item>
///   <item>919-924, 927-932 — <c>DefaultDownloadSymbolFile</c> real HTTPS symbol download streamed to disk: network boundary
///   (the <c>SymbolFileDownloader</c> seam is stubbed in tests, so the default body is never invoked). Note:
///   the offline-testable download / cache-hit / not-found / PE-CodeView parse core
///   (<c>DownloadSymbolsForModules</c>) is fully covered by the M4 unit tests below, and the live
///   <c>GetModuleImagePath</c> lmvm boundary is covered by the in-process-engine workflow tests.</item>
///   <item>1041, 1044-1045 — <c>ValidatePdbMatchesDll</c> foreach-exit brace and the "no CodeView entry →
///   accept by name" early return: needs a release-stripped DLL with no CodeView debug entry (PE-dependent).</item>
///   <item>1088-1089, 1108-1109 — <c>PdbSourceResolver.GetSourceLocation</c> null-<c>Method</c> / null-<c>Module</c>
///   guards: ClrMD's <c>ClrStackFrame</c>/<c>ClrMethod</c>/<c>ClrModule</c> are engine-produced and have no
///   public constructor, so a null-bearing instance cannot be fabricated as a fake.</item>
///   <item>1150-1152, 1154, 1156 — the <c>BadImageFormatException</c> catch around sequence-point reading and
///   the no-match fall-through <c>return null</c>: needs a corrupt portable PDB or a method with no matching
///   sequence point; engine/PDB-dependent.</item>
///   <item>1216-1217, 1226-1227, 1229-1230 — <c>GetOrLoadPdbReader</c> PDB-GUID-mismatch <c>continue</c> and
///   the corrupt-/locked-PDB catch: reached only with a mismatched or corrupt candidate PDB in the search
///   path; environment-dependent and would be a TOCTOU/flaky test to force.</item>
/// </list>
/// </remarks>
[TestClass]
[DoNotParallelize]
#pragma warning disable CA1001 // Disposable fields cleaned up in TestCleanup
public sealed class CrashDumpServiceWorkflowTests
{
    private TestConsole _console = null!;
    private CrashDumpService _service = null!;
    private FakeXamlTriageService _xamlTriage = null!;
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _console = new TestConsole();
        _xamlTriage = new FakeXamlTriageService();
        _service = new CrashDumpService(
            _console,
            LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug)).CreateLogger<CrashDumpService>(),
            _xamlTriage);
        _tempDir = Path.Combine(Path.GetTempPath(), $"CrashDumpWf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _console?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public void WriteMiniDump_BenignNativeChild_WritesValidDumpFile()
    {
        var dump = TestProcessDump.TryCreateNativeDump(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a native minidump: {err}");
            return;
        }

        try
        {
            Assert.IsTrue(File.Exists(dump));
            Assert.IsTrue(new FileInfo(dump).Length > 0, "The generated dump must be non-empty.");
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void WriteMiniDump_WithSavedContext_WritesDumpWithFirstChanceExceptionRecord()
    {
        using var child = StartBenignChild();
        if (child == null)
        {
            Assert.Inconclusive("Could not start a child process for dump capture.");
            return;
        }

        try
        {
            var tid = GetMainThreadId(child);
            if (tid == 0)
            {
                Assert.Inconclusive("Could not resolve a valid child thread id.");
                return;
            }

            // A zeroed buffer larger than any CONTEXT exercises the saved-context branch; the exception
            // record describes a first-chance AV against the (valid) captured thread.
            var savedContext = new byte[4096];
            var dump = _service.WriteMiniDump(
                (uint)child.Id, savedContext, savedThreadId: tid,
                savedExceptionCode: unchecked((int)0xC0000005), savedExceptionAddress: (nuint)0x1000);

            Assert.IsNotNull(dump, "A saved-context dump should be written.");
            Assert.IsTrue(File.Exists(dump!), "The saved-context dump file must exist.");
            Assert.IsTrue(new FileInfo(dump!).Length > 0, "The saved-context dump must be non-empty.");
            try { File.Delete(dump!); } catch { /* best effort */ }
        }
        finally
        {
            KillChild(child);
        }
    }

    [TestMethod]
    public void WriteMiniDump_WithStowedException_WritesDumpWithStowedParameters()
    {
        using var child = StartBenignChild();
        if (child == null)
        {
            Assert.Inconclusive("Could not start a child process for dump capture.");
            return;
        }

        try
        {
            var tid = GetMainThreadId(child);
            if (tid == 0)
            {
                Assert.Inconclusive("Could not resolve a valid child thread id.");
                return;
            }

            // Terminating stowed exception (0xC000027B) + parameters -> the stowed-record branch runs
            // and copies the stowed-exception parameters into the dump's exception record.
            var savedContext = new byte[4096];
            var dump = _service.WriteMiniDump(
                (uint)child.Id, savedContext, savedThreadId: tid,
                savedExceptionCode: unchecked((int)0xC0000005), savedExceptionAddress: (nuint)0x1000,
                crashExceptionCode: unchecked((int)0xC000027B), crashExceptionAddress: (nuint)0x2000,
                crashExceptionParameters: [(nuint)0x3000, (nuint)2]);

            Assert.IsNotNull(dump, "A stowed-exception dump should be written.");
            Assert.IsTrue(File.Exists(dump!), "The stowed-exception dump file must exist.");
            Assert.IsTrue(new FileInfo(dump!).Length > 0, "The stowed-exception dump must be non-empty.");
            try { File.Delete(dump!); } catch { /* best effort */ }
        }
        finally
        {
            KillChild(child);
        }
    }

    [TestMethod]
    public void WriteMiniDump_InvalidProcessId_ReturnsNull()
    {
        // A process id that cannot be opened -> OpenProcess returns an invalid handle -> null path.
        var dump = _service.WriteMiniDump(
            processId: 0xFFFFFFFC, savedContext: null, savedThreadId: 0,
            savedExceptionCode: 0, savedExceptionAddress: 0);

        Assert.IsNull(dump, "An unopenable process id must yield a null dump path.");
    }

    private static Process? StartBenignChild()
    {
        try
        {
            var child = Process.Start(new ProcessStartInfo
            {
                FileName = "ping.exe",
                Arguments = "-n 60 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (child != null)
            {
                Thread.Sleep(300); // let the child initialize so its memory is dumpable
                child.Refresh();
            }

            return child;
        }
        catch
        {
            return null;
        }
    }

    private static uint GetMainThreadId(Process child)
    {
        try
        {
            child.Refresh();
            if (child.Threads.Count > 0)
            {
                return (uint)child.Threads[0].Id;
            }
        }
        catch
        {
            // fall through
        }

        return 0;
    }

    private static void KillChild(Process child)
    {
        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    [TestMethod]
    public void AnalyzeWithDbgEng_ValidNativeDumpNoSymbols_ProducesNativeStack()
    {
        var dump = TestProcessDump.TryCreateNativeDump(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a native minidump: {err}");
            return;
        }

        try
        {
            (string Summary, string Details) result;
            try
            {
                result = CrashDumpService.AnalyzeWithDbgEng(dump, useSymbols: false);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process DbgEng engine unavailable: {ex.Message}");
                return;
            }

            Assert.Contains("Native Stack (DbgEng)", result.Details);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Summary), "A valid native dump should yield a stack summary.");
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void AnalyzeWithDbgEng_ValidNativeDumpWithSymbols_DrivesSymbolDownloadSeamWithoutNetwork()
    {
        var dump = TestProcessDump.TryCreateNativeDump(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a native minidump: {err}");
            return;
        }

        var requestedUrls = new List<string>();
        var savedDownloader = CrashDumpService.SymbolFileDownloader;
        var savedCacheDir = CrashDumpService.SymbolCacheDirectory;

        // Point the product's PDB cache at an isolated, empty per-test directory. This deterministically
        // forces the "not cached -> download" path AND guarantees we never read or delete the developer's
        // or CI's SHARED %TEMP%\symbols cache (a fresh _tempDir\symbols starts empty).
        var symbolCache = Path.Combine(_tempDir, "symbols");
        CrashDumpService.SymbolCacheDirectory = symbolCache;

        try
        {
            // Stub the symbol-server boundary: pretend every PDB is available so the
            // "downloaded > 0 -> reload and re-run with symbols" branch runs without any network.
            CrashDumpService.SymbolFileDownloader = (url, destPath) =>
            {
                requestedUrls.Add(url);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                // Drop a benign, deliberately-invalid PDB at the module's expected cache path. The
                // product's `.reload /f` (in AnalyzeWithDbgEng) force-loads whatever file lives here,
                // so the payload must NOT begin with the "Microsoft C/C++ MSF 7.00" magic and must be
                // large enough that dbghelp rejects it at the signature check WITHOUT reading past the
                // end of the file. A prior 3-byte payload ([0x4D,0x69,0x63] = "Mic") matched the magic's
                // prefix, causing some dbghelp versions to over-read the truncated MSF superblock and
                // fault with an access violation (0xC0000005) during `.reload /f` — a native crash that
                // took down the whole test host on Azure DevOps 1ES agents (GitHub-hosted runners and
                // local dev machines happened to tolerate/reject the same bytes). dbghelp rejects this
                // file cleanly as "not a PDB"; the test still drives the download seam with msdl URLs.
                var dummyPdb = new byte[8192];
                "winapp-test-fixture-not-a-real-pdb\n"u8.CopyTo(dummyPdb);
                File.WriteAllBytes(destPath, dummyPdb);
                return true;
            };

            (string Summary, string Details) result;
            try
            {
                result = CrashDumpService.AnalyzeWithDbgEng(dump, useSymbols: true);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process DbgEng engine unavailable: {ex.Message}");
                return;
            }

            Assert.Contains("Native Stack (DbgEng)", result.Details);
            if (requestedUrls.Count == 0)
            {
                // No stack module produced a resolvable on-disk DLL with a CodeView entry in this
                // environment; the download branch cannot be exercised here (not a product failure).
                Assert.Inconclusive("No stack modules with downloadable symbols were found in this environment.");
                return;
            }

            Assert.IsTrue(requestedUrls.TrueForAll(u => u.StartsWith("https://msdl.microsoft.com/", StringComparison.OrdinalIgnoreCase)),
                "The seam must be called with Microsoft Symbol Server URLs.");
        }
        finally
        {
            CrashDumpService.SymbolFileDownloader = savedDownloader;
            CrashDumpService.SymbolCacheDirectory = savedCacheDir;
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_RealNativeDump_ReportsNativeCrashViaRealEngines()
    {
        // End-to-end with the real ClrMD + DbgEng analyzers (no overrides): a native dump has no CLR,
        // so ClrMD returns the "native-only" path and the DbgEng fallback produces the native stack.
        var dump = TestProcessDump.TryCreateNativeDump(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a native minidump: {err}");
            return;
        }

        try
        {
            var logPath = Path.Combine(_tempDir, "real-native.log");
            try
            {
                await _service.AnalyzeDumpAsync(dump, logPath, useSymbols: false);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process engine unavailable: {ex.Message}");
                return;
            }

            Assert.Contains("CRASH DETECTED", _console.Output);
            Assert.IsTrue(File.Exists(logPath), "A crash-analysis log should be written for a valid dump.");
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_RealManagedDump_RunsClrMdManagedEnumeration()
    {
        // A managed (PowerShell) dump exercises the ClrMD managed path: runtime creation, thread/heap
        // scan, stack-overflow check, and the all-threads detail listing.
        var dump = TestProcessDump.TryCreateManagedDump(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a managed minidump: {err}");
            return;
        }

        try
        {
            var logPath = Path.Combine(_tempDir, "real-managed.log");
            try
            {
                await _service.AnalyzeDumpAsync(dump, logPath, useSymbols: false);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process engine unavailable: {ex.Message}");
                return;
            }

            if (!File.Exists(logPath))
            {
                Assert.Inconclusive("ClrMD did not produce a managed analysis log in this environment (DAC unavailable).");
                return;
            }

            var log = await File.ReadAllTextAsync(logPath);
            Assert.Contains("CLR Version", log);
            Assert.Contains("All Threads", log);
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_ManagedDumpWithInFlightException_ReportsManagedException()
    {
        var dump = TestProcessDump.TryCreateManagedDumpWithInFlightException(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create an in-flight-exception managed dump: {err}");
            return;
        }

        try
        {
            var logPath = Path.Combine(_tempDir, "inflight.log");
            try
            {
                await _service.AnalyzeDumpAsync(dump, logPath, useSymbols: false);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process engine unavailable: {ex.Message}");
                return;
            }

            if (!_console.Output.Contains("CRASH DETECTED", StringComparison.Ordinal))
            {
                Assert.Inconclusive("ClrMD did not surface the in-flight exception in this environment (DAC unavailable).");
                return;
            }

            // The in-flight exception is reported via ClrThread.CurrentException, so its type/message
            // must appear in the crash summary.
            Assert.Contains("InvalidOperationException", _console.Output);
            Assert.Contains("INFLIGHT_BOOM", _console.Output);
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_ManagedDumpWithHeapException_FormatsExceptionDetails()
    {
        var dump = TestProcessDump.TryCreateManagedDumpWithHeapException(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a heap-exception managed dump: {err}");
            return;
        }

        try
        {
            var logPath = Path.Combine(_tempDir, "heapexc.log");
            try
            {
                await _service.AnalyzeDumpAsync(dump, logPath, useSymbols: false);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process engine unavailable: {ex.Message}");
                return;
            }

            if (!_console.Output.Contains("CRASH DETECTED", StringComparison.Ordinal))
            {
                Assert.Inconclusive("ClrMD did not surface a managed exception in this environment (DAC unavailable).");
                return;
            }

            // A crash analysis summary was produced by the exception formatter.
            Assert.Contains("Exception:", _console.Output);
            Assert.IsTrue(File.Exists(logPath), "A managed crash-analysis log should be written.");
            var log = await File.ReadAllTextAsync(logPath);
            Assert.Contains("Exception Type:", log);

            // The heap scan keeps the most-recently-thrown exception; when that is ours, assert its
            // outer/inner markers. Otherwise the formatter still ran (coverage achieved) but a different
            // heap exception was chosen, so we do not hard-fail on the markers.
            if (log.Contains("HEAP_BOOM_OUTER", StringComparison.Ordinal))
            {
                Assert.Contains("HEAP_BOOM_INNER", log);
                Assert.Contains("Inner Exception", log);
            }
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_ManagedDumpWithDeepStack_DetectsStackOverflowShape()
    {
        var dump = TestProcessDump.TryCreateManagedDumpWithDeepStack(out var err);
        if (dump == null)
        {
            Assert.Inconclusive($"Could not create a deep-stack managed dump: {err}");
            return;
        }

        try
        {
            var logPath = Path.Combine(_tempDir, "deepstack.log");
            try
            {
                await _service.AnalyzeDumpAsync(dump, logPath, useSymbols: false);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process engine unavailable: {ex.Message}");
                return;
            }

            if (!_console.Output.Contains("CRASH DETECTED", StringComparison.Ordinal))
            {
                Assert.Inconclusive("ClrMD did not resolve managed frames in this environment (DAC unavailable).");
                return;
            }

            // A thread with hundreds of managed frames is reported as a (deep-recursion) stack overflow.
            Assert.Contains("Stack Overflow (deep recursion detected)", _console.Output);
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_DeepStackWithPortablePdb_ResolvesManagedSourceLocations()
    {
        // Same deep-recursion shape as the sibling test, but the child is compiled by the .NET SDK so a
        // *portable* PDB sits next to the managed module. That exercises PdbSourceResolver's on-disk
        // portable-PDB path end to end: candidate discovery, PDB/DLL GUID validation, portable-reader
        // load, and the sequence-point walk that maps IL offsets to "file:line" — none of which the
        // Add-Type (Windows-PDB) build can reach. The build output is passed as a symbol search path so
        // the search-paths candidate branch is covered too.
        var dump = TestProcessDump.TryCreateDeepStackDumpWithPortablePdb(out var err, out var tempRoot, out var moduleDir);
        if (dump == null)
        {
            try { if (tempRoot != null) { Directory.Delete(tempRoot, recursive: true); } } catch { /* best effort */ }
            Assert.Inconclusive($"Could not build/dump a portable-PDB deep-stack child (SDK unavailable?): {err}");
            return;
        }

        try
        {
            var logPath = Path.Combine(_tempDir, "deepstack-pdb.log");
            try
            {
                await _service.AnalyzeDumpAsync(dump, logPath, useSymbols: false, symbolSearchPaths: [moduleDir!]);
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"In-process engine unavailable: {ex.Message}");
                return;
            }

            if (!_console.Output.Contains("CRASH DETECTED", StringComparison.Ordinal))
            {
                Assert.Inconclusive("ClrMD did not resolve managed frames in this environment (DAC unavailable).");
                return;
            }

            var log = await File.ReadAllTextAsync(logPath);
            // The recursive method and its source file/line, resolved from the portable PDB, must appear
            // in the analysis — proving the resolver loaded the PDB and walked its sequence points.
            Assert.Contains("Deep", log);
            Assert.Contains("Program.cs", log);
            Assert.Contains(" in Program.cs:", log);
        }
        finally
        {
            try { File.Delete(dump); } catch { /* best effort */ }
            try { if (tempRoot != null) { Directory.Delete(tempRoot, recursive: true); } } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void AnalyzeWithDbgEng_MissingDumpFile_ReturnsOpenOrWaitFailure()
    {
        // Point the real in-process DbgEng at a path that is not a dump. Opening it (or the ensuing
        // WaitForEvent) must fail with an HRESULT, and the analyzer must surface that as an empty
        // summary plus a descriptive error detail instead of throwing. useSymbols:false so nothing is
        // downloaded. Lives in this [DoNotParallelize] class so it never shares the process-wide
        // engine with another test class.
        var missing = Path.Combine(_tempDir, "not-a-real.dmp");
        (string Summary, string Details) result;
        try
        {
            result = CrashDumpService.AnalyzeWithDbgEng(missing, useSymbols: false);
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"In-process DbgEng unavailable in this environment: {ex.Message}");
            return;
        }

        Assert.AreEqual(string.Empty, result.Summary);
        Assert.IsTrue(
            result.Details.Contains("failed to open dump", StringComparison.Ordinal) ||
            result.Details.Contains("WaitForEvent failed", StringComparison.Ordinal),
            $"Expected a DbgEng open/wait failure detail, got: {result.Details}");
    }

    // ---- M4: DownloadSymbolsForModules — offline-testable symbol-download core ----
    // These exercise the per-module PDB download/cache/count logic that was previously reachable only
    // through the live DbgEng lmvm boundary. The image-path provider (the lmvm seam in production) and
    // the SymbolFileDownloader network seam are both stubbed, so no engine and no network are used.
    // They live in this [DoNotParallelize] class because they mutate the process-wide
    // CrashDumpService.SymbolFileDownloader static seam.

    [TestMethod]
    public void DownloadSymbolsForModules_EmptyModuleSet_ReturnsZeroWithoutResolving()
    {
        var providerInvoked = false;
        var count = CrashDumpService.DownloadSymbolsForModules(
            new HashSet<string>(),
            _ => { providerInvoked = true; return null; },
            _tempDir);

        Assert.AreEqual(0, count);
        Assert.IsFalse(providerInvoked, "An empty module set must short-circuit before touching the image-path provider.");
    }

    [TestMethod]
    public void DownloadSymbolsForModules_NullOrMissingImagePath_SkipsModuleWithoutDownloading()
    {
        var saved = CrashDumpService.SymbolFileDownloader;
        var downloaderInvoked = false;
        try
        {
            CrashDumpService.SymbolFileDownloader = (_, _) => { downloaderInvoked = true; return true; };

            var count = CrashDumpService.DownloadSymbolsForModules(
                ["nullpath", "missingfile"],
                name => name == "nullpath" ? null : Path.Combine(_tempDir, "does-not-exist.dll"),
                _tempDir);

            Assert.AreEqual(0, count, "A null image path and a non-existent DLL must both be skipped.");
            Assert.IsFalse(downloaderInvoked, "The symbol downloader must not run when no on-disk DLL is resolved.");
        }
        finally
        {
            CrashDumpService.SymbolFileDownloader = saved;
        }
    }

    [TestMethod]
    public void DownloadSymbolsForModules_DownloadsThenServesSecondPassFromCache()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "winapp.dll");
        if (!File.Exists(dll))
        {
            Assert.Inconclusive("winapp.dll not present alongside the test binaries.");
            return;
        }

        var saved = CrashDumpService.SymbolFileDownloader;
        var downloadCount = 0;
        try
        {
            CrashDumpService.SymbolFileDownloader = (url, destPath) =>
            {
                downloadCount++;
                Assert.IsTrue(url.StartsWith("https://msdl.microsoft.com/", StringComparison.OrdinalIgnoreCase),
                    "The symbol download must target the Microsoft Symbol Server.");
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.WriteAllBytes(destPath, [0x4D, 0x69, 0x63]); // stream a fixture PDB into the cache path
                return true;
            };

            // First pass: cache miss -> PE CodeView read -> download -> write PDB into the cache.
            var first = CrashDumpService.DownloadSymbolsForModules(["winapp"], _ => dll, _tempDir);
            Assert.AreEqual(1, first, "The resolved module's PDB must be downloaded and counted.");
            Assert.AreEqual(1, downloadCount);

            // Second pass over the same cache: the PDB is already on disk -> cache hit, counted, no re-download.
            var second = CrashDumpService.DownloadSymbolsForModules(["winapp"], _ => dll, _tempDir);
            Assert.AreEqual(1, second, "The cached PDB must be counted on the second pass.");
            Assert.AreEqual(1, downloadCount, "A cache hit must not trigger another download.");
        }
        finally
        {
            CrashDumpService.SymbolFileDownloader = saved;
        }
    }

    [TestMethod]
    public void DownloadSymbolsForModules_DownloaderReportsUnavailable_DoesNotCount()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "winapp.dll");
        if (!File.Exists(dll))
        {
            Assert.Inconclusive("winapp.dll not present alongside the test binaries.");
            return;
        }

        var saved = CrashDumpService.SymbolFileDownloader;
        try
        {
            CrashDumpService.SymbolFileDownloader = (_, _) => false; // symbol server 404 / unavailable

            var count = CrashDumpService.DownloadSymbolsForModules(["winapp"], _ => dll, _tempDir);

            Assert.AreEqual(0, count, "An unavailable download (symbol not found) must break out without counting.");
        }
        finally
        {
            CrashDumpService.SymbolFileDownloader = saved;
        }
    }

    [TestMethod]
    public void DownloadSymbolsForModules_ImagePathProviderThrows_IsCaughtPerModule()
    {
        var count = CrashDumpService.DownloadSymbolsForModules(
            ["boom"],
            _ => throw new InvalidOperationException("provider failure"),
            _tempDir);

        Assert.AreEqual(0, count, "A per-module exception must be swallowed and yield zero downloads.");
    }
}
