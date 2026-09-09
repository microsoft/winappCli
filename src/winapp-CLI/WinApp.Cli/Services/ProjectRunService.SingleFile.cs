// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Single-file mode for <see cref="ProjectRunService" />: building and evaluating a .NET
/// <b>file-based app</b> — a single <c>.cs</c> the SDK compiles through a virtual
/// <c>&lt;stem&gt;.cs.csproj</c> configured by <c>#:</c> directives.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately does NOT reuse the <c>.csproj</c> build/evaluate passes, for two reasons that are
/// both properties of the SDK rather than style choices:
/// </para>
/// <list type="number">
///   <item>
///   <b>The evaluate pass must be <c>dotnet build</c>, not <c>dotnet msbuild</c>.</b> MSBuild has no
///   <c>.cs</c> project loader — the virtual-project synthesis lives in the <c>dotnet build</c>/
///   <c>dotnet run</c> CLI path — so <c>dotnet msbuild counter.cs --getProperty:X</c> fails with
///   <c>MSB4025: The project file could not be loaded</c>. <c>dotnet build counter.cs --getProperty:X</c>
///   evaluates WITHOUT building, so the evaluate stays as cheap as the <c>.csproj</c> one.
///   </item>
///   <item>
///   <b>No <c>Platform</c> is injected, but a RuntimeIdentifier may be.</b> <c>Platform</c> is
///   deliberately never passed: a file-based app accepts it yet ignores it for RID selection, so
///   <c>Platform=arm64</c> still emits an x64 apphost. A <c>-r win-&lt;arch&gt;</c> IS injected when the
///   app does not declare its own <c>RuntimeIdentifier</c> — without it the SDK builds <c>AnyCPU</c> and
///   a self-contained Windows App SDK app fails outright. That is safe because BOTH passes receive the
///   same token, so the evaluate reads back the RID-qualified directory the build wrote. The run handler
///   honors <c>--arch</c>/<c>--runtime</c> (they override what the file declares) and rejects only
///   <c>--framework</c> and <c>--project</c>, pointing at the equivalent <c>#:property</c> instead.
///   </item>
/// </list>
/// <para>
/// Both passes are otherwise fed IDENTICAL tokens (Configuration + injected RID + user <c>-p</c>) so the
/// evaluate reads the output the build wrote.
/// </para>
/// </remarks>
internal sealed partial class ProjectRunService
{
    /// <summary>
    /// MSBuild properties requested from the single-file evaluate pass (always ≥2 → JSON output).
    /// Beyond the launch/packaging properties, this requests the five <c>WinApp*</c> manifest-inference
    /// properties plus <c>Version</c> and <c>WinAppManifestPath</c>. Undeclared properties evaluate to an
    /// empty string rather than being omitted, so the inference only needs null-or-empty checks.
    /// </summary>
    internal static readonly string[] SingleFileRequestedProperties =
    [
        "TargetDir",
        "OutputPath",
        "RunCommand",
        "RunArguments",
        "AssemblyName",
        "OutputType",
        "WindowsPackageType",
        "WindowsAppSDKSelfContained",
        // The built TFM. Threaded into loose-layout runtime provisioning so the Windows App SDK version
        // is read from the framework the app was actually built for.
        "TargetFramework",
        // Architecture. Single-file mode injects the RID (see ResolveSingleFileRuntimeIdentifierAsync),
        // and --arch/--runtime override a RuntimeIdentifier the file declares, so this is how the app's
        // target architecture reaches the Windows App Runtime provisioning that follows registration.
        // Platform is deliberately NOT consulted: a file-based app accepts it but ignores it for RID
        // selection, so 'Platform=arm64' still yields an x64 apphost.
        "RuntimeIdentifier",
        // Manifest inference. WinAppManifestPath mirrors the NuGet targets' escape hatch so a consumer can
        // point single-file mode at a hand-authored manifest from the .cs itself.
        "WinAppManifestPath",
        "WinAppPackageName",
        "WinAppDisplayName",
        "WinAppPublisher",
        "WinAppVersion",
        "WinAppDescription",
        // Capabilities to declare. Full trust does not substitute for a gated capability — the Windows AI
        // APIs need systemAIModels — so this is the one thing here that manifest generate has no option for.
        "WinAppCapabilities",
        // Launch behavior, sharing the name the NuGet targets already define for a .csproj. A console app
        // launched through AUMID activation gets no console, so setting this once in the file is what makes
        // 'winapp run app.cs' show output without --with-alias on every run.
        "WinAppRunUseExecutionAlias",
        // Read $(Version) — NOT $(VersionPrefix). Setting Version explicitly leaves VersionPrefix EMPTY,
        // so reading VersionPrefix first would silently discard the user's version.
        "Version",
    ];

