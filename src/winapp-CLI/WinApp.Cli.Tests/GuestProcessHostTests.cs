// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="GuestProcessHost"/>, which runs guest child processes inside a Job Object.
/// </summary>
/// <remarks>
/// These launch real processes (<c>cmd.exe</c>), because the behaviour under test — argument
/// fidelity, stream separation, exit codes, and whether a process tree actually dies — cannot be
/// verified against a fake. No Windows Sandbox is involved: the agent runs ordinary child processes,
/// so the same code path is exercised on the host.
/// </remarks>
[TestClass]
public class GuestProcessHostTests
{
    private static string CommandInterpreter =>
        TestPaths.SystemExecutable("cmd.exe");

    private sealed record Captured(StringBuilder StandardOutput, StringBuilder StandardError);

    private static (GuestProcessHost Host, Captured Output) Start(params string[] arguments)
    {
        var captured = new Captured(new StringBuilder(), new StringBuilder());
        var request = new GuestExecRequest
        {
            Executable = CommandInterpreter,
            Arguments = [.. arguments],
        };

        var host = GuestProcessHost.Start(request, (stream, data) =>
        {
            var target = stream == GuestStreamId.StandardError ? captured.StandardError : captured.StandardOutput;
            lock (target)
            {
                target.Append(Encoding.UTF8.GetString(data.Span));
            }
        });

        return (host, captured);
    }

    [TestMethod]
    public async Task Start_CapturesStdoutAndExitCode()
    {
        var (host, output) = Start("/c", "echo hello && exit /b 7");

        await using (host)
        {
            var exitCode = await host.WaitForExitAsync(TestContext.CancellationTokenSource.Token);

            Assert.AreEqual(7, exitCode, "The application's exit code must survive intact.");
            StringAssert.Contains(output.StandardOutput.ToString(), "hello", StringComparison.Ordinal);
            Assert.IsTrue(host.ProcessId > 0);
        }
    }

    [TestMethod]
    public async Task Start_SeparatesStandardErrorFromStandardOutput()
    {
        var (host, output) = Start("/c", "echo out && echo err 1>&2");

        await using (host)
        {
            await host.WaitForExitAsync(TestContext.CancellationTokenSource.Token);

            StringAssert.Contains(output.StandardOutput.ToString(), "out", StringComparison.Ordinal);
            StringAssert.Contains(output.StandardError.ToString(), "err", StringComparison.Ordinal);

            // Mixing the streams would make a --json payload unparseable, since diagnostics must
            // never land on the machine-readable channel.
            Assert.IsFalse(
                output.StandardOutput.ToString().Contains("err", StringComparison.Ordinal),
                "stderr must not leak into stdout.");
        }
    }

    [TestMethod]
    public async Task Start_PreservesArgumentBoundariesWithSpaces()
    {
        // A batch file is used because %1/%2 substitution is what actually proves each argument
        // arrived as its own value. Unicode fidelity is deliberately not asserted here: cmd's echo
        // writes in the OEM code page, so a mismatch would measure the console, not our forwarding.
        // Unicode round-tripping is covered at the protocol layer instead.
        var script = TestPaths.TempFile("args", ".cmd");
        await File.WriteAllTextAsync(
            script,
            "@echo off\r\necho first=[%~1]\r\necho second=[%~2]\r\n",
            TestContext.CancellationTokenSource.Token);

        try
        {
            var (host, output) = Start("/c", script, "a b", "c d e");

            await using (host)
            {
                await host.WaitForExitAsync(TestContext.CancellationTokenSource.Token);

                var text = output.StandardOutput.ToString();
                StringAssert.Contains(text, "first=[a b]", StringComparison.Ordinal);

                // A second argument with spaces confirms boundaries hold across multiple values.
                // Shell metacharacters are deliberately not tested through cmd.exe: cmd re-parses
                // its command line with rules that differ from the standard C runtime quoting
                // ArgumentList applies, so a failure there would measure cmd, not this code. The
                // real guest target is winapp.exe, an ordinary executable.
                StringAssert.Contains(text, "second=[c d e]", StringComparison.Ordinal);
            }
        }
        finally
        {
            File.Delete(script);
        }
    }

