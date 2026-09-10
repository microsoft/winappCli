// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Models;

/// <summary>
/// Whether <c>winapp run</c> operates on a pre-built output folder (folder mode, unchanged),
/// builds a <c>.csproj</c> from source (project mode), or builds a .NET file-based app — a single
/// <c>.cs</c> file configured by <c>#:</c> directives (single-file mode).
/// </summary>
internal enum WinAppRunMode
{
    /// <summary>Input is a build-output folder — the existing, unchanged behavior.</summary>
    Folder,

    /// <summary>Input is a <c>.csproj</c> (or a directory containing exactly one buildable one).</summary>
    Project,

    /// <summary>
    /// Input is an explicitly-specified <c>.cs</c> file-based app. Single-file mode NEVER results from
    /// directory inference — only from a <c>.cs</c> path the user typed — so folder mode is untouched.
    /// </summary>
    SingleFile,
}

/// <summary>
/// The effective packaging of a project-mode target, derived from the evaluated
/// <c>WindowsPackageType</c> MSBuild property (never from manifest presence).
/// </summary>
internal enum ProjectPackaging
{
    /// <summary>MSIX identity (loose-layout register + AUMID launch).</summary>
    Packaged,

    /// <summary>No identity (<c>WindowsPackageType=None</c>) — launch the apphost <c>.exe</c> directly.</summary>
    Unpackaged,
}

/// <summary>
/// The outcome of resolving a <c>.csproj</c> input to a concrete run mode.
/// Either <see cref="Mode"/> is <see cref="WinAppRunMode.Folder"/> (fall back to the existing
/// folder-mode path) or it is <see cref="WinAppRunMode.Project"/> and <see cref="Csproj"/> is set.
/// </summary>
/// <param name="Mode">The resolved run mode.</param>
/// <param name="Csproj">The resolved project file, when <see cref="Mode"/> is Project.</param>
/// <param name="ProjectDirectory">The directory containing the project (project mode) or the input folder.</param>
/// <param name="Solution">The solution the project was resolved from (defines <c>$(SolutionDir)</c> and siblings for the build/evaluate passes); null for a bare <c>.csproj</c>/directory input.</param>
/// <param name="SelectionReason">Why this project was chosen for an ambiguous input (shown in the context line); null if unambiguous.</param>
/// <param name="SingleFile">The resolved <c>.cs</c> file-based app, when <see cref="Mode"/> is <see cref="WinAppRunMode.SingleFile"/>.</param>
internal sealed record RunInputResolution(
    WinAppRunMode Mode,
    FileInfo? Csproj,
    DirectoryInfo ProjectDirectory,
    FileInfo? Solution = null,
    string? SelectionReason = null,
    FileInfo? SingleFile = null);

/// <summary>
/// The package graph a build resolved: the <c>project.assets.json</c> restore wrote, and the RID that
/// build used so the right target inside it is read.
/// </summary>
/// <remarks>
/// These travel together because neither is usable alone. Restore accumulates one target per RID it has
/// ever resolved, so reading the file without knowing which RID the build used would report another
/// architecture's packages — including its Windows App SDK version, which drives runtime provisioning.
/// </remarks>
/// <param name="AssetsFile">The <c>project.assets.json</c> the build consumed.</param>
/// <param name="RuntimeIdentifier">The evaluated <c>RuntimeIdentifier</c>, exactly as the build saw it — not reconstructed from the architecture, so a custom RID such as <c>win10-x64</c> still selects its own target. Null when the build resolved no RID.</param>
internal sealed record PackageGraphSource(FileInfo AssetsFile, string? RuntimeIdentifier);