    /// <inheritdoc />
    public Task<string?> CheckSingleFileSdkAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
        => CheckSdkFloorAsync(
            workingDirectory,
            // 10.0.100 can BUILD a file-based app, but single-file mode also resolves the app's NuGet
            // packages with `dotnet package list --file`, and that option only exists from the 10.0.300
            // feature band. On an older band the discovery silently returns nothing, and a
            // framework-dependent Windows App SDK app loses the framework dependency it needs to launch.
            // Requiring the band that supports the whole flow beats degrading into a broken package.
            minimumMajor: 10,
            minimumPatch: 300,
            upgradeHint: "Running a .NET file-based app (a single .cs) requires .NET SDK 10.0.300 or newer. Install or update it from https://aka.ms/dotnet/download.",
            tooOldReason: "cannot resolve packages for .NET file-based apps",
            cancellationToken);

    /// <summary>
    /// Decides whether to convey the target architecture to both single-file passes as
    /// <c>-r win-&lt;arch&gt;</c>, and records the decision on the returned options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Project mode always injects the RID (defaulting to the machine's architecture), which is what
    /// lets <c>winapp run App.csproj</c> build a self-contained Windows App SDK app with no extra
    /// switches. Single-file mode has to do the same: without a RID the SDK builds <c>AnyCPU</c>, and the
    /// Windows App SDK self-contained targets fail with <c>WindowsAppSDKSelfContained requires a
    /// supported Windows architecture</c> — so a plain <c>winapp run app.cs</c> could not build a WinUI
    /// app at all.
    /// </para>
    /// <para>
    /// Injecting is safe because BOTH passes receive the identical token, so the evaluate reads back the
    /// same RID-qualified output directory the build wrote (<c>bin\debug_win-x64\</c>).
    /// </para>
    /// <para>
    /// A command-line <c>-p</c> is a global property that would silently override a
    /// <c>#:property RuntimeIdentifier</c> in the file, so a cheap pre-evaluate (no build, well under a
    /// second) checks for one first: an app that declares its own architecture keeps it, unless the user
    /// asked for a specific one with <c>--arch</c>/<c>--runtime</c>, which wins as it does in project mode.
    /// </para>
    /// </remarks>
    private async Task<SingleFileRunOptions> ResolveSingleFileRuntimeIdentifierAsync(
        FileInfo singleFile,
        SingleFileRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        var requested = RunArchHelper.ToRuntimeIdentifier(options.Architecture);

        // An explicit --arch/--runtime is authoritative; no need to ask what the file declares.
        if (options.ArchitectureIsExplicit)
        {
            return options with { InjectedRuntimeIdentifier = requested };
        }

        // A user -p:RuntimeIdentifier is theirs to own; never inject over it. Parsed through the same
        // helper the forwarding filter uses, so a padded name like ' RuntimeIdentifier=win-arm64' cannot
        // be invisible here and visible there — which silently dropped the user's override.
        if (options.Properties.Any(p => PropertyNames(p).Any(name => name.Equals("RuntimeIdentifier", StringComparison.OrdinalIgnoreCase))))
        {
            return options with { InjectedRuntimeIdentifier = null };
        }

        var declared = await TryEvaluateSingleFilePropertyAsync(singleFile, options, workingDir, "RuntimeIdentifier", cancellationToken);
        if (!string.IsNullOrEmpty(declared))
        {
            logger.LogDebug(
                "{UISymbol} '{File}' declares RuntimeIdentifier '{Rid}'; not injecting -r {Requested}.",
                UiSymbols.Note, singleFile.Name, declared, requested);
            return options with { InjectedRuntimeIdentifier = null };
        }

        return options with { InjectedRuntimeIdentifier = requested };
    }

