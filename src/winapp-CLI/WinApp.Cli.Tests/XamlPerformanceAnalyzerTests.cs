// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class XamlPerformanceAnalyzerTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Join(Path.GetTempPath(), $"xaml-analysis-{Guid.NewGuid():N}");
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
    public async Task Analyze_ExportsOnlyTargetRowsAndBuildsWinUiSummary()
    {
        var etl = CreateEtl();
        var runner = new ExporterProcessRunner(
            """
            Process,Thread ID,Type,IsInteresting,Duration (ms),Weight (ms),Count,Start (s),Stop (s)
            PerformanceDiagnosticsLab.exe (2900),9340,Region of Interest,True,"1,591.638000",0.000000,1,2.000000000,3.591638000
            PerformanceDiagnosticsLab.exe (2900),9340,WXM::InitializeForCurrentThread,True,"1,441.203800",0.000000,1,2.100000000,3.541203800
            PerformanceDiagnosticsLab.exe (2900),9340,Frame,True,137.128600,0.000000,1,3.400000000,3.537128600
            PerformanceDiagnosticsLab.exe (2900),9340,Frame,False,16.882800,0.000000,1,4.000000000,4.016882800
            PerformanceDiagnosticsLab.exe (2900),9340,UpdateLayout,True,95.953200,0.000000,1,3.410000000,3.505953200
            PerformanceDiagnosticsLab.exe (2900),17112,Create graphics device,True,21.385900,0.000000,1,1.500000000,1.521385900
            Other.exe (42),7,Frame,True,999.000000,0.000000,1,1.000000000,1.999000000
            """);
        var analyzer = new XamlPerformanceAnalyzer(
            new AvailableResolver(),
            runner,
            NullLogger<XamlPerformanceAnalyzer>.Instance);

        var result = await analyzer.AnalyzeAsync(
            requested: true,
            etl,
            new ProcessIdentity(2900, 1234),
            lossStatus: "none",
            TestContext.CancellationToken);

        Assert.AreEqual("analyzed", result.Manifest.Status);
        Assert.AreEqual("complete", result.Manifest.Coverage);
        Assert.AreEqual(6, result.Manifest.MatchedIntervalCount);
        Assert.AreEqual(9340, result.Manifest.Summary!.UiThreadId);
        Assert.AreEqual(1591.638, result.Manifest.Summary.RegionOfInterestMs);
        Assert.AreEqual(1441.2038, result.Manifest.Summary.InitializationMs);
        Assert.AreEqual(137.1286, result.Manifest.Summary.LongestInterestingFrameMs);
        Assert.AreEqual(95.9532, result.Manifest.Summary.LongestInterestingUpdateLayoutMs);
        Assert.AreEqual(21.3859, result.Manifest.Summary.GraphicsDeviceCreationMs);
        Assert.HasCount(6, result.Summary!.Intervals);
        Assert.AreEqual(1234, result.Summary.TargetProcessStartTimeUtcTicks);
        Assert.AreEqual(
            WptXamlProfileResources.AllXamlInfoResourceName,
            Path.GetFileName(runner.ProfilePath));
        Assert.IsFalse(Directory.Exists(runner.OutputDirectory));
    }

    [TestMethod]
    public async Task Analyze_HeaderOnlyCsvIsAnalyzedWithNoActivity()
    {
        var analyzer = new XamlPerformanceAnalyzer(
            new AvailableResolver(),
            new ExporterProcessRunner(
                "Process,Thread ID,Type,IsInteresting,Duration (ms),Weight (ms),Count,Start (s),Stop (s)"),
            NullLogger<XamlPerformanceAnalyzer>.Instance);

        var result = await analyzer.AnalyzeAsync(
            requested: true,
            CreateEtl(),
            new ProcessIdentity(42, 100),
            lossStatus: "none",
            TestContext.CancellationToken);

        Assert.AreEqual("analyzed", result.Manifest.Status);
        Assert.AreEqual("complete", result.Manifest.Coverage);
        Assert.AreEqual(0, result.Manifest.MatchedIntervalCount);
        Assert.IsEmpty(result.Summary!.Intervals);
    }

    [TestMethod]
    public async Task Analyze_MissingToolReportsUnavailableWithoutRunningExporter()
    {
        var runner = new ExporterProcessRunner(string.Empty);
        var analyzer = new XamlPerformanceAnalyzer(
            new UnavailableResolver(),
            runner,
            NullLogger<XamlPerformanceAnalyzer>.Instance);

        var result = await analyzer.AnalyzeAsync(
            requested: true,
            CreateEtl(),
            new ProcessIdentity(42, 100),
            lossStatus: "not-inspected",
            TestContext.CancellationToken);

        Assert.AreEqual("analysis-unavailable", result.Manifest.Status);
        Assert.AreEqual("unavailable", result.Manifest.Coverage);
        Assert.IsNotNull(result.Manifest.Error);
        Assert.IsNotNull(result.Manifest.Remediation);
        Assert.Contains("PerfXamlNotRegistered", result.Manifest.Error);
        Assert.Contains("perfcore.ini", result.Manifest.Remediation);
        Assert.IsNull(runner.Request);
        Assert.IsNull(result.Summary);
    }

    [TestMethod]
    public async Task Analyze_MalformedCsvReportsFailureAndRetainsRawEvidence()
    {
        var analyzer = new XamlPerformanceAnalyzer(
            new AvailableResolver(),
            new ExporterProcessRunner(
                """
                Process,Thread ID,Type,IsInteresting,Duration (ms),Weight (ms),Count,Start (s),Stop (s)
                App.exe (42),not-a-thread,Frame,True,1.0,0,1,2.0,3.0
                """),
            NullLogger<XamlPerformanceAnalyzer>.Instance);

        var result = await analyzer.AnalyzeAsync(
            requested: true,
            CreateEtl(),
            new ProcessIdentity(42, 100),
            lossStatus: "none",
            TestContext.CancellationToken);

        Assert.AreEqual("analysis-failed", result.Manifest.Status);
        Assert.IsNotNull(result.Manifest.Error);
        Assert.Contains("invalid Thread ID", result.Manifest.Error);
        Assert.IsNull(result.Summary);
    }

    public TestContext TestContext { get; set; }

    private string CreateEtl()
    {
        var path = Path.Join(_root, "system.etl");
        File.WriteAllBytes(path, [1]);
        return path;
    }

    private sealed class AvailableResolver : IWptXamlToolResolver
    {
        public WptXamlToolResolution Resolve() => new()
        {
            IsAvailable = true,
            Source = WptXamlToolSource.TrustedWindowsKits,
            UnavailableReason = WptXamlToolUnavailableReason.None,
            ToolkitDirectory = @"C:\Wpt",
            WpaExporterPath = @"C:\Wpt\wpaexporter.exe",
            PerfXamlPath = @"C:\Wpt\perf_xaml.dll",
            PerfcoreIniPath = @"C:\Wpt\perfcore.ini",
            ToolVersion = "11.7.395.48728",
            PluginVersion = "10.0.26100.8249",
        };
    }

    private sealed class UnavailableResolver : IWptXamlToolResolver
    {
        public WptXamlToolResolution Resolve() => new()
        {
            IsAvailable = false,
            Source = WptXamlToolSource.TrustedWindowsKits,
            UnavailableReason = WptXamlToolUnavailableReason.PerfXamlNotRegistered,
            Remediation = "Add perf_xaml.dll to perfcore.ini.",
        };
    }

    private sealed class ExporterProcessRunner(string csv) : IProcessRunner
    {
        public ProcessRunRequest? Request { get; private set; }
        public string? ProfilePath { get; private set; }
        public string? OutputDirectory { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            Action<string>? onOutputLine = null,
            Action<string>? onErrorLine = null,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            ProfilePath = ValueAfter(request.Arguments, "-profile");
            OutputDirectory = ValueAfter(request.Arguments, "-outputfolder");
            Assert.IsTrue(File.Exists(ProfilePath));
            Directory.CreateDirectory(OutputDirectory);
            File.WriteAllText(
                Path.Join(OutputDirectory, "Xaml_Frame_Analysis_Summary_Table_All_Xaml_Info.csv"),
                csv);
            return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
        }

        private static string ValueAfter(IReadOnlyList<string> arguments, string option)
        {
            var index = -1;
            for (var i = 0; i < arguments.Count; i++)
            {
                if (string.Equals(arguments[i], option, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }
            Assert.IsGreaterThanOrEqualTo(0, index);
            Assert.IsLessThan(arguments.Count - 1, index);
            return arguments[index + 1];
        }
    }
}
