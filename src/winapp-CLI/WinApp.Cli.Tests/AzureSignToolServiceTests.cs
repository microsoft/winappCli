// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Tests;

[TestClass]
public class AzureSignToolServiceTests : BaseCommandTests
{
    public AzureSignToolServiceTests() : base(configPaths: false)
    {
    }

    private static readonly string PackageDirName = AzureSignToolService.ArtifactSigningClientPackage.ToLowerInvariant();
    private const string DlibDllName = "Azure.CodeSigning.Dlib.dll";

    private DirectoryInfo _nugetCacheDir = null!;
    private DirectoryInfo _globalWinappDir = null!;
    private FakeSignToolNugetService _nuget = null!;
    private FakeSignToolPackageInstallationService _installer = null!;
    private AzureSignToolService _service = null!;

    [TestInitialize]
    public void SetupService()
    {
        _nugetCacheDir = _tempDirectory.CreateSubdirectory("nuget-cache");
        _globalWinappDir = _tempDirectory.CreateSubdirectory("global-winapp");
        _nuget = new FakeSignToolNugetService(_nugetCacheDir);
        _installer = new FakeSignToolPackageInstallationService();

        _service = new AzureSignToolService(
            new FakeSignToolBuildToolsService(),
            _nuget,
            _installer,
            new FakeSignToolWinappDirectoryService(_globalWinappDir))
        {
            // Tests use placeholder DLL content; bypass the compiled-in SHA-256 pin here. Dedicated
            // tests below exercise the real verification behaviour (accept / reject / reinstall).
            DlibIntegrityVerifier = _ => true,
        };
    }

    private FileInfo CreateDlibInCache()
    {
        var binDir = Path.Join(
            _nugetCacheDir.FullName,
            PackageDirName,
            AzureSignToolService.ArtifactSigningClientVersion,
            "bin",
            "x64");
        Directory.CreateDirectory(binDir);
        var dlibPath = Path.Join(binDir, DlibDllName);
        File.WriteAllText(dlibPath, "fake dlib");
        return new FileInfo(dlibPath);
    }

