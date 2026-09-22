// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class WptEtlLossInspectorTests
{
    private string _etl = null!;

    [TestInitialize]
    public void Initialize()
    {
        _etl = Path.Join(Path.GetTempPath(), $"loss-{Guid.NewGuid():N}.etl");
        File.WriteAllBytes(_etl, [1]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        File.Delete(_etl);
    }

    [TestMethod]
    public async Task Inspect_ReportsNoLossFromTraceTotals()
    {
        var runner = new FakeRunner(
            """
            Total # Lost Buffers : 0
            Total # Lost Events  : 0

            Trace name: example.etl
                # Lost Buffers       : 9
                # Lost Events        : 10
            """);
        var inspector = new WptEtlLossInspector(new Resolver(withXperf: true), runner);

        var result = await inspector.InspectAsync(_etl, TestContext.CancellationToken);

        Assert.AreEqual("none", result.Status);
        Assert.AreEqual(0, result.LostBuffers);
        Assert.AreEqual(0, result.LostEvents);
        CollectionAssert.AreEqual(
            new[] { "-i", Path.GetFullPath(_etl), "-a", "tracestats" },
            runner.Request!.Arguments.ToArray());
    }

    [TestMethod]
    public async Task Inspect_ReportsDetectedLoss()
    {
        var inspector = new WptEtlLossInspector(
            new Resolver(withXperf: true),
            new FakeRunner(
                """
                Total # Lost Buffers : 2
                Total # Lost Events  : 17
                """));

        var result = await inspector.InspectAsync(_etl, TestContext.CancellationToken);

        Assert.AreEqual("detected", result.Status);
        Assert.AreEqual(2, result.LostBuffers);
        Assert.AreEqual(17, result.LostEvents);
    }

    [TestMethod]
    public async Task Inspect_MissingTrustedXperfDoesNotClaimNoLoss()
    {
        var runner = new FakeRunner(string.Empty);
        var inspector = new WptEtlLossInspector(new Resolver(withXperf: false), runner);

        var result = await inspector.InspectAsync(_etl, TestContext.CancellationToken);

        Assert.AreEqual("not-inspected", result.Status);
        Assert.IsNotNull(result.Error);
        Assert.Contains("xperf.exe", result.Error);
        Assert.IsNull(runner.Request);
    }

    [TestMethod]
    public async Task Inspect_MalformedOutputFailsExplicitly()
    {
        var inspector = new WptEtlLossInspector(
            new Resolver(withXperf: true),
            new FakeRunner("Trace statistics completed."));

        var result = await inspector.InspectAsync(_etl, TestContext.CancellationToken);

        Assert.AreEqual("inspection-failed", result.Status);
        Assert.IsNotNull(result.Error);
        Assert.Contains("Total # Lost Buffers", result.Error);
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class Resolver(bool withXperf) : IWptXamlToolResolver
    {
        public WptXamlToolResolution Resolve() => new()
        {
            IsAvailable = true,
            Source = WptXamlToolSource.TrustedWindowsKits,
            UnavailableReason = WptXamlToolUnavailableReason.None,
            XperfPath = withXperf ? @"C:\Wpt\xperf.exe" : null,
            XperfVersion = withXperf ? "10.0.26100.8249" : null,
        };
    }

    private sealed class FakeRunner(string output) : IProcessRunner
    {
        public ProcessRunRequest? Request { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            Action<string>? onOutputLine = null,
            Action<string>? onErrorLine = null,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new ProcessRunResult(0, output, string.Empty));
        }
    }
}
