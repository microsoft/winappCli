// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO.Pipes;
using System.Reflection;
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
public partial class GuestProcessHostTests
{
    private static string CommandInterpreter =>
        TestPaths.SystemExecutable("cmd.exe");

    // The exact Win32 code a native launch failure surfaces depends on GetLastError still being
    // intact when the CLR captures it. Some hosted CI images run process-creation instrumentation
    // (observed on Azure DevOps Microsoft-hosted agents) that resets the thread's last error to 0
    // before capture, so the raw code is unreliable there. GitHub Actions validates it; on Azure
    // DevOps we assert the structured failure but skip the exact-code check. TF_BUILD is set only
    // on Azure DevOps agents, never on GitHub Actions.
    private static bool Win32ErrorIsReliable =>
        !string.Equals(
            Environment.GetEnvironmentVariable("TF_BUILD"),
            "True",
            StringComparison.OrdinalIgnoreCase);

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
            return Task.CompletedTask;
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
    public async Task Start_PreservesOpaqueArgumentsRawStreamsEnvironmentAndDirectory()
    {
        var script = TestPaths.TempFile("barrier-args", ".ps1");
        var directory = Path.GetDirectoryName(script)!;
        await File.WriteAllTextAsync(script, """
            $all = [Environment]::GetCommandLineArgs()
            $index = [Array]::IndexOf($all, $PSCommandPath)
            $text = ''
            for ($i = $index + 1; $i -lt $all.Length; $i++) {
                $text += [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($all[$i])) + "`n"
            }
            $text += [Environment]::CurrentDirectory + "`n" + $env:WINAPP_UI_WORKFLOW_ID + "`n" + $env:WINAPP_CLI_TELEMETRY_OPTOUT + "`n"
            $stdout = [Console]::OpenStandardOutput()
            $bytes = [Text.Encoding]::UTF8.GetBytes($text)
            $stdout.Write($bytes, 0, $bytes.Length)
            [Console]::OpenStandardInput().CopyTo($stdout)
            $err = [byte[]](0, 255, 10, 128)
            [Console]::OpenStandardError().Write($err, 0, $err.Length)
            exit 7
            """, TestContext.CancellationToken);
        string[] arguments = ["", "two words", "日本語 😀", "--", "--json=bogus", "--self-test",
            "\"quoted\"", @"C:\a b\", "tab\tvalue", @"slashes\\", "\\\\\"quote", " \\\\\\\" "];
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        try
        {
            await using var host = new GuestProcessHostFactory().Start(new GuestExecRequest
            {
                Executable = TestPaths.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = ["-NoProfile", "-NonInteractive", "-File", script, .. arguments],
                WorkingDirectory = directory,
                Environment = new Dictionary<string, string>
                {
                    ["WINAPP_UI_WORKFLOW_ID"] = "unicode-😀",
                    ["WINAPP_CLI_TELEMETRY_OPTOUT"] = "1",
                },
            }, (stream, bytes) =>
            {
                var target = stream == GuestStreamId.StandardOutput ? stdout : stderr;
                target.Write(bytes.Span);
                return Task.CompletedTask;
            });

            byte[] input = [0, 255, 128, 13, 10, .. Encoding.UTF8.GetBytes("日本語 😀")];
            await host.WriteStandardInputAsync(input, TestContext.CancellationToken);
            host.CloseStandardInput();
            Assert.AreEqual(7, await host.WaitForExitAsync(TestContext.CancellationToken));
            var prefix = string.Join('\n', arguments.Select(a => Convert.ToBase64String(Encoding.UTF8.GetBytes(a)))) +
                $"\n{directory}\nunicode-😀\n1\n";
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(prefix).Concat(input).ToArray(), stdout.ToArray());
            byte[] expectedError = [0, 255, 10, 128];
            CollectionAssert.AreEqual(expectedError, stderr.ToArray());
        }
        finally
        {
            File.Delete(script);
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
            Arguments = ["/c", "echo workflow=%WINAPP_UI_WORKFLOW_ID%"],

            // This is how the forwarded Cooperative UI Turns owner context reaches guest children.
            Environment = new Dictionary<string, string> { ["WINAPP_UI_WORKFLOW_ID"] = "token-123" },
        };

