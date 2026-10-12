// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.Performance;

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
    [DataRow("start", "recording", null, "Capture ID: capture-id\nStop: winapp perf stop capture-id\n")]
    [DataRow("mark", "recording", null, "Added mark scenario-start\n")]
    [DataRow("stop", "completed", "requested", "Recording stopped.\n")]
    [DataRow("status", "recording", null, "Capture state: recording.\n")]
    [DataRow("status", "completed", "duration-limit", "Capture state: completed.\nStop reason: duration-limit.\n")]
    [DataRow("status", "failed", "worker-failed", "Capture state: failed.\nStop reason: worker-failed.\n")]
    [DataRow("stop", "failed", "worker-failed", "")]
    [DataRow("mark", "failed", "worker-failed", "")]
    public void CaptureConsoleOutputIsSpecificToTheOperation(string operation, string state,
        string? reason, string expected)
    {
        var capture = new PerfCaptureDocument
        {
            Id = "capture-id", Directory = @"C:\capture", SessionName = "test",
            State = state, StopReason = reason,
        };

        PerfCommand.PrintCapture(capture, false, TestAnsiConsole, operation, "scenario-start");

        Assert.AreEqual(expected, TestAnsiConsole.Output.Replace("\r\n", "\n"));
    }

    [TestMethod]
    public void ConciseCaptureOutputPreservesWarnings()
    {
        var capture = new PerfCaptureDocument
        {
            Id = "capture-id", Directory = @"C:\capture", SessionName = "test", State = "completed",
            Warnings = ["Final loss counters unavailable."],
            ProviderStates = [new(PerfProviders.Clr, "unavailable", null, "Denied.")],
        };

        PerfCommand.PrintCapture(capture, false, TestAnsiConsole, "stop");

        StringAssert.Contains(TestAnsiConsole.Output, "Recording stopped.");
        StringAssert.Contains(TestAnsiConsole.Output, "Final loss counters unavailable.");
        StringAssert.Contains(TestAnsiConsole.Output, "Denied.");
        Assert.IsFalse(TestAnsiConsole.Output.Contains(capture.Id, StringComparison.Ordinal));
        Assert.IsFalse(TestAnsiConsole.Output.Contains(capture.Directory, StringComparison.Ordinal));
    }

    [TestMethod]
    public void MarkOutputOmitsCaptureWarnings()
    {
        var capture = new PerfCaptureDocument
        {
            Id = "capture-id", Directory = @"C:\capture", SessionName = "test", State = "recording",
            Warnings = ["Event filtering unavailable."],
        };

        PerfCommand.PrintCapture(capture, false, TestAnsiConsole, "mark", "scenario-start");

        Assert.AreEqual("Added mark scenario-start\n", TestAnsiConsole.Output.Replace("\r\n", "\n"));
    }

    [TestMethod]
    [DataRow("start")]
    [DataRow("mark")]
    [DataRow("stop")]
    [DataRow("status")]
    public void CaptureJsonStillReturnsTheFullDocument(string operation)
    {
        var capture = new PerfCaptureDocument
        {
            Id = "capture-id", Directory = @"C:\capture", SessionName = "test", State = "recording",
            Markers = [new("scenario-start", 42)],
        };
        var previous = Console.Out;
        using var stdout = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            PerfCommand.PrintCapture(capture, true, TestAnsiConsole, operation, "scenario-start");
        }
        finally
        {
            Console.SetOut(previous);
        }

        Assert.AreEqual(JsonSerializer.Serialize(capture, PerfJsonContext.Default.PerfCaptureDocument),
            stdout.ToString().TrimEnd());
        Assert.AreEqual("", TestAnsiConsole.Output);
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
    [DataRow("perf analyze missing --view resources --thread 1 --json")]
    [DataRow("perf analyze missing --view resources --family layout --json")]
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
    public async Task ResourceAnalysisNeedsNoEtlAndReportsPartialCounterFailures()
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Resource-Command-");
        try
        {
            var capture = new PerfCaptureDocument
            {
                Id = Guid.NewGuid().ToString("N"), Directory = directory.FullName, SessionName = "test",
                Target = new(Environment.ProcessId, DateTime.UtcNow), State = "completed",
                Frequency = 1000, ReadyQpc = 1000, StopQpc = 2000,
                ProcessResources = new()
                {
                    Samples = [new(1000, 10, 20, new(10, 20, 3, 4)), new(2000, 30, 40, new(30, 40, 4, 5))],
                },
            };
            capture.Save();
            string[] args = ["perf", "analyze", directory.FullName, "--view", "resources", "--json"];
            var (stdout, stderr, code) = await InvokeProgramAsync(args);
            Assert.AreEqual(0, code, stderr);
            Assert.AreEqual("", stderr);
            using (var result = JsonDocument.Parse(stdout))
            {
                Assert.AreEqual(40, result.RootElement.GetProperty("processResources").GetProperty("totalCpuMs").GetDouble());
                Assert.AreEqual(2, result.RootElement.GetProperty("rows").GetArrayLength());
            }
            Assert.AreEqual(0, await ParseAndInvokeWithCaptureAsync(GetRequiredService<PerfCommand>(),
                ["analyze", directory.FullName, "--view", "resources"]));
            StringAssert.Contains(TestAnsiConsole.Output, "Private MiB");
            StringAssert.Contains(TestAnsiConsole.Output, "User CPU ms");
            StringAssert.Contains(TestAnsiConsole.Output, "CPU: 40");
            capture.ProcessResources.FailedSamples = 1;
            capture.ProcessResources.LastError = "Counter unavailable";
            capture.Save();
            var partial = await InvokeProgramAsync(args);
            Assert.AreEqual(1, partial.ExitCode);
            using var error = JsonDocument.Parse(partial.Stderr);
            Assert.AreEqual("partial_data", error.RootElement.GetProperty("code").GetString());
            using var evidence = JsonDocument.Parse(partial.Stdout);
            Assert.IsFalse(evidence.RootElement.GetProperty("coverage").GetProperty("complete").GetBoolean());
            Assert.AreEqual(2, evidence.RootElement.GetProperty("rows").GetArrayLength());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task CompositionCallsAndCommitNotificationsHaveDistinctJsonAndTextEvidence()
    {
        var commit = new PerfEvent("device-commit", 15, 15, 7, PerfProviders.DirectComposition,
            25, 0, 0, Guid.Empty, "DCompDeviceCommit", "composition", "info", null,
            new() { ["DeviceId"] = "0000000000001234", ["LastCommittedBatchId"] = "9", ["LastConfirmedBatchId"] = "7" });
        var directory = CreateCachedAnalysis(
            [new("commit", "CommitMainDevice", "composition", 7, null, null, "begin", "end",
                10, 15, 5, null, "complete", 5)],
            events: [commit], compositionState: new(PerfProviders.DirectComposition, "enabled", false));
        try
        {
            var (stdout, stderr, code) = await InvokeProgramAsync(
                ["perf", "analyze", directory.FullName, "--view", "calls", "--family", "composition", "--json"]);
            Assert.AreEqual(0, code, stderr);
            using (var calls = JsonDocument.Parse(stdout))
            {
                Assert.AreEqual(1, calls.RootElement.GetProperty("rows").GetArrayLength());
                Assert.AreEqual(5, calls.RootElement.GetProperty("rows")[0].GetProperty("call").GetProperty("durationMs").GetDouble());
                Assert.AreEqual("enabled", calls.RootElement.GetProperty("directCompositionProvider").GetProperty("state").GetString());
            }
            (stdout, stderr, code) = await InvokeProgramAsync(
                ["perf", "analyze", directory.FullName, "--view", "events", "--event", "DCompDeviceCommit", "--json"]);
            Assert.AreEqual(0, code, stderr);
            using (var events = JsonDocument.Parse(stdout))
            {
                var notification = events.RootElement.GetProperty("rows")[0].GetProperty("event");
                Assert.AreEqual("info", notification.GetProperty("phase").GetString());
                Assert.AreEqual("9", notification.GetProperty("fields").GetProperty("LastCommittedBatchId").GetString());
            }
            Assert.AreEqual(0, await ParseAndInvokeWithCaptureAsync(GetRequiredService<PerfCommand>(),
                ["analyze", directory.FullName]));
            StringAssert.Contains(TestAnsiConsole.Output, "Composition submission");
            StringAssert.Contains(TestAnsiConsole.Output, "CommitMainDevice");
            StringAssert.Contains(TestAnsiConsole.Output, "DirectComposition provider: enabled");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
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

        var parsing = command.Parse(["capture", "--view", "parsing"]);
        Assert.IsEmpty(parsing.Errors);
        Assert.AreEqual("parsing", parsing.GetValue(view));
    }

    [TestMethod]
    public async Task DefaultConsoleReportShowsActivityParsingThenDetailedSummary()
    {
        var parserEvent = new PerfEvent("parse-begin", 10, 10, 7, PerfProviders.Xaml, 1, 0, 1,
            Guid.Empty, "ParseXaml", "parsing", "begin", null,
            new() { ["URI"] = "Views/[MainPage].xaml" });
        var directory = CreateCachedAnalysis(
            [
                new("frame", "Frame", "frames", 7, null, null, "f1", "f2", 0, 40, 40, null, "complete", 20),
                new("parse", "ParseXaml", "parsing", 7, null, "frame", "parse-begin", "parse-end", 10, 20, 10, null, "complete", 10),
            ],
            events: [parserEvent]);
        try
        {
            var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<PerfCommand>(),
                ["analyze", directory.FullName]);
            var output = TestAnsiConsole.Output;

            Assert.AreEqual(0, exitCode);
            var activity = output.IndexOf("Observed UI-thread activity", StringComparison.Ordinal);
            var parsing = output.IndexOf("Parsing by resource", StringComparison.Ordinal);
            var details = output.IndexOf("Detailed summary", StringComparison.Ordinal);
            Assert.IsTrue(activity >= 0 && parsing > activity && details > parsing);
            StringAssert.Contains(output, "Views/[MainPage].xaml");
            StringAssert.Contains(output, "primary UI thread 7");
            var sectionLines = output.Split('\n').Where(line =>
                line.Contains("Observed UI-thread activity", StringComparison.Ordinal) ||
                line.Contains("Parsing by resource", StringComparison.Ordinal) ||
                line.Contains("Detailed summary", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(3, sectionLines);
            Assert.IsTrue(sectionLines.All(line => line.Contains('─') || line.Contains("---", StringComparison.Ordinal)));
            Assert.IsFalse(output.Contains('\u001b'), "Plain-text consoles must not contain ANSI escapes.");
            foreach (var hiddenDetail in new[] { "evidence:", "count=", "mean=", "max=", "p95=", "GC coverage:", "Coverage:", "next offset:" })
            {
                Assert.IsFalse(output.Contains(hiddenDetail, StringComparison.Ordinal), hiddenDetail);
            }
            Assert.IsTrue(output.TrimEnd().EndsWith("Returned 2/2", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow("summary", false)]
    [DataRow("parsing", false)]
    [DataRow("summary", true)]
    [DataRow("parsing", true)]
    public async Task ParsingResourceLabelsPreserveFileNamesAndJsonKeepsFullUris(string view, bool json)
    {
        (string Resource, string Label)[] resources =
        [
            ("Views/[MainPage].xaml", "Views/[MainPage].xaml"),
            ("ms-resource:///Files/Controls/HomePage/Views/MainPage.xaml",
                "ms-resource:///Files...Views/MainPage.xaml"),
            ("ms-resource:///Files/Controls/HomePage/Views/SettingsPage.xaml",
                "ms-resource:///Files...s/SettingsPage.xaml"),
            ("ms-resource:///Files/Microsoft.UI.Xaml/Controls/VeryLongDistinctiveResourceName.xaml",
                "ms-...VeryLongDistinctiveResourceName.xaml"),
            (@"C:\Projects\Example\Controls\HomePage\Views\SettingsPage.xaml",
                @"C:\Projects\Example\...s\SettingsPage.xaml"),
            (new string('x', 37) + ".xaml", new string('x', 37) + ".xaml"),
            (new string('x', 38) + ".xaml", "..." + new string('x', 34) + ".xaml"),
            ("ms-resource:///Files/" + new string('a', 50) + ".xaml",
                "..." + new string('a', 34) + ".xaml"),
        ];
        var calls = resources.Select((resource, index) => new PerfCall(
            "parse" + index, "ParseXaml", "parsing", 7, null, null,
            "begin" + index, "end" + index, index, index + 1, 1, null, "complete", 1)).ToArray();
        var events = resources.Select((resource, index) => new PerfEvent(
            "begin" + index, index, index, 7, PerfProviders.Xaml, 1, 0, 1,
            Guid.Empty, "ParseXaml", "parsing", "begin", null,
            new() { ["URI"] = resource.Resource })).ToArray();
        var directory = CreateCachedAnalysis(calls, events: events);
        try
        {
            string[] args = ["analyze", directory.FullName, "--view", view];
            if (json)
            {
                var (stdout, stderr, exitCode) = await InvokeProgramAsync(["perf", .. args, "--json"]);
                Assert.AreEqual(0, exitCode);
                Assert.AreEqual("", stderr);
                using var result = JsonDocument.Parse(stdout);
                var actual = view == "summary"
                    ? result.RootElement.GetProperty("parsingResources").EnumerateArray()
                        .Select(row => row.GetProperty("resource").GetString()).ToArray()
                    : result.RootElement.GetProperty("rows").EnumerateArray()
                        .Select(row => row.GetProperty("name").GetString()).ToArray();
                CollectionAssert.AreEquivalent(resources.Select(resource => resource.Resource).ToArray(), actual);
            }
            else
            {
                var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<PerfCommand>(), args);
                Assert.AreEqual(0, exitCode);
                foreach (var resource in resources)
                {
                    Assert.IsTrue(resource.Label.Length <= 42);
                    StringAssert.Contains(TestAnsiConsole.Output, $"{resource.Label,-42} {1d,11:F3} {1,7}");
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task SummaryOmitsEmptyParsingSection()
    {
        var directory = CreateCachedAnalysis(
            [new("c1", "Layout", "layout", 1, null, null, "v1", "v2",
                0, 10, 10, null, "complete", 10)]);
        try
        {
            var exit = await ParseAndInvokeWithCaptureAsync(GetRequiredService<PerfCommand>(),
                ["analyze", directory.FullName]);

            Assert.AreEqual(0, exit);
            StringAssert.Contains(TestAnsiConsole.Output, "Observed UI-thread activity");
            StringAssert.Contains(TestAnsiConsole.Output, "Detailed summary");
            Assert.IsFalse(TestAnsiConsole.Output.Contains("Parsing by resource", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task SummaryNextPageCommandPreservesQueryAndReplacesOffset()
    {
        TestAnsiConsole.Profile.Width = 4096;
        var directory = CreateCachedAnalysis(Enumerable.Range(0, 25).Select(index =>
            new PerfCall("c" + index, "Operation" + index, "layout", 7, null, null,
                "v" + index, null, 0, 10, 10, null, "complete", 10)));
        try
        {
            var command = GetRequiredService<PerfCommand>();
            var exit = await ParseAndInvokeWithCaptureAsync(command,
                ["analyze", directory.FullName, "--view", "summary", "--thread", "7",
                    "--from-ms", "0", "--to-ms", "20", "--sort", "count", "--limit", "10", "--offset", "10"]);
            Assert.AreEqual(0, exit);
            var output = TestAnsiConsole.Output;
            StringAssert.Contains(output, "Returned 10/25");
            var next = output[output.IndexOf("Next page: winapp ", StringComparison.Ordinal)..]
                .Replace("Next page: winapp ", "", StringComparison.Ordinal).Replace("\r", "").Replace("\n", "");
            var parse = command.Parse(next["perf ".Length..]);
            Assert.IsEmpty(parse.Errors);
            Assert.AreEqual("summary", parse.GetValue(command.Subcommands.Single(c => c.Name == "analyze")
                .Options.OfType<Option<string>>().Single(o => o.Name == "--view")));
            var analyze = command.Subcommands.Single(c => c.Name == "analyze");
            Assert.AreEqual(20, parse.GetValue(analyze.Options.OfType<Option<int>>().Single(o => o.Name == "--offset")));
            Assert.AreEqual(10, parse.GetValue(analyze.Options.OfType<Option<int>>().Single(o => o.Name == "--limit")));
            Assert.AreEqual(7u, parse.GetValue(analyze.Options.OfType<Option<uint?>>().Single(o => o.Name == "--thread")));
            Assert.AreEqual(20d, parse.GetValue(analyze.Options.OfType<Option<double?>>().Single(o => o.Name == "--to-ms")));
            Assert.AreEqual(0, await parse.InvokeAsync());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow("summary", false)]
    [DataRow("calls", false)]
    [DataRow("summary", true)]
    public async Task PartialAnalysisExplainsReasonsWithoutDiscardingResults(string view, bool json)
    {
        var directory = CreateCachedAnalysis(
            [
                new("c1", "Layout", "layout", 1, null, null, "v1", "v2",
                    0, 10, 10, null, "complete", 10),
                new("c2", "Measure", "layout", 1, null, null, "v3", null,
                    20, null, null, null, "missing-end"),
            ]);
        var previous = Console.Error;
        using var stderr = new StringWriter();
        try
        {
            string[] args = ["analyze", directory.FullName, "--view", view, "--to-ms", "50"];
            string error;
            if (json)
            {
                var (stdout, jsonError, exit) = await InvokeProgramAsync(["perf", .. args, "--json"]);
                Assert.AreEqual(1, exit);
                using var result = JsonDocument.Parse(stdout);
                Assert.IsTrue(result.RootElement.GetProperty("rows").GetArrayLength() > 0);
                Assert.AreEqual(2, result.RootElement.GetProperty("coverage").GetProperty("reasons").GetArrayLength());
                using var envelope = JsonDocument.Parse(jsonError);
                Assert.AreEqual("partial_data", envelope.RootElement.GetProperty("code").GetString());
                Assert.IsTrue(envelope.RootElement.GetProperty("partialOutput").GetBoolean());
            }
            else
            {
                Console.SetError(stderr);
                var exit = await ParseAndInvokeWithCaptureAsync(GetRequiredService<PerfCommand>(), args);
                Assert.AreEqual(1, exit);
                StringAssert.Contains(TestAnsiConsole.Output, "Returned");
                error = stderr.ToString();
                StringAssert.Contains(error, "Results may be incomplete:");
                StringAssert.Contains(error, "- The selected time range extends beyond the recording.");
                StringAssert.Contains(error, "- Some UI operations have missing or inconsistent start/end events");
                Assert.IsFalse(error.Contains("coverage.reasons", StringComparison.Ordinal));
                Assert.IsFalse(error.Contains("manifests", StringComparison.Ordinal));
                Assert.IsFalse(TestAnsiConsole.Output.Contains("Coverage:", StringComparison.Ordinal));
            }
        }
        finally
        {
            Console.SetError(previous);
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow("summary")]
    [DataRow("calls")]
    [DataRow("call")]
    public async Task AnalysisOmitsTextLimitationsButRetainsThemInJson(string view)
    {
        TestAnsiConsole.Profile.Width = 4096;
        var directory = CreateCachedAnalysis(
            [new("c1", "Layout", "layout", 1, null, null, "v1", "v2",
                0, 10, 10, null, "complete", 10)]);
        try
        {
            string[] args = ["analyze", directory.FullName, "--view", view];
            if (view == "call")
            {
                args = [.. args, "--id", "c1"];
            }
            var exit = await ParseAndInvokeWithCaptureAsync(GetRequiredService<PerfCommand>(), args);
            Assert.AreEqual(0, exit);
            var output = TestAnsiConsole.Output;
            StringAssert.Contains(output, "Returned");

            var (stdout, stderr, code) = await InvokeProgramAsync(["perf", .. args, "--json"]);
            Assert.AreEqual(0, code, stderr);
            using var json = JsonDocument.Parse(stdout);
            var limitations = json.RootElement.GetProperty("limitations");
            Assert.AreEqual(9, limitations.GetArrayLength());
            foreach (var limitation in limitations.EnumerateArray())
            {
                Assert.IsFalse(output.Contains(limitation.GetString()!, StringComparison.Ordinal));
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SummaryJsonRetainsEvidenceAndStatistics(bool includeResources)
    {
        var directory = CreateCachedAnalysis(
            [new("c1", "Layout", "layout", 1, null, null, "v1", "v2",
                0, 10, 10, null, "complete", 10)],
            resources: includeResources ? new()
            {
                Samples = [new(0, 10, 20, new(10, 20, 3, 4)), new(40, 30, 40, new(30, 40, 4, 5))],
            } : null);
        try
        {
            var (stdout, stderr, code) = await InvokeProgramAsync(
                ["perf", "analyze", directory.FullName, "--json"]);

            Assert.AreEqual(0, code, stderr);
            using var json = JsonDocument.Parse(stdout);
            var row = json.RootElement.GetProperty("rows").EnumerateArray()
                .Single(row => row.GetProperty("id").GetString() == "phase:Layout");
            Assert.AreEqual("v1", row.GetProperty("evidence")[0].GetString());
            Assert.AreEqual(1, row.GetProperty("count").GetInt32());
            Assert.AreEqual(10d, row.GetProperty("meanMs").GetDouble());
            Assert.AreEqual(10d, row.GetProperty("maxMs").GetDouble());
            Assert.AreEqual(10d, row.GetProperty("p95Ms").GetDouble());
            Assert.AreEqual(includeResources ? "observed" : "not-recorded", json.RootElement.GetProperty("processResources")
                .GetProperty("availability").GetString());
            if (includeResources)
            {
                Assert.AreEqual(40, json.RootElement.GetProperty("processResources").GetProperty("totalCpuMs").GetDouble());
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
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
    public async Task UnknownProfileModeIsRejected()
    {
        var (stdout, _, code) = await InvokeProgramAsync(["run", ".", "--profile", "unused", "--profile-mode", "kernel", "--json"]);
        Assert.AreEqual(1, code);
        using var error = JsonDocument.Parse(stdout);
        StringAssert.Contains(error.RootElement.GetProperty("Error").GetString(), "kernel");
    }

    [TestMethod]
    [DataRow("--profile-duration-sec", "2")]
    [DataRow("--profile-max-size-mib", "2")]
    [DataRow("--profile-mode", "elevated")]
    public async Task ProfileSettingsWithoutProfileAreRejected(string option, string value)
    {
        var command = GetRequiredService<RunCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(command, [".", option, value, "--json"]);
        Assert.AreEqual(1, exit);
        using var error = JsonDocument.Parse(TestAnsiConsole.Output);
        StringAssert.Contains(error.RootElement.GetProperty("Error").GetString(), "require --profile");
    }

    [TestMethod]
    [DataRow("elements")]
    [DataRow("summary")]
    public async Task ConsoleRendererIncludesBoundaryOverlapTotals(string view)
    {
        var directory = CreateCachedAnalysis(
            [new("c1", "MeasureElement", "layout", 1, "e1", null, "v1", "v2",
                0, 40, 40, 40, "complete", 40)],
            elements: [new() { Id = "e1", ObjectId = "a", Type = "Panel" }]);
        try
        {
            var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<PerfCommand>(),
                ["analyze", directory.FullName, "--view", view, "--from-ms", "5", "--to-ms", "35"]);

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(TestAnsiConsole.Output, "boundary");
            StringAssert.Contains(TestAnsiConsole.Output, "30.000");
            if (view == "elements")
            {
                foreach (var detail in new[] { "evidence:", "count=", "mean=", "max=", "p95=" })
                {
                    StringAssert.Contains(TestAnsiConsole.Output, detail);
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task ConsoleRendererIncludesEventTimestampPhaseAndFields()
    {
        var e = new PerfEvent("v1", 10, 10, 1, PerfProviders.Xaml, 47, 0, 1, Guid.Empty,
            "MeasureElement", "layout", "begin", "a", new() { ["Width"] = "42" });
        var directory = CreateCachedAnalysis([], events: [e]);
        try
        {
            var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<PerfCommand>(),
                ["analyze", directory.FullName, "--view", "events", "--event", "v1"]);

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(TestAnsiConsole.Output, "10.000");
            StringAssert.Contains(TestAnsiConsole.Output, "begin");
            StringAssert.Contains(TestAnsiConsole.Output, "Width=42");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task PerformanceSkillStartsWithMarkerBoundedSummary()
    {
        var source = await ReadRepositoryFileAsync(
            "plugins", "winapp", "skills", "winapp-performance", "SKILL.md");
        var initialQuery = source.Split('\n').Single(line =>
            line.Contains("winapp perf analyze <directory> --from-marker scenario-start"));

        StringAssert.Contains(initialQuery, "--from-marker");
        StringAssert.Contains(initialQuery, "--to-marker");
        Assert.IsFalse(initialQuery.Contains("--view", StringComparison.Ordinal));
    }

    private static async Task<string> ReadRepositoryFileAsync(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "version.json")))
        {
            directory = directory.Parent;
        }
        Assert.IsNotNull(directory, "Could not locate the repository root.");
        return await File.ReadAllTextAsync(Path.Join([directory.FullName, .. path]), CancellationToken.None);
    }

    private static DirectoryInfo CreateCachedAnalysis(
        IEnumerable<PerfCall> calls,
        IEnumerable<PerfEvent>? events = null,
        IEnumerable<PerfElement>? elements = null,
        PerfProcessResources? resources = null,
        PerfProviderState? compositionState = null)
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Console-");
        File.WriteAllBytes(Path.Join(directory.FullName, "trace.etl"), []);
        using var process = Process.GetCurrentProcess();
        var capture = new PerfCaptureDocument
        {
            Id = Guid.NewGuid().ToString("N"),
            Directory = directory.FullName,
            SessionId = Guid.NewGuid(),
            SessionName = "WinApp-Perf-Test",
            State = "completed",
            Target = PerfProcessIdentity.Read(process),
            ReadyQpc = 0,
            StopQpc = 40,
            Frequency = 1000,
            ProcessResources = resources,
            EventsLost = 0,
            BuffersLost = 0,
            TraceFiles = ["trace.etl"],
            Providers = PerfProviders.All.Where(provider => provider.Id == PerfProviders.Xaml ||
                compositionState is not null && provider.Id == PerfProviders.DirectComposition).ToArray(),
            ProviderStates = compositionState is null ? [] : [compositionState],
        };
        capture.Save();

        var cache = directory.CreateSubdirectory("analysis");
        Write(cache, "calls.ndjson", calls, PerfJsonContext.Default.PerfCall);
        Write(cache, "events.ndjson", events ?? [], PerfJsonContext.Default.PerfEvent);
        Write(cache, "elements.ndjson", elements ?? [], PerfJsonContext.Default.PerfElement);
        Write(cache, "gc.ndjson", [], PerfJsonContext.Default.PerfGcInterval);

        var fingerprintMethod = typeof(PerfAnalysisStore).GetMethod(
            "Fingerprint", BindingFlags.NonPublic | BindingFlags.Static)!;
        var fingerprint = (string)fingerprintMethod.Invoke(
            null, [PerfCaptureDocument.Load(directory.FullName), CancellationToken.None])!;
        var manifest = new PerfAnalysisManifest
        {
            Fingerprint = fingerprint,
            FirstEventMs = 0,
            LastEventMs = 40,
            Events = events?.Count() ?? 0,
            Calls = calls.Count(),
            Elements = elements?.Count() ?? 0,
            Families = new() { ["layout"] = 1 },
        };
        foreach (var file in new[] { "events.ndjson", "calls.ndjson", "elements.ndjson", "gc.ndjson" })
        {
            manifest.CacheHashes[file] = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(Path.Join(cache.FullName, file))));
        }
        File.WriteAllText(Path.Join(cache.FullName, "manifest.json"),
            JsonSerializer.Serialize(manifest, PerfJsonContext.Default.PerfAnalysisManifest));
        return directory;
    }

    private static void Write<T>(DirectoryInfo directory, string name, IEnumerable<T> values, JsonTypeInfo<T> type) =>
        File.WriteAllLines(Path.Join(directory.FullName, name),
            values.Select(value => JsonSerializer.Serialize(value, type)));
}