    [TestMethod]
    public async Task EnsureTrustedSigningDlibAsync_WhenDlibAlreadyCached_ReturnsPathWithoutInstalling()
    {
        var expected = CreateDlibInCache();

        var result = await _service.EnsureTrustedSigningDlibAsync(TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual(expected.FullName, result.FullName);
        Assert.AreEqual(0, _installer.EnsureCallCount, "Should not install when the dlib is already cached");
    }

    [TestMethod]
    public async Task EnsureTrustedSigningDlibAsync_WhenMissingThenInstalled_InstallsAndReturnsPath()
    {
        // Simulate the package install materializing the dlib on disk.
        _installer.EnsureResult = true;
        _installer.OnEnsure = () => CreateDlibInCache();

        var result = await _service.EnsureTrustedSigningDlibAsync(TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual(1, _installer.EnsureCallCount, "Should attempt to install the signing client package");
        Assert.AreEqual(DlibDllName, result.Name);
        Assert.IsTrue(result.Exists);
        StringAssert.Contains(result.FullName, AzureSignToolService.ArtifactSigningClientVersion);
    }

    [TestMethod]
    public async Task EnsureTrustedSigningDlibAsync_WhenInstallSucceedsButDlibMissing_Throws()
    {
        // Install reports success but nothing is written to the cache.
        _installer.EnsureResult = true;

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _service.EnsureTrustedSigningDlibAsync(TestTaskContext, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "Could not find the Trusted Signing client library");
        Assert.AreEqual(1, _installer.EnsureCallCount);
    }

    [TestMethod]
    public async Task EnsureTrustedSigningDlibAsync_WhenCachedDlibFailsIntegrity_RemovesAndReinstalls()
    {
        // A cached dlib whose content does not match a pinned hash must not be trusted: the version
        // directory is removed and the package reinstalled, and the reinstalled (now-verified) copy
        // is returned.
        CreateDlibInCache();
        _installer.EnsureResult = true;
        _installer.OnEnsure = () => CreateDlibInCache();

        var callCount = 0;
        _service.DlibIntegrityVerifier = _ => ++callCount > 1; // reject the cached copy, accept after reinstall

        var result = await _service.EnsureTrustedSigningDlibAsync(TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual(1, _installer.EnsureCallCount, "A cached dlib that fails verification should trigger a reinstall");
        Assert.AreEqual(DlibDllName, result.Name);
    }

    [TestMethod]
    public async Task EnsureTrustedSigningDlibAsync_WhenDownloadedDlibFailsIntegrity_Throws()
    {
        // If even the freshly installed dlib fails verification, refuse to use it (fail closed).
        _installer.EnsureResult = true;
        _installer.OnEnsure = () => CreateDlibInCache();
        _service.DlibIntegrityVerifier = _ => false;

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _service.EnsureTrustedSigningDlibAsync(TestTaskContext, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "failed integrity verification");
    }

    [TestMethod]
    public async Task EnsureTrustedSigningDlibAsync_DefaultVerifier_RejectsUnpinnedContent()
    {
        // Exercise the real compiled-in SHA-256 pin (no verifier override): placeholder DLL content
        // must be rejected, proving the production path does not blindly trust cached/downloaded bits.
        var service = new AzureSignToolService(
            new FakeSignToolBuildToolsService(), _nuget, _installer,
            new FakeSignToolWinappDirectoryService(_globalWinappDir));
        CreateDlibInCache();               // "fake dlib" content — not a pinned hash
        _installer.EnsureResult = true;
        _installer.OnEnsure = () => CreateDlibInCache();

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.EnsureTrustedSigningDlibAsync(TestTaskContext, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "failed integrity verification");
    }

    [TestMethod]
    public async Task EnsureTrustedSigningDlibAsync_WhenInstallFails_Throws()
    {
        _installer.EnsureResult = false;

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _service.EnsureTrustedSigningDlibAsync(TestTaskContext, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "Could not find the Trusted Signing client library");
    }

    [TestMethod]
    public async Task EnsureTrustedSigningDlibAsync_WhenCancelledDuringInstall_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();

        // EnsurePackageAsync swallows OperationCanceledException and returns false, so simulate a
        // download cancelled mid-flight: the token is cancelled while "installing" and the install
        // reports failure. The service must surface cancellation rather than the generic install
        // failure and must not continue into the post-install dlib lookup.
        _installer.EnsureResult = false;
        _installer.OnEnsure = () => cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => _service.EnsureTrustedSigningDlibAsync(TestTaskContext, cts.Token));
    }

    [TestMethod]
    public async Task SignAsync_ForwardsTenantAndSelectsArchMatchedSigntool()
    {
        var dlib = CreateDlibInCache(); // bin/x64/Azure.CodeSigning.Dlib.dll
        var signtool = CreateFakeSigntool("x64");
        var recording = new RecordingBuildToolsService { SignToolToReturn = signtool };
        var service = new AzureSignToolService(
            recording, _nuget, _installer, new FakeSignToolWinappDirectoryService(_globalWinappDir))
        {
            DlibIntegrityVerifier = _ => true,
            TrustedAzureCliBinDirsProvider = () => Array.Empty<string>(),
        };

        var fileToSign = new FileInfo(Path.Join(_tempDirectory.FullName, "app.msix"));
        await File.WriteAllTextAsync(fileToSign.FullName, "MZ", TestContext.CancellationToken);
        var metadata = new FileInfo(Path.Join(_tempDirectory.FullName, "metadata.json"));
        await File.WriteAllTextAsync(metadata.FullName, "{}", TestContext.CancellationToken);

        await service.SignAsync(fileToSign, metadata, "my-tenant-id", TestTaskContext, TestContext.CancellationToken);

        // The x64 dlib forces the architecture-matched x64 signtool to be used as the override.
        Assert.AreEqual(signtool.FullName, recording.CapturedToolPathOverride!.FullName);

        // signtool arguments reference the dlib, the metadata file, and the target file.
        StringAssert.Contains(recording.CapturedArguments!, dlib.FullName);
        StringAssert.Contains(recording.CapturedArguments!, metadata.FullName);
        StringAssert.Contains(recording.CapturedArguments!, fileToSign.FullName);

        // The tenant id is forwarded to the dlib via AZURE_TENANT_ID.
        Assert.IsNotNull(recording.CapturedEnvironment);
        Assert.AreEqual("my-tenant-id", recording.CapturedEnvironment!["AZURE_TENANT_ID"]);

        // signtool runs from a trusted system directory rather than inheriting the caller's working
        // directory, so the dlib's own 'az' lookup can't pick up a repo-local az.cmd next to the target.
        Assert.AreEqual(Environment.SystemDirectory, recording.CapturedWorkingDirectory,
            "SignAsync must anchor signtool's working directory to System32");
    }

    [TestMethod]
    public async Task SignAsync_WithoutTenant_DoesNotInjectEnvironment()
    {
        CreateDlibInCache();
        var signtool = CreateFakeSigntool("x64");
        var recording = new RecordingBuildToolsService { SignToolToReturn = signtool };
        var service = new AzureSignToolService(
            recording, _nuget, _installer, new FakeSignToolWinappDirectoryService(_globalWinappDir))
        {
            DlibIntegrityVerifier = _ => true,
            TrustedAzureCliBinDirsProvider = () => Array.Empty<string>(),
        };

        var fileToSign = new FileInfo(Path.Join(_tempDirectory.FullName, "app.msix"));
        await File.WriteAllTextAsync(fileToSign.FullName, "MZ", TestContext.CancellationToken);
        var metadata = new FileInfo(Path.Join(_tempDirectory.FullName, "metadata.json"));
        await File.WriteAllTextAsync(metadata.FullName, "{}", TestContext.CancellationToken);

        await service.SignAsync(fileToSign, metadata, tenantId: null, TestTaskContext, TestContext.CancellationToken);

        Assert.IsNull(recording.CapturedEnvironment, "No AZURE_TENANT_ID should be injected when no tenant is known");
    }

    [TestMethod]
    public async Task SignAsync_PrependsTrustedAzureCliDirsToPath()
    {
        CreateDlibInCache();
        var signtool = CreateFakeSigntool("x64");
        var recording = new RecordingBuildToolsService { SignToolToReturn = signtool };
        var trustedDir = Path.Join(_tempDirectory.FullName, "trusted-az", "wbin");
        var service = new AzureSignToolService(
            recording, _nuget, _installer, new FakeSignToolWinappDirectoryService(_globalWinappDir))
        {
            DlibIntegrityVerifier = _ => true,
            TrustedAzureCliBinDirsProvider = () => new[] { trustedDir },
        };

        var fileToSign = new FileInfo(Path.Join(_tempDirectory.FullName, "app.msix"));
        await File.WriteAllTextAsync(fileToSign.FullName, "MZ", TestContext.CancellationToken);
        var metadata = new FileInfo(Path.Join(_tempDirectory.FullName, "metadata.json"));
        await File.WriteAllTextAsync(metadata.FullName, "{}", TestContext.CancellationToken);

        await service.SignAsync(fileToSign, metadata, "tenant", TestTaskContext, TestContext.CancellationToken);

        Assert.IsNotNull(recording.CapturedEnvironment);
        var path = recording.CapturedEnvironment!["PATH"];

        // The trusted Azure CLI directory is prepended so the dlib's AzureCliCredential resolves the
        // legitimate az.cmd before any 'az.cmd' injected elsewhere on PATH.
        StringAssert.StartsWith(path, trustedDir + Path.PathSeparator,
            "The trusted Azure CLI directory must be prepended to PATH");

        // Prepending is additive: the caller's existing PATH entries are preserved after the prefix.
        var currentPath = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(currentPath))
        {
            StringAssert.Contains(path, currentPath);
        }
    }