        var output = new StringBuilder();
        var host = GuestProcessHost.Start(request, (_, data) =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
            }
            return Task.CompletedTask;
        });

        await using (host)
        {
            await host.WaitForExitAsync(TestContext.CancellationTokenSource.Token);

            StringAssert.Contains(output.ToString(), "workflow=token-123", StringComparison.Ordinal);
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
    public async Task WaitForExit_AwaitsOutputDeliveryBeforeReadingMore()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var output = new StringBuilder();
        await using var host = GuestProcessHost.Start(
            new GuestExecRequest
            {
                Executable = CommandInterpreter,
                Arguments = ["/c", "for /L %i in (1,1,10000) do @echo line-%i"],
            },
            async (_, data) =>
            {
                Interlocked.Increment(ref callbacks);
                entered.TrySetResult();
                await gate.Task;
                output.Append(Encoding.UTF8.GetString(data.Span));
            });
        var completion = host.WaitForExitAsync(TestContext.CancellationTokenSource.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100, TestContext.CancellationTokenSource.Token);
            Assert.AreEqual(1, Volatile.Read(ref callbacks));
            Assert.IsFalse(completion.IsCompleted);
        }
        finally
        {
            gate.TrySetResult();
        }

        await completion.WaitAsync(TimeSpan.FromSeconds(10));
        StringAssert.Contains(output.ToString(), "line-10000");
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
            return Task.CompletedTask;
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
    public async Task Stop_FullStandardInputPipeDoesNotDelayTheKillDeadline()
    {
        var (host, _) = Start("/c", "ping -n 120 127.0.0.1 > nul");
        await using (host)
        {
            var write = host.WriteStandardInputAsync(new byte[4 * 1024 * 1024], CancellationToken.None);
            await Task.Delay(100, TestContext.CancellationTokenSource.Token);
            Assert.IsFalse(write.IsCompleted, "The child must have a full, unread stdin pipe.");
            await host.StopAsync(TimeSpan.FromMilliseconds(200), TestContext.CancellationTokenSource.Token)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await write.WaitAsync(TimeSpan.FromSeconds(5));
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

        var host = GuestProcessHost.Start(request, (_, _) => Task.CompletedTask);
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
        // Cancelling one operation must take its
        // whole process tree with it, while the agent and every other operation keep running.
        // Agent-level containment alone would leave the grandchild alive until agent teardown.
        var cancelledMarker = TestPaths.TempFile("cancelled-grandchild", ".pid");
        var survivorMarker = TestPaths.TempFile("survivor-grandchild", ".pid");

        var cancelled = GuestProcessHost.Start(SpawningRequest(cancelledMarker), (_, _) => Task.CompletedTask);
        var survivor = GuestProcessHost.Start(SpawningRequest(survivorMarker), (_, _) => Task.CompletedTask);

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

    [TestMethod]
    public void Start_MissingExecutable_ReportsStructuredFailure()
    {
        var request = new GuestExecRequest
        {
            Executable = TestPaths.TempFile("does-not-exist", ".exe"),
            Arguments = [],
        };

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => GuestProcessHost.Start(request, (_, _) => Task.CompletedTask));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
        Assert.IsNotNull(failure.Error.UserAction);
        if (Win32ErrorIsReliable)
        {
            Assert.AreEqual("2", failure.Error.Context!["win32Error"]);
            StringAssert.Contains(failure.Error.Message, new System.ComponentModel.Win32Exception(2).Message);
        }
    }

    [TestMethod]
    public void Start_MissingWorkingDirectory_ReportsNativeError()
    {
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            GuestProcessHost.Start(new GuestExecRequest
            {
                Executable = CommandInterpreter,
                Arguments = ["/c", "exit", "0"],
                WorkingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            }, (_, _) => Task.CompletedTask));
        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
        if (Win32ErrorIsReliable)
        {
            Assert.AreEqual("267", failure.Error.Context!["win32Error"]);
            StringAssert.Contains(failure.Error.Message, new System.ComponentModel.Win32Exception(267).Message);
        }
    }

    [TestMethod]
    public void Start_InvalidExecutable_ReportsWindowsReason()
    {
        var executable = TestPaths.TempFile("not-a-program", ".txt");
        File.WriteAllText(executable, "This is not a Windows executable.");
        try
        {
            var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
                GuestProcessHost.Start(new GuestExecRequest
                {
                    Executable = executable,
                    Arguments = [],
                }, (_, _) => Task.CompletedTask));

            Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
            StringAssert.Contains(failure.Error.Message, executable);
            if (Win32ErrorIsReliable)
            {
                Assert.AreEqual("193", failure.Error.Context!["win32Error"]);
                StringAssert.Contains(failure.Error.Message, new System.ComponentModel.Win32Exception(193).Message);
            }
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [TestMethod]
    [DataRow(@".\guest-command.exe", false)]
    [DataRow("guest-command.exe", false)]
    [DataRow("guest-command", false)]
    [DataRow("guest-command.exe", true)]
    public async Task Start_ResolvesExecutableInRequestedDirectoryOrPath(string executable, bool usePath)
    {
        var directory = TestPaths.TempRoot("guest-command");
        Directory.CreateDirectory(directory);
        File.Copy(CommandInterpreter, Path.Combine(directory, "guest-command.exe"));
        try
        {
            var output = new StringBuilder();
            await using var host = GuestProcessHost.Start(new GuestExecRequest
            {
                Executable = executable,
                Arguments = ["/c", "echo resolved && exit /b 23"],
                WorkingDirectory = usePath ? null : directory,
                Environment = usePath ? new Dictionary<string, string> { ["PATH"] = directory } : null,
            }, (_, bytes) =>
            {
                output.Append(Encoding.UTF8.GetString(bytes.Span));
                return Task.CompletedTask;
            });

            Assert.AreEqual(23, await host.WaitForExitAsync(TestContext.CancellationToken));
            StringAssert.Contains(output.ToString(), "resolved");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Start_SearchesSystemExecutableAndTracksTheActualChild()
    {
        var output = new StringBuilder();
        await using var host = GuestProcessHost.Start(new GuestExecRequest
        {
            Executable = "powershell.exe",
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write($PID); exit 23"],
        }, (_, bytes) =>
        {
            output.Append(Encoding.UTF8.GetString(bytes.Span));
            return Task.CompletedTask;
        });
        Assert.AreEqual(23, await host.WaitForExitAsync(TestContext.CancellationToken));
        Assert.AreEqual(host.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), output.ToString(),
            "The tracked process is the payload itself, not a winapp intermediary.");
        Assert.IsTrue(host.StartTicksUtc > 0, "Short-lived children must retain their process identity.");
    }

    [TestMethod]
    public async Task Start_ContainsChildAndDoesNotInheritUnlistedHandle()
    {
        using var canary = new EventWaitHandle(false, EventResetMode.ManualReset);
        var handle = canary.SafeWaitHandle.DangerousGetHandle();
        Assert.IsTrue(SetHandleInformation(handle, 1, 1));
        var script = $$"""
            Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            public static class Probe {
                [DllImport("kernel32.dll")] public static extern bool SetEvent(IntPtr handle);
                [DllImport("kernel32.dll")] public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
                [DllImport("kernel32.dll", SetLastError=true)] public static extern bool QueryInformationJobObject(
                    IntPtr job, int kind, IntPtr result, uint size, IntPtr returned);
                public static bool InOwnJob(int pid, int agentPid) {
                    // NULL selects the immediate job even when the agent has an outer job.
                    // PowerShell Add-Type can leave its compiler child in the job; do not assume
                    // a one-process list. The creating test host must not be in this inner job.
                    var buffer = Marshal.AllocHGlobal(4096);
                    try {
                        if (!QueryInformationJobObject(IntPtr.Zero, 3, buffer, 4096, IntPtr.Zero)) return false;
                        int count = Marshal.ReadInt32(buffer, 4);
                        bool found = false;
                        for (int i = 0; i < count; i++) {
                            long id = Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size).ToInt64();
                            if (id == agentPid) return false;
                            if (id == pid) found = true;
                        }
                        return found;
                    } finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            '@
            $member = $false
            if (-not [Probe]::IsProcessInJob([IntPtr](-1), [IntPtr]::Zero, [ref]$member) -or -not $member) { exit 91 }
            if (-not [Probe]::InOwnJob($PID, {{Environment.ProcessId}})) { exit 92 }
            $null = [Probe]::SetEvent([IntPtr]({{handle.ToInt64()}}))
            [Console]::Write($PID)
            """;
        var output = new StringBuilder();
        await using var host = GuestProcessHost.Start(new GuestExecRequest
        {
            Executable = TestPaths.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", script],
        }, (_, bytes) =>
        {
            output.Append(Encoding.UTF8.GetString(bytes.Span));
            return Task.CompletedTask;
        });
        Assert.AreEqual(0, await host.WaitForExitAsync(TestContext.CancellationToken), output.ToString());
        Assert.AreEqual(host.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), output.ToString());
        Assert.IsFalse(canary.WaitOne(0), "Only stdin/stdout/stderr may be inherited, not this inheritable event.");
        GC.KeepAlive(canary);
    }

    [TestMethod]
    public async Task Stop_ExitedRootStillTerminatesDescendantsHoldingOutput()
    {
        // cmd exits immediately, leaving ping with inherited output handles.
        var (host, _) = Start("/c", "start /b ping -n 120 127.0.0.1");
        await using (host)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (IsStillRunning(host.ProcessId)) { await Task.Delay(10, timeout.Token); }
            await host.StopAsync(TimeSpan.FromMilliseconds(200), TestContext.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await host.WaitForExitAsync(TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task Stop_ExitedRootAllowsDescendantOutputToDrainWithinGracePeriod()
    {
        var parent = TestPaths.TempFile("draining-parent", ".cmd");
        var child = TestPaths.TempFile("draining-child", ".cmd");
        await File.WriteAllTextAsync(child,
            "@echo off\r\nping -n 2 127.0.0.1 >nul\r\necho final-line\r\n", TestContext.CancellationToken);
        await File.WriteAllTextAsync(parent,
            $"@echo off\r\nstart \"\" /b cmd.exe /d /c \"{child}\"\r\n", TestContext.CancellationToken);
        try
        {
            var (host, output) = Start("/d", "/c", parent);
            await using (host)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                while (IsStillRunning(host.ProcessId)) { await Task.Delay(10, timeout.Token); }
                await host.StopAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                StringAssert.Contains(output.StandardOutput.ToString(), "final-line");
            }
        }
        finally
        {
            File.Delete(parent);
            File.Delete(child);
        }
    }

    [TestMethod]
    public void CreateProcess_NonInheritableRedirectionHandleFailsClosed()
    {
        using var job = GuestJobObject.Create();
        using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        // Keep the handle open so another thread cannot recycle its numeric value. A handle
        // without INHERIT is invalid in PROC_THREAD_ATTRIBUTE_HANDLE_LIST.
        Assert.IsTrue(SetHandleInformation(input.ClientSafePipeHandle.DangerousGetHandle(), 1, 0));
        var startInfo = new ProcessStartInfo(CommandInterpreter);
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("exit 0");
        var create = typeof(GuestProcessHost).GetMethod("CreateProcess", BindingFlags.Static | BindingFlags.NonPublic)!;
        var invocation = Assert.ThrowsExactly<TargetInvocationException>(() =>
            create.Invoke(null, [startInfo, job, input, output, error]));
        var failure = invocation.InnerException as ExecutionTargetException;
        Assert.IsNotNull(failure);
        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
        if (Win32ErrorIsReliable)
        {
            Assert.AreEqual("87", failure.Error.Context!["win32Error"], "Invalid inherited handles must not start a payload.");
        }
    }

    [TestMethod]
    public async Task Start_RepeatedFailuresAndNoOpsReleaseOwnedHandles()
    {
        // Inspect only this operation's owners, not the test runner's global HandleCount:
        // runtime waits and other tests can allocate handles even in a nonparallel test.
        static T Field<T>(GuestProcessHost host, string name) =>
            (T)typeof(GuestProcessHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        for (var i = 0; i < 20; i++)
        {
            Start_MissingExecutable_ReportsStructuredFailure();
            Start_MissingWorkingDirectory_ReportsNativeError();
            Assert.ThrowsExactly<ExecutionTargetException>(() => GuestProcessHost.Start(new GuestExecRequest
            {
                Executable = CommandInterpreter,
                Arguments = ["bad\0argument"],
            }, (_, _) => Task.CompletedTask));
            var (host, _) = Start("/c", "exit", "0");
            SafeHandle[] handles =
            [
                Field<GuestJobObject>(host, "_job").Handle,
                Field<Process>(host, "_process").SafeHandle,
                Field<AnonymousPipeServerStream>(host, "_input").SafePipeHandle,
                Field<AnonymousPipeServerStream>(host, "_output").SafePipeHandle,
                Field<AnonymousPipeServerStream>(host, "_error").SafePipeHandle,
            ];
            await using (host)
            {
                Assert.AreEqual(0, await host.WaitForExitAsync(TestContext.CancellationToken));
            }
            foreach (var handle in handles)
            {
                Assert.IsTrue(handle.IsClosed, "Every operation-owned job/process/pipe handle must be closed.");
            }
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    /// <summary>MSTest injects this; used for per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;
}