    /// <summary>
    /// Reads a single evaluated MSBuild property from a file-based app without building. Returns
    /// <see langword="null"/> when the evaluation fails, so a probe can never block the run.
    /// </summary>
    private async Task<string?> TryEvaluateSingleFilePropertyAsync(
        FileInfo singleFile,
        SingleFileRunOptions options,
        DirectoryInfo workingDir,
        string propertyName,
        CancellationToken cancellationToken)
    {
        var args = BuildSingleFileProbeArguments(singleFile, options, propertyName);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(args));

        try
        {
            var (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken);
            if (exitCode != 0)
            {
                return null;
            }

            // A single --getProperty returns the raw value rather than JSON; the reader handles both.
            return MsBuildPropertyReader.Parse(stdout, [propertyName]).TryGetValue(propertyName, out var value)
                ? value.Trim()
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<SingleFileBuildOutcome> BuildAndResolveSingleFileAsync(
        FileInfo singleFile,
        SingleFileRunOptions options,
        CancellationToken cancellationToken)
    {
        // MSBuildProjectDirectory for a file-based app is the .cs file's OWN directory (not the temp
        // output), so building from there keeps any Directory.Build.props next to the file in scope.
        var workingDir = singleFile.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());

        options = await ResolveSingleFileRuntimeIdentifierAsync(singleFile, options, workingDir, cancellationToken);

        if (!options.NoBuild)
        {
            var buildExit = await RunSingleFileBuildPassAsync(singleFile, options, workingDir, cancellationToken);
            if (buildExit != 0)
            {
                // dotnet's diagnostics were already streamed live; log the summary and propagate the code.
                logger.LogError("{UISymbol} Build failed for {File} (exit code {ExitCode}).", UiSymbols.Error, singleFile.Name, buildExit);
                return new SingleFileBuildOutcome(null, buildExit);
            }
        }

        var evaluateArgs = BuildSingleFileEvaluateArguments(singleFile, options);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(evaluateArgs));

