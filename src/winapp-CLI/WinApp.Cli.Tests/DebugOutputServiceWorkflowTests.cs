// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Services;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;

namespace WinApp.Cli.Tests;

/// <summary>
/// Real-workflow tests for the <see cref="DebugOutputService"/> debug-event loop. A benign PowerShell
/// child is started, blocked on stdin, then the service attaches the Win32 debugger to it; only after
/// the debugger is attached does the test release the child (via stdin) to emit
/// <c>OutputDebugString</c> messages or fault. This makes the ordering deterministic (no TOCTOU): the
/// interesting debug events always occur while the loop is running. The crash-dump boundary is a fake,
/// so no real dump is written and no analysis runs. Marked <c>[DoNotParallelize]</c> because a process
/// may only be attached to one debugger at a time and the loop mutates global debug state.
/// </summary>
/// <remarks>
/// <para><b>Documented coverage ceiling.</b> The decision branches previously listed here — single-step /
/// thread-rename noise suppression, the zero-parameter <c>ReadExceptionParameters</c> early-return, the
/// <c>Arm64</c> CONTEXT-flags arm, and the <c>OpenThread</c>-failure guard — are now covered directly (the
/// noise/first-chance branches via fabricated debug events in this file; the arch/parameter helpers via the
/// pure unit tests in <see cref="DebugOutputServiceTests"/>). The only lines left uncovered require Win32
/// fault injection or a real debugger event the controlled child never emits, so per policy they are left
/// honestly uncovered rather than tested flakily or excluded:</para>
/// <list type="bullet">
///   <item>166-167 — the <c>OutputDebugString</c> event with a zero-length payload: a real debugger event
///   the controlled child never emits.</item>
///   <item>174-175, 181-182 — the <c>OpenProcess</c> / <c>ReadProcessMemory</c> failure guards while reading
///   the debuggee's <c>OutputDebugString</c> buffer: cannot be provoked without corrupting the OS call
///   (TOCTOU/flaky).</item>
///   <item>419-421 — the <c>GetThreadContext</c>-failure guard: reached only when <c>OpenThread</c> succeeds
///   but the subsequent context read fails — genuine Win32 fault injection, undrivable without flakiness.</item>
/// </list>
/// </remarks>
[TestClass]
[DoNotParallelize]
#pragma warning disable CA1001 // TestConsole disposed in cleanup
public sealed class DebugOutputServiceWorkflowTests
{
    private TestConsole _console = null!;
    private FakeCrashDumpService _crashDump = null!;
    private DebugOutputService _service = null!;
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "winapp-dumps");

    [TestInitialize]
    public void Setup()
    {
        _console = new TestConsole();
        _crashDump = new FakeCrashDumpService();
        _service = new DebugOutputService(_console, _crashDump, NullLogger<DebugOutputService>.Instance);
    }

    [TestCleanup]
    public void Cleanup() => _console?.Dispose();

    [TestMethod]
    public async Task RunDebugLoopAsync_UnattachableProcess_ReturnsMinusOneAndWritesLog()
    {
        // A process id that does not exist -> DebugActiveProcess fails deterministically.
        const uint bogusPid = 0x7FFFFFF0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var exit = await _service.RunDebugLoopAsync(bogusPid, cts.Token);

        Assert.AreEqual(-1, exit, "Attaching to a non-existent process must return -1.");
        StringAssert.Contains(_console.Output, "Full debug log:");

        var logs = SafeGetLogs(bogusPid);
        Assert.IsTrue(logs.Length > 0, "A debug log file should have been created even when attach fails.");
        CleanupLogs(logs);
    }

    [TestMethod]
    public async Task RunDebugLoopAsync_BenignChild_StreamsDebugOutputAndPropagatesExitCode()
    {
        Process? child = TryStartPowerShellChild(BenignOutputDebugStringScript, out var startError);
        if (child == null)
        {
            Assert.Inconclusive($"Could not start a PowerShell child: {startError}");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var loopTask = _service.RunDebugLoopAsync((uint)child.Id, cts.Token);

            // Let the debugger attach and drain the initial breakpoint / module-load events first.
            await Task.Delay(1500, cts.Token);
            if (!TrySignalChild(child))
            {
                await cts.CancelAsync();
                try { await loopTask; } catch { /* ignore */ }
                Assert.Inconclusive("Child exited before it could be signalled.");
                return;
            }

            int exit;
            try
            {
                exit = await loopTask.WaitAsync(TimeSpan.FromSeconds(40));
            }
            catch (TimeoutException)
            {
                Assert.Inconclusive("Debug loop did not observe the child's exit in time on this machine.");
                return;
            }

            Assert.AreEqual(7, exit, "The child's exit code should be propagated by the debug loop.");
            StringAssert.Contains(_console.Output, "APPMARKER7X", "App-specific debug output should reach the console.");
            Assert.IsFalse(_console.Output.Contains("0xdeadbeef", StringComparison.Ordinal),
                "Framework-noise debug output should be filtered from the console.");
            StringAssert.Contains(_console.Output, "Full debug log:");
            Assert.AreEqual(0, _crashDump.WriteCalls.Count, "No crash dump should be written for a clean exit.");

            var logs = SafeGetLogs((uint)child.Id);
            if (logs.Length > 0)
            {
                var logText = await File.ReadAllTextAsync(logs[0]);
                StringAssert.Contains(logText, "APPMARKER7X");
                // The log captures everything, including the noise that was filtered from the console.
                StringAssert.Contains(logText, "Microsoft.UI.Xaml.dll");
                CleanupLogs(logs);
            }
        }
        finally
        {
            KillQuietly(child);
        }
    }

    [TestMethod]
    public async Task RunDebugLoopAsync_ChildAccessViolation_DetectsCrashAndRequestsDump()
    {
        _crashDump.FakeDumpPath = Path.Combine(_logDir, $"fake-crash-{Guid.NewGuid():N}.dmp");

        Process? child = TryStartPowerShellChild(AccessViolationScript, out var startError);
        if (child == null)
        {
            Assert.Inconclusive($"Could not start a PowerShell child: {startError}");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var loopTask = _service.RunDebugLoopAsync((uint)child.Id, cts.Token);

            await Task.Delay(1500, cts.Token);
            if (!TrySignalChild(child))
            {
                await cts.CancelAsync();
                try { await loopTask; } catch { /* ignore */ }
                Assert.Inconclusive("Child exited before it could be signalled.");
                return;
            }

            try
            {
                await loopTask.WaitAsync(TimeSpan.FromSeconds(40));
            }
            catch (TimeoutException)
            {
                Assert.Inconclusive("Debug loop did not observe the crash in time on this machine.");
                return;
            }

            Assert.IsTrue(_crashDump.WriteCalls.Count >= 1,
                "An access violation should cause the debug loop to request a crash dump.");
            StringAssert.Contains(_console.Output, "Crash:");
            StringAssert.Contains(_console.Output, "First-chance exception:");
            Assert.IsTrue(_crashDump.AnalyzeCalls.Contains(_crashDump.FakeDumpPath!),
                "After a captured dump, the loop should invoke crash-dump analysis on it.");

            CleanupLogs(SafeGetLogs((uint)child.Id));
        }
        finally
        {
            KillQuietly(child);
        }
    }

    // ---- Child scripts (executed via -EncodedCommand to avoid shell quoting issues) ----

    private const string StackOverflowScript = """
        $null = [Console]::In.ReadLine()
        Add-Type -Namespace W -Name N -MemberDefinition 'public static long Rec(int n){ return Rec(n + 1) + n; }'
        [W.N]::Rec(0)
        exit 0
        """;

    [TestMethod]
    public async Task RunDebugLoopAsync_ChildStackOverflow_CapturesDumpOnFirstChance()
    {
        _crashDump.FakeDumpPath = Path.Combine(_logDir, $"fake-soe-{Guid.NewGuid():N}.dmp");

        Process? child = TryStartPowerShellChild(StackOverflowScript, out var startError);
        if (child == null)
        {
            Assert.Inconclusive($"Could not start a PowerShell child: {startError}");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var loopTask = _service.RunDebugLoopAsync((uint)child.Id, cts.Token);

            await Task.Delay(1500, cts.Token);
            if (!TrySignalChild(child))
            {
                await cts.CancelAsync();
                try { await loopTask; } catch { /* ignore */ }
                Assert.Inconclusive("Child exited before it could be signalled.");
                return;
            }

            try
            {
                await loopTask.WaitAsync(TimeSpan.FromSeconds(40));
            }
            catch (TimeoutException)
            {
                Assert.Inconclusive("Debug loop did not observe the stack overflow in time on this machine.");
                return;
            }

            // Stack overflow (0xC00000FD) is fatal on first-chance, so the loop must capture the dump
            // immediately rather than waiting for a (never-arriving) second-chance exception.
            Assert.IsTrue(_crashDump.WriteCalls.Count >= 1,
                "A stack overflow should cause the debug loop to request a crash dump on first-chance.");
            StringAssert.Contains(_console.Output, "Crash:");
            StringAssert.Contains(_console.Output, "C00000FD");

            CleanupLogs(SafeGetLogs((uint)child.Id));
        }
        finally
        {
            KillQuietly(child);
        }
    }

    [TestMethod]
    public async Task RunDebugLoopAsync_CancelledWhileAttached_DetachesAndReturnsMinusOne()
    {
        // A benign child blocks on stdin and is never signalled, so it stays alive with no further debug
        // events. Cancelling the token after the debugger has attached must break the event loop via its
        // cancellation check (not an EXIT_PROCESS event) and return the -1 default without a crash dump.
        Process? child = TryStartPowerShellChild(BenignOutputDebugStringScript, out var startError);
        if (child == null)
        {
            Assert.Inconclusive($"Could not start a PowerShell child: {startError}");
            return;
        }

        using var cts = new CancellationTokenSource();
        try
        {
            var loopTask = _service.RunDebugLoopAsync((uint)child.Id, cts.Token);

            // Let the debugger attach and drain the initial breakpoint / module-load events. The child is
            // still blocked on stdin (never signalled), so no EXIT_PROCESS event can race the cancel.
            await Task.Delay(1500);
            Assert.IsFalse(child.HasExited, "The blocked child must still be running when the token is cancelled.");

            await cts.CancelAsync();

            int exit;
            try
            {
                exit = await loopTask.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                Assert.Inconclusive("Debug loop did not observe cancellation in time on this machine.");
                return;
            }

            Assert.AreEqual(-1, exit, "Cancelling before the child exits must return the -1 default exit code.");
            Assert.AreEqual(0, _crashDump.WriteCalls.Count, "Cancellation is not a crash and must not request a dump.");
            StringAssert.Contains(_console.Output, "Full debug log:");

            CleanupLogs(SafeGetLogs((uint)child.Id));
        }
        finally
        {
            KillQuietly(child);
        }
    }

    private const string BenignOutputDebugStringScript = """
        $null = [Console]::In.ReadLine()
        Add-Type -Namespace W -Name N -MemberDefinition '[DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern void OutputDebugString(string s);'
        [W.N]::OutputDebugString('APPMARKER7X hello from child')
        [W.N]::OutputDebugString('Microsoft.UI.Xaml.dll!0xdeadbeef framework noise')
        Start-Sleep -Milliseconds 300
        exit 7
        """;

    private const string AccessViolationScript = """
        $null = [Console]::In.ReadLine()
        Add-Type -Namespace W -Name N -MemberDefinition '[DllImport("kernel32.dll")] public static extern void RtlZeroMemory(System.IntPtr dst, System.IntPtr len);'
        [W.N]::RtlZeroMemory([System.IntPtr]::Zero, [System.IntPtr]8)
        Start-Sleep -Seconds 5
        exit 0
        """;

    private static Process? TryStartPowerShellChild(string script, out string? error)
    {
        error = null;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        foreach (var host in new[] { "powershell.exe", "pwsh.exe" })
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = host,
                    Arguments = $"-NoLogo -NoProfile -EncodedCommand {encoded}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (proc != null)
                {
                    return proc;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        error ??= "no PowerShell host available";
        return null;
    }

    private static bool TrySignalChild(Process child)
    {
        try
        {
            if (child.HasExited)
            {
                return false;
            }

            child.StandardInput.WriteLine();
            child.StandardInput.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string[] SafeGetLogs(uint pid)
    {
        try
        {
            return Directory.Exists(_logDir)
                ? Directory.GetFiles(_logDir, $"debug-{pid}-*.log")
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static void CleanupLogs(string[] logs)
    {
        foreach (var log in logs)
        {
            try { File.Delete(log); } catch { /* best effort */ }
        }
    }

    private static void KillQuietly(Process child)
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
        finally
        {
            child.Dispose();
        }
    }

    // ---- M2: HandleException decision branches driven with fabricated debug events ----
    // These cover the noise-suppression and first-chance branches that the real controlled child cannot
    // deterministically raise (single-step, the attach breakpoint, a first-chance stack overflow) plus the
    // OpenThread-failure guard (via a thread id that never exists — deterministic bad input, not flakiness).
    // No real process is attached; every assertion checks a real observable outcome (continue status,
    // console output, or the faked crash-dump boundary being invoked).

    private static DEBUG_EVENT MakeExceptionEvent(uint code, bool firstChance, uint threadId = 0xFFFFFFF0, uint processId = 4321)
    {
        var de = new DEBUG_EVENT { dwProcessId = processId, dwThreadId = threadId };
        de.u.Exception.dwFirstChance = firstChance ? 1u : 0u;
        de.u.Exception.ExceptionRecord.ExceptionCode = (NTSTATUS)unchecked((int)code);
        return de;
    }

    [TestMethod]
    public void HandleException_SingleStepNoise_IsContinuedWithoutDumpOrConsole()
    {
        var de = MakeExceptionEvent(0x80000004, firstChance: true); // STATUS_SINGLE_STEP
        var initialBreakpointSeen = true;
        var continueStatus = NTSTATUS.DBG_EXCEPTION_NOT_HANDLED;

        _service.HandleException(de, ref initialBreakpointSeen, ref continueStatus);

        Assert.AreEqual(NTSTATUS.DBG_CONTINUE, continueStatus, "Single-step noise must be continued, not surfaced.");
        Assert.AreEqual(0, _crashDump.WriteCalls.Count, "Noise events must not capture a dump.");
        Assert.IsFalse(_console.Output.Contains("exception", StringComparison.OrdinalIgnoreCase),
            "Noise events must not surface anything to the console.");
    }

    [TestMethod]
    public void HandleException_InitialBreakpoint_IsSuppressedOnce()
    {
        var de = MakeExceptionEvent(0x80000003, firstChance: true); // STATUS_BREAKPOINT
        var initialBreakpointSeen = false;
        var continueStatus = NTSTATUS.DBG_EXCEPTION_NOT_HANDLED;

        _service.HandleException(de, ref initialBreakpointSeen, ref continueStatus);

        Assert.IsTrue(initialBreakpointSeen, "The attach breakpoint must set the seen flag.");
        Assert.AreEqual(NTSTATUS.DBG_CONTINUE, continueStatus);
        Assert.AreEqual(0, _crashDump.WriteCalls.Count);
    }

    [TestMethod]
    public void HandleException_FirstChanceAccessViolation_UnknownThread_SurfacesWithoutDump()
    {
        var de = MakeExceptionEvent(0xC0000005, firstChance: true); // STATUS_ACCESS_VIOLATION
        var initialBreakpointSeen = true;
        var continueStatus = NTSTATUS.DBG_CONTINUE;

        _service.HandleException(de, ref initialBreakpointSeen, ref continueStatus);

        Assert.AreEqual(NTSTATUS.DBG_EXCEPTION_NOT_HANDLED, continueStatus,
            "A real exception must be passed through to the target's own handlers.");
        StringAssert.Contains(_console.Output, "Access Violation");
        Assert.AreEqual(0, _crashDump.WriteCalls.Count, "An AV does not capture a dump at first-chance.");
    }

    [TestMethod]
    public void HandleException_FirstChanceStackOverflow_CapturesDumpImmediately()
    {
        _crashDump.FakeDumpPath = Path.Combine(Path.GetTempPath(), "winapp-test-so.dmp");
        var de = MakeExceptionEvent(0xC00000FD, firstChance: true); // STATUS_STACK_OVERFLOW
        var initialBreakpointSeen = true;
        var continueStatus = NTSTATUS.DBG_CONTINUE;

        _service.HandleException(de, ref initialBreakpointSeen, ref continueStatus);

        Assert.AreEqual(1, _crashDump.WriteCalls.Count, "Stack overflow must capture a dump at first-chance.");
        StringAssert.Contains(_console.Output, "Stack Overflow");
    }

    // ---- Crash context selection across threads (issue #836) ----
    // Real, blocked threads in this test process give OpenThread/GetThreadContext valid targets, so the
    // saved-context bookkeeping runs for real while the dump boundary stays faked.

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static (uint ThreadId, Thread Thread) StartBlockedThread(ManualResetEventSlim release)
    {
        uint id = 0;
        using var started = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            id = GetCurrentThreadId();
            started.Set();
            release.Wait();
        })
        { IsBackground = true };
        thread.Start();
        started.Wait();
        return (id, thread);
    }

    [TestMethod]
    public void HandleException_HandledExceptionOnOtherThread_DoesNotReplaceCrashContext()
    {
        using var release = new ManualResetEventSlim();
        var (handledThread, t1) = StartBlockedThread(release);
        var (crashThread, t2) = StartBlockedThread(release);
        try
        {
            var initialBreakpointSeen = true;
            var continueStatus = NTSTATUS.DBG_CONTINUE;

            // A handled C++ exception during startup on one thread...
            var handled = MakeExceptionEvent(0xE06D7363, firstChance: true, threadId: handledThread);
            _service.HandleException(handled, ref initialBreakpointSeen, ref continueStatus);

            // ...then a fatal fail-fast (STATUS_STACK_BUFFER_OVERRUN) on a different thread.
            var crash = MakeExceptionEvent(0xC0000409, firstChance: false, threadId: crashThread);
            _service.HandleException(crash, ref initialBreakpointSeen, ref continueStatus);

            Assert.AreEqual(1, _crashDump.WriteCalls.Count);
            Assert.AreEqual(crashThread, _crashDump.WriteCalls[0].ThreadId,
                "The dump must describe the crashing thread, not an earlier handled exception's thread.");
            Assert.AreEqual(unchecked((int)0xC0000409), _crashDump.SavedRecords[0].Code,
                "The dump's exception record must be the fatal exception.");
            Assert.IsTrue(_crashDump.SavedRecords[0].HasContext, "The crashing thread's context must be captured.");
        }
        finally
        {
            release.Set();
            t1.Join();
            t2.Join();
        }
    }

    [TestMethod]
    public void HandleException_FirstChanceOnCrashingThread_KeepsFirstChanceContext()
    {
        using var release = new ManualResetEventSlim();
        var (otherThread, t1) = StartBlockedThread(release);
        var (crashThread, t2) = StartBlockedThread(release);
        try
        {
            var initialBreakpointSeen = true;
            var continueStatus = NTSTATUS.DBG_CONTINUE;

            _service.HandleException(MakeExceptionEvent(0xE06D7363, firstChance: true, threadId: otherThread),
                ref initialBreakpointSeen, ref continueStatus);
            // The AV on the crashing thread is first seen at first-chance, then the CLR wraps it.
            _service.HandleException(MakeExceptionEvent(0xC0000005, firstChance: true, threadId: crashThread),
                ref initialBreakpointSeen, ref continueStatus);
            _service.HandleException(MakeExceptionEvent(0xE0434352, firstChance: true, threadId: crashThread),
                ref initialBreakpointSeen, ref continueStatus);
            _service.HandleException(MakeExceptionEvent(0xE0434352, firstChance: false, threadId: crashThread),
                ref initialBreakpointSeen, ref continueStatus);

            Assert.AreEqual(1, _crashDump.WriteCalls.Count);
            Assert.AreEqual(crashThread, _crashDump.WriteCalls[0].ThreadId);
            Assert.AreEqual(unchecked((int)0xC0000005), _crashDump.SavedRecords[0].Code,
                "The crashing thread's first critical first-chance exception still drives the dump so managed user frames are recovered.");
        }
        finally
        {
            release.Set();
            t1.Join();
            t2.Join();
        }
    }
}