/// <summary>
/// The build-and-resolve result for a project-mode target: the evaluated output
/// paths plus the packaging determination used to pick the launch strategy.
/// </summary>
/// <param name="Csproj">The project that was built/evaluated.</param>
/// <param name="TargetDir">Absolute output directory (contains the manifest/recipe for packaged apps).</param>
/// <param name="RunCommand">The launcher for the unpackaged direct launch: an apphost <c>.exe</c> when <c>UseAppHost</c> is on, or a bare command (e.g. <c>dotnet</c>, paired with <see cref="RunArguments"/>) when it's off; null if not produced.</param>
/// <param name="Packaging">Packaged vs unpackaged.</param>
/// <param name="SelfContained">True when <c>WindowsAppSDKSelfContained=true</c> — the runtime install is skipped.</param>
/// <param name="Architecture">The resolved app architecture (x64 / arm64 / x86) used for build + runtime install.</param>
/// <param name="Framework">The effective target framework the app was built for (mirrors <c>ProjectRunOptions.Framework</c>); null for a single-targeted project. Threaded into the unpackaged runtime install so the version resolves from the built TFM.</param>
/// <param name="NoRestore">Mirrors <c>ProjectRunOptions.NoRestore</c>; threaded into the unpackaged <c>dotnet list package</c> discovery so it can't trigger a restore the user skipped.</param>
/// <param name="RunArguments">Leading launch arguments MSBuild pairs with a non-apphost <see cref="RunCommand"/> (e.g. <c>exec "&lt;app&gt;.dll"</c>); prepended before the user's app args. Null for a plain apphost launch.</param>
/// <param name="ProjectAssetsFile">The evaluated <c>ProjectAssetsFile</c> — restore's output for the inputs this build ran with. Package discovery reads the graph from it because <c>dotnet package list</c> re-evaluates the project and accepts no <c>-c</c>/<c>-r</c>/<c>-p</c>, and MSBuild ranks environment properties below a value the project assigns, so those inputs cannot be reproduced any other way.</param>
internal sealed record ProjectRunResolution(
    FileInfo Csproj,
    string TargetDir,
    string? RunCommand,
    ProjectPackaging Packaging,
    bool SelfContained,
    string Architecture,
    string? Framework = null,
    bool NoRestore = false,
    string? RunArguments = null,
    string? OutputType = null,
    bool? PreferExecutionAlias = null,
    string? ProjectAssetsFile = null,
    string? ProjectAssetsRuntimeIdentifier = null,
    bool IsAot = false,
    string? AppxManifestPath = null,
    string? AppxRecipePath = null);

/// <summary>
/// User-provided build inputs for project mode, forwarded to <c>dotnet build</c> / <c>dotnet msbuild</c>.
/// </summary>
/// <param name="Configuration">Build configuration (default <c>Debug</c>).</param>
/// <param name="Architecture">Canonical target arch (<c>x64</c> / <c>arm64</c> / <c>x86</c>).</param>
/// <param name="Framework">Optional target framework moniker for multi-targeted projects.</param>
/// <param name="NoBuild">Skip the build and evaluate the existing output only.</param>
/// <param name="NoRestore">Pass <c>--no-restore</c> to the build.</param>
/// <param name="Properties">Raw repeatable <c>-p Name=Value</c> passthrough, forwarded to both build and evaluation.</param>
/// <param name="Json">When true, suppress human-readable stdout (banner) and route build diagnostics to stderr so stdout stays pure JSON.</param>
/// <param name="Solution">The solution the target was resolved from; when set, the build/evaluate passes define <c>$(SolutionDir)</c> and sibling <c>Solution*</c> properties so referencing projects build as they do in VS. Null for a bare <c>.csproj</c>.</param>
/// <param name="Platform">The MSBuild <c>Platform</c> winapp injects (<c>-p:Platform=…</c>) into every pass when the target — and its whole <c>ProjectReference</c> closure — declares a <c>&lt;Platforms&gt;</c> that includes the target arch. A RESOLVED input (see <c>ResolvePlatformInjection</c>), never user-supplied; null means arch is conveyed by the RID alone (the safe default). Older WindowsAppSDK targets hard-reject the default <c>Platform=AnyCPU</c> for self-contained / packaged builds, so the explicit Platform is what makes those projects build.</param>
/// <param name="OmitRuntimeIdentifier">Suppresses the injected <c>-r win-&lt;arch&gt;</c> because an effective <c>Platform</c> already conveys the architecture AND the <c>ProjectReference</c> closure splits on <c>RuntimeIdentifier</c> — a combination that otherwise builds the same project twice and fails a packaged build with APPX1101. A RESOLVED input (see <c>ResolvePlatformInjection</c>).</param>
/// <param name="PublishProfile">The architecture-matching publish profile used when MSIX tooling requires a trimmed build to be self-contained, the profile preserves the project's trimming, target framework, and architecture, and forcing a global <c>Platform</c> would break an AnyCPU project reference. Inferred profiles are scoped to the selected app through the .NET SDK's <c>ProjectToOverrideProjectExtensionsPath</c> property. A RESOLVED input (see <c>ResolveRequiredPublishProfileAsync</c>), never user-supplied.</param>
internal sealed record ProjectRunOptions(
    string Configuration,
    string Architecture,
    string? Framework,
    bool NoBuild,
    bool NoRestore,
    IReadOnlyList<string> Properties,
    bool Json = false,
    FileInfo? Solution = null,
    string? Platform = null,
    bool OmitRuntimeIdentifier = false,
    string? PublishProfile = null);