    [TestMethod]
    public async Task SignAsync_WhenResolvedSigntoolArchMismatchesDlib_SwapsToMatchingArchSibling()
    {
        // The dlib only ships x64/x86, so on an ARM64 host the resolved arm64 signtool must be
        // swapped for the sibling x64 signtool that matches the x64 dlib.
        var dlib = CreateDlibInCache(); // bin/x64/Azure.CodeSigning.Dlib.dll
        var arm64Signtool = CreateFakeSigntool("arm64"); // what tool resolution returns
        var x64Signtool = CreateFakeSigntool("x64");      // sibling under the same bin parent
        var recording = new RecordingBuildToolsService { SignToolToReturn = arm64Signtool };
        var service = new AzureSignToolService(
            recording, _nuget, _installer, new FakeSignToolWinappDirectoryService(_globalWinappDir))
        {
            DlibIntegrityVerifier = _ => true,
        };

        var fileToSign = new FileInfo(Path.Join(_tempDirectory.FullName, "app.msix"));
        await File.WriteAllTextAsync(fileToSign.FullName, "MZ", TestContext.CancellationToken);
        var metadata = new FileInfo(Path.Join(_tempDirectory.FullName, "metadata.json"));
        await File.WriteAllTextAsync(metadata.FullName, "{}", TestContext.CancellationToken);

        await service.SignAsync(fileToSign, metadata, "tenant", TestTaskContext, TestContext.CancellationToken);

        Assert.IsNotNull(dlib);
        Assert.AreEqual(x64Signtool.FullName, recording.CapturedToolPathOverride!.FullName,
            "The x64 dlib should force a swap from the resolved arm64 signtool to the sibling x64 signtool");
    }

