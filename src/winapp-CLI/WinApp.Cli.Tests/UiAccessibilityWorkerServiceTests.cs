// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class UiAccessibilityWorkerServiceTests
{
    private static string _fixtureRoot = null!;
    private static UiAccessibilityWorkerPaths _paths = null!;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static async Task BuildFixture(TestContext context)
    {
        // The fixture lives under the test build output, not a shared temp directory. It never
        // inspects UI or invokes the supplied winapp path.
        _fixtureRoot = Path.Combine(AppContext.BaseDirectory, "aw " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_fixtureRoot);
        var project = Path.Combine(_fixtureRoot, "fixture.csproj");
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>winapp-accessibility-worker</AssemblyName>
                <UseAppHost>true</UseAppHost>
                <NuGetAudit>false</NuGetAudit>
              </PropertyGroup>
            </Project>
            """, context.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_fixtureRoot, "Program.cs"), """
            using System;
            using System.Diagnostics;
            using System.IO;
            using System.Linq;
            using System.Text;
            using System.Text.Json;
            using System.Threading.Tasks;

            internal static class Program
            {
                private static async Task<int> Main(string[] args)
                {
                    Console.OutputEncoding = new UTF8Encoding(false);
                    if (args.Length > 0 && args[0] == "fixture-child")
                    {
                        await Task.Delay(TimeSpan.FromMinutes(2));
                        return 0;
                    }
                    Process child = null;
                    if (args.Contains("424242"))
                    {
                        var start = new ProcessStartInfo(Environment.ProcessPath)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        start.ArgumentList.Add("fixture-child");
                        child = Process.Start(start);
                    }
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        arguments = args,
                        processId = Environment.ProcessId,
                        childProcessId = child == null ? 0 : child.Id
                    }));
                    Console.Out.Flush();
                    for (var i = 0; i < 2048; i++)
                        Console.Error.WriteLine("fixture progress \u03bb " + i);
                    Console.Error.WriteLine("fixture diagnostic");
                    if (child != null)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(2));
                        child.Dispose();
                    }
                    return 17;
                }
            }
            """, context.CancellationToken);

        var native = Path.Combine(_fixtureRoot, "winapp.exe");
        await File.WriteAllTextAsync(native, "Benign native-path placeholder; never executed by this fixture.", context.CancellationToken);
        var workerDirectory = Path.Combine(_fixtureRoot, "accessibility");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var build = await new ProcessRunner().RunAsync(new ProcessRunRequest("dotnet",
            ["build", project, "--nologo", "--verbosity", "quiet", "--output", workerDirectory, "-p:UseSharedCompilation=false"]),
            cancellationToken: timeout.Token);
        Assert.AreEqual(0, build.ExitCode, build.StandardOutput + build.StandardError);
        _paths = new(native, Path.Combine(workerDirectory, "winapp-accessibility-worker.exe"));
        Assert.IsTrue(File.Exists(_paths.Worker));
    }

    [ClassCleanup]
    public static void CleanupFixture()
    {
        if (_fixtureRoot is not null && Directory.Exists(_fixtureRoot))
        {
            Directory.Delete(_fixtureRoot, recursive: true);
        }
    }

    [TestMethod]
    public void NativePathResolutionUsesTheRunningBundleNotTheWorkingDirectory()
    {
        var paths = UiAccessibilityWorkerPaths.Resolve(@"C:\Demo Folder\winapp.exe", @"D:\unrelated", "winapp");
        Assert.AreEqual(@"C:\Demo Folder\winapp.exe", paths.Winapp);
        Assert.AreEqual(@"C:\Demo Folder\accessibility\winapp-accessibility-worker.exe", paths.Worker);
    }

    [TestMethod]
    public void DotnetHostResolutionUsesTheAdjacentWinappApphostNotDotnet()
    {
        var paths = UiAccessibilityWorkerPaths.Resolve(@"C:\Program Files\dotnet\dotnet.exe", @"D:\Local Demo\", "winapp");
        Assert.AreEqual(@"D:\Local Demo\winapp.exe", paths.Winapp);
        Assert.AreEqual(@"D:\Local Demo\accessibility\winapp-accessibility-worker.exe", paths.Worker);
    }

    [TestMethod]
    [DataRow(null, "winapp")]
    [DataRow("winapp.exe", "winapp")]
    [DataRow(@"C:\testhost.exe", "WinApp.Cli.Tests")]
    [DataRow(@"C:\dotnet.exe", "testhost")]
    public void UnknownOrTestHostIdentityCannotBecomeTheNativeTool(string? processPath, string entryAssembly)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            UiAccessibilityWorkerPaths.Resolve(processPath, @"C:\bundle", entryAssembly));
    }

    [TestMethod]
    public async Task MissingWorkerDoesNotSearchPathOrFallBackToSampleCheckout()
    {
        var runner = new RunnerFake();
        var paths = _paths with { Worker = Path.Combine(_fixtureRoot, "missing", "winapp-accessibility-worker.exe") };
        var launcher = new UiAccessibilityWorkerService(runner, () => paths);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exception = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            launcher.RunAsync(["scan", "--pid", "1"], output, error, TestContext.CancellationToken));
        StringAssert.Contains(exception.Message, paths.Worker);
        StringAssert.Contains(exception.Message, "complete local accessibility demo bundle");
        StringAssert.Contains(exception.Message, "accessibility folder next to winapp.exe");
        Assert.AreEqual(0, runner.Calls);
        Assert.IsEmpty(output.ToString());
        Assert.IsEmpty(error.ToString());
    }

    [TestMethod]
    public async Task DotnetHostedCliWithoutNativeApphostFailsBeforeLaunchingWorker()
    {
        var runner = new RunnerFake();
        var paths = _paths with { Winapp = Path.Combine(_fixtureRoot, "absent", "winapp.exe") };
        var launcher = new UiAccessibilityWorkerService(runner, () => paths);
        var exception = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            launcher.RunAsync(["scan", "--pid", "1"], TextWriter.Null, TextWriter.Null, TestContext.CancellationToken));
        StringAssert.Contains(exception.Message, paths.Winapp);
        Assert.AreEqual(0, runner.Calls);
    }

    [TestMethod]
    [DataRow("scan")]
    [DataRow("survey")]
    [DataRow("explore")]
    [DataRow("journey")]
    [DataRow("loop")]
    [DataRow("check")]
    [DataRow("repair")]
    [DataRow("assessment-template")]
    [DataRow("assess")]
    public async Task NativePathIsInjectedOnlyForBackendOperationsThatAcceptIt(string verb)
    {
        var runner = new RunnerFake();
        var launcher = new UiAccessibilityWorkerService(runner, () => _paths);
        Assert.AreEqual(3, await launcher.RunAsync([verb], TextWriter.Null, TextWriter.Null, TestContext.CancellationToken));
        Assert.IsNotNull(runner.Request);
        Assert.AreEqual(_paths.Worker, runner.Request.FileName);
        CollectionAssert.AreEqual(verb is "assess" or "assessment-template" ? new[] { verb } : [verb, "--winapp", _paths.Winapp],
            runner.Request.Arguments.ToArray());
    }

    [TestMethod]
    public async Task RealCompanionFixturePreservesArgumentBoundariesStreamsAndExitCode()
    {
        var launcher = new UiAccessibilityWorkerService(new ProcessRunner(), () => _paths);
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] arguments =
        [
            "scan", "--pid", "1", "--json", "--output",
            Path.Combine(_fixtureRoot, @"evidence & (literal)\trailing\") + " \"quoted\"",
        ];
        var code = await launcher.RunAsync(arguments, output, error, TestContext.CancellationToken);
        Assert.AreEqual(17, code);
        using var json = JsonDocument.Parse(output.ToString());
        string[] expectedArguments = [.. arguments, "--winapp", _paths.Winapp];
        CollectionAssert.AreEqual(expectedArguments,
            json.RootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()).ToArray());
        var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(2049, lines);
        Assert.AreEqual("fixture progress \u03bb 0", lines[0]);
        Assert.AreEqual("fixture progress \u03bb 2047", lines[^2]);
        Assert.AreEqual("fixture diagnostic", lines[^1]);
    }

    [TestMethod]
    public async Task PreCancelledCallCannotResolveOrStartAWorker()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new RunnerFake();
        var launcher = new UiAccessibilityWorkerService(runner, () =>
            throw new AssertFailedException("Pre-cancelled calls must not resolve paths."));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            launcher.RunAsync(["scan"], TextWriter.Null, TextWriter.Null, cancellation.Token));
        Assert.AreEqual(0, runner.Calls);
    }

    [TestMethod]
    public async Task CancellationTerminatesTheOwnedFixtureAndItsChild()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(30));
        using var output = new ReadyWriter();
        using var error = new StringWriter();
        var launcher = new UiAccessibilityWorkerService(new ProcessRunner(), () => _paths);
        var task = launcher.RunAsync(["scan", "--pid", "424242"], output, error, cancellation.Token);
        Process? worker = null;
        Process? child = null;
        try
        {
            var ready = await output.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.CancellationToken);
            using var json = JsonDocument.Parse(ready);
            worker = Process.GetProcessById(json.RootElement.GetProperty("processId").GetInt32());
            child = Process.GetProcessById(json.RootElement.GetProperty("childProcessId").GetInt32());
            Assert.IsFalse(worker.HasExited);
            Assert.IsFalse(child.HasExited);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => task);
            await Task.WhenAll(worker.WaitForExitAsync(TestContext.CancellationToken), child.WaitForExitAsync(TestContext.CancellationToken))
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
            Assert.IsTrue(worker.HasExited);
            Assert.IsTrue(child.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            if (worker is { HasExited: false })
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync(CancellationToken.None);
            }
            if (child is { HasExited: false })
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
            }
            try { await task; }
            catch (OperationCanceledException) { }
            worker?.Dispose();
            child?.Dispose();
        }
    }

    private sealed class ReadyWriter : StringWriter
    {
        internal TaskCompletionSource<string> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value is not null)
            {
                Ready.TrySetResult(value);
            }
        }
    }

    private sealed class RunnerFake : IProcessRunner
    {
        internal int Calls { get; private set; }
        internal ProcessRunRequest? Request { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request, Action<string>? onOutputLine = null,
            Action<string>? onErrorLine = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            return Task.FromResult(new ProcessRunResult(3, "", ""));
        }
    }
}
