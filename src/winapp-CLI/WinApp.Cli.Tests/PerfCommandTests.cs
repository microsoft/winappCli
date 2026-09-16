// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class PerfCommandTests : BaseCommandTests
{
    private FakeSystemUiQuery systemQuery = null!;
    private FakeUiAutomationService uiAutomation = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        systemQuery = new();
        uiAutomation = new();
        return services
            .AddSingleton<ISystemUiQuery>(systemQuery)
            .AddSingleton<IUiAutomation>(uiAutomation);
    }

    [TestMethod]
    [DataRow("perf start --app --output unused --json")]
    [DataRow("perf start --app 1 --output unused --json=not-bool")]
    [DataRow("perf start --output unused --json")]
    [DataRow("perf start --pid 1 --output unused --json")]
    [DataRow("perf unknown --json")]
    [DataRow("perf analyze missing --limit 101 --json")]
    [DataRow("perf analyze missing --view frames --type Button --json")]
    [DataRow("perf analyze missing --view call --json")]
    [DataRow("perf analyze missing --view call --id c1 --depth 5 --json")]
    [DataRow("perf analyze missing --view gc --thread 1 --json")]
    [DataRow("perf analyze missing --view calls --sort self --json")]
    [DataRow("perf analyze missing --view gc --sort self --json")]
    [DataRow("perf analyze missing --view hotspots --sort duration --json")]
    [DataRow("perf analyze missing --view frames --min-frame-ms 10 --json")]
    [DataRow("perf analyze missing --view hotspots --min-frame-ms -1 --json")]
    [DataRow("perf analyze missing --view events --family layout --json")]
    [DataRow("perf --json")]
    public async Task InvalidPerfInvocationsEmitOnlyStructuredErrors(string command)
    {
        var (stdout, stderr, code) = await InvokeProgramAsync(command.Split(' '));
        Assert.AreEqual(1, code);
        Assert.AreEqual("", stdout);
        using var error = JsonDocument.Parse(stderr);
        Assert.AreEqual("invalid_arguments", error.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    [DataRow("--app", "1234")]
    [DataRow("--app", "WinUIBenchmarkApp")]
    [DataRow("--app", "Benchmark")]
    [DataRow("--app", "WinUI Performance Lab")]
    [DataRow("-a", "WinUIBenchmarkApp")]
    public void PerfStartAcceptsRequiredAppSelector(string option, string selector)
    {
        var command = GetRequiredService<PerfCommand>().Subcommands.Single(c => c.Name == "start");
        var app = command.Options.OfType<Option<string>>().Single(o => o.Name == "--app");
        var parse = command.Parse([option, selector, "--output", "unused"]);
        Assert.IsEmpty(parse.Errors);
        Assert.AreEqual(selector, parse.GetValue(app));
    }

    [TestMethod]
    [DataRow("call")]
    [DataRow("element")]
    public void ExpansionDepthDefaultsAreResolvedByView(string view)
    {
        var command = GetRequiredService<PerfCommand>().Subcommands.Single(c => c.Name == "analyze");
        var depth = command.Options.OfType<Option<int?>>().Single(o => o.Name == "--depth");
        var parse = command.Parse(["capture", "--view", view, "--id", "id"]);
        Assert.IsEmpty(parse.Errors);
        Assert.IsNull(parse.GetValue(depth));
    }

    [TestMethod]
    public void AnalyzeAcceptsGcDurationAndHotspotThreshold()
    {
        var command = GetRequiredService<PerfCommand>().Subcommands.Single(c => c.Name == "analyze");
        var view = command.Options.OfType<Option<string>>().Single(o => o.Name == "--view");
        var sort = command.Options.OfType<Option<string>>().Single(o => o.Name == "--sort");
        var minimum = command.Options.OfType<Option<double?>>().Single(o => o.Name == "--min-frame-ms");

        var gc = command.Parse(["capture", "--view", "gc", "--sort", "duration"]);
        Assert.IsEmpty(gc.Errors);
        Assert.AreEqual("gc", gc.GetValue(view));
        Assert.AreEqual("duration", gc.GetValue(sort));

        var hotspots = command.Parse(["capture", "--view", "hotspots", "--min-frame-ms", "8.5"]);
        Assert.IsEmpty(hotspots.Errors);
        Assert.AreEqual("hotspots", hotspots.GetValue(view));
        Assert.AreEqual(8.5, hotspots.GetValue(minimum));
    }

    [TestMethod]
    [DataRow("pid")]
    [DataRow("name")]
    [DataRow("partial")]
    [DataRow("title")]
    public async Task PerfStartResolvesAppBeforePreparingCapture(string kind)
    {
        using var process = Process.GetCurrentProcess();
        var info = new UiProcessInfo(process.Id, "WinUIBenchmarkApp", 42, "WinUI Performance Lab");
        systemQuery.ProcessesById[process.Id] = info;
        var selector = kind switch
        {
            "pid" => process.Id.ToString(),
            "name" => info.ProcessName,
            "partial" => "Benchmark",
            _ => info.MainWindowTitle!,
        };
        if (kind == "name") { systemQuery.ByNameResult = [info]; }
        if (kind == "partial") { systemQuery.MatchingResult = [info]; }
        if (kind == "title") { uiAutomation.WindowsByTitleResult = [((nint)42, process.Id, selector)]; }

        // A nonempty output proves resolution reached preparation without launching a worker.
        var output = _tempDirectory.CreateSubdirectory("capture");
        File.WriteAllText(Path.Join(output.FullName, "keep.txt"), "keep");
        var (code, error) = await InvokePerfStartErrorAsync(selector, output.FullName);
        Assert.AreEqual(1, code);
        StringAssert.Contains(error, "The capture output directory must be empty.");
        Assert.IsFalse(File.Exists(Path.Join(output.FullName, "capture.json")));
    }

    [TestMethod]
    public async Task PerfStartMissingAppReturnsStructuredErrorWithoutCreatingCapture()
    {
        var output = Path.Join(_tempDirectory.FullName, "capture");
        var (code, error) = await InvokePerfStartErrorAsync("MissingApp", output);
        Assert.AreEqual(1, code);
        StringAssert.Contains(error, "No running app found matching 'MissingApp'.");
        Assert.IsFalse(Directory.Exists(output));
    }

    [TestMethod]
    public async Task PerfStartAmbiguousAppPreservesResolverGuidanceWithoutCreatingCapture()
    {
        systemQuery.ByNameResult =
        [
            new UiProcessInfo(101, "MyApp", 1, "First"),
            new UiProcessInfo(102, "MyApp", 2, "Second"),
        ];
        var output = Path.Join(_tempDirectory.FullName, "capture");
        var (code, error) = await InvokePerfStartErrorAsync("MyApp", output);
        Assert.AreEqual(1, code);
        StringAssert.Contains(error, "PID 101");
        StringAssert.Contains(error, "PID 102");
        StringAssert.Contains(error, "Use --app with a PID or a more specific window title.");
        Assert.IsFalse(Directory.Exists(output));
    }

    private async Task<(int ExitCode, string Message)> InvokePerfStartErrorAsync(string app, string output)
    {
        var previous = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            var code = await ParseAndInvokeWithCaptureAsync(GetRequiredService<PerfCommand>(),
                ["start", "--app", app, "--output", output, "--json"]);
            using var error = JsonDocument.Parse(stderr.ToString());
            Assert.AreEqual("performance_error", error.RootElement.GetProperty("code").GetString());
            return (code, error.RootElement.GetProperty("message").GetString()!);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [TestMethod]
    public async Task InvalidProfileValueKeepsTheRunJsonEnvelope()
    {
        var (stdout, stderr, code) = await InvokeProgramAsync(["run", ".", "--profile", "unused", "--profile-duration-sec", "nope", "--json"]);
        Assert.AreEqual(1, code);
        Assert.AreEqual("", stderr);
        using var error = JsonDocument.Parse(stdout);
        Assert.IsTrue(error.RootElement.TryGetProperty("Error", out _));
    }

    [TestMethod]
    [DataRow("--profile-duration-sec", "2")]
    [DataRow("--profile-max-size-mib", "2")]
    public async Task ProfileSettingsWithoutProfileAreRejected(string option, string value)
    {
        var command = GetRequiredService<RunCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(command, [".", option, value, "--json"]);
        Assert.AreEqual(1, exit);
        using var error = JsonDocument.Parse(TestAnsiConsole.Output);
        StringAssert.Contains(error.RootElement.GetProperty("Error").GetString(), "require --profile");
    }
}