/// <summary>
/// The effective build inputs used to classify runnable candidates (multi-<c>.csproj</c> directory or
/// solution) under the SAME MSBuild globals the build/evaluate passes use, so a project whose
/// <c>OutputType</c>/test markers are conditional is classified the way it will actually build. Null
/// classifies against MSBuild defaults (pre-existing behavior).
/// </summary>
/// <param name="Configuration">The effective <c>-c</c> Configuration (e.g. <c>Debug</c>/<c>Release</c>).</param>
/// <param name="Architecture">The resolved target architecture, mapped to a RID for the evaluate.</param>
/// <param name="Framework">The explicit <c>--framework</c> TargetFramework, or null when unset.</param>
/// <param name="Properties">The raw user <c>-p Name=Value</c> properties (dedicated-switch dupes filtered).</param>
internal sealed record ProjectClassificationInputs(
    string Configuration,
    string Architecture,
    string? Framework,
    IReadOnlyList<string> Properties);

/// <summary>
/// Outcome of <see cref="Services.IProjectRunService.BuildAndResolveAsync"/>. On success,
/// <see cref="Resolution"/> is set and <see cref="ExitCode"/> is 0. On a build failure, the dotnet
/// errors have already been surfaced and <see cref="ExitCode"/> is the non-zero dotnet exit code.
/// </summary>
internal sealed record ProjectBuildOutcome(ProjectRunResolution? Resolution, int ExitCode);

