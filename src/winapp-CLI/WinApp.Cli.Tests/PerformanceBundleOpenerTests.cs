// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class PerformanceBundleOpenerTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Join(Path.GetTempPath(), $"performance-open-{Guid.NewGuid():N}");
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
    public void Command_ParsesSupportedViewer()
    {
        var command = new PerfOpenCommand();

        var parseResult = command.Parse([_root, "--with", "wpa"]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.AreEqual("wpa", parseResult.GetValue(PerfOpenCommand.WithOption));
    }

    [TestMethod]
    public void OpenWithWpa_UsesAbsoluteViewerAndArgumentList()
    {
        var bundle = CreateBundle("traces/system.etl", "etl");
        var launcher = new CapturingLauncher();
        var wpa = Path.Join(_root, "wpa.exe");
        var opener = new PerformanceBundleOpener(new FixedViewerResolver(wpa), launcher);

        var result = opener.Open(bundle, "wpa");

        Assert.AreEqual("opened", result.Status);
        Assert.AreEqual(wpa, launcher.StartInfo!.FileName);
        Assert.IsFalse(launcher.StartInfo.UseShellExecute);
        Assert.HasCount(1, launcher.StartInfo.ArgumentList);
        Assert.AreEqual(
            Path.Join(bundle, "traces", "system.etl"),
            launcher.StartInfo.ArgumentList[0]);
    }

    [TestMethod]
    public void Open_RejectsArtifactEscapingBundle()
    {
        var bundle = Path.Join(_root, "escape.winappperf");
        Directory.CreateDirectory(bundle);
        var outside = Path.Join(_root, "outside.etl");
        File.WriteAllBytes(outside, [1]);
        File.WriteAllText(
            Path.Join(bundle, "manifest.json"),
            """{"schemaVersion":"0.2","artifacts":[{"path":"../outside.etl","kind":"etl","collector":"wpr","sizeBytes":1}]}""");
        var launcher = new CapturingLauncher();
        var opener = new PerformanceBundleOpener(new FixedViewerResolver("C:\\wpa.exe"), launcher);

        var result = opener.Open(bundle, "wpa");

        Assert.AreEqual("invalid", result.Status);
        StringAssert.Contains(result.Error, "escapes the bundle root");
        Assert.IsNull(launcher.StartInfo);
    }

    [TestMethod]
    public void OpenWithWpa_WhenViewerMissing_PrintsActionableHandoff()
    {
        var bundle = CreateBundle("traces/system.etl", "etl");
        var opener = new PerformanceBundleOpener(
            new FixedViewerResolver(null),
            new CapturingLauncher());

        var result = opener.Open(bundle, "wpa");

        Assert.AreEqual("unavailable", result.Status);
        StringAssert.Contains(result.Error, "Windows Performance Analyzer");
        StringAssert.Contains(result.Handoff, "winapp perf open");
    }

    [TestMethod]
    public void OpenWithDefault_UsesShellAssociationWithoutArguments()
    {
        var bundle = CreateBundle("traces/managed.nettrace", "nettrace");
        var launcher = new CapturingLauncher();
        var opener = new PerformanceBundleOpener(new FixedViewerResolver(null), launcher);

        var result = opener.Open(bundle, "default");

        Assert.AreEqual("opened", result.Status);
        Assert.IsTrue(launcher.StartInfo!.UseShellExecute);
        Assert.AreEqual(
            Path.Join(bundle, "traces", "managed.nettrace"),
            launcher.StartInfo.FileName);
        Assert.IsEmpty(launcher.StartInfo.ArgumentList);
    }

    public TestContext TestContext { get; set; } = null!;

    private string CreateBundle(string artifact, string kind)
    {
        var bundle = Path.Join(_root, $"{Guid.NewGuid():N}.winappperf");
        var artifactPath = Path.Join(bundle, artifact.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllBytes(artifactPath, [1, 2, 3]);
        File.WriteAllText(
            Path.Join(bundle, "manifest.json"),
            $$"""{"schemaVersion":"0.2","artifacts":[{"path":"{{artifact}}","kind":"{{kind}}","collector":"test","sizeBytes":3}]}""");
        return bundle;
    }

    private sealed class FixedViewerResolver(string? wpaPath) : IPerformanceViewerResolver
    {
        public string? ResolveWpa() => wpaPath;
    }

    private sealed class CapturingLauncher : IPerformanceViewerLauncher
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public void Launch(ProcessStartInfo startInfo) => StartInfo = startInfo;
    }
}