        var (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(workingDir, evaluateArgs, cancellationToken);

        if (exitCode != 0)
        {
            logger.LogError("{UISymbol} Could not evaluate properties for {File} (exit code {ExitCode}).", UiSymbols.Error, singleFile.Name, exitCode);
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

            return new SingleFileBuildOutcome(null, exitCode);
        }

        var props = MsBuildPropertyReader.Parse(stdout, SingleFileRequestedProperties);

        // `--no-build` means "run what's already built", but winapp's evaluate injects `-r win-<arch>`,
        // which adds a segment to the output path: bin\debug_win-x64\ versus the bin\debug\ a plain
        // `dotnet build app.cs` produces. Re-evaluate without the RID and take that instead when the
        // primary path is absent, so --no-build finds a normal build's output. A winapp build writes the
        // RID-qualified path, so it matches first and never gets here.
        //
        // Gated on the architecture being INFERRED. An unqualified output carries no architecture in its
        // path, so accepting it under an explicit --arch/--runtime would launch whatever was built and
        // provision runtime packages for the requested target instead — a silent architecture mismatch.
        // When the user named an architecture, a missing output is reported rather than substituted.
        if (options.NoBuild && !options.ArchitectureIsExplicit)
        {
            var primaryOutput = GetProp(props, "TargetDir");
            if (string.IsNullOrEmpty(primaryOutput))
            {
                primaryOutput = GetProp(props, "OutputPath");
            }

            if (!string.IsNullOrEmpty(primaryOutput) && !Directory.Exists(Path.GetFullPath(primaryOutput)))
            {
                var withoutRid = await TryEvaluateWithoutRuntimeIdentifierAsync(singleFile, options, workingDir, cancellationToken);
                if (withoutRid != null)
                {
                    props = withoutRid;
                }
            }
        }

        var outputType = GetProp(props, "OutputType");
        if (!string.IsNullOrEmpty(outputType) && !ProjectDetectionService.IsExecutableOutputType(outputType))
        {
            throw new ProjectRunException(
                $"'{singleFile.Name}' is not a runnable app (OutputType='{outputType}'). Add '#:property OutputType=WinExe' (or 'Exe') to the file.");
        }

        // Packaged vs unpackaged is read from the effective WindowsPackageType, exactly as project mode
        // determines it. An unpackaged app has no identity to register, so it launches its apphost
        // directly through the shared unpackaged path.
        var packaging = string.Equals(GetProp(props, "WindowsPackageType"), "None", StringComparison.OrdinalIgnoreCase)
            ? ProjectPackaging.Unpackaged
            : ProjectPackaging.Packaged;

        // TargetDir and OutputPath both evaluate to the same absolute, trailing-slash path for a
        // file-based app. Prefer TargetDir (matching project mode) and fall back to OutputPath.
        var outputDirectory = GetProp(props, "TargetDir");
        if (string.IsNullOrEmpty(outputDirectory))
        {
            outputDirectory = GetProp(props, "OutputPath");
        }

        if (string.IsNullOrEmpty(outputDirectory))
        {
            throw new ProjectRunException(
                $"Could not resolve the build output directory for '{singleFile.Name}'. Ensure it builds successfully with 'dotnet build {singleFile.Name}'.");
        }

        outputDirectory = Path.GetFullPath(outputDirectory);

        if (!Directory.Exists(outputDirectory))
        {
            var reason = options.NoBuild
                ? "Remove --no-build so the app is built first."
                : $"Build it manually with 'dotnet build {singleFile.Name}' to see why.";
            throw new ProjectRunException(
                $"The build output directory for '{singleFile.Name}' does not exist ({outputDirectory}). {reason}");
        }

        var runCommand = GetProp(props, "RunCommand");
        var runArguments = GetProp(props, "RunArguments");

        if (packaging == ProjectPackaging.Unpackaged && !RunCommandIsLaunchable(runCommand))
        {
            var reason = options.NoBuild
                ? $"The runnable executable was not found under '{outputDirectory}'. Remove --no-build so the app is built first."
                : "The build did not produce a runnable executable (RunCommand).";
            throw new ProjectRunException(
                $"'{singleFile.Name}' resolves to an unpackaged app but no launchable executable is available. {reason}");
        }

        // Only a packaged app needs a concrete Executable for its manifest; an unpackaged one launches
        // RunCommand directly, and probing for an apphost that a non-apphost build never produces would
        // fail the run for no reason.
        var executableName = packaging == ProjectPackaging.Packaged
            ? ResolveSingleFileExecutableName(props, singleFile, outputDirectory)
            : Path.GetFileName(runCommand);

        return new SingleFileBuildOutcome(
            new SingleFileRunResolution(
                singleFile,
                outputDirectory,
                executableName,
                ResolveSingleFileArchitecture(props),
                GetProp(props, "TargetFramework") is { Length: > 0 } tfm ? tfm : null,
                string.Equals(GetProp(props, "WindowsAppSDKSelfContained"), "true", StringComparison.OrdinalIgnoreCase),
                packaging,
                string.IsNullOrEmpty(runCommand) ? null : runCommand,
                string.IsNullOrEmpty(runArguments) ? null : runArguments,
                props,
                BuildEvaluationProperties(options)),
            0);
    }

