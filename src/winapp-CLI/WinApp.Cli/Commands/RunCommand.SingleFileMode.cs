// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.Xml;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

/// <summary>
/// Single-file mode for <c>winapp run</c>: building a .NET <b>file-based app</b> (a single <c>.cs</c>
/// configured by <c>#:</c> directives) and running it as a packaged app with identity.
/// </summary>
/// <remarks>
/// The mode is a thin front-end. It builds the file, evaluates its MSBuild properties, resolves or
/// synthesizes an appxmanifest into the build output, and then hands off to the <b>unchanged</b> shared
/// folder pipeline (<c>ExecuteRunPipelineAsync</c>) — a file-based app's build output is structurally
/// identical to any other WinUI loose layout, so the only thing it was ever missing was a manifest.
/// </remarks>
internal partial class RunCommand
{
    public partial class Handler
    {
        /// <summary>
        /// Options that describe how to build a <c>.csproj</c> and have no meaning for a file-based app,
        /// mapped to the <c>#:</c> directive that replaces them. A file-based app declares its own target
        /// framework inline, and it is itself the project.
        /// </summary>
        private static readonly (Option Option, string Name, string Replacement)[] SingleFileRejectedOptions =
        [
            (ProjectOption, "--project", "A .cs file-based app IS the project; omit --project."),
            (FrameworkOption, "--framework", "Declare the target framework in the file instead, e.g. '#:property TargetFramework=net10.0-windows10.0.22621.0'."),
        ];

        /// <summary>
        /// Single-file mode entry point: build the <c>.cs</c>, resolve its evaluated properties, resolve or
        /// generate the manifest, then launch through the shared folder pipeline.
        /// </summary>
        private async Task<int> RunSingleFileModeAsync(
            ParseResult parseResult,
            FileInfo singleFile,
            string? appArgs,
            bool isJson,
            CancellationToken cancellationToken)
        {
            var configuration = parseResult.GetValue(ConfigurationOption) ?? "Debug";
            var archOption = parseResult.GetValue(ArchOption);
            var runtimeOption = parseResult.GetValue(RuntimeOption);
            var noBuild = parseResult.GetValue(NoBuildOption);
            var noRestore = parseResult.GetValue(NoRestoreOption);
            var properties = parseResult.GetValue(PropertyOption) ?? [];

            var noLaunch = parseResult.GetValue(NoLaunchOption);
            var withAlias = parseResult.GetValue(WithAliasOption);
            var withoutAlias = parseResult.GetValue(WithoutAliasOption);
            var debugOutput = parseResult.GetValue(DebugOutputOption);
            var unregisterOnExit = parseResult.GetValue(UnregisterOnExitOption);
            var detach = parseResult.GetValue(DetachOption);
            var clean = parseResult.GetValue(CleanOption);
            var useSymbols = parseResult.GetValue(SymbolsOption);
            var executable = parseResult.GetValue(ExecutableOption);
            var manifest = parseResult.GetValue(ManifestOption);
            var outputAppXDirectory = parseResult.GetValue(OutputAppXDirectoryOption);

            // Reject project-only build knobs, pointing at the #: directive that replaces each.
            foreach (var (option, name, replacement) in SingleFileRejectedOptions)
            {
                if (parseResult.GetResult(option) is not null)
                {
                    return Fail($"{name} does not apply to '{singleFile.Name}'. {replacement}", isJson);
                }
            }

            if (!TryValidateProperties(properties, isJson, out var propertyError))
            {
                return propertyError;
            }

            // Same resolution project mode uses: --runtime's arch beats --arch, else the process arch.
            if (!TryResolveArchitecture(archOption, runtimeOption, out var architecture, out var archError))
            {
                return Fail(archError!, isJson);
            }

            var architectureIsExplicit = !string.IsNullOrWhiteSpace(archOption) || !string.IsNullOrWhiteSpace(runtimeOption);

            if (!isJson && logger.IsEnabled(LogLevel.Information))
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Search} {singleFile.Name}  ·  {configuration} | {architecture}  ·  file-based app");
            }