/// <summary>
/// User-provided build inputs for single-file mode (a <c>.cs</c> file-based app).
/// <para>
/// Deliberately a much smaller set than <see cref="ProjectRunOptions"/>. A file-based app declares its
/// own <c>TargetFramework</c> through <c>#:property</c> directives, so <c>--framework</c> and
/// <c>--project</c> are rejected by the run handler. <c>Platform</c> is never injected — a file-based
/// app accepts it but ignores it for RID selection — while a <c>RuntimeIdentifier</c> IS injected when
/// the app declares none, which is what lets a self-contained Windows App SDK app build instead of
/// failing as <c>AnyCPU</c>. <c>--arch</c>/<c>--runtime</c> are honored and override what the file
/// declares.
/// </para>
/// </summary>
/// <param name="Configuration">Build configuration (default <c>Debug</c>). File-based apps write to <c>bin\debug</c>/<c>bin\release</c>.</param>
/// <param name="Architecture">
/// The target architecture winapp conveys as <c>-r win-&lt;arch&gt;</c> when the app does not declare a
/// <c>RuntimeIdentifier</c> of its own. Resolved from <c>--arch</c>/<c>--runtime</c>, else the current
/// process architecture.
/// </param>
/// <param name="ArchitectureIsExplicit">
/// True when <see cref="Architecture"/> came from <c>--arch</c>/<c>--runtime</c> rather than the process
/// default. An explicit request overrides a <c>#:property RuntimeIdentifier</c> in the file (matching
/// project mode, where a dedicated switch beats an in-project value); the default defers to it.
/// </param>
/// <param name="NoBuild">Skip the build and evaluate the existing output only.</param>
/// <param name="NoRestore">Pass <c>--no-restore</c> to the build.</param>
/// <param name="Properties">Raw repeatable <c>-p Name=Value</c> passthrough, mirrored across the build and evaluate passes.</param>
/// <param name="Json">When true, suppress human-readable stdout and route build diagnostics to stderr so stdout stays pure JSON.</param>
internal sealed record SingleFileRunOptions(
    string Configuration,
    string Architecture,
    bool ArchitectureIsExplicit,
    bool NoBuild,
    bool NoRestore,
    IReadOnlyList<string> Properties,
    bool Json = false)
{
    /// <summary>
    /// The <c>RuntimeIdentifier</c> to inject into both passes, or <see langword="null"/> to inject none
    /// because the app declares its own. Resolved by the build service after a cheap pre-evaluate.
    /// </summary>
    public string? InjectedRuntimeIdentifier { get; init; }
}

/// <summary>
/// The build-and-resolve result for a <c>.cs</c> file-based app: the evaluated output folder, the
/// concrete executable to reference from the generated manifest, and the raw evaluated MSBuild
/// properties the manifest inference reads.
/// </summary>
/// <param name="SingleFile">The <c>.cs</c> file that was built/evaluated.</param>
/// <param name="OutputDirectory">Absolute build-output directory (under <c>%TEMP%\dotnet\runfile\…</c>). Stable across edits, so package identity and <c>LocalState</c> survive code changes.</param>
/// <param name="ExecutableName">The app executable's bare file name (e.g. <c>counter.exe</c>), written concretely into the generated manifest.</param>
/// <param name="Architecture">The architecture the app was built for (<c>x64</c>/<c>arm64</c>/<c>x86</c>), resolved from the app's <c>RuntimeIdentifier</c> alone — <c>Platform</c> is deliberately ignored, since a file-based app accepts it but does not use it for RID selection. Threaded into Windows App Runtime provisioning so a cross-architecture app gets matching runtime packages.</param>
/// <param name="TargetFramework">The TFM the app was built for, threaded into runtime provisioning so the Windows App SDK version resolves from the right framework; null when unresolved.</param>
/// <param name="SelfContained">True when <c>WindowsAppSDKSelfContained=true</c> — the app carries its own Windows App SDK, so no framework dependency is added and no runtime is provisioned.</param>
/// <param name="Packaging">Packaged (register a loose layout and launch via AUMID) vs unpackaged (launch the apphost directly), from the effective <c>WindowsPackageType</c>.</param>
/// <param name="RunCommand">The launcher for an unpackaged launch: an apphost <c>.exe</c>, or a bare command (e.g. <c>dotnet</c>) paired with <see cref="RunArguments"/>; null when not produced.</param>
/// <param name="RunArguments">Leading launch arguments MSBuild pairs with a non-apphost <see cref="RunCommand"/>; prepended before the user's app args.</param>
/// <param name="Properties">Every evaluated MSBuild property from the <c>--getProperty</c> pass, keyed case-insensitively.</param>
/// <param name="ProjectAssetsFile">The evaluated <c>ProjectAssetsFile</c> — restore's output for the inputs this build ran with. Package discovery reads the graph from it because <c>dotnet package list --file</c> re-evaluates the app and accepts no <c>-c</c>/<c>-r</c>/<c>-p</c>, and MSBuild ranks environment properties below a value the file assigns via <c>#:property</c>, so those inputs cannot be reproduced any other way.</param>
internal sealed record SingleFileRunResolution(
    FileInfo SingleFile,
    string OutputDirectory,
    string ExecutableName,
    string Architecture,
    string? TargetFramework,
    bool SelfContained,
    ProjectPackaging Packaging,
    string? RunCommand,
    string? RunArguments,
    IReadOnlyDictionary<string, string> Properties,
    string? ProjectAssetsFile = null,
    string? ProjectAssetsRuntimeIdentifier = null);