    /// <inheritdoc />
    public async Task<SingleFileIdentityResolution> ResolveSingleFileIdentityAsync(
        FileInfo singleFile,
        SingleFileIdentityInputs inputs,
        CancellationToken cancellationToken)
    {
        // Deliberately evaluates rather than builds: `--getProperty` evaluates the virtual project without
        // compiling anything, and the build root this needs is the SDK's per-file
        // %TEMP%\dotnet\runfile\<stem>-<hash> directory, which sits ABOVE the bin\<config>[_<rid>] tail and
        // so is invariant across configurations. `winapp unregister app.cs` therefore costs one ~1.5s
        // evaluation and works without the app being buildable on this machine at all.
        //
        // Every identity-shaping input is applied, and the RID goes through the SAME resolution the run
        // uses (probe what the file declares, inject only otherwise) rather than a copy — so the two can
        // never drift. See SingleFileIdentityInputs for why each one reaches identity.
        var options = new SingleFileRunOptions(
            Configuration: inputs.Configuration,
            Architecture: inputs.Architecture,
            ArchitectureIsExplicit: inputs.ArchitectureIsExplicit,
            NoBuild: true,
            NoRestore: false,
            Properties: inputs.Properties);

        // Same working directory the build pass uses: MSBuildProjectDirectory for a file-based app is the
        // .cs file's OWN directory, so evaluating from there keeps any Directory.Build.props next to the
        // file in scope and resolves the identity the build would have produced.
        var workingDir = singleFile.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());

        options = await ResolveSingleFileRuntimeIdentifierAsync(singleFile, options, workingDir, cancellationToken);

