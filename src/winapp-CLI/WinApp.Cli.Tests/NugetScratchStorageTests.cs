// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class NugetScratchStorageTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup() =>
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"nuget-scratch-tests-{Guid.NewGuid():N}")).FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void BlockedScratch_FailsBeforeNugetCanLockConfiguration()
    {
        var blocker = Path.Combine(_root, "blocked");
        File.WriteAllText(blocker, "keep");
        var loaded = false;
        var provider = new NugetSourceProvider(new CurrentDirectoryProvider(_root))
        {
            ScratchDirectoryProvider = () => Path.Combine(blocker, "scratch"),
            LoadSettings = _ =>
            {
                loaded = true;
                return NullSettings.Instance;
            },
        };
        var elapsed = Stopwatch.StartNew();

        var error = Assert.ThrowsExactly<NugetStorageException>(() => _ = provider.Settings);

        Assert.IsFalse(loaded, "NuGet's configuration loader must not enter its lengthy lock retry.");
        Assert.IsLessThan(TimeSpan.FromSeconds(2), elapsed.Elapsed);
        StringAssert.Contains(error.Message, "NUGET_SCRATCH");
        StringAssert.Contains(error.Message, "all processes sharing the same packages cache");
        Assert.AreEqual("keep", File.ReadAllText(blocker));
    }

    [TestMethod]
    public void WritableScratch_UsesSelectedLockNamespaceAndRemovesProbe()
    {
        var scratch = Path.Combine(_root, "selected-scratch");
        var previousSetting = Environment.GetEnvironmentVariable("NUGET_SCRATCH");
        var provider = new NugetSourceProvider(new CurrentDirectoryProvider(_root))
        {
            ScratchDirectoryProvider = () => scratch,
        };

        provider.EnsureScratchStorage();

        Assert.IsTrue(Directory.Exists(Path.Combine(scratch, "lock")));
        Assert.IsEmpty(Directory.GetFiles(scratch, "*", SearchOption.AllDirectories));
        Assert.AreEqual(previousSetting, Environment.GetEnvironmentVariable("NUGET_SCRATCH"));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, ".winapp")));
    }

    [TestMethod]
    public async Task Download_RechecksScratchAfterConfigurationWasCached()
    {
        var provider = new NugetSourceProvider(new CurrentDirectoryProvider(_root))
        {
            ScratchDirectoryProvider = () => Path.Combine(_root, "scratch"),
            LoadSettings = _ => NullSettings.Instance,
        };
        _ = provider.Settings;
        var blocker = Path.Combine(_root, "blocked");
        File.WriteAllText(blocker, "keep");
        provider.ScratchDirectoryProvider = () => Path.Combine(blocker, "scratch");
        var packages = Path.Combine(_root, "packages");
        using var cacheContext = new SourceCacheContext();

        var error = await Assert.ThrowsExactlyAsync<NugetStorageException>(() =>
            new NugetPackageDownloader(provider).DownloadPackageAsync(
                new PackageIdentity("Probe.Package", new NuGetVersion("1.0.0")),
                packages, cacheContext, CancellationToken.None));

        StringAssert.Contains(error.Message, "scratch locks");
        Assert.IsFalse(Directory.Exists(packages));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("relative-scratch")]
    public void InvalidScratch_IsNotSilentlyReplaced(string path)
    {
        var provider = new NugetSourceProvider(new CurrentDirectoryProvider(_root))
        {
            ScratchDirectoryProvider = () => path,
        };

        var error = Assert.ThrowsExactly<NugetStorageException>(provider.EnsureScratchStorage);

        StringAssert.Contains(error.Message, "fully qualified");
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }
}
