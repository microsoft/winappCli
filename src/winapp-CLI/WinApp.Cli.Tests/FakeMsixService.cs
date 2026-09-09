// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake MSIX service that returns predictable identity results without performing real operations.
/// </summary>
internal class FakeMsixService : IMsixService
{
    public MsixIdentityResult FakeIdentityResult { get; set; } = new("TestPackage", "CN=TestPublisher", "TestApp");
    public List<(string ManifestPath, bool Clean)> AddLooseLayoutCalls { get; } = [];
    public List<(string? RuntimeArch, string? ProjectFile, string? Framework, bool NoRestore)> AddLooseLayoutRuntimeCalls { get; } = [];
    public List<(string? ProjectFile, string? Architecture, string? Framework, bool NoRestore)> EnsureRuntimeInstalledCalls { get; } = [];
    public List<(string? EntryPoint, string? ManifestPath, bool NoInstall, bool KeepIdentity)> AddSparseIdentityCalls { get; } = [];
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>When set, <see cref="AddSparseIdentityAsync"/> throws this exception.</summary>
    public Exception? SparseExceptionToThrow { get; set; }

    /// <summary>
    /// When set, <see cref="EnsureWindowsAppRuntimeInstalledAsync"/> throws this to exercise the
    /// unpackaged run's runtime-prep failure path (abort with a non-zero exit, no launch). Kept
    /// separate from <see cref="ExceptionToThrow"/> so identity vs runtime-prep failures are isolated.
    /// </summary>
    public Exception? EnsureRuntimeInstalledException { get; set; }

    /// <summary>Records the input folder passed to each <see cref="CreateMsixPackageAsync"/> call.</summary>
    public List<DirectoryInfo> CreatePackageCalls { get; } = [];

    /// <summary>Controls the <c>Signed</c> flag returned by <see cref="CreateMsixPackageAsync"/>.</summary>
    public bool PackageSigned { get; set; }

    /// <summary>When set, <see cref="CreateMsixPackageAsync"/> throws this exception.</summary>
    public Exception? PackageExceptionToThrow { get; set; }

    /// <summary>Records the input folders passed to each <see cref="CreateMsixBundleAsync"/> call.</summary>
    public List<DirectoryInfo[]> CreateBundleCalls { get; } = [];

    /// <summary>Controls the <c>Signed</c> flag returned by <see cref="CreateMsixBundleAsync"/>.</summary>
    public bool BundleSigned { get; set; }

    /// <summary>When set, <see cref="CreateMsixBundleAsync"/> throws this exception.</summary>
    public Exception? BundleExceptionToThrow { get; set; }