        var args = BuildSingleFileEvaluateArguments(singleFile, options);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(args));

        var (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken);
        if (exitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new ProjectRunException(
                $"Could not evaluate '{singleFile.Name}' to determine its package identity. {detail?.Trim()}".TrimEnd());
        }

        var props = MsBuildPropertyReader.Parse(stdout, SingleFileRequestedProperties);

        var packaging = string.Equals(GetProp(props, "WindowsPackageType"), "None", StringComparison.OrdinalIgnoreCase)
            ? ProjectPackaging.Unpackaged
            : ProjectPackaging.Packaged;

        return new SingleFileIdentityResolution(
            SingleFileManifestPlanner.ResolvePackageName(singleFile, props),
            packaging,
            ResolveSingleFileBuildRoot(props));
    }

    /// <summary>
    /// Re-evaluates a file-based app WITHOUT the injected RuntimeIdentifier, so <c>--no-build</c> can find
    /// the <c>bin\&lt;config&gt;</c> output a plain <c>dotnet build app.cs</c> produced rather than the
    /// <c>bin\&lt;config&gt;_&lt;rid&gt;</c> winapp's own build would have written.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the re-evaluation fails or still points nowhere, and the caller
    /// then keeps the original properties so the error names the path the user most likely expected.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>?> TryEvaluateWithoutRuntimeIdentifierAsync(
        FileInfo singleFile,
        SingleFileRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        try
        {
            var args = BuildSingleFileEvaluateArguments(singleFile, options, includeRuntimeIdentifier: false);
            logger.LogDebug("{UISymbol} --no-build fallback: dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(args));

            var (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken);
            if (exitCode != 0)
            {
                return null;
            }

            var props = MsBuildPropertyReader.Parse(stdout, SingleFileRequestedProperties);
            var candidate = GetProp(props, "TargetDir");
            if (string.IsNullOrEmpty(candidate))
            {
                candidate = GetProp(props, "OutputPath");
            }

            return !string.IsNullOrEmpty(candidate) && Directory.Exists(Path.GetFullPath(candidate))
                ? props
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException      // process start / MSBuild property parse
                or IOException                   // reading the evaluate output, probing the directory
                or UnauthorizedAccessException   // an unreadable candidate directory
                or ArgumentException             // a malformed path from the evaluated properties
                or NotSupportedException)        // a path shape GetFullPath refuses
        {
            // This is a best-effort compatibility probe: the caller falls back to the primary path and
            // reports it, so anything unexpected here must not fail a run that would otherwise work.
            logger.LogDebug("--no-build fallback evaluation failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reduces the evaluated output path to the SDK's per-file build root
    /// (<c>%TEMP%\dotnet\runfile\&lt;stem&gt;-&lt;hash&gt;</c>), used to confirm a registration really
    /// belongs to this <c>.cs</c> before removing it.
    /// </summary>
    /// <remarks>
    /// Trimming the <c>bin\&lt;config&gt;[_&lt;rid&gt;]</c> tail is what makes the check independent of
    /// configuration and architecture: <c>bin\debug</c> and <c>bin\release_win-arm64</c> share a root, so
    /// a package registered by <c>winapp run app.cs -c Release</c> is still recognized here.
    /// <para>
    /// The <c>bin</c> segment is VERIFIED rather than assumed, because this value becomes a trusted root
    /// for removing a registration and its app data. A custom <c>-p OutputPath=C:\apps\B\out</c> would
    /// otherwise reduce to <c>C:\apps</c>, and <c>winapp unregister B\counter.cs</c> could then accept and
    /// delete a same-identity registration under <c>C:\apps\A</c>. Returning <see langword="null"/> makes
    /// the caller fall back to identity alone rather than trusting an over-wide directory.
    /// </para>
    /// </remarks>
    private static string? ResolveSingleFileBuildRoot(IReadOnlyDictionary<string, string> props)
    {
        var outputPath = GetProp(props, "OutputPath");
        if (string.IsNullOrEmpty(outputPath))
        {
            outputPath = GetProp(props, "TargetDir");
        }

        if (string.IsNullOrEmpty(outputPath))
        {
            return null;
        }

        // <root>\bin\<config>\ → up two levels. TrimEnd first: the evaluated value carries a trailing
        // separator, which would otherwise cost one level of the walk.
        var current = Path.GetFullPath(outputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var binDirectory = Path.GetDirectoryName(current);
        if (binDirectory is null ||
            !string.Equals(Path.GetFileName(binDirectory), "bin", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var buildRoot = Path.GetDirectoryName(binDirectory);
        return string.IsNullOrEmpty(buildRoot) ? null : buildRoot;
    }

    /// <summary>
    /// Resolves the architecture the app was actually built for, so the Windows App Runtime installed
    /// after registration matches it.
    /// </summary>
    /// <remarks>
    /// Reads <c>RuntimeIdentifier</c> only. For a file-based app that is the sole property that changes
    /// the apphost's architecture — <c>#:property Platform=arm64</c> is accepted but leaves
    /// <c>RuntimeIdentifier</c> empty and still emits an x64 apphost on an x64 host, so treating
    /// <c>Platform</c> as the architecture would provision arm64 runtime packages for an x64 binary.
    /// With no RID the SDK builds for the host, which is what the fallback reports.
    /// </remarks>
    private static string ResolveSingleFileArchitecture(IReadOnlyDictionary<string, string> props)
        => RunArchHelper.ArchitectureFromRid(GetProp(props, "RuntimeIdentifier"))
            ?? RunArchHelper.DefaultArchitecture();

    /// <summary>
    /// Resolves the app executable's bare file name, written CONCRETELY into the generated manifest.
    /// <para>
    /// A <c>$targetnametoken$.exe</c> placeholder would be unusable here: every WinAppSDK self-contained
    /// output ships a <c>RestartAgent.exe</c> next to the app exe, so the token form always trips the
    /// "multiple .exe files found" ambiguity and would force <c>--executable</c> on every single run.
    /// </para>
    /// <c>RunCommand</c> is the authoritative apphost path when <c>UseAppHost</c> is on; otherwise fall
    /// back to <c>AssemblyName</c>.exe, and finally to the file stem.
    /// </summary>
    private static string ResolveSingleFileExecutableName(
        IReadOnlyDictionary<string, string> props,
        FileInfo singleFile,
        string outputDirectory)
    {
        var runCommand = GetProp(props, "RunCommand");
        if (!string.IsNullOrEmpty(runCommand) &&
            runCommand.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            Path.IsPathRooted(runCommand))
        {
            return Path.GetFileName(runCommand);
        }

        var assemblyName = GetProp(props, "AssemblyName");

        // Normalize to a bare file name. Both the RunCommand branch above and AssemblyName come from
        // MSBuild properties the .cs file controls, and this value is used TWICE: to probe the output
        // directory, and (via the caller) as the manifest's Executable attribute. A rooted or
        // separator-bearing value would make Path.Combine silently discard outputDirectory and would put
        // a path where the manifest schema expects a package-relative file name.
        var candidate = Path.GetFileName(
            !string.IsNullOrEmpty(assemblyName)
                ? assemblyName + ".exe"
                : Path.GetFileNameWithoutExtension(singleFile.Name) + ".exe");

        if (string.IsNullOrEmpty(candidate) || !File.Exists(Path.Join(outputDirectory, candidate)))
        {
            throw new ProjectRunException(
                $"Could not find the application executable '{candidate}' in the build output ({outputDirectory}). " +
                $"Ensure '{singleFile.Name}' declares '#:property OutputType=WinExe' (or 'Exe') and builds an apphost.");
        }

        return candidate;
    }

    /// <summary>
    /// Runs the single-file BUILD pass. Mirrors <see cref="RunBuildPassAsync"/>'s output regime exactly —
    /// stderr under <c>--json</c>/<c>--quiet</c>, inherited stdio (native terminal logger) on a real TTY,
    /// live line streaming otherwise — so a <c>.cs</c> build looks and behaves like a <c>.csproj</c> build.
    /// </summary>
    private async Task<int> RunSingleFileBuildPassAsync(
        FileInfo singleFile,
        SingleFileRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        var verbosity = ResolveBuildVerbosity(logger, options.Json);
        var banner = $"Building {singleFile.Name} ({options.Configuration})...";
        var stopwatch = Stopwatch.StartNew();

        if (options.Json || !logger.IsEnabled(LogLevel.Information))
        {
            var redirectedArgs = BuildSingleFileBuildPassArguments(singleFile, options, verbosity);
            if (options.Json)
            {
                Console.Error.WriteLine($"dotnet {RedactSecretsForDisplay(redirectedArgs)}");
            }
            return await dotNetService.RunDotnetStreamingAsync(
                workingDir, redirectedArgs,
                onOutputLine: static line => Console.Error.WriteLine(line),
                onErrorLine: static line => Console.Error.WriteLine(line),
                cancellationToken);
        }

        var nativeTerminal = NativeTerminalGateOverrideForTests?.Invoke()
            ?? ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger);
        var buildArgs = BuildSingleFileBuildPassArguments(singleFile, options, verbosity, nativeTerminal);
        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Wrench} {banner}");
        ansiConsole.MarkupLineInterpolated($"[dim]   dotnet {Markup.Escape(RedactSecretsForDisplay(buildArgs))}[/]");

        int streamedExit;
        if (nativeTerminal)
        {
            streamedExit = await dotNetService.RunDotnetInheritedAsync(workingDir, buildArgs, cancellationToken);
        }
        else
        {
            var writeLock = new object();
            void WriteLive(string line)
            {
                lock (writeLock)
                {
                    ansiConsole.WriteLine(line);
                }
            }

            streamedExit = await dotNetService.RunDotnetStreamingAsync(
                workingDir, buildArgs, WriteLive, WriteLive, cancellationToken);
        }

        if (streamedExit == 0)
        {
            ansiConsole.MarkupLineInterpolated(
                $"{UiSymbols.Check} Built {Path.GetFileNameWithoutExtension(singleFile.Name)} in {stopwatch.Elapsed.TotalSeconds:0.0}s");
        }

        return streamedExit;
    }
}