    [TestMethod]
    public async Task Start_AppliesForwardedEnvironment()
    {
        var request = new GuestExecRequest
        {
            Executable = CommandInterpreter,
            Arguments = ["/c", "echo owner=%WINAPP_UI_OWNER_ID%"],

            // This is how the forwarded Cooperative UI Turns owner context reaches guest children.
            Environment = new Dictionary<string, string> { ["WINAPP_UI_OWNER_ID"] = "token-123" },
        };

        var output = new StringBuilder();
        var host = GuestProcessHost.Start(request, (_, data) =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
            }
        });

        await using (host)
        {
            await host.WaitForExitAsync(TestContext.CancellationTokenSource.Token);

            StringAssert.Contains(output.ToString(), "owner=token-123", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task WaitForExit_DrainsOutputBeforeReturning()
    {
        // Reporting an exit code while output frames are still in flight would let a caller observe
        // a completed operation with truncated output.
        var (host, output) = Start("/c", "for /L %i in (1,1,400) do @echo line-%i");

        await using (host)
        {
            await host.WaitForExitAsync(TestContext.CancellationTokenSource.Token);

            StringAssert.Contains(output.StandardOutput.ToString(), "line-400", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task StandardInput_IsForwardedToTheChild()
    {
        // findstr reads standard input directly, so this exercises stdin forwarding without cmd's
        // parse-time variable expansion getting in the way.
        var request = new GuestExecRequest
        {
            Executable = TestPaths.SystemExecutable("findstr.exe"),
            Arguments = ["."],
        };

        var output = new StringBuilder();
        var host = GuestProcessHost.Start(request, (stream, data) =>
        {
            if (stream == GuestStreamId.StandardOutput)
            {
                lock (output)
                {
                    output.Append(Encoding.UTF8.GetString(data.Span));
                }
            }
        });

        await using (host)
        {
            await host.WriteStandardInputAsync(
                Encoding.UTF8.GetBytes("typed-line\r\n"),
                TestContext.CancellationTokenSource.Token);

            // Many console applications only finish once they see end of input.
            host.CloseStandardInput();

            await host.WaitForExitAsync(TestContext.CancellationTokenSource.Token);

            StringAssert.Contains(output.ToString(), "typed-line", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task Stop_TerminatesAProcessThatIgnoresGracefulShutdown()
    {
        // A process that never exits on its own: graceful stop must time out and the job must kill it.
        var (host, _) = Start("/c", "ping -n 120 127.0.0.1 > nul");

        await using (host)
        {
            var exitCode = await host.StopAsync(TimeSpan.FromMilliseconds(300), TestContext.CancellationTokenSource.Token);

            Assert.AreNotEqual(0, exitCode, "A terminated process must not report success.");
        }
    }

    [TestMethod]
    public async Task Dispose_KillsTheWholeProcessTree()
    {
        // A grandchild is what actually matters here. Killing only the tracked process ID orphans
        // it, and in a Sandbox an orphan keeps holding files the next deployment has to replace --
        // so the test captures the grandchild's own process ID and asserts on that, not just on the
        // process winapp started.
        var marker = TestPaths.TempFile("grandchild", ".pid");

        var script =
            $"$p = Start-Process ping -ArgumentList '-n','120','127.0.0.1' -PassThru -WindowStyle Hidden; " +
            $"Set-Content -LiteralPath '{marker}' -Value $p.Id; Start-Sleep -Seconds 120";

        var request = new GuestExecRequest
        {
            Executable = TestPaths.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", script],
        };

        var host = GuestProcessHost.Start(request, (_, _) => { });
        var processId = host.ProcessId;

        try
        {
            var grandchildId = await ReadGrandchildIdAsync(marker, TestContext.CancellationTokenSource.Token);

            Assert.IsTrue(IsStillRunning(grandchildId), "The grandchild should be running before disposal.");

            await host.DisposeAsync();

            // Give the kernel a moment to tear the job down.
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.CancellationTokenSource.Token);

            Assert.IsFalse(
                IsStillRunning(processId),
                "Disposing the host must terminate the process it started.");

            Assert.IsFalse(
                IsStillRunning(grandchildId),
                "Disposing the host must terminate the whole job, including grandchildren.");
        }
        finally
        {
            await host.DisposeAsync();

            try
            {
                File.Delete(marker);
            }
            catch (IOException)
            {
                // Temp cleanup is not worth failing a test over.
            }
        }
    }

    /// <summary>Waits for the spawned grandchild to publish its process ID.</summary>
    private static async Task<int> ReadGrandchildIdAsync(string marker, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(marker) &&
                    int.TryParse(File.ReadAllText(marker).Trim(), out var id) &&
                    id > 0)
                {
                    return id;
                }
            }
            catch (IOException)
            {
                // Mid-write; the next poll sees the complete value.
            }

            await Task.Delay(100, cancellationToken);
        }

        Assert.Fail("The grandchild never reported its process ID.");
        return 0;
    }

    /// <summary>Whether a process ID still names a live process.</summary>
    /// <remarks>
    /// Looked up by ID rather than filtered out of <c>Process.GetProcesses()</c>: that call returns
    /// a <see cref="System.Diagnostics.Process"/> for every process on the machine, and disposing
    /// only the one that matched would leak the rest.
    /// </remarks>
    private static bool IsStillRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with that ID exists, which is exactly the outcome under test.
            return false;
        }
    }

    [TestMethod]
    public async Task StopAsync_KillsTheGrandchildOfOneOperationAndLeavesAnotherRunning()
    {
        // The property the containment barrier exists for: cancelling one operation must take its
        // whole process tree with it, while the agent and every other operation keep running.
        // Agent-level containment alone would leave the grandchild alive until agent teardown.
        var barrier = FindWinappBinary();

        if (barrier is null)
        {
            Assert.Inconclusive("The winapp binary is not built, so the containment barrier cannot be exercised.");
            return;
        }

        var cancelledMarker = TestPaths.TempFile("cancelled-grandchild", ".pid");
        var survivorMarker = TestPaths.TempFile("survivor-grandchild", ".pid");

        var cancelled = GuestProcessHost.Start(SpawningRequest(cancelledMarker), (_, _) => { }, barrier);
        var survivor = GuestProcessHost.Start(SpawningRequest(survivorMarker), (_, _) => { }, barrier);

        try
        {
            var cancelledParent = cancelled.ProcessId;
            var cancelledGrandchild = await ReadGrandchildIdAsync(
                cancelledMarker, TestContext.CancellationTokenSource.Token);
            var survivorGrandchild = await ReadGrandchildIdAsync(
                survivorMarker, TestContext.CancellationTokenSource.Token);

            Assert.IsTrue(IsStillRunning(cancelledGrandchild));
            Assert.IsTrue(IsStillRunning(survivorGrandchild));

            // Cancel only the first operation. The agent stays alive, so nothing here depends on
            // agent-level containment.
            await cancelled.StopAsync(TimeSpan.FromMilliseconds(300), TestContext.CancellationTokenSource.Token);
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.CancellationTokenSource.Token);

            Assert.IsFalse(IsStillRunning(cancelledParent), "The cancelled operation's process must exit.");
            Assert.IsFalse(
                IsStillRunning(cancelledGrandchild),
                "The cancelled operation's grandchild must exit with its job, not survive until agent teardown.");

            Assert.IsTrue(
                IsStillRunning(survivorGrandchild),
                "Cancelling one operation must not disturb another that is still running.");
        }
        finally
        {
            await cancelled.DisposeAsync();
            await survivor.DisposeAsync();
            TryDeleteFile(cancelledMarker);
            TryDeleteFile(survivorMarker);
        }
    }

    /// <summary>A request whose command spawns a grandchild and publishes its process ID.</summary>
    private static GuestExecRequest SpawningRequest(string marker)
    {
        var script =
            $"$p = Start-Process ping -ArgumentList '-n','120','127.0.0.1' -PassThru -WindowStyle Hidden; " +
            $"Set-Content -LiteralPath '{marker}' -Value $p.Id; Start-Sleep -Seconds 120";

        return new GuestExecRequest
        {
            Executable = TestPaths.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", script],
        };
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Temp cleanup is not worth failing a test over.
        }
    }

    /// <summary>Locates the built winapp binary, which acts as the containment barrier.</summary>
    /// <remarks>
    /// The barrier is a winapp verb, so exercising it needs the real executable rather than the
    /// test host. Returning null lets the test report inconclusive instead of passing vacuously.
    /// </remarks>
    private static string? FindWinappBinary()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = new DirectoryInfo(Path.Join(directory.FullName, "src", "winapp-CLI", "WinApp.Cli", "bin"));

            if (candidate.Exists)
            {
                var binary = candidate
                    .EnumerateFiles("winapp.exe", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (binary is not null)
                {
                    return binary.FullName;
                }
            }

            directory = directory.Parent;
        }

        return null;
    }

    [TestMethod]
    public void Start_MissingExecutable_ReportsStructuredFailure()    {
        var request = new GuestExecRequest
        {
            Executable = TestPaths.TempFile("does-not-exist", ".exe"),
            Arguments = [],
        };

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => GuestProcessHost.Start(request, (_, _) => { }));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
        Assert.IsNotNull(failure.Error.UserAction);
    }

    /// <summary>MSTest injects this; used for per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;
}