    public Task<MsixIdentityResult> AddLooseLayoutIdentityAsync(
        FileInfo appxManifestPath,
        DirectoryInfo inputDirectory,
        DirectoryInfo outputAppXDirectory,
        TaskContext taskContext,
        LayoutReconciliation reconciliation,
        bool clean = false,
        string? executable = null,
        string? runtimeArch = null,
        FileInfo? projectFile = null,
        string? framework = null,
        bool noRestore = false,
        CancellationToken cancellationToken = default)
    {
        AddLooseLayoutCalls.Add((appxManifestPath.FullName, clean));
        LayoutReconciliations.Add(reconciliation);
        AddLooseLayoutRuntimeCalls.Add((runtimeArch, projectFile?.FullName, framework, noRestore));
        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }
        return Task.FromResult(FakeIdentityResult);
    }

    /// <summary>
    /// The ownership each loose-layout call was made with, in order. This is how a caller says
    /// whether the directory is winapp's to prune, so it is worth asserting on.
    /// </summary>
    public List<LayoutReconciliation> LayoutReconciliations { get; } = [];

    public bool EnsureRuntimeInstalledResult { get; set; } = true;

    /// <summary>Records each <see cref="MaterializeLooseLayoutAsync"/> call's manifest and output folder.</summary>
    public List<(string Manifest, string OutputDirectory)> MaterializeLooseLayoutCalls { get; } = [];

    /// <inheritdoc/>
    public Task<MsixIdentityResult> MaterializeLooseLayoutAsync(
        FileInfo appxManifestPath,
        DirectoryInfo inputDirectory,
        DirectoryInfo outputAppXDirectory,
        TaskContext taskContext,
        LayoutReconciliation reconciliation,
        string? executable = null,
        FileInfo? projectFile = null,
        string? framework = null,
        bool noRestore = false,
        CancellationToken cancellationToken = default)
    {
        MaterializeLooseLayoutCalls.Add((appxManifestPath.FullName, outputAppXDirectory.FullName));
        LayoutReconciliations.Add(reconciliation);
        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }
        return Task.FromResult(FakeIdentityResult);
    }

    public Task<bool> EnsureWindowsAppRuntimeInstalledAsync(
        FileInfo? projectFile,
        string? architecture,
        string? framework,
        bool noRestore,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        EnsureRuntimeInstalledCalls.Add((projectFile?.FullName, architecture, framework, noRestore));
        if (EnsureRuntimeInstalledException != null)
        {
            throw EnsureRuntimeInstalledException;
        }
        return Task.FromResult(EnsureRuntimeInstalledResult);
    }

    public Task<MsixIdentityResult> AddSparseIdentityAsync(
        string? entryPointPath,
        FileInfo appxManifestPath,
        bool noInstall,
        bool keepIdentity,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        AddSparseIdentityCalls.Add((entryPointPath, appxManifestPath?.FullName, noInstall, keepIdentity));
        if (SparseExceptionToThrow != null)
        {
            throw SparseExceptionToThrow;
        }
        return Task.FromResult(FakeIdentityResult);
    }

    public List<(string ManifestPath, bool AutoSign)> CreateSparseIdentityCalls { get; } = [];
    public List<(string Target, string ManifestPath)> EmbedIdentityCalls { get; } = [];

    public Task<CreateMsixPackageResult> CreateSparseIdentityPackageAsync(
        FileInfo manifestPath,
        FileSystemInfo? outputPath,
        TaskContext taskContext,
        bool autoSign = false,
        FileInfo? certificatePath = null,
        string certificatePassword = "password",
        bool generateDevCert = false,
        bool installDevCert = false,
        string? publisher = null,
        CancellationToken cancellationToken = default)
    {
        CreateSparseIdentityCalls.Add((manifestPath.FullName, autoSign));
        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }
        return Task.FromResult(new CreateMsixPackageResult(new FileInfo("fake.identity.msix"), autoSign));
    }

    public Task<MsixIdentityResult> EmbedIdentityAsync(
        FileInfo target,
        FileInfo manifestPath,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        EmbedIdentityCalls.Add((target.FullName, manifestPath.FullName));
        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }
        return Task.FromResult(FakeIdentityResult);
    }

    public Task<CreateMsixPackageResult> CreateMsixPackageAsync(
        DirectoryInfo inputFolder,
        FileSystemInfo? outputPath,
        TaskContext taskContext,
        string? packageName = null,
        bool skipPri = false,
        bool autoSign = false,
        FileInfo? certificatePath = null,
        string certificatePassword = "password",
        bool generateDevCert = false,
        bool installDevCert = false,
        string? publisher = null,
        FileInfo? manifestPath = null,
        bool selfContained = false,
        string? executable = null,
        CancellationToken cancellationToken = default)
    {
        CreatePackageCalls.Add(inputFolder);
        if (PackageExceptionToThrow != null)
        {
            throw PackageExceptionToThrow;
        }
        return Task.FromResult(new CreateMsixPackageResult(new FileInfo("fake.msix"), PackageSigned));
    }

    public Task<CreateMsixBundleResult> CreateMsixBundleAsync(
        DirectoryInfo[] inputFolders,
        FileSystemInfo? outputPath,
        TaskContext taskContext,
        string? packageName = null,
        bool skipPri = false,
        bool autoSign = false,
        FileInfo? certificatePath = null,
        string certificatePassword = "password",
        bool generateDevCert = false,
        bool installDevCert = false,
        string? publisher = null,
        FileInfo? manifestPath = null,
        bool selfContained = false,
        string? executable = null,
        CancellationToken cancellationToken = default)
    {
        CreateBundleCalls.Add(inputFolders);
        if (BundleExceptionToThrow != null)
        {
            throw BundleExceptionToThrow;
        }
        return Task.FromResult(new CreateMsixBundleResult(new FileInfo("fake.msixbundle"), BundleSigned, []));
    }
}