            // Building a bare .cs through a virtual project requires .NET 10 — a higher floor than the
            // 8.0.100 that --getProperty alone needs.
            var workingDir = singleFile.Directory ?? new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());
            var sdkError = await projectRunService.CheckSingleFileSdkAsync(workingDir, cancellationToken);
            if (sdkError != null)
            {
                return Fail(sdkError, isJson);
            }

            SingleFileBuildOutcome outcome;
            try
            {
                outcome = await projectRunService.BuildAndResolveSingleFileAsync(
                    singleFile,
                    new SingleFileRunOptions(configuration, architecture, architectureIsExplicit, noBuild, noRestore, properties, isJson),
                    cancellationToken);
            }
            catch (ProjectRunException ex)
            {
                return Fail(ex.Message, isJson);
            }

            if (outcome.Resolution is null)
            {
                var code = outcome.ExitCode == 0 ? 1 : outcome.ExitCode;
                if (isJson)
                {
                    PrintJson(aumid: null, processId: null, $"Build failed (exit code {code}).");
                }
                return code;
            }

            var resolution = outcome.Resolution;
            var outputFolder = new DirectoryInfo(resolution.OutputDirectory);

            // Unpackaged: reuse the shared project-mode path so a .cs behaves exactly like a .csproj with
            // WindowsPackageType=None — runtime provisioning, direct apphost launch, and the same
            // rejection of packaged-only options. The .cs stands in for the project file; its package list
            // resolves through `dotnet package list --file`.
            if (resolution.Packaging == ProjectPackaging.Unpackaged)
            {
                var unpackaged = new ProjectRunResolution(
                    singleFile,
                    resolution.OutputDirectory,
                    resolution.RunCommand,
                    ProjectPackaging.Unpackaged,
                    resolution.SelfContained,
                    resolution.Architecture,
                    resolution.TargetFramework,
                    noRestore,
                    resolution.RunArguments,
                    // Carry the assets file across: the unpackaged path still provisions the Windows App
                    // Runtime, and without it that decision is made from a re-evaluated graph rather than
                    // the one the build resolved.
                    ProjectAssetsFile: resolution.ProjectAssetsFile);

                return await RunUnpackagedProjectAsync(
                    unpackaged, singleFile, appArgs,
                    noLaunch, withAlias, withoutAlias, debugOutput, unregisterOnExit, detach, clean, useSymbols,
                    executable, manifest, outputAppXDirectory, isJson,
                    cancellationToken);
            }

            // Resolve the effective executable ONCE, before the manifest is generated. Generation writes a
            // concrete Executable attribute, so an explicit --executable has to be known here — passing it
            // only downstream would leave the generated manifest naming the build's executable, and
            // placeholder resolution then has nothing to substitute and silently keeps that name.
            var effectiveExecutable = string.IsNullOrWhiteSpace(executable) ? resolution.ExecutableName : executable;

            // Decide the launch mechanism before the manifest is resolved: alias launch needs a
            // uap5:ExecutionAlias, and a generated manifest only gets one when this is already known.
            // The alias NAME is deliberately not resolved here. For a generated manifest it comes from the
            // planned identity below; for an authored one it comes from the manifest itself during
            // staging. Resolving it now would mean asking for a package name before knowing whether the
            // app even declares one.
            var aliasDecision = ResolveAliasLaunch(
                withAlias,
                withoutAlias,
                noLaunch,
                detach,
                isJson,
                resolution.Properties.GetValueOrDefault("OutputType"),
                ReadAliasPreference(resolution.Properties));

            withAlias = aliasDecision.UseAlias;

            FileInfo resolvedManifest;
            try
            {
                resolvedManifest = await ResolveSingleFileManifestAsync(resolution, manifest, outputFolder, effectiveExecutable, aliasDecision, isJson, cancellationToken);
            }
            catch (ProjectRunException ex)
            {
                return Fail(ex.Message, isJson);
            }

            HintNoConsoleOutput(resolution, withoutAlias, noLaunch, detach, isJson);

            var effectiveLayout = outputAppXDirectory ?? new DirectoryInfo(Path.Join(outputFolder.FullName, "AppX"));

            // Snapshot BEFORE the pipeline runs. Once registration succeeds, FindDevPackages reports the
            // package this run just created, so "was it already registered, and from where?" has exactly
            // one moment at which it can be answered.
            var priorRegistrations = FindPriorRegistrations(resolvedManifest);

            // A tier-3 manifest can be named <stem>.appxmanifest, which ManifestHelper.FindManifest does not
            // probe for, so the resolved manifest is always passed explicitly rather than left to
            // folder-mode auto-detection.
            // Pass the .cs itself as the "project file": AddLooseLayoutIdentityAsync resolves the app's
            // NuGet package list from it (via `dotnet package list --file`) to decide the Windows App SDK
            // framework dependency and runtime. Passing null instead would fall back to globbing the
            // current directory for any .csproj, which for a file-based app can only find an unrelated
            // project — and would write THAT project's Windows App SDK dependency into this app's manifest.
            // The effective executable also covers an AUTHORED manifest that still uses the
            // $targetnametoken$ placeholder: resolving that by scanning the output hits the "multiple .exe
            // files found" ambiguity, because every WinAppSDK self-contained output ships a
            // RestartAgent.exe beside the app.
            return await ExecuteRunPipelineAsync(
                outputFolder, resolvedManifest, outputAppXDirectory, appArgs,
                noLaunch, withAlias, debugOutput, unregisterOnExit, detach, clean, useSymbols,
                effectiveExecutable,
                isJson,
                runtimeArch: resolution.Architecture,
                projectFile: singleFile,
                framework: resolution.TargetFramework,
                noRestore: noRestore,
                selfContained: resolution.SelfContained,
                aliasDecision,
                cancellationToken,
                // Reported only once the package actually exists — see ReportSingleFileRegistrationImpact.
                onRegistered: () => ReportSingleFileRegistrationImpact(
                    resolution.SingleFile,
                    priorRegistrations,
                    effectiveLayout,
                    unregisterOnExit,
                    isJson,
                    BuildUnregisterCommand(
                        resolution.SingleFile,
                        configuration,
                        archOption,
                        runtimeOption,
                        properties,
                        manifest,
                        outputAppXDirectory,
                        effectiveLayout)),
                projectAssetsFile: ToAssetsFile(resolution.ProjectAssetsFile));
        }

        /// <summary>
        /// Resolves the manifest for a file-based app, in strict precedence order:
        /// <list type="number">
        ///   <item><c>--manifest</c> — an explicit path on the command line always wins.</item>
        ///   <item><c>WinAppManifestPath</c> — the same escape hatch the NuGet targets expose, declared from the <c>.cs</c>.</item>
        ///   <item>A manifest the user authored <b>next to the <c>.cs</c></b>, named <c>&lt;stem&gt;.appxmanifest</c> — used verbatim; nothing is generated.</item>
        ///   <item>Otherwise, generate <c>Package.appxmanifest</c> into the build output.</item>
        /// </list>
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This deliberately inverts the probe order used by the
        /// <c>Microsoft.Windows.SDK.BuildTools.WinApp</c> targets</b>, which check the OUTPUT directory
        /// before the project directory. That order is correct for MAUI, where the manifest is a build
        /// product generated into <c>$(OutputPath)</c>. It would be actively wrong here: winapp generates
        /// into the output directory, so under that order the generated file would permanently shadow a
        /// manifest the user hand-wrote next to their <c>.cs</c> and their edits would silently never apply.
        /// </para>
        /// <para>
        /// Tier 4 regenerates on every run. The manifest is a build product in a temp directory, and the
        /// <c>#:property</c> values it is derived from can change between runs.
        /// </para>
        /// <para>
        /// The names differ by tier on purpose. The SOURCE directory is shared — <c>foo.cs</c> and
        /// <c>bar.cs</c> can sit side by side — so tier 3 discovers ONLY the per-file
        /// <c>&lt;stem&gt;.appxmanifest</c>, matching the SDK's own per-file <c>&lt;stem&gt;.run.json</c>
        /// convention. The OUTPUT directory is not shared: the SDK gives every file-based app its own
        /// <c>%TEMP%\dotnet\runfile\&lt;stem&gt;-&lt;hash&gt;\</c>, so tier 4 writes the canonical
        /// <c>Package.appxmanifest</c> that every existing winapp probe already understands.
        /// </para>
        /// </remarks>
        private async Task<FileInfo> ResolveSingleFileManifestAsync(
            SingleFileRunResolution resolution,
            FileInfo? explicitManifest,
            DirectoryInfo outputFolder,
            string effectiveExecutable,
            AliasLaunchDecision aliasDecision,
            bool isJson,
            CancellationToken cancellationToken)
        {
            // Tier 1: --manifest. Left for ExecuteRunPipelineAsync to validate and report.
            if (explicitManifest != null)
            {
                return explicitManifest;
            }

            var stem = Path.GetFileNameWithoutExtension(resolution.SingleFile.Name);

            // Tiers 2 and 3: a manifest the app author supplied, via WinAppManifestPath or named
            // <stem>.appxmanifest next to the .cs. Shared with `winapp unregister` so both commands agree
            // on which manifest an app registers under.
            var authored = SingleFileManifestPlanner.FindAuthoredManifest(resolution.SingleFile, resolution.Properties);
            if (authored != null)
            {
                LogSingleFileManifestSource(authored.File, authored.Source, isJson);
                return authored.File;
            }

            // Tier 4: generate into the build output. The output directory belongs to exactly one .cs, so
            // the canonical Package.appxmanifest name is unambiguous there and stays discoverable by every
            // existing winapp manifest probe.
            var info = SingleFileManifestPlanner.Plan(resolution.SingleFile, resolution.Properties);
            const string manifestFileName = "Package.appxmanifest";

            // The status task converts a thrown exception into a non-zero return code rather than
            // rethrowing, so ignoring the result would let a failed generation (disk full, denied write)
            // fall through to registration against a missing or half-written manifest.
            var generateResult = await statusService.ExecuteWithStatusAsync("Generating manifest...", async (taskContext, ct) =>
            {
                await GenerateSingleFileManifestAsync(outputFolder, info, effectiveExecutable, manifestFileName, taskContext, ct);
                return (0, $"Manifest generated for {info.PackageName} {info.Version}");
            }, cancellationToken);

            if (generateResult != 0)
            {
                throw new ProjectRunException(
                    $"Could not generate a manifest for '{resolution.SingleFile.Name}' in '{outputFolder.FullName}'.");
            }

            var generatedManifest = new FileInfo(Path.Join(outputFolder.FullName, manifestFileName));

            // Declared capabilities are applied after generation rather than through the template, because
            // each one may also need its XML namespace declared, added to IgnorableNamespaces, and a
            // MaxVersionTested floor raised — structure the placeholder substitution cannot express.
            if (info.Capabilities.Count > 0)
            {
                ApplyGeneratedManifestCapabilities(generatedManifest, info.Capabilities, isJson);
            }

            // Alias launch needs a uap5:ExecutionAlias, which the template does not declare. For a
            // generated manifest the usual advice ("run winapp manifest add-alias") is impossible to
            // follow: this file is regenerated on every run, so any alias the user adds is destroyed by
            // the next one. Add it here instead, named from the package family just planned.
            if (aliasDecision.UseAlias &&
                ExecutionAliasResolver.BuildDefaultAliasName(
                    appLauncherService.ComputePackageFamilyName(info.PackageName, info.PublisherDN)) is { Length: > 0 } aliasName)
            {
                await AddGeneratedManifestAliasAsync(generatedManifest, aliasName, isJson, cancellationToken);
            }

            return generatedManifest;
        }

        /// <summary>
        /// Writes the app's declared capabilities into the freshly generated manifest.
        /// </summary>
        /// <remarks>
        /// Failures are fatal, not advisory: the user asked for a capability, and registering without it
        /// produces an app whose API calls fail at runtime with nothing pointing back at the manifest.
        /// </remarks>
        private void ApplyGeneratedManifestCapabilities(FileInfo manifest, IReadOnlyList<AppxCapability> capabilities, bool isJson)
        {
            try
            {
                var document = AppxManifestDocument.Load(manifest.FullName);
                foreach (var capability in capabilities)
                {
                    document.EnsureCapability(capability);
                }

                document.Save(manifest.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                throw new ProjectRunException(
                    $"Could not declare capabilities in '{manifest.FullName}': {ex.Message}");
            }

            if (!isJson && logger.IsEnabled(LogLevel.Debug))
            {
                ansiConsole.MarkupLineInterpolated(
                    $"{UiSymbols.Note} Declared capabilities: {string.Join(", ", capabilities.Select(c => c.Name))}.");
            }
        }

        /// <summary>
        /// Reads the app's <c>WinAppRunUseExecutionAlias</c> preference, or null when it declares none.
        /// </summary>
        /// <remarks>
        /// The property is shared verbatim with the NuGet targets so a <c>.cs</c> and a <c>.csproj</c>
        /// spell the same preference the same way. Setting it explicitly overrides the output-type
        /// default in BOTH directions, which is what lets a console app opt out from inside the file.
        /// A malformed value reads as "no preference" — see
        /// <see cref="MsBuildPropertyReader.ParseOptionalBoolean"/> — and is warned about, because a typo
        /// that quietly suppressed a console app's output would be invisible.
        /// </remarks>
        private bool? ReadAliasPreference(IReadOnlyDictionary<string, string> properties)
        {
            properties.TryGetValue(UseExecutionAliasProperty, out var raw);
            var preference = MsBuildPropertyReader.ParseOptionalBoolean(raw, out var malformed);
            if (malformed)
            {
                logger.LogWarning(
                    "{UISymbol} Ignoring '#:property {Property}={Value}': expected 'true' or 'false'.",
                    UiSymbols.Warning, UseExecutionAliasProperty, raw?.Trim());
            }

            return preference;
        }

        /// <summary>
        /// The property name an app uses to override the default launch mechanism, shared verbatim with
        /// the NuGet targets.
        /// </summary>
        internal const string UseExecutionAliasProperty = "WinAppRunUseExecutionAlias";

        /// <summary>
        /// Tells the user their console app will print nothing, when it is launched through AUMID.
        /// </summary>
        /// <remarks>
        /// Console apps launch through their alias by default, so reaching this means the user asked for
        /// AUMID — via <c>--without-alias</c>, <c>--detach</c>, or the property. Those are deliberate, so
        /// the note only fires for <c>--without-alias</c>, where "no output" is the surprising part of an
        /// otherwise reasonable request.
        /// </remarks>
        private void HintNoConsoleOutput(SingleFileRunResolution resolution, bool withoutAlias, bool noLaunch, bool detach, bool isJson)
        {
            if (!withoutAlias || noLaunch || detach || isJson || !logger.IsEnabled(LogLevel.Information))
            {
                return;
            }

            if (!resolution.Properties.TryGetValue("OutputType", out var outputType) ||
                !string.Equals(outputType?.Trim(), "Exe", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ansiConsole.MarkupLineInterpolated(
                $"{UiSymbols.Note} '{resolution.SingleFile.Name}' is a console app launched via AUMID, which gives it no console, so it will not print here.");
        }

        /// <summary>
        /// Adds the execution alias to the freshly generated manifest so alias launch works for a
        /// file-based app. The name is identity-derived and prefixed (see
        /// <see cref="ExecutionAliasResolver.BuildDefaultAliasName"/>), so it cannot contend with a real
        /// tool on PATH or with an unrelated app's alias.
        /// </summary>
        private async Task AddGeneratedManifestAliasAsync(FileInfo manifest, string aliasName, bool isJson, CancellationToken cancellationToken)
        {
            var result = await manifestService.AddExecutionAliasAsync(
                new AddExecutionAliasOptions(manifest, aliasName, AppId: SingleFileManifestPlanner.ApplicationId),
                cancellationToken);

            if (result.Status is AddExecutionAliasStatus.Added or AddExecutionAliasStatus.AlreadyExists)
            {
                if (!isJson && logger.IsEnabled(LogLevel.Debug))
                {
                    ansiConsole.MarkupLineInterpolated($"{UiSymbols.Note} Added execution alias '{result.AliasName}' to the generated manifest.");
                }

                return;
            }

            // Non-fatal: the launch itself reports "No execution alias found" with actionable guidance, and
            // failing the run here would block a user who could still launch via AUMID.
            logger.LogWarning(
                "{UISymbol} Could not add an execution alias to the generated manifest ({Status}). --with-alias may fail; author a manifest with a uap5:ExecutionAlias to control it.",
                UiSymbols.Warning, result.Status);
        }

        /// <summary>
        /// Writes the inferred manifest and the default asset set into the build output.
        /// </summary>
        /// <remarks>
        /// <paramref name="executableName"/> is written CONCRETELY (e.g. <c>Executable="counter.exe"</c>)
        /// rather than as the <c>$targetnametoken$.exe</c> build placeholder. Every WinAppSDK
        /// self-contained output ships a <c>RestartAgent.exe</c> next to the app exe, so the placeholder
        /// form would always trip the "multiple .exe files found" ambiguity and force the user to pass
        /// <c>--executable</c> on every run.
        /// </remarks>
        private Task GenerateSingleFileManifestAsync(
            DirectoryInfo outputFolder,
            SingleFileManifestInfo info,
            string executableName,
            string manifestFileName,
            TaskContext taskContext,
            CancellationToken cancellationToken) =>
            manifestTemplateService.GenerateCompleteManifestAsync(
                outputFolder,
                info.PackageName,
                info.PublisherDN,
                info.Version,
                ManifestTemplates.Packaged,
                info.Description,
                taskContext,
                manifestFileName,
                executableName,
                info.DisplayName,
                SingleFileManifestPlanner.ApplicationId,
                cancellationToken);

        private void LogSingleFileManifestSource(FileInfo manifest, string reason, bool isJson)
        {
            if (!isJson && logger.IsEnabled(LogLevel.Debug))
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Note} Using manifest {manifest.FullName} ({reason}).");
            }
        }

        /// <summary>
        /// The package name and the install locations of dev registrations already holding it, captured
        /// BEFORE a run registers anything, so the notice can still tell "already registered" from
        /// "registered by this run".
        /// </summary>
        private sealed record PriorRegistrations(string PackageName, IReadOnlyList<string> InstallLocations)
        {
            public static readonly PriorRegistrations None = new(string.Empty, []);
        }

        /// <summary>
        /// Reads the manifest's identity and the dev registrations already holding it.
        /// </summary>
        /// <remarks>
        /// Must run BEFORE registration. Afterwards <c>FindDevPackages</c> returns the package this run just
        /// created, which would make every run look like a re-registration and suppress the notice entirely.
        /// </remarks>
        private PriorRegistrations FindPriorRegistrations(FileInfo manifest)
        {
            try
            {
                if (!manifest.Exists)
                {
                    return PriorRegistrations.None;
                }

                var packageName = AppxManifestDocument.Load(manifest.FullName).IdentityName;
                if (string.IsNullOrEmpty(packageName))
                {
                    return PriorRegistrations.None;
                }

                return new PriorRegistrations(
                    packageName,
                    [.. packageRegistrationService.FindDevPackages(packageName)
                        .Where(p => p.IsDevelopmentMode)
                        .Select(p => p.InstallLocation)
                        .OfType<string>()
                        .Where(location => location.Length > 0)]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException or InvalidOperationException)
            {
                // Purely advisory — an unreadable or malformed manifest must not fail the run. Unexpected
                // exception types still surface rather than being silently swallowed.
                logger.LogDebug("Could not check for an existing registration: {Message}", ex.Message);
                return PriorRegistrations.None;
            }
        }

        /// <summary>
        /// Reports what a registration leaves behind: a warning when it REPLACES a different app's
        /// registration, and a one-time note when it creates one that outlives the run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The persistence note is deliberately tied to the FIRST registration of an identity rather than
        /// printed every run. <c>winapp run app.cs</c> is an inner-loop command that gets run dozens of
        /// times while iterating, and a notice on every one of them trains people to stop reading the
        /// output. It is also only true once: re-running the same file REPLACES its own registration
        /// rather than accumulating another, so nothing new is left behind after the first time. It is not
        /// gated on <c>OutputType</c> either — a windowed app persists exactly as a console one does, and
        /// also lands in the Start menu, so scoping the note to console apps would teach the wrong model.
        /// </para>
        /// <para>
        /// The replacement warning still fires for two apps that explicitly share one
        /// <c>WinAppPackageName</c>; the path hash in the default identity is what keeps unrelated
        /// same-named files from colliding in the first place.
        /// </para>
        /// <para>
        /// <paramref name="layoutDirectory"/> must be the EFFECTIVE AppX layout directory (honoring
        /// <c>--output-appx-directory</c>), because that is what a registration's install location points
        /// at. Comparing against the build output instead would warn about every re-run that uses a
        /// custom layout path, which trains users to ignore a warning that is usually real.
        /// </para>
        /// </remarks>
        private void ReportSingleFileRegistrationImpact(
            FileInfo singleFile,
            PriorRegistrations prior,
            DirectoryInfo layoutDirectory,
            bool unregisterOnExit,
            bool isJson,
            string? unregisterCommand = null)
        {
            // Gate on Warning, not Information: --quiet suppresses Information but still promises
            // warnings, and silently replacing another app's registration is exactly what a user needs
            // to hear about.
            if (isJson || prior.PackageName.Length == 0 || !logger.IsEnabled(LogLevel.Warning))
            {
                return;
            }

            var installedElsewhere = prior.InstallLocations
                .FirstOrDefault(location => !PathsPointToSameLocation(location, layoutDirectory.FullName));
            if (installedElsewhere is not null)
            {
                logger.LogWarning(
                    "{UISymbol} Replacing the existing registration of '{PackageName}', which was installed from a different location ({InstallLocation}). Set '#:property {Property}=<name>' to give this app its own package identity.",
                    UiSymbols.Warning, prior.PackageName, installedElsewhere, SingleFileManifestPlanner.PackageNameProperty);
                return;
            }

            // Already registered from here, or --unregister-on-exit removes it: the note would be wrong.
            if (prior.InstallLocations.Count > 0 || unregisterOnExit || !logger.IsEnabled(LogLevel.Information))
            {
                return;
            }

            ansiConsole.MarkupLineInterpolated(
                $"{UiSymbols.Note} '{singleFile.Name}' stays registered as '{prior.PackageName}' after it exits. Remove it with '{unregisterCommand ?? $"winapp unregister {singleFile.Name}"}', or use --unregister-on-exit.");
        }

        /// <summary>
        /// Builds the <c>winapp unregister</c> command that removes THIS registration, runnable from any
        /// directory.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The bare file name only works from the file's own directory and breaks entirely on a path
        /// containing spaces, so the full path is quoted through <see cref="WindowsCommandLine.JoinArguments"/>.
        /// </para>
        /// <para>
        /// Which form to emit follows what <c>unregister</c> accepts. It re-derives a file-based app's
        /// identity through <c>SingleFileManifestPlanner.ResolvePackageName</c>, which reads an authored
        /// manifest for tiers 2-3 and infers the name for tier 4 — so the <c>.cs</c> form resolves the same
        /// package the run registered, provided the identity-shaping options are repeated. A tier-1
        /// <c>--manifest</c> is the one thing it cannot re-derive, and <c>unregister</c> REJECTS an input
        /// alongside <c>--manifest</c> (they can name different packages) as well as the resolution options
        /// without one. That case therefore emits the manifest form alone.
        /// </para>
        /// <para>
        /// A secret-looking property value is masked with the same policy the build echo uses, so the
        /// notice cannot copy a credential into a CI log. The caller tells the user to restore it.
        /// </para>
        /// </remarks>
        private static string BuildUnregisterCommand(
            FileInfo singleFile,
            string configuration,
            string? archOption,
            string? runtimeOption,
            IReadOnlyList<string> properties,
            FileInfo? explicitManifest,
            DirectoryInfo? outputAppXDirectory,
            DirectoryInfo effectiveLayout)
        {
            var tokens = new List<string> { "winapp", "unregister" };

            if (explicitManifest != null)
            {
                tokens.Add("--manifest");
                tokens.Add(explicitManifest.FullName);
            }
            else
            {
                tokens.Add(singleFile.FullName);

                if (!string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase))
                {
                    tokens.Add("-c");
                    tokens.Add(configuration);
                }

                // --runtime's architecture beats --arch, so repeat whichever the user actually passed.
                if (!string.IsNullOrWhiteSpace(runtimeOption))
                {
                    tokens.Add("-r");
                    tokens.Add(runtimeOption);
                }
                else if (!string.IsNullOrWhiteSpace(archOption))
                {
                    tokens.Add("--arch");
                    tokens.Add(archOption);
                }

                // A command-line property overrides the file's own #:property directives, so one that
                // shaped the identity has to be repeated for the same name to come back out.
                foreach (var property in properties)
                {
                    tokens.Add("-p");
                    tokens.Add(ProjectRunService.RedactSecretPropertyForDisplay(property));
                }
            }

            // Names the layout rather than the identity, so it applies to both forms. `unregister` refuses
            // to remove a package whose install location it cannot tie to something the caller named. From
            // a manifest it trusts only the manifest's own directory and the current directory, and from a
            // .cs it trusts the verified bin\<config> build root — which a nonstandard OutputPath can
            // prevent it from resolving at all. Naming the layout outright is the one thing that always
            // holds, so the printed command works instead of safety-skipping and exiting 0.
            tokens.Add("--output-appx-directory");
            tokens.Add((outputAppXDirectory ?? effectiveLayout).FullName);

            return WindowsCommandLine.JoinArguments(tokens)
                ?? $"winapp unregister {singleFile.FullName}";
        }

        /// <summary>
        /// True when two paths refer to the same directory, or when one contains the other. A registered
        /// package's install location is the AppX layout NESTED inside the build output, so a plain equality
        /// check would report every re-run as a cross-location collision.
        /// </summary>
        private static bool PathsPointToSameLocation(string first, string second)
        {
            static string Normalize(string path) =>
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            try
            {
                var a = Normalize(first);
                var b = Normalize(second);
                return a.Equals(b, StringComparison.OrdinalIgnoreCase)
                    || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
    }
}

