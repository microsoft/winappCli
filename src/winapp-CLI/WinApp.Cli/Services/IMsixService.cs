// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal interface IMsixService
{
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
        FileInfo? projectFile = null,
        string? framework = null,
        bool noRestore = false,
        PackageGraphSource? packageGraph = null,
        string? targetArch = null,
        bool runtimeAlreadyBundled = false,
        CancellationToken cancellationToken = default);

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
        CancellationToken cancellationToken = default);

    public Task<MsixIdentityResult> AddSparseIdentityAsync(
        string? entryPointPath,
        FileInfo appxManifestPath,
        bool noInstall,
        bool keepIdentity,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds an identity-only sparse MSIX package from a sparse appxmanifest.xml
    /// (one that declares uap10:AllowExternalContent). Only the manifest is packaged —
    /// application binaries and visual assets are resolved from the external content
    /// location at registration time. Optionally signs the resulting package.
    /// </summary>
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Embeds the <c>&lt;msix&gt;</c> identity element (read from a sparse appxmanifest.xml)
    /// into a target. When the target is an .exe, the element is embedded into the exe's
    /// side-by-side (fusion) manifest via mt.exe. When the target is an .xml/.manifest file,
    /// the element is inserted or replaced in that external SxS manifest.
    /// </summary>
    public Task<MsixIdentityResult> EmbedIdentityAsync(
        FileInfo target,
        FileInfo manifestPath,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);

    /// <param name="selfContained">
    /// When <c>true</c>, the app carries its own Windows App SDK copy: no framework
    /// <c>&lt;PackageDependency&gt;</c> is added to the manifest and no runtime is provisioned. Callers
    /// pass the evaluated <c>WindowsAppSDKSelfContained</c>; the default preserves the previous behavior
    /// for callers that cannot determine it.
    /// </param>
    /// <param name="projectAssetsFile">
    /// The <c>project.assets.json</c> the build consumed. Package discovery reads the graph from it, so a
    /// configuration- or RID-conditional <c>PackageReference</c> is seen exactly as the build resolved it;
    /// when null (or the file is gone) discovery falls back to re-evaluating the project.
    /// </param>
    public Task<MsixIdentityResult> AddLooseLayoutIdentityAsync(
        FileInfo appxManifestPath,
        DirectoryInfo inputDirectory,
        DirectoryInfo outputAppXDirectory,
        TaskContext taskContext,
        bool clean = false,
        string? executable = null,
        string? runtimeArch = null,
        FileInfo? projectFile = null,
        string? framework = null,
        bool noRestore = false,
        bool selfContained = false,
        bool ensureExecutionAlias = false,
        PackageGraphSource? packageGraph = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the Windows App Runtime framework packages (Framework / DDLM / Singleton / Main) are
    /// installed for a project-mode <b>unpackaged</b> app before it is launched. The DDLM this lays down
    /// is exactly what an unpackaged WinUI app's bootstrapper resolves at startup. Reuses the same install
    /// path as the packaged flow; callers must gate on <c>WindowsAppSDKSelfContained</c> (skip when true).
    /// Returns <c>true</c> when a runtime was actually provisioned, or <c>false</c> when the project has no
    /// Windows App SDK reference and the install was skipped (so callers don't report a runtime as "ready"
    /// for a plain desktop/console app).
    /// </summary>
    /// <param name="projectFile">The project whose package list drives runtime version resolution; <c>null</c> falls back to a cwd glob.</param>
    /// <param name="architecture">The app's resolved architecture (<c>x64</c> / <c>arm64</c> / <c>x86</c>), so the correct-arch Framework/DDLM is installed.</param>
    /// <param name="framework">
    /// The effective target framework moniker the app was built for (e.g. <c>net10.0-windows10.0.26100.0</c>),
    /// or <c>null</c>. For a multi-targeted project this pins runtime resolution to the built TFM's Windows App
    /// SDK version, so a sibling TFM referencing a different SDK version can't gate the wrong runtime.
    /// </param>
    /// <param name="taskContext">Status/debug sink.</param>
    /// <param name="noRestore">When true, runtime discovery passes <c>--no-restore</c> to <c>dotnet list package</c> so a no-restore run doesn't trigger an implicit restore.</param>
    /// <param name="projectAssetsFile">The <c>project.assets.json</c> the build consumed, when the caller knows it. Package discovery reads the graph from it instead of re-evaluating the project, so a configuration- or RID-conditional Windows App SDK reference is seen.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<bool> EnsureWindowsAppRuntimeInstalledAsync(
        FileInfo? projectFile,
        string? architecture,
        string? framework,
        bool noRestore,
        TaskContext taskContext,
        PackageGraphSource? packageGraph = null,
        CancellationToken cancellationToken = default);
}