/// <summary>
/// Outcome of <see cref="Services.IProjectRunService.BuildAndResolveSingleFileAsync"/>. Mirrors
/// <see cref="ProjectBuildOutcome"/>: on success <see cref="Resolution"/> is set and
/// <see cref="ExitCode"/> is 0; on a build failure dotnet already surfaced its diagnostics.
/// </summary>
internal sealed record SingleFileBuildOutcome(SingleFileRunResolution? Resolution, int ExitCode);

/// <summary>
/// The package identity a <c>.cs</c> file-based app registers under, resolved by evaluation alone so
/// <c>winapp unregister app.cs</c> can name it without building the app.
/// </summary>
/// <param name="PackageName">The <c>Identity/@Name</c> the app registers under — read from an authored manifest when it has one, otherwise inferred exactly as <c>winapp run</c> infers it.</param>
/// <param name="Packaging">Packaged vs unpackaged, from the effective <c>WindowsPackageType</c>. An unpackaged app never registers, so there is nothing to remove.</param>
/// <param name="BuildRootDirectory">The SDK's per-file build root (<c>%TEMP%\dotnet\runfile\&lt;stem&gt;-&lt;hash&gt;</c>), used to confirm a registration belongs to this file; null when it could not be determined.</param>
internal sealed record SingleFileIdentityResolution(
    string PackageName,
    ProjectPackaging Packaging,
    string? BuildRootDirectory);

/// <summary>
/// Every <c>run</c> input that can change the package identity a <c>.cs</c> file-based app registers
/// under, bundled so <c>unregister</c> reproduces the same evaluation.
/// </summary>
/// <remarks>
/// Deliberately a record rather than loose parameters. Each of these reaches identity through a
/// different route — a command-line <c>-p</c> overrides the file's own <c>#:property</c> directives,
/// and a <c>Directory.Build.props</c> beside the <c>.cs</c> can set <c>WinAppPackageName</c> or
/// <c>WinAppManifestPath</c> conditionally on <c>$(Configuration)</c> or <c>$(RuntimeIdentifier)</c>.
/// Missing any one of them makes <c>unregister</c> resolve a different package than <c>run</c>
/// registered, stranding the real registration while potentially removing a same-rooted one. Naming
/// the set keeps that surface visible instead of spread across a parameter list.
/// </remarks>
/// <param name="Configuration">Build configuration (<c>-c</c>); <c>Debug</c> when the caller did not choose one.</param>
/// <param name="Architecture">Target architecture, which becomes the injected <c>-r win-&lt;arch&gt;</c> unless the app declares its own.</param>
/// <param name="ArchitectureIsExplicit">True when <c>--arch</c>/<c>--runtime</c> named the architecture, which then overrides what the file declares.</param>
/// <param name="Properties">Repeatable <c>-p Name=Value</c> overrides.</param>
internal sealed record SingleFileIdentityInputs(
    string Configuration,
    string Architecture,
    bool ArchitectureIsExplicit,
    IReadOnlyList<string> Properties)
{
    /// <summary>The inputs a run with no identity-shaping switches uses.</summary>
    public static SingleFileIdentityInputs Default { get; } =
        new("Debug", RunArchHelper.DefaultArchitecture(), ArchitectureIsExplicit: false, []);
}