    private FileInfo CreateFakeSigntool(string architecture)
    {
        var dir = Path.Join(_tempDirectory.FullName, "sdk", "bin", architecture);
        Directory.CreateDirectory(dir);
        var signtool = new FileInfo(Path.Join(dir, "signtool.exe"));
        File.WriteAllText(signtool.FullName, "fake signtool");
        return signtool;
    }
}

internal sealed class FakeSignToolNugetService(DirectoryInfo cacheDir) : INugetService
{
    public DirectoryInfo GetNuGetGlobalPackagesDir() => cacheDir;

    public Task<string> GetLatestVersionAsync(string packageName, SdkInstallMode sdkInstallMode, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<Dictionary<string, string>> InstallPackageAsync(string package, string version, TaskContext taskContext, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public DirectoryInfo GetNuGetPackageDir(string packageName, string version)
        => throw new NotImplementedException();

    public bool IsPackageInstalled(string packageName, string version)
        => throw new NotImplementedException();
}

internal sealed class FakeSignToolWinappDirectoryService(DirectoryInfo globalDir) : IWinappDirectoryService
{
    public DirectoryInfo GetGlobalWinappDirectory() => globalDir;

    public DirectoryInfo GetLocalWinappDirectory(DirectoryInfo? baseDirectory = null)
        => throw new NotImplementedException();

    public void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory)
    {
        // no-op
    }
}

internal sealed class FakeSignToolPackageInstallationService : IPackageInstallationService
{
    public bool EnsureResult { get; set; } = true;
    public int EnsureCallCount { get; private set; }
    public Action? OnEnsure { get; set; }

    public void InitializeWorkspace(DirectoryInfo rootDirectory)
        => throw new NotImplementedException();

    public Task<Dictionary<string, string>> InstallPackagesAsync(
        DirectoryInfo rootDirectory,
        IEnumerable<string> packages,
        TaskContext taskContext,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        bool ignoreConfig = false,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<bool> EnsurePackageAsync(
        DirectoryInfo rootDirectory,
        string packageName,
        TaskContext taskContext,
        string? version = null,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        CancellationToken cancellationToken = default)
    {
        EnsureCallCount++;
        OnEnsure?.Invoke();
        return Task.FromResult(EnsureResult);
    }
}

internal sealed class FakeSignToolBuildToolsService : IBuildToolsService
{
    public FileInfo? GetBuildToolPath(string toolName) => throw new NotImplementedException();

    public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, FileInfo? toolPathOverride = null, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}

internal sealed class RecordingBuildToolsService : IBuildToolsService
{
    public FileInfo SignToolToReturn { get; set; } = null!;

    public Tool? CapturedTool { get; private set; }
    public string? CapturedArguments { get; private set; }
    public FileInfo? CapturedToolPathOverride { get; private set; }
    public IReadOnlyDictionary<string, string>? CapturedEnvironment { get; private set; }
    public string? CapturedWorkingDirectory { get; private set; }

    public FileInfo? GetBuildToolPath(string toolName) => throw new NotImplementedException();

    public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default)
        => Task.FromResult(SignToolToReturn);

    public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, FileInfo? toolPathOverride = null, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        CapturedTool = tool;
        CapturedArguments = arguments;
        CapturedToolPathOverride = toolPathOverride;
        CapturedEnvironment = environment;
        CapturedWorkingDirectory = workingDirectory;
        return Task.FromResult((string.Empty, string.Empty));
    }
}
