// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
using System.CommandLine;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.Performance;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class PerformanceStage3Tests : BaseCommandTests
{
    private static readonly string[] ExpectedInvokerTokens =
        ["invoke", "private-selector", "--window", "77"];
    private static readonly string[] ExpectedFailureLifecycleVerbs =
        ["focus", "invoke", "set-value"];
    private static readonly string[] ExpectedDefaultScenarioRunArguments =
        ["--configuration", "Release"];
    private readonly FakeCommandScenarioRunner _commandRunner = new();

    public PerformanceStage3Tests() : base(configPaths: false)
    {
    }

    protected override IServiceCollection ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IPerformanceScenarioRunner>(_commandRunner);

    [TestMethod]
    public void ScenarioValidation_UsesUiParserAndRejectsPrivateTargetingAndWorkflowArguments()
    {
        AssertScenarioRejected(["click", "private-selector", "--app", "private-app"]);
        AssertScenarioRejected(["click", "private-selector", "--window", "123"]);
        AssertScenarioRejected(["screenshot", "--output", "private.png"]);
        AssertScenarioRejected(["inspect", "--json"]);
        AssertScenarioRejected(["yield"]);
        AssertScenarioRejected(["not-a-ui-verb"]);
        AssertScenarioRejected(["screenshot", "-o=private.png"]);
        AssertScenarioRejected(["screenshot", "-oprivate.png"]);
        AssertScenarioRejected(["send-keys", "alt+f4", "--via", "send-input", "--allow-system-keys"]);
    }

    [TestMethod]
    public async Task ScenarioCommand_ValuelessPropertyReturnsJsonError()
    {
        var scenarioPath = Path.Join(_tempDirectory.FullName, "scenario.json");
        File.WriteAllText(scenarioPath, ValidScenarioJson());
        var output = Path.Join(_tempDirectory.FullName, "command.winappperfset");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<PerfScenarioCommand>(),
            [scenarioPath, "--property", "--output", output, "--json"]);

        Assert.AreEqual(1, exitCode);
        using var document = JsonDocument.Parse(TestAnsiConsole.Output);
        StringAssert.Contains(
            document.RootElement.GetProperty("error").GetString(),
            "without a value");
        Assert.IsNull(_commandRunner.Request);
    }

    [TestMethod]
    public async Task UiInvoker_AppendsExactWindowAndDoesNotPersistOrRewritePrivateArguments()
    {
        IReadOnlyList<string>? capturedTokens = null;
        var root = new Command("ui");
        var invoke = new Command("invoke");
        var selector = new Argument<string>("selector");
        var window = new Option<long?>("--window");
        invoke.Arguments.Add(selector);
        invoke.Options.Add(window);
        invoke.SetAction(parseResult =>
        {
            capturedTokens = parseResult.Tokens.Select(value => value.Value).ToArray();
            return 0;
        });
        var yield = new Command("yield");
        yield.SetAction(_ => 0);
        root.Subcommands.Add(invoke);
        root.Subcommands.Add(yield);
        var invoker = new PerformanceUiCommandInvoker(root);

        var exitCode = await invoker.InvokeAsync(
            ["invoke", "private-selector"],
            77,
            TestContext.CancellationToken);

        Assert.AreEqual(0, exitCode);
        CollectionAssert.AreEqual(
            ExpectedInvokerTokens,
            capturedTokens!.ToArray());
    }

    [TestMethod]
    public void ScenarioValidation_RejectsUnknownSchemaFieldsAndInvalidTolerances()
    {
        var unknown = ValidScenarioJson().Replace(
            "\"id\":\"privacy\"",
            "\"id\":\"privacy\",\"unexpected\":true",
            StringComparison.Ordinal);
        Assert.ThrowsExactly<InvalidDataException>(() => LoadScenario(unknown));

        var both = ValidScenarioJson().Replace(
            "\"absoluteTolerance\":0",
            "\"absoluteTolerance\":0,\"percentTolerance\":5",
            StringComparison.Ordinal);
        Assert.ThrowsExactly<InvalidDataException>(() => LoadScenario(both));
    }

    [TestMethod]
    public void ScenarioValidation_TreatsAtPrefixedValuesAsLiteralArguments()
    {
        var scenario = LoadScenario(ValidScenarioJson().Replace(
            "\"private-selector\"",
            "\"@assistant\"",
            StringComparison.Ordinal));

        Assert.AreEqual("set-value", scenario.Steps.Single().Verb);
    }

    [TestMethod]
    public void SetManifest_PersistsOnlySanitizedStepMetadata()
    {
        var scenario = LoadScenario(ValidScenarioJson());
        var output = Path.Join(_tempDirectory.FullName, "privacy.winappperfset");
        new PerformanceSetWriter().Write(
            output,
            scenario,
            1,
            5,
            new()
            {
                Status = "failed",
                FailureReason = "private-child-output private-entered-value",
                Conditions = PerformanceSetWriter.CurrentConditions(),
                Iterations =
                [
                    Iteration(
                        ordinal: 1,
                        warmup: true,
                        values: [1],
                        steps:
                        [
                            new()
                            {
                                Ordinal = 1,
                                Phase = "measure",
                                Verb = "set-value",
                                Status = "completed",
                                DurationMs = 1,
                                ExitCode = 0,
                            },
                        ]),
                ],
            });

        var manifest = File.ReadAllText(Path.Join(output, "manifest.json"));
        Assert.IsFalse(manifest.Contains("private-selector", StringComparison.Ordinal));
        Assert.IsFalse(manifest.Contains("private-entered-value", StringComparison.Ordinal));
        Assert.IsFalse(manifest.Contains("\"ui\"", StringComparison.Ordinal));
        StringAssert.Contains(manifest, "\"verb\":\"set-value\"");
        StringAssert.Contains(manifest, "\"failureReason\":\"scenario-runner-failed\"");
    }

    [TestMethod]
    public void Median_IsDeterministicForOddAndEvenCounts()
    {
        Assert.AreEqual(3d, PerformanceSetComparer.Median([9, 1, 3]));
        Assert.AreEqual(5d, PerformanceSetComparer.Median([8, 2, 6, 4]));
    }

    [TestMethod]
    public void Compare_ReportsAbsoluteToleranceWithoutProducingAVerdict()
    {
        var definition = Metric(absolute: 10);
        var baseline = WriteSet("baseline-a", "same", definition, [10, 20, 30]);
        var equal = WriteSet("equal-a", "same", definition, [20, 30, 40]);
        var outsideTolerance = WriteSet("outside-a", "same", definition, [21, 31, 41]);

        var equalResult = new PerformanceSetComparer().Compare(baseline, equal);
        var outsideResult = new PerformanceSetComparer().Compare(baseline, outsideTolerance);

        Assert.AreEqual("completed", equalResult.Status);
        Assert.AreEqual("completed", outsideResult.Status);
        Assert.AreEqual("compared", outsideResult.Metrics[0].Status);
        Assert.AreEqual(11d, outsideResult.Metrics[0].Difference);
        Assert.AreEqual(10d, outsideResult.Metrics[0].AllowedDifference);
        Assert.AreEqual(10d, outsideResult.Metrics[0].AbsoluteTolerance);
    }

    [TestMethod]
    public void Compare_PercentToleranceUsesMedianAndExcludesWarmups()
    {
        var definition = Metric(percent: 10);
        var baseline = WriteSet("baseline-p", "same", definition, [90, 100, 110], warmupValue: 10_000);
        var candidate = WriteSet("candidate-p", "same", definition, [100, 110, 120], warmupValue: 0);

        var result = new PerformanceSetComparer().Compare(baseline, candidate);

        Assert.AreEqual("completed", result.Status);
        Assert.AreEqual(100d, result.Metrics[0].BaselineMedian);
        Assert.AreEqual(110d, result.Metrics[0].CandidateMedian);
        Assert.AreEqual(10d, result.Metrics[0].PercentTolerance);
        Assert.AreEqual(10d, result.Metrics[0].AllowedDifference);
    }

    [TestMethod]
    public void Compare_ZeroRelativeBaselineStillReportsTheConfiguredTolerance()
    {
        var definition = Metric(percent: 5);
        var baseline = WriteSet("baseline-zero", "same", definition, [0, 0, 0]);
        var candidate = WriteSet("candidate-zero", "same", definition, [0, 0, 0]);

        var result = new PerformanceSetComparer().Compare(baseline, candidate);

        Assert.AreEqual("completed", result.Status);
        Assert.AreEqual("compared", result.Metrics[0].Status);
        Assert.AreEqual(0d, result.Metrics[0].AllowedDifference);
        Assert.AreEqual(5d, result.Metrics[0].PercentTolerance);
    }

    [TestMethod]
    public void Compare_RejectsScenarioMetricAndEnvironmentMismatches()
    {
        var definition = Metric(absolute: 0);
        var baseline = WriteSet("baseline-mismatch", "baseline-hash", definition, [1]);
        var hashMismatch = WriteSet("hash-mismatch", "candidate-hash", definition, [1]);
        Assert.AreEqual(
            "invalid",
            new PerformanceSetComparer().Compare(baseline, hashMismatch).Status);

        var changedMetric = definition with { AbsoluteTolerance = 1 };
        var metricMismatch = WriteSet("metric-mismatch", "baseline-hash", changedMetric, [1]);
        Assert.AreEqual(
            "invalid",
            new PerformanceSetComparer().Compare(baseline, metricMismatch).Status);

        var changedConditions = PerformanceSetWriter.CurrentConditions() with
        {
            LogicalProcessorCount = PerformanceSetWriter.CurrentConditions().LogicalProcessorCount + 1,
        };
        var environmentMismatch = WriteSet(
            "environment-mismatch",
            "baseline-hash",
            definition,
            [1],
            conditions: changedConditions);
        Assert.AreEqual(
            "invalid",
            new PerformanceSetComparer().Compare(baseline, environmentMismatch).Status);
    }

    [TestMethod]
    public void Compare_RejectsIncompleteMeasuredIterations()
    {
        var definition = Metric(absolute: 0);
        var baseline = WriteSet("baseline-complete", "same", definition, [1]);
        var incomplete = WriteSet(
            "candidate-incomplete",
            "same",
            definition,
            [1],
            setStatus: "failed");

        Assert.AreEqual(
            "incomplete",
            new PerformanceSetComparer().Compare(baseline, incomplete).Status);
    }

    [TestMethod]
    public void SetWriter_PublishesCopiedIterationAndManifestAtomically()
    {
        var scenario = LoadScenario(ValidScenarioJson());
        var source = Path.Join(_tempDirectory.FullName, "source.winappperf");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Join(source, "manifest.json"), "{}");
        File.WriteAllText(Path.Join(source, "timeline.ndjson"), "{}");
        var output = Path.Join(_tempDirectory.FullName, "atomic.winappperfset");

        new PerformanceSetWriter().Write(
            output,
            scenario,
            0,
            1,
            new()
            {
                Status = "completed",
                Conditions = PerformanceSetWriter.CurrentConditions(),
                Iterations = [Iteration(1, false, [1]) with { SourceBundle = source }],
            });

        Assert.IsTrue(File.Exists(Path.Join(output, "manifest.json")));
        Assert.IsTrue(File.Exists(Path.Join(
            output,
            "iterations",
            "001-measure.winappperf",
            "timeline.ndjson")));
        Assert.IsEmpty(Directory.EnumerateDirectories(
            _tempDirectory.FullName,
            ".*.staging"));
    }

    [TestMethod]
    public void SetWriter_FailureLeavesNoPublishedOrStagingDirectory()
    {
        var scenario = LoadScenario(ValidScenarioJson());
        var output = Path.Join(_tempDirectory.FullName, "failed.winappperfset");

        Assert.ThrowsExactly<InvalidDataException>(() => new PerformanceSetWriter().Write(
            output,
            scenario,
            0,
            1,
            new()
            {
                Status = "completed",
                Conditions = PerformanceSetWriter.CurrentConditions(),
                Iterations =
                [
                    Iteration(1, false, [1]) with
                    {
                        SourceBundle = Path.Join(_tempDirectory.FullName, "missing.winappperf"),
                    },
                ],
            }));

        Assert.IsFalse(Directory.Exists(output));
        Assert.IsEmpty(Directory.EnumerateDirectories(
            _tempDirectory.FullName,
            ".*.staging"));
    }

    [TestMethod]
    public void MetricExtractor_UsesForcedBoundarySamplesAndFailedProbeIntervals()
    {
        var bundle = CreateMetricBundle();
        var definitions = new[]
        {
            MetricNamed("measure.durationMs"),
            MetricNamed("measure.cpuTimeMs"),
            MetricNamed("measure.averageCpuCores"),
            MetricNamed("measure.peakPrivateBytes"),
            MetricNamed("measure.privateBytesChange"),
            MetricNamed("measure.readBytes"),
            MetricNamed("measure.writeBytes"),
            MetricNamed("measure.failedProbeDurationMs"),
        };

        var values = PerformanceMetricExtractor
            .Extract(bundle, 100, 600, definitions)
            .ToDictionary(metric => metric.Name, metric => metric.Value);

        Assert.AreEqual(500d, values["measure.durationMs"]);
        Assert.AreEqual(50d, values["measure.cpuTimeMs"]);
        Assert.AreEqual(0.1d, values["measure.averageCpuCores"]);
        Assert.AreEqual(200d, values["measure.peakPrivateBytes"]);
        Assert.AreEqual(100d, values["measure.privateBytesChange"]);
        Assert.AreEqual(400d, values["measure.readBytes"]);
        Assert.AreEqual(600d, values["measure.writeBytes"]);
        Assert.AreEqual(200d, values["measure.failedProbeDurationMs"]);
    }

    [TestMethod]
    public void MetricExtractor_UsesProcessGenerationDeltasAndCompleteMemorySamples()
    {
        var mainStart = ResourceAt(100, 10, 100, 100, 200);
        var mainAndHelper = ResourceAt(300, 30, 200, 210, 320);
        mainAndHelper = mainAndHelper with
        {
            Processes =
            [
                mainAndHelper.Processes[0],
                new()
                {
                    ProcessId = 43,
                    ProcessStartTimeUtcTicks = 2,
                    TotalProcessorTimeMs = 5,
                    PrivateBytes = 50,
                    ReadBytes = 10,
                    WriteBytes = 20,
                    IsTerminal = false,
                    IsPartial = false,
                },
            ],
            Aggregate = mainAndHelper.Aggregate with
            {
                PrivateBytes = 250,
                ReadBytes = 220,
                WriteBytes = 340,
            },
        };
        var helperTerminal = new ResourceSample
        {
            Timestamp = new(400),
            IntervalMs = 0,
            OwnedProcessCount = 1,
            PartialProcessCount = 1,
            IsTerminal = true,
            Processes =
            [
                new()
                {
                    ProcessId = 43,
                    ProcessStartTimeUtcTicks = 2,
                    TotalProcessorTimeMs = 15,
                    ReadBytes = 110,
                    WriteBytes = 220,
                    IsTerminal = true,
                    IsPartial = true,
                },
            ],
            Aggregate = new(),
        };
        var mainEnd = ResourceAt(600, 60, 200, 500, 800);
        var bundle = CreateMetricBundle(
            [mainStart, mainAndHelper, helperTerminal, mainEnd]);

        var values = PerformanceMetricExtractor.Extract(
                bundle,
                100,
                600,
                [
                    MetricNamed("measure.peakPrivateBytes"),
                    MetricNamed("measure.privateBytesChange"),
                    MetricNamed("measure.readBytes"),
                    MetricNamed("measure.writeBytes"),
                ])
            .ToDictionary(metric => metric.Name, metric => metric.Value);

        Assert.AreEqual(250d, values["measure.peakPrivateBytes"]);
        Assert.AreEqual(100d, values["measure.privateBytesChange"]);
        Assert.AreEqual(500d, values["measure.readBytes"]);
        Assert.AreEqual(800d, values["measure.writeBytes"]);
    }

    [TestMethod]
    public async Task ScenarioCommand_DefaultsExecuteAndAreRetainedInCompletedSet()
    {
        var scenarioPath = Path.Join(_tempDirectory.FullName, "scenario.json");
        File.WriteAllText(scenarioPath, ValidScenarioJson());
        var output = Path.Join(_tempDirectory.FullName, "command.winappperfset");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            GetRequiredService<PerfScenarioCommand>(),
            [scenarioPath, "--output", output, "--json"]);

        Assert.AreEqual(0, exitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Join(output, "manifest.json")));
        Assert.AreEqual("completed", document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(1, document.RootElement.GetProperty("warmup").GetInt32());
        Assert.AreEqual(5, document.RootElement.GetProperty("repeat").GetInt32());
        Assert.AreEqual(1, _commandRunner.Request!.Warmup);
        Assert.AreEqual(5, _commandRunner.Request.Repeat);
        Assert.AreEqual(
            PerformanceBundleSchema.CurrentVersion,
            document.RootElement
                .GetProperty("conditions")
                .GetProperty("recordingSchemaVersion")
                .GetString());
        CollectionAssert.AreEqual(
            ExpectedDefaultScenarioRunArguments,
            _commandRunner.Request.AdditionalRunArguments!.ToArray());
    }

    [TestMethod]
    public async Task ScenarioRunner_ExecutesWarmupsAndRepeatsAgainstExactWindowWithOneWorkflowPerIteration()
    {
        var scenario = LoadScenario("""
            {
              "schemaVersion":"0.1",
              "id":"lifecycle",
              "setup":[{"ui":["focus","private-setup-selector"]}],
              "measure":[{"ui":["set-value","private-measure-selector","private-value"]}],
              "cleanup":[{"ui":["invoke","private-cleanup-selector"]}],
              "metrics":[{"name":"measure.durationMs","absoluteTolerance":0}]
            }
            """);
        var clock = new AdvancingPerformanceClock();
        var factory = new FakeRecordingSessionFactory();
        var workflow = new UiWorkflowContext();
        var actionReporter = new UiActionBoundaryReporter();
        var invoker = new FakeUiCommandInvoker(factory, workflow, actionReporter);
        var runner = new PerformanceScenarioRunner(
            factory,
            invoker,
            workflow,
            actionReporter,
            clock,
            new AdvancingScenarioDelay(clock));
        var iterationRoot = Path.Join(_tempDirectory.FullName, "runner-iterations");

        var result = await runner.RunAsync(
            new(scenario, new DirectoryInfo(_tempDirectory.FullName), 1, 2, iterationRoot),
            TestContext.CancellationToken);

        Assert.AreEqual("completed", result.Status);
        Assert.HasCount(3, result.Iterations);
        Assert.IsTrue(result.Iterations[0].IsWarmup);
        Assert.IsTrue(result.Iterations.Skip(1).All(value => !value.IsWarmup));
        Assert.IsTrue(result.Iterations.All(value =>
            value.Status == "completed"
            && value.SourceBundle is not null
            && Directory.Exists(value.SourceBundle)));
        Assert.HasCount(9, invoker.Invocations);
        Assert.IsTrue(invoker.Invocations.All(value => value.WindowHandle == 77));
        Assert.IsTrue(invoker.Invocations.All(value =>
            !value.Arguments.Contains("--window", StringComparer.OrdinalIgnoreCase)));
        Assert.HasCount(3, invoker.WorkflowIds.Distinct(StringComparer.Ordinal).ToArray());
        Assert.IsTrue(factory.Sessions.All(value => value.ForcedSnapshotCount >= 3));
        Assert.IsTrue(factory.Sessions.All(value =>
            value.Markers.Any(marker => marker == "measure:phase-start")
            && value.Markers.Any(marker => marker == "measure:phase-end")
            && value.Markers.Any(marker => marker == "lifecycle:yield")));
        Assert.IsTrue(factory.Sessions.All(value =>
            value.LaunchArguments.Contains("--detach", StringComparer.Ordinal)));

        var persisted = string.Join(
            "\n",
            result.Iterations.SelectMany(value =>
                Directory.EnumerateFiles(value.SourceBundle!, "*", SearchOption.AllDirectories)
                    .Select(File.ReadAllText)));
        Assert.IsFalse(persisted.Contains("private-value", StringComparison.Ordinal));
        Assert.IsFalse(persisted.Contains("private-measure-selector", StringComparison.Ordinal));

        var actionEntries = factory.Sessions
            .SelectMany(session => File.ReadLines(Path.Join(session.BundlePath, "timeline.ndjson")))
            .Select(line => JsonDocument.Parse(line))
            .Where(document =>
                document.RootElement.GetProperty("type").GetString() == "UiAction")
            .ToArray();
        try
        {
            Assert.HasCount(12, actionEntries);
            Assert.IsTrue(actionEntries.All(document =>
                document.RootElement.GetProperty("processId").GetInt32() == 42
                && document.RootElement.GetProperty("processStartTimeUtcTicks").GetInt64() == 1
                && document.RootElement.GetProperty("windowHandle").GetInt64() == 77));
            Assert.IsTrue(actionEntries.Any(document =>
                document.RootElement.GetProperty("actionKind").GetString()
                    == "ValuePattern.SetValue"
                && document.RootElement.GetProperty("boundary").GetString() == "action-start"));
            Assert.IsTrue(actionEntries.Any(document =>
                document.RootElement.GetProperty("actionKind").GetString() == "InvokePattern"
                && document.RootElement.GetProperty("boundary").GetString() == "action-end"
                && document.RootElement.GetProperty("status").GetString() == "completed"));
            Assert.IsTrue(actionEntries.All(document =>
                !document.RootElement.TryGetProperty("selector", out _)
                && !document.RootElement.TryGetProperty("value", out _)
                && !document.RootElement.TryGetProperty("name", out _)));
        }
        finally
        {
            foreach (var document in actionEntries)
            {
                document.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task ScenarioRunner_AttachedLateDoesNotRunUiOrWaitForOrKillExistingProcess()
    {
        var scenario = LoadScenario(ValidScenarioJson(["invoke", "private-selector"]));
        var clock = new AdvancingPerformanceClock();
        var factory = new FakeRecordingSessionFactory
        {
            Disposition = StartupLaunchDisposition.AttachedLate,
            ResponsiveWindow = null,
        };
        var workflow = new UiWorkflowContext();
        var actionReporter = new UiActionBoundaryReporter();
        var invoker = new FakeUiCommandInvoker(factory, workflow, actionReporter);
        var runner = new PerformanceScenarioRunner(
            factory,
            invoker,
            workflow,
            actionReporter,
            clock,
            new AdvancingScenarioDelay(clock));

        var result = await runner.RunAsync(
            new(
                scenario,
                null,
                0,
                1,
                Path.Join(_tempDirectory.FullName, "attached-late-iterations")),
            TestContext.CancellationToken);

        Assert.AreEqual("failed", result.Status);
        Assert.HasCount(1, result.Iterations);
        Assert.AreEqual("attached-late", result.Iterations[0].FailureReason);
        Assert.IsEmpty(invoker.Invocations);
        Assert.AreEqual(0, factory.Sessions[0].ExitWaitObservationCount);
        Assert.IsFalse(factory.Sessions[0].KillRequested);
        Assert.IsTrue(Directory.Exists(result.Iterations[0].SourceBundle));
    }

    [TestMethod]
    public async Task ScenarioRunner_MeasureFailureStillRunsCleanupAndYields()
    {
        var scenario = LoadScenario("""
            {
              "schemaVersion":"0.1",
              "id":"cleanup-after-failure",
              "setup":[{"ui":["focus","private-setup"]}],
              "measure":[{"ui":["invoke","private-measure"]}],
              "cleanup":[{"ui":["set-value","private-cleanup","private-value"]}],
              "metrics":[{"name":"measure.durationMs","absoluteTolerance":0}]
            }
            """);
        var clock = new AdvancingPerformanceClock();
        var factory = new FakeRecordingSessionFactory();
        var workflow = new UiWorkflowContext();
        var actionReporter = new UiActionBoundaryReporter();
        var invoker = new FakeUiCommandInvoker(factory, workflow, actionReporter)
        {
            FailVerb = "invoke",
        };
        var runner = new PerformanceScenarioRunner(
            factory,
            invoker,
            workflow,
            actionReporter,
            clock,
            new AdvancingScenarioDelay(clock));

        var result = await runner.RunAsync(
            new(
                scenario,
                null,
                0,
                1,
                Path.Join(_tempDirectory.FullName, "failure-iterations")),
            TestContext.CancellationToken);

        Assert.AreEqual("failed", result.Status);
        CollectionAssert.AreEqual(
            ExpectedFailureLifecycleVerbs,
            invoker.Invocations.Select(value => value.Arguments[0]).ToArray());
        Assert.AreEqual("completed", result.Iterations[0].Steps.Single(value =>
            value.Phase == "cleanup").Status);
        Assert.IsTrue(factory.Sessions[0].Markers.Contains("lifecycle:yield"));
        Assert.IsTrue(factory.Sessions[0].HasNewlyLaunchedTargetExited);
        var failedAction = File.ReadLines(Path.Join(
                factory.Sessions[0].BundlePath,
                "timeline.ndjson"))
                .Select(line => JsonDocument.Parse(line))
            .Single(document =>
                document.RootElement.GetProperty("type").GetString() == "UiAction"
                && document.RootElement.GetProperty("actionKind").GetString() == "InvokePattern"
                && document.RootElement.GetProperty("boundary").GetString() == "action-end");
        using (failedAction)
        {
            Assert.AreEqual(
                "failed",
                failedAction.RootElement.GetProperty("status").GetString());
        }
    }

    [TestMethod]
    public async Task ScenarioRunner_TargetExitTimeoutFailsWithoutKillingProcess()
    {
        var scenario = LoadScenario(ValidScenarioJson(["invoke", "private-selector"]));
        var clock = new AdvancingPerformanceClock();
        var factory = new FakeRecordingSessionFactory();
        var workflow = new UiWorkflowContext();
        var actionReporter = new UiActionBoundaryReporter();
        var invoker = new FakeUiCommandInvoker(factory, workflow, actionReporter)
        {
            ExitOnYield = false,
        };
        var runner = new PerformanceScenarioRunner(
            factory,
            invoker,
            workflow,
            actionReporter,
            clock,
            new AdvancingScenarioDelay(clock));

        var result = await runner.RunAsync(
            new(
                scenario,
                null,
                0,
                1,
                Path.Join(_tempDirectory.FullName, "exit-timeout-iterations")),
            TestContext.CancellationToken);

        Assert.AreEqual("failed", result.Status);
        Assert.IsTrue(factory.Sessions[0].ExitWaitObservationCount > 0);
        Assert.IsFalse(factory.Sessions[0].KillRequested);
        Assert.IsFalse(factory.Sessions[0].HasNewlyLaunchedTargetExited);
        Assert.IsTrue(Directory.Exists(result.Iterations[0].SourceBundle));
    }

    [TestMethod]
    public async Task ScenarioRunner_WaitsForAllOwnedProcessesToExit()
    {
        var scenario = LoadScenario(ValidScenarioJson(["invoke", "private-selector"]));
        var clock = new AdvancingPerformanceClock();
        var factory = new FakeRecordingSessionFactory
        {
            KeepHelperAliveAfterTargetExit = true,
        };
        var workflow = new UiWorkflowContext();
        var actionReporter = new UiActionBoundaryReporter();
        var runner = new PerformanceScenarioRunner(
            factory,
            new FakeUiCommandInvoker(factory, workflow, actionReporter),
            workflow,
            actionReporter,
            clock,
            new AdvancingScenarioDelay(clock));

        var result = await runner.RunAsync(
            new(
                scenario,
                null,
                0,
                1,
                Path.Join(_tempDirectory.FullName, "helper-timeout-iterations")),
            TestContext.CancellationToken);

        Assert.AreEqual("failed", result.Status);
        Assert.AreEqual("target-exit-timeout", result.Iterations[0].FailureReason);
        Assert.IsTrue(factory.Sessions[0].HasNewlyLaunchedTargetExited);
        Assert.IsTrue(factory.Sessions[0].HasActiveProcesses);
    }

    [TestMethod]
    public async Task ScenarioRunner_CancellationYieldsWorkflowAndReturnsCancelledResult()
    {
        var scenario = LoadScenario(ValidScenarioJson(["invoke", "private-selector"]));
        var clock = new AdvancingPerformanceClock();
        var factory = new FakeRecordingSessionFactory();
        var workflow = new UiWorkflowContext();
        var actionReporter = new UiActionBoundaryReporter();
        using var cancellation = new CancellationTokenSource();
        var invoker = new FakeUiCommandInvoker(factory, workflow, actionReporter)
        {
            CancelOnInvoke = cancellation,
        };
        var runner = new PerformanceScenarioRunner(
            factory,
            invoker,
            workflow,
            actionReporter,
            clock,
            new AdvancingScenarioDelay(clock));

        var result = await runner.RunAsync(
            new(
                scenario,
                null,
                0,
                1,
                Path.Join(_tempDirectory.FullName, "cancelled-iterations")),
            cancellation.Token);

        Assert.AreEqual("cancelled", result.Status);
        Assert.IsTrue(factory.Sessions[0].Markers.Contains("lifecycle:yield"));
        Assert.AreEqual(1, invoker.YieldCount);
    }

    private void AssertScenarioRejected(IReadOnlyList<string> ui)
    {
        var json = ValidScenarioJson(ui);
        Assert.ThrowsExactly<InvalidDataException>(() => LoadScenario(json));
    }

    private ValidatedPerformanceScenario LoadScenario(string json)
    {
        var path = Path.Join(_tempDirectory.FullName, $"scenario-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return GetRequiredService<IPerformanceScenarioLoader>().Load(path);
    }

    private static string ValidScenarioJson(IReadOnlyList<string>? ui = null)
    {
        var arguments = JsonSerializer.Serialize(ui ?? ["set-value", "private-selector", "private-entered-value"]);
        return $$"""
            {
              "schemaVersion":"0.1",
              "id":"privacy",
              "setup":[],
              "measure":[{"ui":{{arguments}}}],
              "cleanup":[],
              "metrics":[{"name":"measure.durationMs","absoluteTolerance":0}]
            }
            """;
    }

    private string WriteSet(
        string name,
        string hash,
        PerformanceMetricDefinition definition,
        IReadOnlyList<double> values,
        double? warmupValue = null,
        string setStatus = "completed",
        PerformanceSetConditions? conditions = null)
    {
        var iterations = new List<PerformanceScenarioIteration>();
        if (warmupValue is { } warmup)
        {
            iterations.Add(Iteration(1, true, [warmup]));
        }
        iterations.AddRange(values.Select((value, index) =>
            Iteration(index + iterations.Count + 1, false, [value])));
        var manifest = new PerformanceSetManifest
        {
            SchemaVersion = PerformanceSetWriter.SchemaVersion,
            Status = setStatus,
            CreatedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Scenario = new() { Id = "scenario", DefinitionHash = hash },
            Warmup = warmupValue is null ? 0 : 1,
            Repeat = values.Count,
            Conditions = conditions ?? PerformanceSetWriter.CurrentConditions(),
            MetricDefinitions = [definition],
            Iterations = iterations,
        };
        var directory = Path.Join(_tempDirectory.FullName, $"{name}.winappperfset");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Join(directory, "manifest.json"),
            JsonSerializer.Serialize(
                manifest,
                PerformanceJsonContext.Default.PerformanceSetManifest));
        return directory;
    }

    private static PerformanceMetricDefinition Metric(double? absolute = null, double? percent = null) =>
        new()
        {
            Name = "measure.durationMs",
            Unit = "ms",
            Direction = "lower",
            AbsoluteTolerance = absolute,
            PercentTolerance = percent,
        };

    private static PerformanceMetricDefinition MetricNamed(string name)
    {
        Assert.IsTrue(PerformanceMetricCatalog.TryGet(name, out var unit, out var direction));
        return new()
        {
            Name = name,
            Unit = unit,
            Direction = direction,
            AbsoluteTolerance = 0,
        };
    }

    private static PerformanceScenarioIteration Iteration(
        int ordinal,
        bool warmup,
        IReadOnlyList<double> values,
        IReadOnlyList<PerformanceScenarioStepResult>? steps = null) => new()
        {
            Ordinal = ordinal,
            IsWarmup = warmup,
            Status = "completed",
            DurationMs = 1,
            ExitCode = 0,
            Steps = steps ?? [],
            Metrics = values.Select(value => new PerformanceMetricValue
            {
                Name = "measure.durationMs",
                Value = value,
            }).ToArray(),
        };

    private string CreateMetricBundle(IReadOnlyList<ResourceSample>? samples = null)
    {
        var output = Path.Join(_tempDirectory.FullName, "metric.winappperf");
        var calibration = new PerformanceClockCalibration(
            new PerformanceTimestamp(0),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            1_000,
            TimeSpan.Zero);
        using var writer = new PerformanceBundleWriter(output, calibration);
        writer.Write(
        [
            new(StartupEventType.ActivationRequested, new(0), TimeSpan.Zero),
            new(StartupEventType.ProcessObserved, new(50), TimeSpan.Zero, new(42, 1)),
            new(StartupEventType.WindowResponsive, new(80), TimeSpan.Zero, WindowHandle: 7),
            new(StartupEventType.WindowResponseFailed, new(200), TimeSpan.Zero, WindowHandle: 7),
            new(StartupEventType.WindowResponseRecovered, new(400), TimeSpan.Zero, WindowHandle: 7),
        ]);
        foreach (var sample in samples ??
                 [ResourceAt(100, 10, 100, 100, 200), ResourceAt(600, 60, 200, 500, 800)])
        {
            writer.Write(sample);
        }
        writer.Complete(
            "completed",
            "duration",
            StartupLaunchDisposition.Launched,
            42,
            new()
            {
                CadenceMs = 250,
                TimeoutMs = 100,
                Method = "SendMessageTimeout(WM_NULL)",
            },
            new()
            {
                Requested = false,
                Status = "not-requested",
                Profile = XamlPerformanceAnalyzer.ProfileName,
                Coverage = "not-requested",
                LossStatus = "not-applicable",
            },
            NotCollectedManagedDiagnostics());
        return output;
    }

    private static ResourceSample ResourceAt(
        long counter,
        double cpuMs,
        long privateBytes,
        ulong readBytes,
        ulong writeBytes)
    {
        var process = new ProcessResourceSample
        {
            ProcessId = 42,
            ProcessStartTimeUtcTicks = 1,
            TotalProcessorTimeMs = cpuMs,
            PrivateBytes = privateBytes,
            ReadBytes = readBytes,
            WriteBytes = writeBytes,
            IsTerminal = false,
            IsPartial = false,
        };
        return new()
        {
            Timestamp = new(counter),
            IntervalMs = counter == 100 ? 0 : 500,
            OwnedProcessCount = 1,
            PartialProcessCount = 0,
            IsTerminal = false,
            Processes = [process],
            Aggregate = new()
            {
                PrivateBytes = privateBytes,
                ReadBytes = readBytes,
                WriteBytes = writeBytes,
            },
        };
    }

    private static ManagedDiagnosticsResult NotCollectedManagedDiagnostics() => new()
    {
        Collector = "Managed EventPipe",
        Status = "not-collected",
        Coverage = "not-collected",
        LossStatus = "not-applicable",
    };

    private sealed class FakeCommandScenarioRunner : IPerformanceScenarioRunner
    {
        public PerformanceScenarioRunRequest? Request { get; private set; }

        public Task<PerformanceScenarioRunResult> RunAsync(
            PerformanceScenarioRunRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            var iterations = Enumerable.Range(1, request.Warmup + request.Repeat)
                .Select(ordinal => Iteration(
                    ordinal,
                    ordinal <= request.Warmup,
                    [1]))
                .ToArray();
            return Task.FromResult(new PerformanceScenarioRunResult
            {
                Status = "completed",
                Conditions = PerformanceSetWriter.CurrentConditions(),
                Iterations = iterations,
            });
        }
    }

    private sealed class AdvancingPerformanceClock : IPerformanceClock
    {
        private long _counter;

        public long Frequency => 1_000;

        public PerformanceTimestamp GetTimestamp() => new(Interlocked.Increment(ref _counter) * 10);

        public PerformanceClockCalibration Calibrate() => new(
            GetTimestamp(),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Frequency,
            TimeSpan.Zero);

        public void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _counter, (long)(duration.TotalSeconds * Frequency / 10));
    }

    private sealed class AdvancingScenarioDelay(AdvancingPerformanceClock clock)
        : IPerformanceScenarioDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clock.Advance(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRecordingSessionFactory : IPerformanceRecordingSessionFactory
    {
        public StartupLaunchDisposition Disposition { get; init; } =
            StartupLaunchDisposition.Launched;

        public PerformanceWindowTarget? ResponsiveWindow { get; init; } =
            new(77, new ProcessIdentity(42, 1));

        public bool KeepHelperAliveAfterTargetExit { get; init; }

        public List<FakeRecordingSession> Sessions { get; } = [];

        public FakeRecordingSession? Current { get; private set; }

        public IPerformanceRecordingSession Create(string outputDirectory)
        {
            Current = new(
                outputDirectory,
                Disposition,
                ResponsiveWindow,
                KeepHelperAliveAfterTargetExit);
            Sessions.Add(Current);
            return Current;
        }
    }

    private sealed class FakeRecordingSession : IPerformanceRecordingSession
    {
        private readonly PerformanceBundleWriter _writer;
        private long _counter = 100;
        private long _cpuTicks;
        private bool _completed;
        private bool _exited;
        private readonly bool _keepHelperAliveAfterTargetExit;

        public FakeRecordingSession(
            string bundlePath,
            StartupLaunchDisposition disposition,
            PerformanceWindowTarget? responsiveWindow,
            bool keepHelperAliveAfterTargetExit)
        {
            BundlePath = bundlePath;
            Disposition = disposition;
            ResponsiveWindow = responsiveWindow;
            _keepHelperAliveAfterTargetExit = keepHelperAliveAfterTargetExit;
            _writer = new(
                bundlePath,
                new(
                    new(0),
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    1_000,
                    TimeSpan.Zero));
            var preExisting = disposition == StartupLaunchDisposition.AttachedLate;
            _writer.Write(
            [
                new(StartupEventType.ActivationRequested, new(0), TimeSpan.Zero),
                new(
                    StartupEventType.ProcessObserved,
                    new(10),
                    TimeSpan.Zero,
                    new(42, 1),
                    WasPresentBeforeActivation: preExisting),
                new(
                    StartupEventType.WindowObserved,
                    new(20),
                    TimeSpan.Zero,
                    new(42, 1),
                    WindowHandle: responsiveWindow?.WindowHandle ?? 77,
                    WasPresentBeforeActivation: preExisting),
                new(
                    StartupEventType.WindowVisible,
                    new(30),
                    TimeSpan.Zero,
                    new(42, 1),
                    WindowHandle: responsiveWindow?.WindowHandle ?? 77,
                    WasPresentBeforeActivation: preExisting),
                new(
                    StartupEventType.WindowResponsive,
                    new(40),
                    TimeSpan.Zero,
                    new(42, 1),
                    WindowHandle: responsiveWindow?.WindowHandle ?? 77,
                    WasPresentBeforeActivation: preExisting),
            ]);
            CaptureResourceSnapshot(force: true);
        }

        public string BundlePath { get; }
        public DateTimeOffset TimelineStartedUtc =>
            _writer.TimelineStartedUtc
            ?? throw new InvalidOperationException("The fake timeline has not started.");
        public StartupLaunchDisposition Disposition { get; }
        public int ActivationProcessId => 42;
        public bool HasObservedProcesses => true;
        public bool LaunchProcessAdmissionFailed => false;
        public bool HasActiveProcesses => !_exited || _keepHelperAliveAfterTargetExit;
        public bool HasNewlyLaunchedTarget => Disposition != StartupLaunchDisposition.AttachedLate;
        public bool HasNewlyLaunchedTargetExited => _exited;
        public int? TargetExitCode => _exited ? 0 : null;
        public ProcessIdentity? TargetProcess => ResponsiveWindow?.Process;
        public PerformanceWindowTarget? ResponsiveWindow { get; }
        public IReadOnlyList<PerformanceTimelineEntry> LastWrittenEvents { get; private set; } = [];
        public IReadOnlyList<StartupEvent> LastObservedEvents { get; private set; } = [];
        public int ForcedSnapshotCount { get; private set; }
        public int ExitWaitObservationCount { get; private set; }
        public bool KillRequested { get; private set; }
        public List<string> Markers { get; } = [];
        public IReadOnlyList<string> LaunchArguments { get; private set; } = [];

        public (string EtlPath, string TemporaryDirectory) CreateWprPaths() =>
            _writer.CreateWprPaths();

        public string CreateManagedPath() => _writer.CreateManagedPath();

        public void ConfigureWprCollector(IWprCollector collector)
        {
        }

        public Task<PerformanceRecordingLaunchResult> LaunchAsync(
            IReadOnlyList<string> runArguments,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken)
        {
            LaunchArguments = runArguments.ToArray();
            return Task.FromResult(new PerformanceRecordingLaunchResult(0, true, []));
        }

        public StartupObservationUpdate Observe()
        {
            ExitWaitObservationCount++;
            LastWrittenEvents = [];
            return new([], Disposition, new Dictionary<int, ProcessAdmissionStatus>());
        }

        public void CaptureResourceSnapshot(bool force = false)
        {
            if (force)
            {
                ForcedSnapshotCount++;
            }
            _counter += 10;
            _cpuTicks += TimeSpan.FromMilliseconds(5).Ticks;
            var counters = new ProcessResourceSample
            {
                ProcessId = 42,
                ProcessStartTimeUtcTicks = 1,
                TotalProcessorTimeMs = TimeSpan.FromTicks(_cpuTicks).TotalMilliseconds,
                UserProcessorTimeMs = TimeSpan.FromTicks(_cpuTicks).TotalMilliseconds,
                KernelProcessorTimeMs = 0,
                CpuCoresUsed = 0.5,
                CpuPercentOfMachine = 12.5,
                PrivateBytes = 100 + _counter,
                WorkingSetBytes = 200 + _counter,
                ReadOperationCount = (ulong)_counter,
                WriteOperationCount = (ulong)_counter,
                OtherOperationCount = 0,
                ReadBytes = (ulong)(_counter * 2),
                WriteBytes = (ulong)(_counter * 3),
                OtherBytes = 0,
                ReadBytesPerSecond = 20,
                WriteBytesPerSecond = 30,
                ThreadCount = 2,
                HandleCount = 3,
                GdiObjectCount = 4,
                UserObjectCount = 5,
                IsTerminal = false,
                IsPartial = false,
            };
            _writer.Write(new ResourceSample
            {
                Timestamp = new(_counter),
                IntervalMs = 10,
                OwnedProcessCount = 1,
                PartialProcessCount = 0,
                IsTerminal = false,
                Processes = [counters],
                Aggregate = new()
                {
                    CpuCoresUsed = counters.CpuCoresUsed,
                    CpuPercentOfMachine = counters.CpuPercentOfMachine,
                    PrivateBytes = counters.PrivateBytes,
                    WorkingSetBytes = counters.WorkingSetBytes,
                    ReadBytes = counters.ReadBytes,
                    WriteBytes = counters.WriteBytes,
                    ReadBytesPerSecond = counters.ReadBytesPerSecond,
                    WriteBytesPerSecond = counters.WriteBytesPerSecond,
                    ThreadCount = counters.ThreadCount,
                    HandleCount = counters.HandleCount,
                    GdiObjectCount = counters.GdiObjectCount,
                    UserObjectCount = counters.UserObjectCount,
                },
            });
        }

        public double WriteScenarioMarker(
            string phase,
            string boundary,
            int? stepOrdinal = null,
            string? verb = null,
            string? status = null)
        {
            Markers.Add($"{phase}:{boundary}");
            _counter += 10;
            return _writer.WriteScenarioMarker(
                new(_counter),
                phase,
                boundary,
                stepOrdinal,
                verb,
                status);
        }

        public void WriteUiActionBoundary(
            string phase,
            int stepOrdinal,
            string verb,
            UiActionBoundary boundary,
            PerformanceWindowTarget target)
        {
            _counter += 10;
            _writer.WriteUiAction(
                new(_counter),
                phase,
                stepOrdinal,
                verb,
                boundary,
                target);
        }


        public PerformanceRecordResult Complete(
            string status,
            string stopReason,
            WprCollectorResult wpr,
            ManagedDiagnosticsResult managed,
            XamlAnalysisResult? xaml = null)
        {
            _completed = true;
            return _writer.Complete(
                status,
                stopReason,
                Disposition,
                ActivationProcessId,
                new()
                {
                    CadenceMs = 250,
                    TimeoutMs = 100,
                    Method = "SendMessageTimeout(WM_NULL)",
                },
                wpr,
                managed,
                xaml);
        }

        public void MarkExited()
        {
            if (_exited)
            {
                return;
            }
            _exited = true;
            _counter += 10;
            _writer.Write(
            [
                new(
                    StartupEventType.ProcessExited,
                    new(_counter),
                    TimeSpan.Zero,
                    new(42, 1),
                    ExitCode: 0),
            ]);
        }

        public void Dispose()
        {
            if (!_completed)
            {
                _writer.Dispose();
            }
        }
    }

    private sealed class FakeUiCommandInvoker(
        FakeRecordingSessionFactory sessions,
        IUiWorkflowContext workflowContext,
        IUiActionBoundaryReporter actionReporter) : IPerformanceUiCommandInvoker
    {
        public string? FailVerb { get; init; }
        public bool ExitOnYield { get; init; } = true;
        public CancellationTokenSource? CancelOnInvoke { get; init; }
        public List<(IReadOnlyList<string> Arguments, long WindowHandle)> Invocations { get; } = [];
        public List<string> WorkflowIds { get; } = [];
        public int YieldCount { get; private set; }

        public async Task<int> InvokeAsync(
            IReadOnlyList<string> uiArguments,
            long windowHandle,
            CancellationToken cancellationToken)
        {
            WorkflowIds.Add(workflowContext.CurrentId!);
            Invocations.Add((uiArguments, windowHandle));
            using var action = BeginAction(uiArguments[0]);
            if (CancelOnInvoke is not null)
            {
                CancelOnInvoke.Cancel();
                await Task.FromCanceled(cancellationToken);
            }
            var failed = string.Equals(
                uiArguments[0],
                FailVerb,
                StringComparison.OrdinalIgnoreCase);
            if (!failed)
            {
                action?.Complete();
            }
            return failed ? 1 : 0;
        }

        public Task<int> YieldAsync(CancellationToken cancellationToken)
        {
            WorkflowIds.Add(workflowContext.CurrentId!);
            YieldCount++;
            if (ExitOnYield)
            {
                sessions.Current!.MarkExited();
            }
            return Task.FromResult(0);
        }

        private IUiActionScope? BeginAction(string verb) => verb switch
        {
            "invoke" => actionReporter.Begin("InvokePattern"),
            "set-value" => actionReporter.Begin("ValuePattern.SetValue"),
            "click" => actionReporter.Begin("MouseClick"),
            _ => null,
        };
    }
}
