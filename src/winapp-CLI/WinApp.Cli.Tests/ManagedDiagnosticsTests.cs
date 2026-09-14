// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;
using WinApp.Cli.Services.Performance;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class ManagedDiagnosticsTests
{
    private static readonly string[] VersionArguments = ["--version"];
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Join(Path.GetTempPath(), $"managed-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Resolver_UsesAbsoluteProfileToolAndRecordsVersion()
    {
        var tools = Path.Join(_root, ".dotnet", "tools");
        Directory.CreateDirectory(tools);
        var executable = Path.Join(tools, "dotnet-trace.exe");
        File.WriteAllBytes(executable, []);
        var runner = new VersionProcessRunner();
        var resolver = new DiagnosticToolResolver(
            runner,
            _ => ".;relative",
            () => _root);

        var result = await resolver.ResolveAsync(
            ManagedDiagnosticTool.DotNetTrace,
            TestContext.CancellationToken);

        Assert.IsTrue(result.IsAvailable);
        Assert.AreEqual(executable, result.ExecutablePath);
        Assert.AreEqual("9.0.1", result.Version);
        Assert.AreEqual(executable, runner.Request!.FileName);
        CollectionAssert.AreEqual(VersionArguments, runner.Request.Arguments.ToArray());
    }

    [TestMethod]
    public void RecordCommand_ParsesManagedCollectorOptions()
    {
        var command = new PerfRecordCommand();

        var parseResult = command.Parse(
            [".", "--with-dotnet-trace", "--with-dotnet-counters", "--duration-sec", "10"]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.IsTrue(parseResult.GetValue(PerfRecordCommand.WithDotNetTraceOption));
        Assert.IsTrue(parseResult.GetValue(PerfRecordCommand.WithDotNetCountersOption));
    }

    [TestMethod]
    public async Task Session_AttachesOnlyToNewEvidencedManagedGenerationAndSoftStops()
    {
        var processFactory = new FakeOwnedToolProcessFactory();
        await using var session = CreateSession(
            processFactory,
            new SequenceManagedProcessProbe(ManagedProcessKind.Managed));
        var process = new ProcessIdentity(42, 1234);

        await session.ObserveAsync(
            [
                new(
                    StartupEventType.ProcessObserved,
                    new PerformanceTimestamp(1),
                    TimeSpan.Zero,
                    process,
                    WasPresentBeforeActivation: false),
            ],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.Launched);

        Assert.HasCount(1, processFactory.Processes);
        CollectionAssert.Contains(processFactory.Requests[0].Arguments.ToArray(), "42");
        CollectionAssert.Contains(processFactory.Requests[0].Arguments.ToArray(), "00:00:00:10");
        Assert.AreEqual("recorded", session.Result.DotNetTrace.Status);
        Assert.AreEqual("attached-after-activation", session.Result.DotNetTrace.Coverage);
        Assert.AreEqual(42, session.Result.DotNetTrace.TargetProcessId);
        Assert.AreEqual(1234, session.Result.DotNetTrace.TargetProcessStartTimeUtcTicks);
        Assert.AreEqual("traces/managed.nettrace", session.Result.DotNetTrace.Artifact);
        Assert.AreEqual(Environment.NewLine, processFactory.Processes[0].SoftStopInput);
    }

    [TestMethod]
    public async Task Session_DoesNotAttachToPreExistingGeneration()
    {
        var processFactory = new FakeOwnedToolProcessFactory();
        await using var session = CreateSession(
            processFactory,
            new SequenceManagedProcessProbe(ManagedProcessKind.Managed));

        await session.ObserveAsync(
            [
                new(
                    StartupEventType.ProcessObserved,
                    new PerformanceTimestamp(1),
                    TimeSpan.Zero,
                    new ProcessIdentity(42, 1234),
                    WasPresentBeforeActivation: true),
            ],
            StartupLaunchDisposition.AttachedLate,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.AttachedLate);

        Assert.IsEmpty(processFactory.Requests);
        Assert.AreEqual("attached-late-not-supported", session.Result.DotNetTrace.Status);
    }

    [TestMethod]
    public async Task Session_RetriesUntilRuntimeLoads()
    {
        var processFactory = new FakeOwnedToolProcessFactory();
        await using var session = CreateSession(
            processFactory,
            new SequenceManagedProcessProbe(
                ManagedProcessKind.NotManaged,
                ManagedProcessKind.Managed));
        var processEvent = new StartupEvent(
            StartupEventType.ProcessObserved,
            new PerformanceTimestamp(1),
            TimeSpan.Zero,
            new ProcessIdentity(42, 1234));

        await session.ObserveAsync(
            [processEvent],
            StartupLaunchDisposition.Pending,
            TestContext.CancellationToken);
        await session.ObserveAsync(
            [],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);

        Assert.HasCount(1, processFactory.Requests);
    }

    [TestMethod]
    public async Task Session_ReportsNonManagedTargetWithoutStartingTool()
    {
        var processFactory = new FakeOwnedToolProcessFactory();
        await using var session = CreateSession(
            processFactory,
            new SequenceManagedProcessProbe(ManagedProcessKind.NotManaged));

        await session.ObserveAsync(
            [
                new(
                    StartupEventType.ProcessObserved,
                    new PerformanceTimestamp(1),
                    TimeSpan.Zero,
                    new ProcessIdentity(42, 1234)),
            ],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.Launched);

        Assert.AreEqual("non-managed-target", session.Result.DotNetTrace.Status);
        Assert.IsEmpty(processFactory.Requests);
    }

    [TestMethod]
    public async Task Collector_PreservesArtifactAndReportsQuotaExceeded()
    {
        var processFactory = new FakeOwnedToolProcessFactory
        {
            ArtifactLength = ManagedDiagnosticsSession.ArtifactQuotaBytes + 1,
        };
        await using var session = CreateSession(
            processFactory,
            new SequenceManagedProcessProbe(ManagedProcessKind.Managed));

        await session.ObserveAsync(
            [
                new(
                    StartupEventType.ProcessObserved,
                    new PerformanceTimestamp(1),
                    TimeSpan.Zero,
                    new ProcessIdentity(42, 1234)),
            ],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.Launched);

        Assert.AreEqual("quota-exceeded", session.Result.DotNetTrace.Status);
        Assert.AreEqual("exceeded", session.Result.DotNetTrace.QuotaStatus);
        Assert.IsTrue(File.Exists(Path.Join(_root, "managed.nettrace")));
    }

    [TestMethod]
    public async Task Collector_StartFailure_IsExplicitAndDoesNotStopUnownedProcess()
    {
        var processFactory = new FakeOwnedToolProcessFactory
        {
            ThrowOnStart = true,
        };
        await using var session = CreateSession(
            processFactory,
            new SequenceManagedProcessProbe(ManagedProcessKind.Managed));

        await session.ObserveAsync(
            [
                new(
                    StartupEventType.ProcessObserved,
                    new PerformanceTimestamp(1),
                    TimeSpan.Zero,
                    new ProcessIdentity(42, 1234)),
            ],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.Launched);

        Assert.AreEqual("start-failed", session.Result.DotNetTrace.Status);
        StringAssert.Contains(session.Result.DotNetTrace.Error, "controlled start failure");
        Assert.IsEmpty(processFactory.Processes);
    }

    [TestMethod]
    public async Task Collector_SoftStopTimeout_IsExplicit()
    {
        var processFactory = new FakeOwnedToolProcessFactory
        {
            SoftStopSucceeds = false,
        };
        await using var session = CreateSession(
            processFactory,
            new SequenceManagedProcessProbe(ManagedProcessKind.Managed));

        await session.ObserveAsync(
            [
                new(
                    StartupEventType.ProcessObserved,
                    new PerformanceTimestamp(1),
                    TimeSpan.Zero,
                    new ProcessIdentity(42, 1234)),
            ],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.Launched);

        Assert.AreEqual("stop-timeout", session.Result.DotNetTrace.Status);
        Assert.IsNotNull(session.Result.DotNetTrace.StopStartedUtc);
        Assert.IsNotNull(session.Result.DotNetTrace.StopCompletedUtc);
    }

    public TestContext TestContext { get; set; } = null!;

    private ManagedDiagnosticsSession CreateSession(
        FakeOwnedToolProcessFactory processFactory,
        IManagedProcessProbe probe) => new(
            probe,
            processFactory,
            new(
                ManagedDiagnosticTool.DotNetTrace,
                true,
                Path.Join(_root, "dotnet-trace.exe"),
                "9.0.1"),
            new(ManagedDiagnosticTool.DotNetCounters, false),
            withTrace: true,
            withCounters: false,
            Path.Join(_root, "managed.nettrace"),
            Path.Join(_root, "managed-counters.json"),
            durationSeconds: 10);

    private sealed class SequenceManagedProcessProbe(params ManagedProcessKind[] results)
        : IManagedProcessProbe
    {
        private int _index;

        public ManagedProcessProbeResult Probe(ProcessIdentity process)
        {
            var index = Math.Min(_index++, results.Length - 1);
            return new(results[index]);
        }
    }

    private sealed class VersionProcessRunner : IProcessRunner
    {
        public ProcessRunRequest? Request { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            Action<string>? onOutputLine = null,
            Action<string>? onErrorLine = null,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new ProcessRunResult(0, "9.0.1\r\n", string.Empty));
        }
    }

    private sealed class FakeOwnedToolProcessFactory : IOwnedToolProcessFactory
    {
        public long ArtifactLength { get; init; } = 3;

        public bool ThrowOnStart { get; init; }

        public bool SoftStopSucceeds { get; init; } = true;

        public List<ProcessRunRequest> Requests { get; } = [];

        public List<FakeOwnedToolProcess> Processes { get; } = [];

        public IOwnedToolProcess Start(ProcessRunRequest request)
        {
            if (ThrowOnStart)
            {
                throw new IOException("controlled start failure");
            }
            Requests.Add(request);
            var outputIndex = request.Arguments.ToList().IndexOf("--output");
            var process = new FakeOwnedToolProcess(
                request.Arguments[outputIndex + 1],
                ArtifactLength,
                SoftStopSucceeds);
            Processes.Add(process);
            return process;
        }
    }

    private sealed class FakeOwnedToolProcess(
        string outputPath,
        long artifactLength,
        bool softStopSucceeds)
        : IOwnedToolProcess
    {
        private readonly TaskCompletionSource<ProcessRunResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProcessRunResult> Completion => _completion.Task;

        public string? SoftStopInput { get; private set; }

        public Task<bool> RequestSoftStopAsync(string input, TimeSpan timeout)
        {
            SoftStopInput = input;
            if (!softStopSucceeds)
            {
                return Task.FromResult(false);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using (var stream = File.Create(outputPath))
            {
                stream.SetLength(artifactLength);
            }
            _completion.TrySetResult(new(0, string.Empty, string.Empty));
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            _completion.TrySetResult(new(1, string.Empty, "disposed"));
            return ValueTask.CompletedTask;
        }
    }
}
