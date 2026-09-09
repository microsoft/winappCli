// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="PackageInstallationService"/>. A controllable in-memory NuGet fake (backed by a
/// real temp cache directory so the "already present" checks behave realistically) and a fake config
/// service drive the version-resolution and version-merge logic without any network. Transitive-graph
/// resolution now lives inside the real NuGet client (<see cref="INugetService.InstallPackageAsync"/>
/// returns the full installed set), so this suite exercises the service's own resolve/normalize/merge
/// orchestration over that returned set — the graph walk itself is covered by the NugetService tests.
/// </summary>
[TestClass]
public class PackageInstallationServiceTests
{
    private DirectoryInfo _tempDir = null!;
    private DirectoryInfo _cacheDir = null!;
    private DirectoryInfo _rootDir = null!;
    private FakeConfigService _config = null!;
    private FakeNugetService _nuget = null!;
    private PackageInstallationService _service = null!;
    private TaskContext _taskContext = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Join(Path.GetTempPath(), $"PkgInstTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
        _cacheDir = _tempDir.CreateSubdirectory("cache");
        _rootDir = new DirectoryInfo(Path.Join(_tempDir.FullName, "root"));
        _config = new FakeConfigService();
        _nuget = new FakeNugetService { CacheDirectory = _cacheDir };
        _service = new PackageInstallationService(_config, _nuget, NullLogger<PackageInstallationService>.Instance);

        var task = new GroupableTask("test", null);
        _taskContext = new TaskContext(task, null, new TestConsole(), NullLogger<PackageInstallationServiceTests>.Instance, new Lock());
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            _tempDir.Delete(recursive: true);
        }
    }

    /// <summary>Marks a package as a complete, already-extracted cache entry (directory + ".nupkg.metadata" completion marker) so the product's <c>IsPackageInstalled</c> gate treats it as present.</summary>
    private void MarkPresent(string name, string version) => _nuget.MarkInstalled(name, version);

    #region InitializeWorkspace

    [TestMethod]
    public void InitializeWorkspace_CreatesMissingDirectory()
    {
        Assert.IsFalse(_rootDir.Exists);

        _service.InitializeWorkspace(_rootDir);

        _rootDir.Refresh();
        Assert.IsTrue(_rootDir.Exists);
    }

    [TestMethod]
    public void InitializeWorkspace_ExistingDirectory_NoThrow()
    {
        _rootDir.Create();

        _service.InitializeWorkspace(_rootDir);

        _rootDir.Refresh();
        Assert.IsTrue(_rootDir.Exists);
    }

    #endregion

    #region EnsurePackageAsync

    [TestMethod]
    public async Task EnsurePackageAsync_Success_ReturnsTrue_AndCreatesWorkspace()
    {
        _nuget.DefaultVersion = "1.6.0";

        var ok = await _service.EnsurePackageAsync(_rootDir, "Pkg.X", _taskContext);

        Assert.IsTrue(ok);
        _rootDir.Refresh();
        Assert.IsTrue(_rootDir.Exists);
        CollectionAssert.Contains(_nuget.InstalledPackages, ("Pkg.X", "1.6.0"));
    }

    [TestMethod]
    public async Task EnsurePackageAsync_ExplicitVersion_DoesNotQueryLatest()
    {
        var ok = await _service.EnsurePackageAsync(_rootDir, "Pkg.X", _taskContext, version: "2.0.0");

        Assert.IsTrue(ok);
        CollectionAssert.DoesNotContain(_nuget.QueriedPackages, "Pkg.X");
        CollectionAssert.Contains(_nuget.InstalledPackages, ("Pkg.X", "2.0.0"));
    }

    [TestMethod]
    public async Task EnsurePackageAsync_AlreadyPresent_DelegatesSoTheGraphIsStillVerified()
    {
        MarkPresent("Pkg.X", "3.0.0");

        var ok = await _service.EnsurePackageAsync(_rootDir, "Pkg.X", _taskContext, version: "3.0.0");

        Assert.IsTrue(ok);
        // The root is cached, but EnsurePackageAsync must still hand off to the NuGet service: that call
        // short-circuits the completed root (no re-download) while walking its dependency graph, so a
        // transitive package that went missing is repaired instead of being reported as a complete install.
        CollectionAssert.Contains(_nuget.InstalledPackages, ("Pkg.X", "3.0.0"), "The install path must still be entered for a cached root.");
    }

    [TestMethod]
    public async Task EnsurePackageAsync_NugetThrows_ReturnsFalse()
    {
        _nuget.PackagesToThrow.Add("Pkg.Bad");

        var ok = await _service.EnsurePackageAsync(_rootDir, "Pkg.Bad", _taskContext);

        Assert.IsFalse(ok);
    }

    #endregion

    #region InstallPackagesAsync — version resolution

    [TestMethod]
    public async Task InstallPackagesAsync_NoConfig_UsesLatestVersion()
    {
        _nuget.DefaultVersion = "1.6.0";

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.X"], _taskContext);

        Assert.AreEqual("1.6.0", result["Pkg.X"]);
        CollectionAssert.Contains(_nuget.InstalledPackages, ("Pkg.X", "1.6.0"));
    }

    [TestMethod]
    public async Task InstallPackagesAsync_PinnedConfigVersion_UsedInsteadOfLatest()
    {
        _config.ExistsResult = true;
        _config.Config.SetVersion("Pkg.X", "1.2.3");

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.X"], _taskContext);

        Assert.AreEqual("1.2.3", result["Pkg.X"]);
        CollectionAssert.DoesNotContain(_nuget.QueriedPackages, "Pkg.X");
    }

    [TestMethod]
    public async Task InstallPackagesAsync_ConfigWithoutPinForPackage_FallsBackToLatest()
    {
        _config.ExistsResult = true;
        _config.Config.SetVersion("Some.Other.Package", "9.9.9");
        _nuget.DefaultVersion = "1.6.0";

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.X"], _taskContext);

        Assert.AreEqual("1.6.0", result["Pkg.X"]);
        CollectionAssert.Contains(_nuget.QueriedPackages, "Pkg.X");
    }

    [TestMethod]
    public async Task InstallPackagesAsync_IgnoreConfig_UsesLatestEvenWhenPinned()
    {
        _config.ExistsResult = true;
        _config.Config.SetVersion("Pkg.X", "1.2.3");
        _nuget.DefaultVersion = "1.6.0";

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.X"], _taskContext, ignoreConfig: true);

        Assert.AreEqual("1.6.0", result["Pkg.X"]);
        CollectionAssert.Contains(_nuget.QueriedPackages, "Pkg.X");
    }

    #endregion

    #region InstallPackagesAsync — version merge

    [TestMethod]
    public async Task InstallPackagesAsync_MergesInstalledVersions_HigherWins()
    {
        // Two fresh packages whose installs both surface a shared transitive package at different versions.
        _nuget.InstallReturns["Pkg.A"] = new() { ["Pkg.A"] = "1.6.0", ["Shared"] = "1.0.0" };
        _nuget.InstallReturns["Pkg.B"] = new() { ["Pkg.B"] = "1.6.0", ["Shared"] = "2.0.0" };

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.A", "Pkg.B"], _taskContext);

        Assert.AreEqual("2.0.0", result["Shared"], "Higher shared version should win the merge.");
        Assert.AreEqual("1.6.0", result["Pkg.A"]);
        Assert.AreEqual("1.6.0", result["Pkg.B"]);
    }

    [TestMethod]
    public async Task InstallPackagesAsync_MergesInstalledVersions_LowerDoesNotDowngrade()
    {
        // The shared package is surfaced at the HIGHER version by the first install and a LOWER version by
        // the second. The merge must keep the higher one — exercises the compare-not-greater branch (the
        // second occurrence is not greater than the tracked one, so the existing value is retained).
        _nuget.InstallReturns["Pkg.A"] = new() { ["Pkg.A"] = "1.6.0", ["Shared"] = "2.0.0" };
        _nuget.InstallReturns["Pkg.B"] = new() { ["Pkg.B"] = "1.6.0", ["Shared"] = "1.0.0" };

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.A", "Pkg.B"], _taskContext);

        Assert.AreEqual("2.0.0", result["Shared"], "A lower version surfaced by a later install must not downgrade the merged result.");
        Assert.AreEqual("1.6.0", result["Pkg.A"]);
        Assert.AreEqual("1.6.0", result["Pkg.B"]);
    }

    #endregion

    private sealed class FakeConfigService : IConfigService
    {
        public FileInfo ConfigPath { get; set; } = new(Path.Join(Path.GetTempPath(), "winapp.yaml"));
        public bool ExistsResult { get; set; }
        public WinappConfig Config { get; set; } = new();

        public bool Exists() => ExistsResult;
        public WinappConfig Load() => Config;
        public void Save(WinappConfig cfg) => Config = cfg;
    }
}
