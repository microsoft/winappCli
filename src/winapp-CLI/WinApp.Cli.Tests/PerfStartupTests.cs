// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public sealed partial class PerfStartupTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(Architecture.Arm64, Architecture.X64, false)]
    [DataRow(Architecture.Arm64, Architecture.X86, false)]
    [DataRow(Architecture.Arm64, Architecture.Arm64, true)]
    [DataRow(Architecture.X64, Architecture.X64, true)]
    public void StartupProfilingRequiresANativeCliOnArm64(Architecture osArchitecture,
        Architecture processArchitecture, bool supported)
    {
        var error = PerfStartupGate.HostArchitectureError(osArchitecture, processArchitecture);
        Assert.AreEqual(supported, error is null);
        if (!supported)
        {
            StringAssert.Contains(error, "ARM64 winapp CLI");
            StringAssert.Contains(error, "perf start");
        }
    }

    [TestMethod]
    public void ProfileEnvironmentPreservesInheritedValuesAndOverridesDebugHeapCaseInsensitively()
    {
        var inherited = new Dictionary<string, string?>
        {
            ["Path"] = @"C:\Windows",
            ["_no_debug_heap"] = "0",
            ["WINAPP_UNICODE"] = "\u00e9",
            ["EMPTY"] = "",
            ["OMITTED"] = null,
        };
        var block = PerfStartupGate.CreateEnvironmentBlock(inherited);
        string[] expected =
        [
            "_no_debug_heap=1", "EMPTY=", @"Path=C:\Windows", "WINAPP_UNICODE=\u00e9",
        ];
        CollectionAssert.AreEquivalent(expected, block.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        Assert.IsTrue(block.EndsWith("\0\0", StringComparison.Ordinal));
        Assert.AreEqual("0", inherited["_no_debug_heap"], "Only the child's environment should change.");
        Assert.AreEqual("_NO_DEBUG_HEAP=1\0\0", PerfStartupGate.CreateEnvironmentBlock([]));
    }

    [TestMethod]
    public async Task EmulatedX64CliRejectsProfileBeforeCreatingArtifacts()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.Arm64)
        {
            Assert.Inconclusive("This case requires ARM64 Windows.");
        }
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Join(repository.FullName, "version.json")))
        {
            repository = repository.Parent;
        }
        Assert.IsNotNull(repository);
        var executable = Path.Join(repository.FullName, "artifacts", "cli", "win-x64", "winapp.exe");
        if (!File.Exists(executable))
        {
            Assert.Inconclusive("Build the x64 CLI artifact to exercise its emulated startup-profiling rejection.");
        }
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Emulated-Cli-");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        Process? process = null;
        try
        {
            var capture = Path.Join(directory.FullName, "capture");
            process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "run", Path.Join(directory.FullName, "missing.csproj"),
                    "--profile", capture, "--detach", "--json",
                },
            }) ?? throw new InvalidOperationException("Could not launch the emulated x64 CLI.");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(1, process.ExitCode);
            Assert.AreEqual("", await stderr);
            using var result = JsonDocument.Parse(await stdout);
            StringAssert.Contains(result.RootElement.GetProperty("Error").GetString(), "ARM64 winapp CLI");
            Assert.IsFalse(result.RootElement.TryGetProperty("Profile", out _));
            Assert.IsFalse(Directory.Exists(capture));
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); }
                await process.WaitForExitAsync(CancellationToken.None);
                process.Dispose();
            }
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task ProfiledProcessKeepsNormalHeapFlagsAndReceivesTheChildEnvironment()
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Heap-");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        using var baseline = Process.Start(new ProcessStartInfo(Path.Join(Environment.SystemDirectory, "ping.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            ArgumentList = { "-n", "30", "127.0.0.1" },
        }) ?? throw new InvalidOperationException("Could not launch the normal heap baseline.");
        try
        {
            var baselineFlags = ReadHeapFlags(baseline);
            var marker = Path.Join(directory.FullName, "environment.txt");
            using var process = await PerfStartupGate.LaunchAsync(
                Path.Join(Environment.SystemDirectory, "cmd.exe"),
                $"/d /c set PATH>\"{marker}\" & exit /b 7",
                directory.FullName, LaunchStdioMode.Suppress, pid =>
                {
                    using var target = Process.GetProcessById(checked((int)pid));
                    Assert.AreEqual(baselineFlags & 0x70u, ReadHeapFlags(target) & 0x70u,
                        "The startup debugger must not enable additional heap checks.");
                    return Task.CompletedTask;
                }, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(7, process.ExitCode);
            var variables = await File.ReadAllLinesAsync(marker, timeout.Token);
            var path = variables.Single(line => line.StartsWith("PATH=", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(Environment.GetEnvironmentVariable("PATH"), path[5..]);
        }
        finally
        {
            if (!baseline.HasExited) { baseline.Kill(); }
            await baseline.WaitForExitAsync(CancellationToken.None);
            directory.Delete(recursive: true);
        }
    }

    private static unsafe uint ReadHeapFlags(Process process)
    {
        Assert.AreEqual(0, NtQueryInformationProcess(process.Handle, 0, out var information,
            (uint)sizeof(ProcessBasicInformation), out _));
        uint flags = 0;
        Assert.AreNotEqual(0, ReadProcessMemory(process.Handle, information.Peb + 0xbc,
            &flags, sizeof(uint), out var read));
        Assert.AreEqual((nuint)sizeof(uint), read);
        return flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint Peb;
        public nint AffinityMask;
        public nint BasePriority;
        public nint ProcessId;
        public nint ParentProcessId;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int informationClass,
        out ProcessBasicInformation information, uint length, out uint returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int ReadProcessMemory(nint process, nint address, void* buffer,
        nuint size, out nuint read);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PrivateSessionBecomesReadyBeforeTheApplicationEntryPointRuns(bool wow64)
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Startup-");
        PrivateEtwSession? session = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var marker = Path.Join(directory.FullName, "main-ran");
        try
        {
            var launcher = new AppLauncherService(NullLogger<AppLauncherService>.Instance);
            using var process = await launcher.LaunchExecutableForProfilingAsync(
                Path.Join(wow64 ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64") :
                    Environment.SystemDirectory, "cmd.exe"), $"/d /c echo started>\"{marker}\" & exit /b 7",
                directory.FullName, LaunchStdioMode.Suppress, pid =>
                {
                    Assert.IsFalse(File.Exists(marker), "The application must not run before recording is ready.");
                    session = new("WinApp-Perf-Startup-" + Guid.NewGuid().ToString("N"),
                        Guid.NewGuid(), checked((int)pid), Path.Join(directory.FullName, "trace.etl"), 16);
                    Assert.IsTrue(session.CanEnable);
                    session.Enable(PerfProviders.Xaml, ulong.MaxValue);
                    Assert.IsFalse(File.Exists(marker), "Provider enablement must precede the application entry point.");
                    return Task.CompletedTask;
                }, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(7, process.ExitCode, "The restored entry-point instruction must run correctly.");
            Assert.IsTrue(File.Exists(marker));
        }
        finally
        {
            session?.Dispose();
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadinessFailureDoesNotLeaveAnApplicationSuspendedOrRunning()
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Startup-Fail-");
        int? pid = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PerfStartupGate.LaunchAsync(
                Path.Join(Environment.SystemDirectory, "cmd.exe"), "/d /c exit /b 0",
                directory.FullName, LaunchStdioMode.Suppress, value =>
                {
                    pid = checked((int)value);
                    throw new InvalidOperationException("Deliberate readiness failure.");
                }, timeout.Token));
            Assert.IsNotNull(pid);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid.Value));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task CancellationBeforeResumeDoesNotExecuteOrLeaveTheApplication()
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Startup-Cancel-");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        int? pid = null;
        var marker = Path.Join(directory.FullName, "main-ran");
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => PerfStartupGate.LaunchAsync(
                Path.Join(Environment.SystemDirectory, "cmd.exe"), $"/d /c echo started>\"{marker}\"",
                directory.FullName, LaunchStdioMode.Suppress, value =>
                {
                    pid = checked((int)value);
                    timeout.Cancel();
                    return Task.CompletedTask;
                }, timeout.Token));
            Assert.IsNotNull(pid);
            Assert.IsFalse(File.Exists(marker));
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid.Value));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task PackagedReadinessUsesWorkerStatusInsteadOfRacingManifestWrites()
    {
        var root = Directory.CreateTempSubdirectory("WinApp-Perf-Startup-Status-");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        Task? controller = null;
        try
        {
            var directory = Path.Join(root.FullName, "capture");
            var service = new PerfCaptureService(new FakeWinappDirectoryService(root), new FakeAppLauncherService());
            var run = new PerfRunCapture(service, directory, 30, 128, false);
            var preparing = run.PrepareAsync(timeout.Token);
            var path = Directory.GetFiles(Path.Join(root.FullName, "perf-control"), "control.json",
                SearchOption.AllDirectories).Single();
            var registration = PerfCaptureService.ReadRegistration(path);
            var capture = PerfCaptureDocument.Load(directory);
            using var current = Process.GetCurrentProcess();
            var identity = PerfProcessIdentity.Read(current);
            using var pipe = new NamedPipeServerStream(PerfControlChannel.PipeName(registration.Id),
                PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            controller = Task.Run(async () =>
            {
                for (var i = 0; i < 3; i++)
                {
                    await pipe.WaitForConnectionAsync(timeout.Token);
                    var request = await PerfControlChannel.ReadAsync(pipe, PerfJsonContext.Default.PerfControlRequest,
                        4096, timeout.Token);
                    Assert.AreEqual("status", request.Operation);
                    Assert.AreEqual(registration.Credential, request.Credential);
                    if (i == 2)
                    {
                        capture.Target = identity;
                        capture.State = "recording";
                        capture.StartupCoverage = PerfStartupGate.Coverage;
                    }
                    await PerfControlChannel.WriteAsync(pipe, new PerfControlResponse(capture),
                        PerfJsonContext.Default.PerfControlResponse, timeout.Token);
                    pipe.Disconnect();
                }
            }, timeout.Token);
            await preparing;
            File.Delete(Path.Join(directory, "capture.json"));

            await run.BindAsync(checked((uint)current.Id), DateTime.UnixEpoch, timeout.Token);

            await controller;
            Assert.IsNull(run.Error);
            Assert.AreEqual("recording", run.Result.State);
            Assert.AreEqual(PerfStartupGate.Coverage, run.Result.StartupCoverage);
        }
        finally
        {
            timeout.Cancel();
            if (controller is not null)
            {
                try { await controller; }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            }
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow("completed", true)]
    [DataRow("failed", true)]
    [DataRow("completed", false)]
    public async Task FinalizedPackagedStartupUsesTheBoundPrivateRegistration(string state, bool matchingTarget)
    {
        var root = Directory.CreateTempSubdirectory("WinApp-Perf-Startup-Final-");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        Task? controller = null;
        try
        {
            var directory = Path.Join(root.FullName, "capture");
            var service = new PerfCaptureService(new FakeWinappDirectoryService(root), new FakeAppLauncherService());
            var run = new PerfRunCapture(service, directory, 1, 128, false);
            var preparing = run.PrepareAsync(timeout.Token);
            var path = Directory.GetFiles(Path.Join(root.FullName, "perf-control"), "control.json",
                SearchOption.AllDirectories).Single();
            var registration = PerfCaptureService.ReadRegistration(path);
            Assert.IsNull(registration.Target);
            var capture = PerfCaptureDocument.Load(directory);
            // An already-exited target must not need to be reopened after recording completes.
            var target = new PerfProcessIdentity(int.MaxValue, DateTime.UtcNow);
            var otherTarget = target with { Pid = int.MaxValue - 1 };
            using var pipe = new NamedPipeServerStream(PerfControlChannel.PipeName(registration.Id),
                PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            controller = Task.Run(async () =>
            {
                await pipe.WaitForConnectionAsync(timeout.Token);
                var request = await PerfControlChannel.ReadAsync(pipe, PerfJsonContext.Default.PerfControlRequest,
                    4096, timeout.Token);
                Assert.AreEqual("status", request.Operation);
                await PerfControlChannel.WriteAsync(pipe, new PerfControlResponse(capture),
                    PerfJsonContext.Default.PerfControlResponse, timeout.Token);
                pipe.Disconnect();

                await pipe.WaitForConnectionAsync(timeout.Token);
                request = await PerfControlChannel.ReadAsync(pipe, PerfJsonContext.Default.PerfControlRequest,
                    4096, timeout.Token);
                Assert.AreEqual("status", request.Operation);
                Assert.AreEqual(registration.Credential, request.Credential);
                File.WriteAllText(path, JsonSerializer.Serialize(
                    registration with { Target = matchingTarget ? target : otherTarget },
                    PerfJsonContext.Default.PerfControlRegistration));
                capture.Target = matchingTarget ? otherTarget : target;
                capture.State = state;
                capture.ReadyQpc = 1;
                capture.StopQpc = 10;
                capture.StartupCoverage = PerfStartupGate.Coverage;
                capture.Error = state == "failed" ? "Deliberate recording failure." : null;
                capture.Save();
                pipe.Disconnect();
            }, timeout.Token);
            await preparing;

            await run.BindAsync(checked((uint)target.Pid), DateTime.UnixEpoch, timeout.Token);

            await controller;
            Assert.AreEqual(matchingTarget ? state : "failed", run.Result.State);
            if (matchingTarget && state == "completed")
            {
                Assert.IsNull(run.Error);
                Assert.AreEqual(PerfStartupGate.Coverage, run.Result.StartupCoverage);
            }
            else if (matchingTarget)
            {
                Assert.AreEqual(capture.Error, run.Error);
            }
            else
            {
                Assert.IsNotNull(run.Error, "An editable capture manifest must not override the registered target.");
            }
        }
        finally
        {
            timeout.Cancel();
            if (controller is not null)
            {
                try { await controller; }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            }
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void StartupBreakpointsMatchSupportedInstructionSets()
    {
        CollectionAssert.AreEqual(new byte[] { 0xcc }, PerfStartupGate.Breakpoint(Machine.Amd64));
        CollectionAssert.AreEqual(new byte[] { 0xcc }, PerfStartupGate.Breakpoint(Machine.I386));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0x3e, 0xd4 }, PerfStartupGate.Breakpoint(Machine.Arm64));
        Assert.Throws<NotSupportedException>(() => PerfStartupGate.Breakpoint(Machine.Arm));
    }
}
