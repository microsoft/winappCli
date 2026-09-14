// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class WprCollectorTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Join(Path.GetTempPath(), $"wpr-collector-{Guid.NewGuid():N}");
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
    [DataRow(0)]
    [DataRow(301)]
    public void Safety_RejectsUnboundedOrExcessiveDuration(int durationSeconds)
    {
        var error = WprRecordingSafety.Validate(
            durationSeconds,
            WprRecordingSafety.MinimumFreeBytes);

        StringAssert.Contains(error, "--duration-sec between 1 and 300");
    }

    [TestMethod]
    public void Safety_RejectsOutputVolumeWithLessThanOneGiBFree()
    {
        var error = WprRecordingSafety.Validate(
            durationSeconds: 10,
            WprRecordingSafety.MinimumFreeBytes - 1);

        StringAssert.Contains(error, "at least 1 GiB free");
    }

    [TestMethod]
    public void Safety_AcceptsBoundedDurationAndFreeSpace()
    {
        Assert.IsNull(WprRecordingSafety.Validate(
            durationSeconds: 10,
            WprRecordingSafety.MinimumFreeBytes));
    }

    [TestMethod]
    public async Task StartAndStop_UseSameUniqueInstanceAndRetainEtl()
    {
        var runner = new FakeProcessRunner();
        var factory = new WprCollectorFactory(runner);
        var etl = Path.Join(_root, "traces", "system.etl");
        var temporary = Path.Join(_root, "traces", ".wpr-temp");
        await using var collector = factory.Create(etl, temporary);

        await collector.StartAsync(TestContext.CancellationToken);
        await collector.StopAsync();

        Assert.HasCount(2, runner.Requests);
        CollectionAssert.AreEqual(
            new[] { "-start", "FileIO.Verbose", "-filemode", "-recordtempto", temporary },
            runner.Requests[0].Arguments.Take(5).ToArray());
        Assert.AreEqual("-instancename", runner.Requests[0].Arguments[^2]);
        Assert.AreEqual("-instancename", runner.Requests[1].Arguments[^2]);
        Assert.AreEqual(runner.Requests[0].Arguments[^1], runner.Requests[1].Arguments[^1]);
        StringAssert.StartsWith(runner.Requests[0].Arguments[^1], "winapp-perf-");
        Assert.AreEqual("recorded", collector.Result.Status);
        Assert.AreEqual("raw-etl-event-loss-not-inspected", collector.Result.Coverage);
        Assert.AreEqual("traces/system.etl", collector.Result.Artifact);
        Assert.IsTrue(collector.Result.FileSize > 0);
        Assert.IsNotNull(collector.Result.StartedUtc);
        Assert.IsNotNull(collector.Result.StopStartedUtc);
        Assert.IsNotNull(collector.Result.StopCompletedUtc);
        Assert.IsFalse(Directory.Exists(temporary));
    }

    [TestMethod]
    public async Task StartFailure_DoesNotIssueUnsafeStop()
    {
        var runner = new FakeProcessRunner
        {
            StartResult = new(5, string.Empty, "Access is denied."),
        };
        var factory = new WprCollectorFactory(runner);
        await using var collector = factory.Create(
            Path.Join(_root, "system.etl"),
            Path.Join(_root, "temp"));

        await collector.StartAsync(TestContext.CancellationToken);
        await collector.StopAsync();

        Assert.HasCount(1, runner.Requests);
        Assert.AreEqual("start-failed", collector.Result.Status);
        StringAssert.Contains(collector.Result.Error, "Access is denied");
    }

    [TestMethod]
    public async Task CancelAfterSuccessfulStart_StillAllowsOwnedSoftStop()
    {
        var runner = new FakeProcessRunner();
        var factory = new WprCollectorFactory(runner);
        var collector = factory.Create(
            Path.Join(_root, "system.etl"),
            Path.Join(_root, "temp"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => collector.StartAsync(cts.Token));
        await collector.DisposeAsync();

        Assert.HasCount(2, runner.Requests);
        Assert.IsTrue(runner.CancellationTokens.All(token => token == CancellationToken.None));
        Assert.AreEqual("recorded", collector.Result.Status);
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public ProcessRunResult StartResult { get; set; } = new(0, string.Empty, string.Empty);

        public List<ProcessRunRequest> Requests { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            Action<string>? onOutputLine = null,
            Action<string>? onErrorLine = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            CancellationTokens.Add(cancellationToken);
            if (request.Arguments[0] == "-stop")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(request.Arguments[1])!);
                File.WriteAllBytes(request.Arguments[1], [1, 2, 3]);
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            }

            return Task.FromResult(StartResult);
        }
    }
}
