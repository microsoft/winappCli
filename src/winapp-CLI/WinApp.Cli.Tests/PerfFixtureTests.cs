// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
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

                analysis.Manifest.AnalyzerVersion = 0;
                File.WriteAllText(Path.Join(analysis.Directory, "manifest.json"),
                    JsonSerializer.Serialize(analysis.Manifest, PerfJsonContext.Default.PerfAnalysisManifest));
                var rebuilt = PerfAnalysisStore.Open(gcCapture.FullName, gcTimeout.Token);
                Assert.AreEqual(1, rebuilt.Manifest.AnalyzerVersion);
                var rebuiltResult = PerfQuery.Execute(rebuilt, new(View: "gc", Limit: 100));
                Assert.AreEqual(JsonSerializer.Serialize(result, PerfJsonContext.Default.PerfQueryResult),
                    JsonSerializer.Serialize(rebuiltResult, PerfJsonContext.Default.PerfQueryResult));
                rebuilt.Manifest.DecoderVersion = 0;
                File.WriteAllText(Path.Join(rebuilt.Directory, "manifest.json"),
                    JsonSerializer.Serialize(rebuilt.Manifest, PerfJsonContext.Default.PerfAnalysisManifest));
                var redecoded = PerfAnalysisStore.Open(gcCapture.FullName, gcTimeout.Token);
                Assert.AreEqual(1, redecoded.Manifest.DecoderVersion);
                Assert.AreEqual(JsonSerializer.Serialize(result, PerfJsonContext.Default.PerfQueryResult),
                    JsonSerializer.Serialize(PerfQuery.Execute(redecoded, new(View: "gc", Limit: 100)),
                        PerfJsonContext.Default.PerfQueryResult));
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

    private static async Task<string> BuildFixtureAsync(string name, DirectoryInfo directory, bool publish,
        CancellationToken token)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Join(repository.FullName, "version.json")))
        {
            repository = repository.Parent;
        }
        Assert.IsNotNull(repository, "Could not locate the repository root.");
        var project = Path.Join(repository.FullName, "src", "winapp-CLI", "WinApp.Cli.Tests",
            "TestApps", name, name + ".csproj");
        var output = Path.Join(directory.FullName, name);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                publish ? "publish" : "build", project, "--configuration", "Release",
                "--output", output, "--artifacts-path", Path.Join(directory.FullName, name + "-build"),
                "--disable-build-servers", "--nologo",
            },
        };
        if (publish)
        {
            start.ArgumentList.Add("--runtime");
            start.ArgumentList.Add(RuntimeInformation.RuntimeIdentifier);
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
