// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PerfFixtureTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NativeAndManagedFixturesProduceRealNativeAndGcCaptures()
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Fixtures-");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            var probeDirectory = await BuildFixtureAsync("PerfNativeProbe", directory, true, timeout.Token);
            var gcDirectory = await BuildFixtureAsync("PerfGcFixture", directory, false, timeout.Token);
            var probe = Path.Join(probeDirectory, "WinApp.Cli.Tests.exe");
            Assert.IsTrue(File.Exists(probe), "The native probe must be published as an executable.");

            var nativeCapture = directory.CreateSubdirectory("native-capture");
            using var captureTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            captureTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            var nativeOutput = await RunAsync(new(probe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "fixture", "0", nativeCapture.FullName },
            }, captureTimeout.Token);
            using (var report = JsonDocument.Parse(nativeOutput))
            {
                Assert.AreEqual(1, report.RootElement.GetProperty("Events").GetInt32());
                Assert.AreEqual(0, report.RootElement.GetProperty("DecodeErrors").GetInt32());
                Assert.IsTrue(report.RootElement.GetProperty("Descriptors").EnumerateObject()
                    .Any(descriptor => descriptor.Name.StartsWith("42/0/0/4 ", StringComparison.Ordinal)));
            }
            Assert.IsTrue(File.Exists(Path.Join(nativeCapture.FullName, "done")));
            Assert.IsTrue(nativeCapture.GetFiles("trace.etl*").Any(file => file.Length > 0));
            await VerifyStartupEventAsync(probe, directory.CreateSubdirectory("startup-capture"), timeout.Token);

            var gcCapture = directory.CreateSubdirectory("gc-capture");
            using var gcTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            gcTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var fixture = Process.Start(new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { Path.Join(gcDirectory, "PerfGcFixture.dll"), gcCapture.FullName },
            }) ?? throw new InvalidOperationException("Could not start PerfGcFixture.");
            var stderr = fixture.StandardError.ReadToEndAsync();
            try
            {
                var pid = await fixture.StandardOutput.ReadLineAsync(gcTimeout.Token);
                Assert.IsNotNull(pid, "The GC fixture exited before its managed runtime became ready.");
                Assert.AreEqual(fixture.Id.ToString(CultureInfo.InvariantCulture), pid,
                    "The managed runtime must be ready before the probe attaches.");
                var stdout = fixture.StandardOutput.ReadToEndAsync();
                var gcOutput = await RunAsync(new(probe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { "gc", pid, gcCapture.FullName },
                }, gcTimeout.Token);
                using (var report = JsonDocument.Parse(gcOutput))
                {
                    Assert.IsGreaterThan(0, report.RootElement.GetProperty("Events").GetInt32());
                    Assert.AreEqual(0, report.RootElement.GetProperty("DecodeErrors").GetInt32());
                }
                await fixture.WaitForExitAsync(gcTimeout.Token);
                Assert.AreEqual(0, fixture.ExitCode, $"GC fixture failed: {await stdout}\n{await stderr}");

                var capture = PerfCaptureDocument.Load(gcCapture.FullName);
                Assert.AreEqual("completed", capture.State);
                Assert.AreEqual(fixture.Id, capture.Target?.Pid);
                Assert.AreEqual(0u, capture.EventsLost);
                Assert.AreEqual(0u, capture.BuffersLost);
                var analysis = PerfAnalysisStore.Open(gcCapture.FullName, gcTimeout.Token);
                var result = PerfQuery.Execute(analysis, new(View: "gc", Limit: 100));
                Assert.AreEqual(0, analysis.Manifest.DecodeErrors);
                Assert.IsTrue(result.Rows.Any(row =>
                    row.GcInterval is { IsGcSuspension: true, Status: "complete", DurationMs: > 0 }));
                Assert.IsTrue(result.Rows.Any(row =>
                    row.GcInterval is { Kind: "collection", CollectionType: "blocking", Status: "complete", DurationMs: > 0 }));

                analysis.Manifest.AnalyzerVersion = 1;
                File.WriteAllText(Path.Join(analysis.Directory, "manifest.json"),
                    JsonSerializer.Serialize(analysis.Manifest, PerfJsonContext.Default.PerfAnalysisManifest));
                var rebuilt = PerfAnalysisStore.Open(gcCapture.FullName, gcTimeout.Token);
                Assert.AreEqual(2, rebuilt.Manifest.AnalyzerVersion);
                var rebuiltResult = PerfQuery.Execute(rebuilt, new(View: "gc", Limit: 100));
                Assert.AreEqual(JsonSerializer.Serialize(result, PerfJsonContext.Default.PerfQueryResult),
                    JsonSerializer.Serialize(rebuiltResult, PerfJsonContext.Default.PerfQueryResult));
            }
            finally
            {
                await StopAsync(fixture);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task EmulatedX64StartupRecordsTheFirstNativeEvent()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.Arm64)
        {
            Assert.Inconclusive("x64 startup is covered by the native fixture on x64; this case requires ARM64 emulation.");
        }
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Startup-X64-");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            var output = await BuildFixtureAsync("PerfNativeProbe", directory, true, timeout.Token, "win-x64");
            await VerifyStartupEventAsync(Path.Join(output, "WinApp.Cli.Tests.exe"),
                directory.CreateSubdirectory("capture"), timeout.Token);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task VerifyStartupEventAsync(string executable, DirectoryInfo directory, CancellationToken token)
    {
        var provider = Guid.NewGuid();
        PrivateEtwSession? session = null;
        long readyQpc = 0;
        var marker = Path.Join(directory.FullName, "main-ran");
        var launcher = new AppLauncherService(NullLogger<AppLauncherService>.Instance);
        try
        {
            using var process = await launcher.LaunchExecutableForProfilingAsync(executable,
                $"startup-event {provider} \"{directory.FullName}\"", directory.FullName, LaunchStdioMode.Suppress,
                pid =>
                {
                    Assert.IsFalse(File.Exists(marker));
                    session = new("WinApp-Perf-Startup-Fixture-" + Guid.NewGuid().ToString("N"),
                        Guid.NewGuid(), checked((int)pid), Path.Join(directory.FullName, "trace.etl"), 16);
                    Assert.IsTrue(session.CanEnable);
                    session.Enable(provider, ulong.MaxValue);
                    readyQpc = Stopwatch.GetTimestamp();
                    return Task.CompletedTask;
                }, token);
            try
            {
                while (!File.Exists(marker)) { await Task.Delay(20, token); }
                Assert.IsNotNull(session);
                session.Stop();
                Assert.IsTrue(session.Stopped);
                Assert.AreEqual(0u, session.EventsLost);
                Assert.AreEqual(0u, session.BuffersLost);
                var events = new List<PerfRawEvent>();
                var clock = PerfEtwReader.Read(Path.Join(directory.FullName, "trace.etl"),
                    checked((int)process.ProcessId), [provider], events.Add, token);
                Assert.AreEqual(Stopwatch.Frequency, clock.Frequency);
                Assert.AreEqual(1, events.Count, "The first event emitted after entry-point resume must be recorded.");
                Assert.AreEqual((ushort)42, events[0].EventId);
                Assert.IsTrue(events[0].Qpc >= readyQpc);
                Assert.IsNull(events[0].DecodeError);
                CollectionAssert.AreEqual(new byte[] { 42, 0, 0, 0 }, events[0].Payload);
                File.WriteAllText(Path.Join(directory.FullName, "release"), "");
                await process.WaitForExitAsync(token);
                Assert.AreEqual(7, process.ExitCode, "Entry-point restoration must preserve native execution.");
            }
            finally
            {
                process.Kill();
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static async Task<string> BuildFixtureAsync(string name, DirectoryInfo directory, bool publish,
        CancellationToken token, string? runtime = null)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Join(repository.FullName, "version.json")))
        {
            repository = repository.Parent;
        }
        Assert.IsNotNull(repository, "Could not locate the repository root.");
        var project = Path.Join(repository.FullName, "src", "winapp-CLI", "WinApp.Cli.Tests",
            "TestApps", name, name + ".csproj");
        var outputName = runtime is null ? name : name + "-" + runtime;
        var output = Path.Join(directory.FullName, outputName);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                publish ? "publish" : "build", project, "--configuration", "Release",
                "--output", output, "--artifacts-path", Path.Join(directory.FullName, outputName + "-build"),
                "--disable-build-servers", "--nologo",
            },
        };
        if (publish)
        {
            start.ArgumentList.Add("--runtime");
            start.ArgumentList.Add(runtime ?? RuntimeInformation.RuntimeIdentifier);
            var installer = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio", "Installer");
            if (Directory.Exists(installer))
            {
                start.Environment["PATH"] = installer + Path.PathSeparator + start.Environment["PATH"];
            }
        }
        await RunAsync(start, token);
        return output;
    }

    private static async Task<string> RunAsync(ProcessStartInfo start, CancellationToken token)
    {
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {start.FileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(token);
            var output = await stdout;
            var error = await stderr;
            Assert.AreEqual(0, process.ExitCode,
                $"{start.FileName} {string.Join(' ', start.ArgumentList)} failed:\n{output}\n{error}");
            return output;
        }
        finally
        {
            await StopAsync(process);
        }
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}
