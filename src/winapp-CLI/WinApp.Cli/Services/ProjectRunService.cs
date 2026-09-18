// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <inheritdoc cref="IProjectRunService" />
internal sealed partial class ProjectRunService(
    IDotNetService dotNetService,
    IProjectDetectionService projectDetectionService,
    ICsWinRTMetadataShimService csWinRTMetadataShimService,
    IAnsiConsole ansiConsole,
    ILogger<ProjectRunService> logger) : IProjectRunService
{
    /// <summary>MSBuild properties requested from the evaluate step (always ≥2 → JSON output).</summary>
    private static readonly string[] RequestedProperties =
    [
        "TargetDir",
        // The publish output directory (`bin/<cfg>/<tfm>/<rid>/publish/`). Distinct from TargetDir — it is
        // where deployment transforms (trimming, single-file, ReadyToRun, Native AOT, self-contained) land,
        // so `winapp pack` (publish mode) packages this, not the build output.
        "PublishDir",
        "MSBuildProjectDirectory",
        "FinalAppxManifestName",
        // The NuGet targets' manifest escape hatch: a project can point WinAppManifestPath at an authored
        // manifest outside the publish output. Honored here so a project that classifies as packaged through
        // it (matching `winapp run`) can also be packaged, instead of failing to find a manifest.
        "WinAppManifestPath",
        "AppxPackageRecipe",
        "AssemblyName",
        "TargetName",
        "NativeBinary",
        "RunCommand",
        "RunArguments",
        // The project.assets.json restore wrote for THESE build inputs. Package discovery reads it rather
        // than re-evaluating the project: `dotnet package list` takes no -c/-r/-p, and conveying them
        // through the environment does not work either, because MSBuild ranks environment properties below
        // a value the project assigns while the build's own -c/-r/-p outrank it.
        "ProjectAssetsFile",
        "WindowsPackageType",
        "WindowsAppSDKSelfContained",
        "EnableMsixTooling",
        // Whether the Windows App SDK MSIX packaging targets are active for this project. Distinguishes a
        // native-MSIX project (winapp lets the SDK produce the package) from a generic publish-layout one.
        "MsixPackageSupport",
        // Project signing configuration, resolved into one signing policy for the final artifact (spec §6).
        "AppxPackageSigningEnabled",
        "PackageCertificateKeyFile",
        "PackageCertificatePassword",
        "PackageCertificateThumbprint",
        "AppxPackageSigningTimestampServerUrl",
        "_WinAppRunSupportActive",
        "OutputType",
        // The app's own launch preference. Read here so a .csproj run directly gets the same behavior as
        // one launched through `dotnet run`, where the NuGet targets forward it — the same documented
        // property must not mean different things depending on how the project was started.
        "WinAppRunUseExecutionAlias",
        "PublishTrimmed",
        "PublishAot",
        "SelfContained",
        "PublishProfile",
        "PublishProfileName",
        "PublishProfileFullPath",
        "WebPublishProfileFile",
        "PublishProfileImported",
        "_PublishProfileRootFolder",
        "TargetFramework",
        "Platform",
        "RuntimeIdentifier",
    ];

    /// <summary>
    /// Property names owned by a dedicated <c>-c</c>/<c>-r</c>/<c>-f</c> switch. A same-named user
    /// <c>-p</c> is dropped from BOTH passes so they can't resolve a different Configuration/RID/TFM (see
    /// <see cref="WarnOnOverriddenFlags"/>). <c>Platform</c> is intentionally NOT reserved — project mode
    /// forwards it as-is and conveys arch via the RuntimeIdentifier only. <c>Configuration</c>/
    /// <c>RuntimeIdentifier</c> are always pinned; <c>TargetFramework</c> only when a TFM is resolved (a
    /// bare <c>-p:TargetFramework</c> is promoted and re-emitted via <c>-f</c>, so dropping it is safe).
    /// </summary>
    private static readonly string[] DedicatedFlagProperties = ["Configuration", "RuntimeIdentifier", "TargetFramework"];

    /// <summary>
    /// Test seam for the "real interactive terminal" gate the build pass uses to choose the native terminal
    /// logger launcher over plain line streaming. <see langword="null"/> in production
    /// (the gate is <see cref="ProgressDisplay.ShouldUseLiveSpinner(IAnsiConsole, ILogger)"/>); overridable
    /// only because that gate reads process-global state that is always false under the test host.
    /// </summary>
    internal Func<bool>? NativeTerminalGateOverrideForTests { get; set; }

    /// <inheritdoc />
    public Task<string?> CheckSdkAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
        => CheckSdkFloorAsync(
            workingDirectory,
            minimumMajor: 8,
            minimumPatch: 100,
            upgradeHint: "Running csproj requires .NET SDK 8.0.100 or newer. Install or update it from https://aka.ms/dotnet/download.",
            tooOldReason: "is too old for project mode",
            cancellationToken);

    /// <summary>
    /// Probes <c>dotnet --version</c> and reports whether the installed SDK meets a minimum floor.
    /// Shared by project mode (8.0.100, for MSBuild <c>--getProperty</c>) and single-file mode (10.0.300,
    /// the first band whose <c>dotnet package list --file</c> can resolve a file-based app's packages) so
    /// the two cannot drift apart.
    /// </summary>
    /// <param name="tooOldReason">Completes the sentence "The .NET SDK &lt;version&gt; …".</param>
    /// <returns>An actionable error message if the SDK is missing/too old, otherwise <c>null</c>.</returns>
    private async Task<string?> CheckSdkFloorAsync(
        DirectoryInfo workingDirectory,
        int minimumMajor,
        int minimumPatch,
        string upgradeHint,
        string tooOldReason,
        CancellationToken cancellationToken)
    {
        int exitCode;
        string output;
        try
        {
            (exitCode, output, _) = await dotNetService.RunDotnetCommandAsync(workingDirectory, "--version", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Honor Ctrl+C during the SDK probe instead of reporting it as a missing SDK.
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            // dotnet not on PATH → Process.Start throws Win32Exception (or FileNotFoundException when a
            // resolved path has since disappeared). Anything else is unexpected and should surface.
            return $"The .NET SDK was not found. {upgradeHint}";
        }

        if (exitCode != 0)
        {
            return $"Could not determine the .NET SDK version ('dotnet --version' failed). {upgradeHint}";
        }

        var versionLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(versionLine) && TryParseSdkVersion(versionLine, out var major, out var minor, out var patch))
        {
            var capable = major > minimumMajor
                || (major == minimumMajor && (minor > 0 || (minor == 0 && patch >= minimumPatch)));
            if (!capable)
            {
                return $"The .NET SDK {versionLine} {tooOldReason}. {upgradeHint}";
            }
        }

        // Present but unparseable version → assume a modern SDK; the build will surface a real error
        // if the SDK is genuinely incapable.
        return null;
    }

    /// <summary>
    /// SHIM (temporary): resolves the <c>CsWinRTWindowsMetadata</c> folder to inject for SDK-less builds,
    /// or <c>null</c> when the user already set the property (their value wins) or no injection is
    /// needed/possible. See <see cref="CsWinRTMetadataShimService"/>.
    /// </summary>
    private string? ResolveCsWinRTMetadataShim(ProjectRunOptions options, string? framework)
    {
        if (UserSetCsWinRTMetadata(options))
        {
            return null;
        }

        return csWinRTMetadataShimService.ResolveMetadataFolder(framework);
    }

    /// <summary>
    /// True when the user supplied their own <c>-p:CsWinRTWindowsMetadata=…</c>; their value wins and the
    /// shim must not inject (or trigger a restore to resolve) anything.
    /// </summary>
    private static bool UserSetCsWinRTMetadata(ProjectRunOptions options) =>
        options.Properties.Any(p =>
            p.StartsWith("CsWinRTWindowsMetadata=", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs the pre-build steps — effective-framework pinning, the CsWinRT metadata shim, and the
    /// pre-build restore (whole-solution when applicable) — before the build pass. Property discovery is
    /// buffered because its output is parsed; restores stream their output live. Returns the
    /// (possibly framework-pinned) options, the build-pass options (with <c>NoRestore</c> set when a
    /// pre-restore already covered the target), and the resolved CsWinRT metadata folder (or null).
    /// </summary>
    private async Task<(ProjectRunOptions Options, ProjectRunOptions BuildOptions, string? CsWinRTMetadata)>
        PrepareBuildInputsAsync(
            FileInfo csproj,
            ProjectRunOptions options,
            DirectoryInfo workingDir,
            CancellationToken cancellationToken,
            bool aotPublish = false,
            bool publish = false)
    {
        // Pin an effective single TFM for a multi-targeted project (default = first declared) BEFORE any
        // pass so build/evaluate/packaging/provisioning all agree. No-op when single-targeted / --framework set.
        options = await ResolveEffectiveFrameworkAsync(csproj, options, workingDir, cancellationToken);

        // The shim needs the effective single TFM to steer ref-pack selection on SDK-less hosts. Resolved
        // separately since options.Framework stays null for a normal single-targeted project; never used as
        // a build -p:TargetFramework.
        var shimFramework = await ResolveShimFrameworkAsync(csproj, options, workingDir, cancellationToken);

        // SHIM (temporary): on hosts with no registered Windows SDK, resolve ref-pack winmds to inject as
        // -p:CsWinRTWindowsMetadata. Skipped when the user set the property. null = no injection.
        var csWinRTMetadata = ResolveCsWinRTMetadataShim(options, shimFramework);

        // Decide whether to inject an explicit -p:Platform (else arch is conveyed by the RID alone). Injected
        // only when the target AND its whole ProjectReference closure declare a <Platforms> including the
        // arch, so it can't desync a no-<Platforms> reference (MSB3030/PRI252). Threaded into every pass
        // below (restore/build/evaluate) via `options`, keeping them in lock-step.
        options = ResolvePlatformInjection(csproj, options, requireConcreteRid: aotPublish);
        if (!aotPublish)
        {
            options = await ResolveRequiredPublishProfileAsync(
                csproj,
                options,
                workingDir,
                csWinRTMetadata,
                cancellationToken);
        }
        var buildOptions = options;

        // When the target lives in a solution, restore the whole solution's managed projects up front so
        // build-dependency siblings that aren't ProjectReferences (e.g. a COM server) have project.assets.json
        // (else NETSDK1004) — matching VS / `dotnet build <sln>`. Gated on actually building + restore not opted out.
        if (!options.NoBuild && !options.NoRestore)
        {
            // (1) Restore the owning solution's managed siblings. An all-managed whole-solution restore also
            // covered the target, so the passes below can skip their own restore.
            var restoredWholeSolution = await RestoreSolutionSiblingsAsync(csproj, options, workingDir, cancellationToken);

            // (2) SHIM (temporary): on a clean SDK-less host the ref pack may not be on disk when the shim
            // first resolves, so it no-ops and the first build fails even though it restores the ref pack.
            // Pre-populate it with an explicit restore, then re-resolve. Only when the shim would inject.
            if (csWinRTMetadata is null
                && !UserSetCsWinRTMetadata(options)
                && csWinRTMetadataShimService.IsWindowsSdkAbsent())
            {
                var restoreExit = restoredWholeSolution
                    ? 0
                    : await RunRestorePassAsync(csproj, options, workingDir, cancellationToken);
                if (restoreExit == 0)
                {
                    csWinRTMetadata = ResolveCsWinRTMetadataShim(options, shimFramework);
                    // A project-scoped restore mirrors Platform and fully covers the build. A solution-scoped
                    // restore omits Platform to avoid MSB4126, so a platform-specific build must restore again.
                    // Never reuse this build-context restore to skip the PUBLISH pass's restore: `dotnet
                    // publish` sets _IsPublishing=true, which can pull in publish-only dependencies (e.g. a
                    // PackageReference conditioned on '$(_IsPublishing)') that the build restore never fetched.
                    if (!publish && (!restoredWholeSolution || !HasEffectivePlatform(options)))
                    {
                        buildOptions = options with { NoRestore = true };
                    }
                }
            }
            else if (!publish && restoredWholeSolution && !HasEffectivePlatform(options))
            {
                buildOptions = options with { NoRestore = true };
            }
        }

        return (options, buildOptions, csWinRTMetadata);
    }

    /// <inheritdoc />
    public Task<ProjectBuildOutcome> BuildAndResolveAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
        => BuildOrPublishAndResolveAsync(csproj, options, publish: false, cancellationToken);

    /// <summary>
    /// Publishes the project (<c>dotnet publish</c>) and resolves the evaluated <c>PublishDir</c> as the
    /// payload — where deployment transforms (trimming, single-file, ReadyToRun, Native AOT, self-contained)
    /// land — so <c>winapp pack</c> packages what actually ships, not the build output. Mirrors
    /// <see cref="BuildAndResolveAsync"/>; the returned <c>ProjectRunResolution.TargetDir</c> is the publish
    /// directory. (General publish; the Native-AOT-specific verification lives with <c>winapp run --aot</c>.)
    /// </summary>
    public Task<ProjectBuildOutcome> PublishAndResolveAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
        => BuildOrPublishAndResolveAsync(csproj, options, publish: true, cancellationToken);

    private async Task<ProjectBuildOutcome> BuildOrPublishAndResolveAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        bool publish,
        CancellationToken cancellationToken)
    {
        var workingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        WarnOnOverriddenFlags(options);

        // Restore output must remain visible: NuGet can spend minutes retrying an unreachable feed, and
        // buffering those diagnostics makes the command look frozen. Property discovery remains buffered
        // because winapp parses it, but every restore below streams progress and failures immediately.
        var (preparedOptions, buildOptions, csWinRTMetadata) = await PrepareBuildInputsAsync(
            csproj, options, workingDir, cancellationToken, publish: publish);
        options = preparedOptions;

        // Reject a non-runnable project (e.g. a class library) before building it — the post-build
        // evaluate below would otherwise only catch it after the user paid for a full build.
        // A project declaring an executable OutputType is the common case, so trust it and skip the
        // probe. Otherwise (absent — the SDK default a bare class library relies on — or non-executable)
        // resolve the effective value, and fast-fail only on a confident non-executable result so an
        // inconclusive evaluate never rejects a real app.
        // The ambient solution is irrelevant here: an explicitly-specified .csproj carries one too, so
        // gating on it would skip this check for `winapp run path\To\Some.csproj` inside a solution.
        if (!options.NoBuild)
        {
            var declaredOutputType = ProjectDetectionService.TryGetDeclaredOutputType(csproj);
            if (string.IsNullOrEmpty(declaredOutputType) || !ProjectDetectionService.IsExecutableOutputType(declaredOutputType))
            {
                var evaluatedOutputType = await TryEvaluateOutputTypeAsync(csproj, options, workingDir, csWinRTMetadata, cancellationToken);
                if (!string.IsNullOrEmpty(evaluatedOutputType) && !ProjectDetectionService.IsExecutableOutputType(evaluatedOutputType))
                {
                    throw new ProjectRunException(
                        $"'{csproj.Name}' is not an executable app project (OutputType='{evaluatedOutputType}'). winapp requires an executable project (OutputType Exe or WinExe).");
                }
            }
        }

        // Two passes: (1) BUILD — a plain `dotnet build` whose console log
        // STREAMS live so the user sees progress (skipped under --no-build); then (2) EVALUATE — a
        // fast `dotnet msbuild --getProperty` that returns the resolved output paths as JSON. The
        // split is required because `--getProperty` SUPPRESSES normal MSBuild console output, so a
        // single combined pass would build silently. The evaluate pass is fed the SAME effective
        // Configuration/RID/Platform/TFM/-p as the build so its TargetDir/RunCommand match what was
        // actually built.
        // Run the compile pass: build mode skips it under --no-build (just evaluate existing output); publish
        // mode ALWAYS runs it — `dotnet publish --no-build` still executes the publish targets/transforms.
        if (publish || !options.NoBuild)
        {
            var buildExit = await RunBuildPassAsync(csproj, buildOptions, workingDir, csWinRTMetadata, cancellationToken, publish);
            if (buildExit != 0)
            {
                // dotnet's diagnostics were already streamed live; log the summary and propagate the exit code.
                logger.LogError("{UISymbol} {Op} failed for {Project} (exit code {ExitCode}).", UiSymbols.Error, publish ? "Publish" : "Build", csproj.Name, buildExit);
                return new ProjectBuildOutcome(null, buildExit);
            }
        }

        var evaluateArgs = BuildEvaluateArguments(csproj, options, csWinRTMetadata, publish: publish);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(evaluateArgs));

        var (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(workingDir, evaluateArgs, cancellationToken);

        if (exitCode != 0)
        {
            // The build (if any) succeeded but property evaluation failed — surface dotnet's
            // diagnostics and propagate the exit code rather than launch against unknown output.
            logger.LogError("{UISymbol} Could not evaluate project properties for {Project} (exit code {ExitCode}).", UiSymbols.Error, csproj.Name, exitCode);
            var combined = string.Join(Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.TrimEnd()));
            if (!string.IsNullOrWhiteSpace(combined))
            {
                // Keep stdout clean for --json consumers; route diagnostics to stderr instead.
                if (options.Json)
                {
                    Console.Error.WriteLine(combined);
                }
                else
                {
                    ansiConsole.WriteLine(combined);
                }
            }

            return new ProjectBuildOutcome(null, exitCode);
        }

        var props = MsBuildPropertyReader.Parse(stdout, RequestedProperties);

        // `--no-build` means "run what's already built", but winapp's evaluate injects `-r win-<arch>`
        // and (when the project declares <Platforms>) `-p:Platform=<arch>`, and each adds a segment to
        // the output path — bin\ARM64\Debug\<tfm>\win-arm64\ versus the bin\Debug\<tfm>\ that Visual
        // Studio and a plain `dotnet build` produce. So drop those injected knobs progressively and take
        // the first variant whose TargetDir exists. A winapp build writes the fully-qualified path, so it
        // matches on the first try and never gets here. Build mode only: publish mode ran the publish pass
        // (even under --no-build) and resolves the payload from PublishDir below.
        if (options.NoBuild && !publish)
        {
            var primaryTargetDir = GetProp(props, "TargetDir");
            if (!string.IsNullOrEmpty(primaryTargetDir) && !Directory.Exists(primaryTargetDir))
            {
                // Ordered from "closest to what winapp would have built" to "plain dotnet build".
                var fallbacks = new List<(bool Rid, bool Platform, bool PublishProfile)>();
                if (!string.IsNullOrWhiteSpace(options.Platform))
                {
                    fallbacks.Add((false, true, true)); // no RID, keep resolved Platform/profile
                }
                fallbacks.Add((false, false, false)); // plain `dotnet build` / VS layout
                fallbacks.Add((true, false, false));  // RID-only (including older winapp versions)

                foreach (var (includeRid, includePlatform, includePublishProfile) in fallbacks)
                {
                    var args = BuildEvaluateArguments(
                        csproj, options, csWinRTMetadata,
                        includeRuntimeIdentifier: includeRid,
                        includePlatform: includePlatform,
                        includePublishProfile: includePublishProfile);
                    logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(args));

                    var (fallbackExit, fallbackStdout, _) = await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken);
                    if (fallbackExit != 0)
                    {
                        continue;
                    }

                    var fallbackProps = MsBuildPropertyReader.Parse(fallbackStdout, RequestedProperties);
                    var fallbackTargetDir = GetProp(fallbackProps, "TargetDir");
                    if (!string.IsNullOrEmpty(fallbackTargetDir) && Directory.Exists(fallbackTargetDir))
                    {
                        logger.LogDebug(
                            "{UISymbol} --no-build: '{Primary}' not found; using existing output '{Fallback}' (RID={Rid}, Platform={Platform}, PublishProfile={PublishProfile}).",
                            UiSymbols.Note, primaryTargetDir, fallbackTargetDir, includeRid, includePlatform, includePublishProfile);
                        props = fallbackProps;
                        break;
                    }
                }
            }
        }

        var outputType = GetProp(props, "OutputType");
        if (!string.IsNullOrEmpty(outputType) &&
            !string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectRunException(
                $"'{csproj.Name}' is not an executable app project (OutputType='{outputType}'). winapp requires an executable project (OutputType Exe or WinExe).");
        }

        var targetDir = GetProp(props, "TargetDir");
        var runCommand = GetProp(props, "RunCommand");
        var runArguments = GetProp(props, "RunArguments");
        var selfContained = string.Equals(GetProp(props, "WindowsAppSDKSelfContained"), "true", StringComparison.OrdinalIgnoreCase);

        // Publish mode packages the deployment payload from PublishDir — EXCEPT for an MSIX-tooling app
        // (WinUI / EnableMsixTooling), whose package is assembled from an MSBuild-generated .appxrecipe.
        // That recipe, the generated AppxManifest.xml and the compiled XAML live in the build output
        // (TargetDir); `dotnet publish` does NOT reproduce that layout in PublishDir (its publish folder
        // omits the manifest, the recipe and the .xbf). So keep TargetDir when a recipe exists — recipe-based
        // staging in MsixService gathers the manifest, compiled XAML and source-tree assets — and redirect
        // to PublishDir only for recipe-less apps (self-contained / trimmed / single-file / AOT / simple),
        // whose publish folder is the complete payload.
        var appxRecipePath = ResolveEvaluatedFileIfPresent(props, "AppxPackageRecipe", workingDir.FullName);
        if (publish && appxRecipePath is null)
        {
            var publishDir = GetProp(props, "PublishDir");
            if (!string.IsNullOrEmpty(publishDir))
            {
                targetDir = Path.GetFullPath(publishDir, workingDir.FullName);
            }
        }

        var packaging = DeterminePackaging(props, targetDir);

        // For a packaged app, carry the MSBuild-evaluated manifest and recipe so callers can package the
        // authoritative layout (aligns with the Native AOT resolver). Prefer the project's explicit
        // WinAppManifestPath escape hatch (the same one that can activate packaged support) over the
        // SDK-generated FinalAppxManifestName. Only meaningful when packaged.
        var appxManifestPath = packaging == ProjectPackaging.Packaged
            ? ResolveEvaluatedFileIfPresent(props, "WinAppManifestPath", workingDir.FullName)
                ?? ResolveEvaluatedFileIfPresent(props, "FinalAppxManifestName", workingDir.FullName)
            : null;
        if (packaging != ProjectPackaging.Packaged)
        {
            appxRecipePath = null;
        }

        if (string.IsNullOrEmpty(targetDir))
        {
            throw new ProjectRunException(
                $"Could not resolve the build output directory (TargetDir) for '{csproj.Name}'. Ensure the project builds successfully.");
        }

        if (packaging == ProjectPackaging.Unpackaged)
        {
            if (string.IsNullOrEmpty(runCommand) || !RunCommandIsLaunchable(runCommand))
            {
                var reason = options.NoBuild
                    ? $"The runnable executable was not found under '{targetDir}'. Remove --no-build so the project is built first, or build it manually."
                    : "The build did not produce a runnable executable (RunCommand).";
                throw new ProjectRunException(
                    $"'{csproj.Name}' resolves to an unpackaged app but no launchable executable is available. {reason}");
            }
        }

        var resolution = new ProjectRunResolution(
            csproj,
            targetDir,
            string.IsNullOrEmpty(runCommand) ? null : runCommand,
            packaging,
            selfContained,
            options.Architecture,
            options.Framework,
            options.NoRestore,
            string.IsNullOrEmpty(runArguments) ? null : runArguments,
            string.IsNullOrEmpty(outputType) ? null : outputType,
            ReadAliasPreference(props),
            GetProp(props, "ProjectAssetsFile") is { Length: > 0 } assetsFile ? assetsFile : null,
            GetProp(props, "RuntimeIdentifier") is { Length: > 0 } assetsRid ? assetsRid : null,
            AppxManifestPath: appxManifestPath,
            AppxRecipePath: appxRecipePath);

        return new ProjectBuildOutcome(resolution, 0);
    }

    /// <summary>
    /// Resolves an evaluated MSBuild path property (relative to <paramref name="projectDirectory"/>) to an
    /// absolute path, returning <c>null</c> when the property is unset or the resolved file does not exist.
    /// Non-throwing counterpart to the Native AOT resolver's <c>ResolveEvaluatedFile</c>, used for the
    /// optional packaged-app manifest/recipe where absence is a normal (non-packaged / non-recipe) case.
    /// </summary>
    internal static string? ResolveEvaluatedFileIfPresent(
        IReadOnlyDictionary<string, string> properties,
        string name,
        string projectDirectory)
    {
        var value = GetProp(properties, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string resolved;
        try
        {
            resolved = Path.GetFullPath(value, projectDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // These paths (WinAppManifestPath / FinalAppxManifestName / AppxPackageRecipe) come from the
        // project's evaluated properties — untrusted input. Probing a UNC / mapped-network-drive /
        // reparse-redirected path with File.Exists can trigger outbound SMB authentication, so treat a
        // network location as "not present" without probing it (matching the project-keyfile guard).
        if (PathSafety.IsNetworkPath(resolved)
            || PathSafety.IsNetworkDriveRoot(resolved)
            || PathSafety.RedirectsToNetwork(resolved))
        {
            return null;
        }

        return File.Exists(resolved) ? resolved : null;
    }

    // RunCommand is an apphost path when UseAppHost is on, but a bare command name (e.g. "dotnet", with
    // RunArguments = exec "<app>.dll") when it's off. Validate a rooted path with File.Exists; resolve a
    // bare name against PATH so a genuinely missing launcher errors cleanly instead of failing at Start.
    internal static bool RunCommandIsLaunchable(string runCommand)
    {
        if (Path.IsPathRooted(runCommand))
        {
            return File.Exists(runCommand);
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        var extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasExtension = Path.HasExtension(runCommand);

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (hasExtension && File.Exists(Path.Combine(dir, runCommand)))
            {
                return true;
            }

            foreach (var ext in extensions)
            {
                if (File.Exists(Path.Combine(dir, runCommand + ext)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<bool> IsDefinitivelyUnpackagedAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var props = await TryEvaluateProjectPropertiesAsync(csproj, options, cancellationToken);

        // Only an EXPLICIT WindowsPackageType=None is definitive. An unset value is NOT — a packaged app
        // declaring identity via an emitted recipe also evaluates empty here pre-build, so
        // DeterminePackaging's post-build recipe fallback stays authoritative. Evaluation failure →
        // indeterminate; let the authoritative build classify.
        return props is not null
            && string.Equals(GetProp(props, "WindowsPackageType"), "None", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cheap, side-effect-free probe (no build) that reports whether the Windows App SDK MSIX packaging
    /// targets are active for the project (<c>MsixPackageSupport</c> or <c>EnableMsixTooling</c>). When
    /// true, <c>winapp pack</c> lets the SDK produce the package via <see cref="PublishNativeMsixAsync"/>
    /// rather than assembling a generic publish layout. Indeterminate/failed evaluation returns
    /// <see langword="false"/> so the generic path stays the default.
    /// </summary>
    public async Task<bool> IsNativeMsixProjectAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var props = await TryEvaluateProjectPropertiesAsync(csproj, options, cancellationToken);
        if (props is null)
        {
            return false;
        }

        return IsTrue(GetProp(props, "MsixPackageSupport"))
            || IsTrue(GetProp(props, "EnableMsixTooling"));
    }

    /// <summary>
    /// Evaluates the project's MSIX signing configuration (spec §6) so the caller can resolve one signing
    /// policy for the final artifact. Returns <see langword="null"/> when evaluation could not run.
    /// </summary>
    public async Task<ProjectSigningProperties?> EvaluateProjectSigningAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var props = await TryEvaluateProjectPropertiesAsync(csproj, options, cancellationToken);
        if (!options.NoRestore && !IsProjectRestored(props))
        {
            // Signing configuration is frequently imported from a referenced NuGet package's build/*.props,
            // which are present only via obj/<project>.nuget.g.props AFTER restore. A --getProperty evaluate on
            // an unrestored clean checkout does NOT fail — it succeeds and returns those imported properties
            // empty — so restoring only when the evaluate returns null would miss this case and silently
            // resolve to unsigned. Restore and re-evaluate whenever the project has not been restored (its
            // evaluated ProjectAssetsFile is absent), so a project that requires signing is not silently
            // delivered unsigned on its first (unrestored) packaging run.
            var restoreWorkingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
            await RunRestorePassAsync(csproj, options, restoreWorkingDir, cancellationToken);
            props = await TryEvaluateProjectPropertiesAsync(csproj, options, cancellationToken);
        }
        if (props is null)
        {
            return null;
        }

        var workingDir = (csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory())).FullName;
        var enabledRaw = GetProp(props, "AppxPackageSigningEnabled");
        bool? enabled = string.IsNullOrWhiteSpace(enabledRaw)
            ? null
            : string.Equals(enabledRaw, "true", StringComparison.OrdinalIgnoreCase);

        var keyFile = GetProp(props, "PackageCertificateKeyFile");
        string? resolvedKeyFile = null;
        if (!string.IsNullOrWhiteSpace(keyFile))
        {
            try
            {
                resolvedKeyFile = Path.GetFullPath(keyFile, workingDir);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                resolvedKeyFile = keyFile;
            }
        }

        return new ProjectSigningProperties(
            enabled,
            resolvedKeyFile,
            GetProp(props, "PackageCertificatePassword") is { Length: > 0 } pwd ? pwd : null,
            GetProp(props, "PackageCertificateThumbprint") is { Length: > 0 } tp ? tp : null,
            GetProp(props, "AppxPackageSigningTimestampServerUrl") is { Length: > 0 } ts ? ts : null);
    }

    /// <summary>
    /// Whether the project has been restored, judged by the presence of its evaluated
    /// <c>ProjectAssetsFile</c> (<c>obj/&lt;project&gt;.nuget.g.props</c> and its sibling
    /// <c>project.assets.json</c> are written together by restore). A clean checkout evaluates the
    /// <c>ProjectAssetsFile</c> path but the file does not yet exist. Returns <see langword="false"/> when
    /// evaluation could not run at all, so the caller restores and retries.
    /// </summary>
    private static bool IsProjectRestored(IReadOnlyDictionary<string, string>? props)
    {
        if (props is null)
        {
            return false;
        }
        var assetsFile = GetProp(props, "ProjectAssetsFile");
        return !string.IsNullOrWhiteSpace(assetsFile) && File.Exists(assetsFile);
    }

    /// <summary>
    /// Runs the shared evaluate pass (same effective TFM / RID / platform / shim / profile as a real build)
    /// and returns the parsed <see cref="RequestedProperties"/>, or <see langword="null"/> when dotnet
    /// could not be started or evaluation failed. Evaluate-only: no build is triggered.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> TryEvaluateProjectPropertiesAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var workingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());

        // Pin the same effective TFM the real build/evaluate passes use, so a multi-targeted project
        // is probed against a single-TFM inner build, not the empty cross-targeting outer node.
        options = await ResolveEffectiveFrameworkAsync(csproj, options, workingDir, cancellationToken);
        var shimFramework = await ResolveShimFrameworkAsync(csproj, options, workingDir, cancellationToken);
        var csWinRTMetadata = ResolveCsWinRTMetadataShim(options, shimFramework);
        options = ResolvePlatformInjection(csproj, options);
        options = await ResolveRequiredPublishProfileAsync(
            csproj,
            options,
            workingDir,
            csWinRTMetadata,
            cancellationToken);

        var evaluateArgs = BuildEvaluateArguments(csproj, options, csWinRTMetadata);
        int exitCode;
        string stdout;
        try
        {
            (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDir, evaluateArgs, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        return exitCode != 0 ? null : MsBuildPropertyReader.Parse(stdout, RequestedProperties);
    }

    /// <summary>
    /// Determines packaged vs unpackaged from the evaluated properties, never from
    /// manifest presence.
    /// </summary>
    private static ProjectPackaging DeterminePackaging(IReadOnlyDictionary<string, string> props, string targetDir)
    {
        var windowsPackageType = GetProp(props, "WindowsPackageType");

        if (string.Equals(windowsPackageType, "None", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectPackaging.Unpackaged;
        }

        if (!string.IsNullOrEmpty(windowsPackageType))
        {
            // MSIX (or any other non-empty value) → packaged.
            return ProjectPackaging.Packaged;
        }

        // Unset/empty (common on the --no-build evaluate-only path, where MSIX targets don't run):
        // fall back to EnableMsixTooling, the WinApp run-support gate, or an emitted recipe.
        if (string.Equals(GetProp(props, "EnableMsixTooling"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectPackaging.Packaged;
        }

        // The Microsoft.Windows.SDK.BuildTools.WinApp integration activates run support for an executable
        // Windows project that ships an appxmanifest.xml but sets no WindowsPackageType (e.g.
        // samples/dotnet-app). Those run WITH identity off the copied manifest, so honor the signal rather
        // than launching the apphost without identity (which breaks Package.Current).
        if (string.Equals(GetProp(props, "_WinAppRunSupportActive"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectPackaging.Packaged;
        }

        if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
        {
            try
            {
                if (Directory.EnumerateFiles(targetDir, "*.build.appxrecipe").Any())
                {
                    return ProjectPackaging.Packaged;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Ignore — fall through to unpackaged.
            }
        }

        return ProjectPackaging.Unpackaged;
    }

    /// <summary>
    /// SHIM (temporary): runs an explicit <c>dotnet restore</c> so the <c>Microsoft.Windows.SDK.NET.Ref</c>
    /// ref pack lands on disk BEFORE the shim resolves its winmd folder (fixes the clean-host first-build
    /// failure). Returns the dotnet exit code so the caller only skips the build's own restore on success.
    /// </summary>
    private async Task<int> RunRestorePassAsync(FileInfo csproj, ProjectRunOptions options, DirectoryInfo workingDir, CancellationToken cancellationToken)
    {
        var restoreArgs = BuildRestorePassArguments(csproj, options, ResolveRestoreVerbosity(logger, options.Json));
        logger.LogDebug("{UISymbol} Restoring before SDK-less CsWinRT metadata resolution.", UiSymbols.Note);
        var exitCode = await RunRestoreCommandAsync(
            restoreArgs, $"Restoring {csproj.Name} dependencies...", options, workingDir, cancellationToken);
        if (exitCode != 0)
        {
            // Non-fatal: fall through and let the build pass restore + surface any real error itself.
            WriteRestoreFallbackWarning(
                options,
                $"{UiSymbols.Warning} Initial restore failed (exit code {exitCode}); continuing with the build, which will retry restore.");
        }
        return exitCode;
    }

    /// <summary>
    /// Restores the owning solution's managed sibling projects before the target build so build-dependency
    /// siblings that aren't <c>ProjectReference</c>s still have a <c>project.assets.json</c> (NETSDK1004
    /// parity with VS / <c>dotnet build &lt;sln&gt;</c>). Only fires for a solution-resolved run. When every
    /// listed project is managed, a single <c>dotnet restore &lt;sln&gt;</c> covers the whole graph and this
    /// returns <see langword="true"/> (caller skips the build pass's restore). When a native project is
    /// present (<c>dotnet restore</c> can't handle it VS-less) OR the whole-solution restore fails, managed
    /// siblings are restored individually and this returns <see langword="false"/>. All restores are
    /// best-effort; the build pass surfaces any real error.
    /// </summary>
    private async Task<bool> RestoreSolutionSiblingsAsync(FileInfo target, ProjectRunOptions options, DirectoryInfo workingDir, CancellationToken cancellationToken)
    {
        if (options.Solution is null)
        {
            return false;
        }

        var (allManaged, siblings) = ComputeSolutionRestorePlan(options.Solution, target);
        if (siblings.Count == 0)
        {
            // Solution lists only the target (or only native siblings) — nothing extra to restore; the
            // normal target restore is unchanged.
            return false;
        }

        // An inferred PublishProfile belongs only to the selected app. Restoring the whole solution would
        // pass it to unrelated projects, so restore siblings individually and let the target build restore
        // itself under the inferred profile.
        if (allManaged && string.IsNullOrWhiteSpace(options.PublishProfile))
        {
            // Closest to VS: one restore over the whole solution pulls the target and every sibling.
            var args = BuildRestorePassArguments(options.Solution, options, ResolveRestoreVerbosity(logger, options.Json));
            logger.LogDebug("{UISymbol} Restoring solution before build for build-dependency parity.", UiSymbols.Note);
            var exitCode = await RunRestoreCommandAsync(
                args, $"Restoring {options.Solution.Name} dependencies...", options, workingDir, cancellationToken);
            if (exitCode == 0)
            {
                return true;
            }

            // Whole-solution restore failed. Don't defer to the target-only build restore (that leaves
            // non-ProjectReference managed siblings unrestored — the NETSDK1004 case this prevents); fall
            // back to per-sibling restore before returning false.
            WriteRestoreFallbackWarning(
                options,
                $"{UiSymbols.Warning} Solution restore failed (exit code {exitCode}); retrying managed dependencies individually.");
        }

        // Native project present (dotnet restore <sln> errors VS-less) or the solution restore failed:
        // restore managed siblings individually and skip natives; the target restores in the normal pass.
        await RestoreSiblingsIndividuallyAsync(siblings, options, workingDir, cancellationToken);
        return false;
    }

    /// <summary>
    /// Best-effort restores each managed sibling project individually (skipping the target, which the normal
    /// build pass restores). Used both when a native sibling forces a per-project plan and as the fallback
    /// when a whole-solution restore fails. Each restore is non-fatal; a real error surfaces at build time.
    /// </summary>
    private async Task RestoreSiblingsIndividuallyAsync(
        IReadOnlyList<FileInfo> siblings,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        var siblingOptions = options with { PublishProfile = null };
        foreach (var sibling in siblings)
        {
            var args = BuildRestorePassArguments(
                sibling,
                siblingOptions,
                ResolveRestoreVerbosity(logger, siblingOptions.Json));
            logger.LogDebug("{UISymbol} Restoring solution sibling {Sibling} before build for build-dependency parity.", UiSymbols.Note, sibling.Name);
            var exitCode = await RunRestoreCommandAsync(
                args, $"Restoring {sibling.Name} dependencies...", siblingOptions, workingDir, cancellationToken);
            if (exitCode != 0)
            {
                WriteRestoreFallbackWarning(
                    options,
                    $"{UiSymbols.Warning} Restore of {sibling.Name} failed (exit code {exitCode}); continuing with the build, which will report any unresolved dependency errors.");
            }
        }
    }

    private static bool HasEffectivePlatform(ProjectRunOptions options) =>
        !string.IsNullOrWhiteSpace(options.Platform)
        || UserSpecifiesProperty(options.Properties, "Platform");

    private void WriteRestoreFallbackWarning(ProjectRunOptions options, string message)
    {
        if (options.Json || !logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        if (!logger.IsEnabled(LogLevel.Information))
        {
            Console.Error.WriteLine(message);
            return;
        }

        logger.LogWarning("{Message}", message);
    }

    /// <summary>
    /// Runs a pre-build restore with live output so slow package downloads, feed retries, and NuGet errors
    /// are visible as they happen. Output is always streamed through winapp so authenticated NuGet source
    /// URLs can be redacted before they reach the terminal or CI logs.
    /// </summary>
    internal async Task<int> RunRestoreCommandAsync(
        string arguments,
        string banner,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        if (options.Json || !logger.IsEnabled(LogLevel.Information))
        {
            if (options.Json)
            {
                Console.Error.WriteLine($"dotnet {RedactSecretsForDisplay(arguments)}");
            }

            return await dotNetService.RunDotnetStreamingAsync(
                workingDir, arguments,
                onOutputLine: static line => Console.Error.WriteLine(NugetErrorMessage.Redact(line)),
                onErrorLine: static line => Console.Error.WriteLine(NugetErrorMessage.Redact(line)),
                cancellationToken: cancellationToken);
        }

        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Sync} {banner}");
        ansiConsole.MarkupLineInterpolated($"[dim]   dotnet {Markup.Escape(RedactSecretsForDisplay(arguments))}[/]");

        var writeLive = CreateSynchronizedRedactedLineWriter();

        return await dotNetService.RunDotnetStreamingAsync(
            workingDir, arguments, writeLive, writeLive, cancellationToken: cancellationToken);
    }

    private static string? ResolveRestoreVerbosity(ILogger logger, bool json) =>
        !json && !logger.IsEnabled(LogLevel.Information) ? "quiet" : null;

    private Action<string> CreateSynchronizedRedactedLineWriter()
    {
        var writeLock = new object();
        var writer = ansiConsole.Profile.Out.Writer;
        return line =>
        {
            lock (writeLock)
            {
                writer.WriteLine(NugetErrorMessage.Redact(line));
            }
        };
    }

    /// <summary>
    /// Runs the project-mode build pass, streaming dotnet's output live. Output routing:
    /// <list type="bullet">
    ///   <item><c>--json</c>/<c>--quiet</c>: stream all build output to <b>stderr</b> so stdout stays pure
    ///   JSON / clean. Keeps <c>-tl:off</c>.</item>
    ///   <item>Real interactive terminal with <c>--no-restore</c>: print a <c>🔧 Building…</c> header + dim
    ///   invocation, then hand the terminal to dotnet with <b>inherited stdio</b> so its native terminal
    ///   logger renders the live build. Omits <c>-tl:off</c>.</item>
    ///   <item>Otherwise (agent/CI/redirected): header + dim invocation, then stream output live to stdout.
    ///   Keeps <c>-tl:off</c>.</item>
    /// </list>
    /// Output always streams (never hidden behind a spinner) so success-path warnings stay visible and the
    /// sanitized injected-arg invocation is self-describing.
    /// </summary>
    internal async Task<int> RunBuildPassAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        string? csWinRTMetadataFolder,
        CancellationToken cancellationToken,
        bool publish = false)
    {
        var verbosity = ResolveBuildVerbosity(logger, options.Json);

        var banner = $"{(publish ? "Publishing" : "Building")} {csproj.Name} ({options.Configuration} | {options.Architecture})...";
        var stopwatch = Stopwatch.StartNew();

        // Native AOT publish needs the VS Installer directory on PATH so vswhere.exe (and the MSVC toolchain
        // the AOT linker invokes) resolves. The native-MSIX packaging pass already applies this; the generic
        // publish pass must too, or `winapp pack` on a generic <PublishAot> project fails with MSB3073/exit
        // 123 on a machine whose PATH omits the Installer directory even though `run --aot` succeeds. Null
        // (build mode, or when the Installer directory is already resolvable) leaves the environment untouched.
        var publishEnvironment = publish ? BuildAotPublishEnvironment() : null;

        // --json/--quiet: stdout stays pure JSON, so route the invocation AND build output to stderr
        // (Console.Error is synchronized, so concurrent stdout/stderr callbacks are safe).
        if (options.Json || !logger.IsEnabled(LogLevel.Information))
        {
            var redirectedArgs = BuildBuildPassArguments(csproj, options, verbosity, csWinRTMetadataFolder, publish: publish);
            // --json emits the invocation on stderr (the injected args stay discoverable); --quiet suppresses it.
            if (options.Json)
            {
                Console.Error.WriteLine($"dotnet {RedactSecretsForDisplay(redirectedArgs)}");
            }
            return await dotNetService.RunDotnetStreamingAsync(
                workingDir, redirectedArgs,
                onOutputLine: static line => Console.Error.WriteLine(NugetErrorMessage.Redact(line)),
                onErrorLine: static line => Console.Error.WriteLine(NugetErrorMessage.Redact(line)),
                publishEnvironment, cancellationToken);
        }

        // Info-enabled paths (default interactive / --verbose / agent-CI): print the header and the sanitized
        // dotnet invocation (winapp injects args the user never typed — RID, shim, -p forwarding) so
        // failures are self-describing.
        var nativeTerminal = NativeTerminalGateOverrideForTests?.Invoke()
            ?? ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger);
        // A build without --no-restore may emit authenticated NuGet source URLs. Keep that path streamed
        // through winapp so credentials can be redacted; native inherited stdio is safe only for build-only output.
        nativeTerminal &= options.NoRestore;
        var buildArgs = BuildBuildPassArguments(csproj, options, verbosity, csWinRTMetadataFolder, nativeTerminal, publish);
        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Wrench} {banner}");
        ansiConsole.MarkupLineInterpolated($"[dim]   dotnet {Markup.Escape(RedactSecretsForDisplay(buildArgs))}[/]");

        int streamedExit;
        if (nativeTerminal)
        {
            // Real interactive terminal: hand the console to dotnet (inherited stdio, no -tl:off) so its
            // native terminal logger renders the live build directly — single warnings, live progress.
            // winapp never sees the lines; the persistent header/invocation above and ✓ Built below frame it.
            streamedExit = await dotNetService.RunDotnetInheritedAsync(workingDir, buildArgs, publishEnvironment, cancellationToken);
        }
        else
        {
            // Info-enabled but NOT a TTY (agent/CI/redirected/piped --verbose): stream dotnet's output live
            // to stdout, serializing writes so concurrent stdout/stderr callbacks don't interleave.
            var writeLive = CreateSynchronizedRedactedLineWriter();

            streamedExit = await dotNetService.RunDotnetStreamingAsync(
                workingDir, buildArgs, writeLive, writeLive, publishEnvironment, cancellationToken);
        }

        if (streamedExit == 0)
        {
            PrintBuildSucceeded(csproj, options, stopwatch.Elapsed, publish);
        }

        return streamedExit;
    }

    /// <summary>
    /// Prints the persistent build-completion line (UX). Callers gate this to info-enabled, non-json paths
    /// so it never pollutes <c>--json</c> stdout or a <c>--quiet</c> run.
    /// </summary>
    private void PrintBuildSucceeded(FileInfo csproj, ProjectRunOptions options, TimeSpan elapsed, bool publish = false) =>
        ansiConsole.MarkupLineInterpolated(
            $"{UiSymbols.Check} {(publish ? "Published" : "Built")} {Path.GetFileNameWithoutExtension(csproj.Name)} in {elapsed.TotalSeconds:0.0}s");

    /// <summary>
    /// Maps the CLI's effective log level to a dotnet <c>-v</c> verbosity for the build pass. <c>--verbose</c>
    /// stays at <c>minimal</c> on purpose (it already streams the build live and unlocks winapp's decision
    /// traces; <c>-v normal</c> would bury those under MSBuild task lines). <c>--quiet</c> keeps dotnet quiet;
    /// everything else is minimal.
    /// </summary>
    private static string ResolveBuildVerbosity(ILogger logger, bool json)
    {
        // Reserved: no CLI switch currently enables Trace (--verbose maps to Debug), so this branch is
        // unreachable today. Kept so a future trace-level switch raises dotnet's verbosity with it.
        if (logger.IsEnabled(LogLevel.Trace))
        {
            return "normal";
        }

        // --quiet suppresses Information (and is never combined with --json); keep dotnet quiet too.
        if (!json && !logger.IsEnabled(LogLevel.Information))
        {
            return "quiet";
        }

        // Default AND --verbose (Debug): keep dotnet skimmable. --verbose still streams the build live and
        // shows winapp's LogDebug traces — without the -v normal flood.
        return "minimal";
    }

    private void WarnOnOverriddenFlags(ProjectRunOptions options)
    {
        // Match dotnet's behavior (dedicated flag wins over a same-named -p) but leave a debug trail.
        foreach (var property in options.Properties)
        {
            foreach (var segment in PropertySegments(property))
            {
                var name = PropertyName(segment);
                if (name.Equals("Configuration", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("RuntimeIdentifier", StringComparison.OrdinalIgnoreCase))
                {
                    // A lone -p RuntimeIdentifier with no --arch is HONORED as an exact-RID override
                    // (ExactRuntimeIdentifier), not overridden — don't claim otherwise.
                    if (name.Equals("RuntimeIdentifier", StringComparison.OrdinalIgnoreCase) &&
                        options.ExactRuntimeIdentifier is { Length: > 0 })
                    {
                        continue;
                    }

                    logger.LogDebug(
                        "{UISymbol} -p:{Property} is overridden by the dedicated flag (matches dotnet precedence).",
                        UiSymbols.Note, segment);
                }
                else if (name.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase))
                {
                    // A bare -p:TargetFramework (no --framework) is PROMOTED to the effective framework and
                    // honored, so it's not overridden. It's only overridden when a dedicated --framework
                    // resolved a DIFFERENT TFM — warn just then.
                    var value = segment.Split('=', 2).ElementAtOrDefault(1)?.Trim() ?? string.Empty;
                    if (!string.IsNullOrEmpty(options.Framework) &&
                        !options.Framework.Equals(value, StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogDebug(
                            "{UISymbol} -p:{Property} is overridden by --framework '{Framework}' (matches dotnet precedence).",
                            UiSymbols.Note, segment, options.Framework);
                    }
                }
                else if (name.Equals("Platform", StringComparison.OrdinalIgnoreCase))
                {
                    // A user -p:Platform is authoritative: it is forwarded as-is and SUPPRESSES winapp's own
                    // conditional Platform injection (ResolvePlatformInjection). The RID still follows --arch,
                    // so an inconsistent pair builds a mismatched app — warn so the divergence isn't silent.
                    logger.LogDebug(
                        "{UISymbol} -p:{Property} is forwarded as-is; the RuntimeIdentifier still follows --arch, so ensure they are consistent.",
                        UiSymbols.Note, segment);
                }
            }
        }
    }

    /// <summary>
    /// Reads the project's <c>WinAppRunUseExecutionAlias</c> preference, warning when it is malformed.
    /// </summary>
    /// <remarks>
    /// A value that is not a valid boolean reads as "no preference", matching the NuGet targets, which
    /// forward no switch for one. It is warned about rather than accepted silently: the property's whole
    /// purpose is to override the launch mechanism, and a typo that quietly does nothing is invisible
    /// until a console app's output goes missing.
    /// </remarks>
    private bool? ReadAliasPreference(IReadOnlyDictionary<string, string> props)
    {
        var raw = GetProp(props, Commands.RunCommand.Handler.UseExecutionAliasProperty);
        var preference = MsBuildPropertyReader.ParseOptionalBoolean(raw, out var malformed);
        if (malformed)
        {
            logger.LogWarning(
                "{UISymbol} Ignoring {Property}='{Value}': expected 'true' or 'false'.",
                UiSymbols.Warning, Commands.RunCommand.Handler.UseExecutionAliasProperty, raw);
        }

        return preference;
    }

    private static string GetProp(IReadOnlyDictionary<string, string> props, string name)
        => props.TryGetValue(name, out var value) ? value.Trim() : string.Empty;

    /// <summary>
    /// F1 pre-build probe: runs the side-effect-free evaluate (no <c>-t:Build</c>, no restore) purely to
    /// read the resolved <c>OutputType</c>, so a non-runnable project can be rejected BEFORE the user pays
    /// for a full build. Reuses the exact evaluate args the post-build pass uses (same RID/Config/TFM/-p)
    /// so OutputType resolves identically; the other requested properties (TargetDir/RunCommand) are
    /// meaningless pre-build and ignored. Returns the evaluated OutputType, or <see langword="null"/> when
    /// the evaluate failed or produced nothing — the caller then proceeds to the normal build+evaluate
    /// path rather than reject on an inconclusive probe.
    /// </summary>
    private async Task<string?> TryEvaluateOutputTypeAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        string? csWinRTMetadata,
        CancellationToken cancellationToken)
    {
        var evaluateArgs = BuildEvaluateArguments(csproj, options, csWinRTMetadata);
        try
        {
            var (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDir, evaluateArgs, cancellationToken);
            if (exitCode != 0)
            {
                return null;
            }

            var props = MsBuildPropertyReader.Parse(stdout, RequestedProperties);
            var outputType = GetProp(props, "OutputType");
            return string.IsNullOrEmpty(outputType) ? null : outputType;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Honor Ctrl+C during the probe rather than masking it as an inconclusive result.
            throw;
        }
        catch (Exception)
        {
            // dotnet unavailable / evaluate failed → inconclusive; let the normal path classify.
            return null;
        }
    }

    /// <summary>
    /// Parses the leading <c>major.minor.patch</c> of a <c>dotnet --version</c> string
    /// (e.g. <c>8.0.100</c>, <c>10.0.301</c>, <c>8.0.100-preview.1</c>).
    /// </summary>
    internal static bool TryParseSdkVersion(string versionText, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return false;
        }

        // Strip any prerelease/build suffix.
        var core = versionText.Trim();
        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            core = core[..dash];
        }

        var parts = core.Split('.');
        if (parts.Length < 3)
        {
            return false;
        }

        return int.TryParse(parts[0], out major)
            && int.TryParse(parts[1], out minor)
            && int.TryParse(parts[2], out patch);
    }
}
