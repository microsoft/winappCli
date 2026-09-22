// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.Tracing;
using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class ManagedDiagnosticsTests
{
    private static readonly string[] ExpectedLifecycle = ["stop", "eof", "dispose"];
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
    public void EventPipeConfiguration_CombinesSamplesRuntimeEventsAndCounters()
    {
        var configuration = ManagedDiagnosticsSession.EventPipeConfiguration;

        Assert.AreEqual(64, configuration.CircularBufferSizeInMegabytes);
        Assert.IsTrue(configuration.RequestRundown);
        Assert.IsFalse(configuration.RequestStackwalk);
        Assert.HasCount(3, configuration.Providers);
        var profiler = configuration.Providers[0];
        Assert.AreEqual("Microsoft-DotNETCore-SampleProfiler", profiler.Name);
        Assert.AreEqual(EventLevel.Informational, profiler.EventLevel);
        Assert.AreEqual(0L, profiler.Keywords);
        var runtime = configuration.Providers[1];
        Assert.AreEqual("Microsoft-Windows-DotNETRuntime", runtime.Name);
        Assert.AreEqual(EventLevel.Informational, runtime.EventLevel);
        Assert.AreEqual(0x100003801DL, runtime.Keywords);
        var counters = configuration.Providers[2];
        Assert.AreEqual("System.Runtime", counters.Name);
        Assert.AreEqual("1", counters.Arguments["EventCounterIntervalSec"]);
    }

    [TestMethod]
    public void ProcessProbe_RejectsReusedProcessIdBeforeInspectingModules()
    {
        using var process = Process.GetCurrentProcess();

        var result = new ManagedProcessProbe().Probe(new(
            process.Id,
            process.StartTime.ToUniversalTime().Ticks + 1));

        Assert.AreEqual(ManagedProcessKind.Unavailable, result.Kind);
        StringAssert.Contains(result.Error, "PID was reused");
    }

    [TestMethod]
    public async Task Session_AttachesToNewCoreClrGenerationAndDrainsBeforeDisposal()
    {
        var events = new List<string>();
        var client = new FakeEventPipeClient(() => new FakeEventPipeSession(
            [1, 2, 3],
            FakeStopMode.Success,
            events));
        await using var session = CreateSession(
            client,
            new SequenceManagedProcessProbe(ManagedProcessKind.Managed));
        var process = new ProcessIdentity(42, 1234);

        await session.ObserveAsync(
            [Observed(process)],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.Launched);

        Assert.HasCount(1, client.Starts);
        Assert.AreEqual(process, client.Starts[0].Target);
        CollectionAssert.AreEqual(ExpectedLifecycle, events);
        Assert.AreEqual("recorded", session.Result.Status);
        Assert.AreEqual("attached-after-activation", session.Result.Coverage);
        Assert.AreEqual(42, session.Result.TargetProcessId);
        Assert.AreEqual(1234L, session.Result.TargetProcessStartTimeUtcTicks);
        Assert.AreEqual("traces/managed.nettrace", session.Result.Artifact);
        Assert.AreEqual(3L, session.Result.FileSize);
    }

    [TestMethod]
    public async Task Session_DoesNotAttachToPreExistingGeneration()
    {
        var client = new FakeEventPipeClient();
        await using var session = CreateSession(
            client,
            new SequenceManagedProcessProbe(ManagedProcessKind.Managed));

        await session.ObserveAsync(
            [Observed(new ProcessIdentity(42, 1234), presentBeforeActivation: true)],
            StartupLaunchDisposition.AttachedLate,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.AttachedLate);

        Assert.IsEmpty(client.Starts);
        Assert.AreEqual("attached-late", session.Result.Status);
    }

    [TestMethod]
    public async Task Session_RetriesUntilCoreClrAppears()
    {
        var client = new FakeEventPipeClient();
        await using var session = CreateSession(
            client,
            new SequenceManagedProcessProbe(
                ManagedProcessKind.NotManaged,
                ManagedProcessKind.Managed));
        var process = new ProcessIdentity(42, 1234);

        await session.ObserveAsync(
            [Observed(process)],
            StartupLaunchDisposition.Pending,
            TestContext.CancellationToken);
        await session.ObserveAsync(
            [],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);

        Assert.HasCount(1, client.Starts);
        await session.StopAsync(StartupLaunchDisposition.Launched);
    }

    [TestMethod]
    public async Task Session_ReportsNativeTargetWithoutStartingEventPipe()
    {
        var client = new FakeEventPipeClient();
        await using var session = CreateSession(
            client,
            new SequenceManagedProcessProbe(ManagedProcessKind.NotManaged));

        await session.ObserveAsync(
            [Observed(new ProcessIdentity(42, 1234))],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.Launched);

        Assert.AreEqual("non-managed-target", session.Result.Status);
        Assert.IsEmpty(client.Starts);
    }

    [TestMethod]
    public async Task Session_ReportsNotObservedWhenNoNewProcessGenerationExists()
    {
        await using var session = CreateSession(
            new FakeEventPipeClient(),
            new SequenceManagedProcessProbe(ManagedProcessKind.Managed));

        await session.StopAsync(StartupLaunchDisposition.Pending);

        Assert.AreEqual("not-observed", session.Result.Status);
    }

    [TestMethod]
    public async Task Session_ReportsInspectionFailureWhenProcessCannotBeInspected()
    {
        var client = new FakeEventPipeClient();
        await using var session = CreateSession(
            client,
            new SequenceManagedProcessProbe(
                new ManagedProcessProbeResult(ManagedProcessKind.Unavailable, "access denied")));

        await session.ObserveAsync(
            [Observed(new ProcessIdentity(42, 1234))],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.Launched);

        Assert.AreEqual("inspection-failed", session.Result.Status);
        StringAssert.Contains(session.Result.Error, "access denied");
    }

    [TestMethod]
    public async Task Session_ReportsExplicitStartFailure()
    {
        var client = new FakeEventPipeClient
        {
            StartException = new IOException("controlled start race"),
        };
        await using var session = CreateSession(
            client,
            new SequenceManagedProcessProbe(ManagedProcessKind.Managed));

        await session.ObserveAsync(
            [Observed(new ProcessIdentity(42, 1234))],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.Launched);

        Assert.AreEqual("start-failed", session.Result.Status);
        StringAssert.Contains(session.Result.Error, "controlled start race");
    }

    [TestMethod]
    public async Task Session_CapsArtifactAndContinuesDraining()
    {
        var events = new List<string>();
        var eventPipeSession = new FakeEventPipeSession(
            [1, 2, 3, 4, 5, 6],
            FakeStopMode.Success,
            events);
        var client = new FakeEventPipeClient(() => eventPipeSession);
        await using var session = CreateSession(
            client,
            new SequenceManagedProcessProbe(ManagedProcessKind.Managed),
            artifactQuotaBytes: 4);

        await session.ObserveAsync(
            [Observed(new ProcessIdentity(42, 1234))],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.Launched);

        Assert.AreEqual("quota-exceeded", session.Result.Status);
        Assert.AreEqual("partial", session.Result.Coverage);
        Assert.AreEqual("exceeded", session.Result.QuotaStatus);
        Assert.AreEqual(4L, session.Result.FileSize);
        Assert.IsNotNull(session.Result.Error);
        Assert.Contains("additional bytes were discarded while draining", session.Result.Error);
        Assert.AreEqual(6, eventPipeSession.StreamBytesRead);
        Assert.AreEqual(4L, new FileInfo(Path.Join(_root, "managed.nettrace")).Length);
    }

    [TestMethod]
    public async Task Session_ReportsStopFailureAfterDraining()
    {
        var client = new FakeEventPipeClient(() => new FakeEventPipeSession(
            [1],
            FakeStopMode.Failure,
            []));
        await using var session = CreateSession(
            client,
            new SequenceManagedProcessProbe(ManagedProcessKind.Managed));

        await session.ObserveAsync(
            [Observed(new ProcessIdentity(42, 1234))],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.Launched);

        Assert.AreEqual("stop-failed", session.Result.Status);
        Assert.IsNotNull(session.Result.StopStartedUtc);
        Assert.IsNotNull(session.Result.StopCompletedUtc);
    }

    [TestMethod]
    public async Task Session_RecordsDrainedTraceWhenTargetExitedBeforeStop()
    {
        var client = new FakeEventPipeClient(() => new FakeEventPipeSession(
            [1],
            FakeStopMode.TargetExited,
            []));
        await using var session = CreateSession(
            client,
            new SequenceManagedProcessProbe(ManagedProcessKind.Managed));

        await session.ObserveAsync(
            [Observed(new ProcessIdentity(42, 1234))],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.Launched);

        Assert.AreEqual("recorded", session.Result.Status);
        Assert.AreEqual("not-inspected", session.Result.LossStatus);
        Assert.IsNull(session.Result.Error);
        Assert.AreEqual(1L, session.Result.FileSize);
    }

    [TestMethod]
    public async Task Session_ReportsStopTimeoutAndClosesArtifact()
    {
        var client = new FakeEventPipeClient(() => new FakeEventPipeSession(
            [1],
            FakeStopMode.Timeout,
            []));
        await using var session = CreateSession(
            client,
            new SequenceManagedProcessProbe(ManagedProcessKind.Managed),
            stopGracePeriod: TimeSpan.FromMilliseconds(50));

        await session.ObserveAsync(
            [Observed(new ProcessIdentity(42, 1234))],
            StartupLaunchDisposition.Launched,
            TestContext.CancellationToken);
        await session.StopAsync(StartupLaunchDisposition.Launched);

        Assert.AreEqual("stop-timeout", session.Result.Status);
        Assert.IsNotNull(session.Result.StopStartedUtc);
        Assert.IsNotNull(session.Result.StopCompletedUtc);
        Assert.IsTrue(File.Exists(Path.Join(_root, "managed.nettrace")));
    }

    public TestContext TestContext { get; set; } = null!;

    private ManagedDiagnosticsSession CreateSession(
        FakeEventPipeClient client,
        IManagedProcessProbe probe,
        long artifactQuotaBytes = ManagedDiagnosticsSession.ArtifactQuotaBytes,
        TimeSpan? stopGracePeriod = null) => new(
            probe,
            client,
            Path.Join(_root, "managed.nettrace"),
            artifactQuotaBytes,
            stopGracePeriod);

    private static StartupEvent Observed(
        ProcessIdentity process,
        bool presentBeforeActivation = false) => new(
            StartupEventType.ProcessObserved,
            new PerformanceTimestamp(1),
            TimeSpan.Zero,
            process,
            WasPresentBeforeActivation: presentBeforeActivation);

    private sealed class SequenceManagedProcessProbe : IManagedProcessProbe
    {
        private readonly ManagedProcessProbeResult[] _results;
        private int _index;

        public SequenceManagedProcessProbe(params ManagedProcessKind[] kinds)
            : this(kinds.Select(kind => new ManagedProcessProbeResult(kind)).ToArray())
        {
        }

        public SequenceManagedProcessProbe(params ManagedProcessProbeResult[] results)
        {
            _results = results;
        }

        public ManagedProcessProbeResult Probe(ProcessIdentity process)
        {
            var index = Math.Min(_index++, _results.Length - 1);
            return _results[index];
        }
    }

    private sealed class FakeEventPipeClient(
        Func<FakeEventPipeSession>? createSession = null) : IManagedEventPipeClient
    {
        public Exception? StartException { get; init; }

        public List<(ProcessIdentity Target, ManagedEventPipeConfiguration Configuration)> Starts { get; } = [];

        public Task<IManagedEventPipeSession> StartSessionAsync(
            ProcessIdentity target,
            ManagedEventPipeConfiguration configuration,
            CancellationToken cancellationToken)
        {
            if (StartException is not null)
            {
                return Task.FromException<IManagedEventPipeSession>(StartException);
            }
            Starts.Add((target, configuration));
            return Task.FromResult<IManagedEventPipeSession>(
                createSession?.Invoke() ?? new FakeEventPipeSession([], FakeStopMode.Success, []));
        }
    }

    private enum FakeStopMode
    {
        Success,
        Failure,
        TargetExited,
        Timeout,
    }

    private sealed class FakeEventPipeSession(
        byte[] bytes,
        FakeStopMode stopMode,
        List<string> events) : IManagedEventPipeSession
    {
        private readonly GatedEventStream _stream = new(bytes, events);

        public Stream EventStream => _stream;

        public int StreamBytesRead => _stream.BytesRead;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            events.Add("stop");
            return stopMode switch
            {
                FakeStopMode.Success => Complete(),
                FakeStopMode.Failure => Fail(),
                FakeStopMode.TargetExited => TargetExited(),
                FakeStopMode.Timeout => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(cancellationToken)),
            };
        }

        public void Dispose()
        {
            events.Add("dispose");
            _stream.Complete();
            _stream.Dispose();
        }

        private Task Complete()
        {
            _stream.Complete();
            return Task.CompletedTask;
        }

        private Task Fail()
        {
            _stream.Complete();
            return Task.FromException(new IOException("controlled stop failure"));
        }

        private Task TargetExited()
        {
            _stream.Complete();
            return Task.FromException(
                new ServerNotAvailableException("Process 42 is not running."));
        }
    }

    private sealed class GatedEventStream(byte[] bytes, List<string> events) : Stream
    {
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public int BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Complete() => _completed.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < bytes.Length)
            {
                var count = Math.Min(buffer.Length, bytes.Length - _position);
                bytes.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                BytesRead += count;
                return count;
            }

            await _completed.Task.WaitAsync(cancellationToken);
            events.Add("eof");
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
