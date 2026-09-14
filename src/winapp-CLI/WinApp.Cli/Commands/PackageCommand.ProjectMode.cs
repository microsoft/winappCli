// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Parsing;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class PackageCommand
{
    public partial class Handler
    {
        /// <summary>
        /// Logs a user-facing error and returns exit code 1. Consolidates the project-mode
        /// error path (before/after the build, where no status spinner is active).
        /// </summary>
        private int Fail(string message)
        {
            logger.LogError("{UISymbol} {Message}", UiSymbols.Error, message);
            return 1;
        }

        /// <summary>
        /// The actionable error when a project resolves to a packageable-layout dead end: it publishes
        /// as an unpackaged (<c>WindowsPackageType=None</c>) app with no MSIX manifest to package.
        /// </summary>
        private static string UnpackagedProjectMessage(string csprojName)
            => $"'{csprojName}' publishes as an unpackaged app (WindowsPackageType=None), which has no MSIX " +
               "manifest to package. To create an MSIX, configure the project as a packaged WinUI app " +
               "(EnableMsixTooling=true with a Package.appxmanifest), or package a pre-built layout folder " +
               "that contains an AppxManifest.xml.";

        /// <summary>
        /// Returns an actionable scope error when an explicit <c>-p</c> requests a packaging flow project
        /// mode does not expose — Store-upload archives (<c>.msixupload</c>) or resource-split bundles
        /// (language/scale resource packages) — or <c>null</c> when the properties are in scope. Project
        /// mode delivers a single <c>.msix</c> or an architecture-only <c>.msixbundle</c>; failing here
        /// stops it from producing an architecture-only package that looks like the requested Store or
        /// resource-split artifact (spec §2, §9.4, §12).
        /// </summary>
        private static string? DetectUnsupportedScopeRequest(IReadOnlyList<string> properties)
        {
            foreach (var property in properties)
            {
                var separator = property.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var name = property[..separator].Trim();
                var value = property[(separator + 1)..].Trim();

                // UapAppxPackageBuildMode selects the Store-upload output. StoreUpload/CI/StoreAndSideload all
                // emit a .msixupload archive, which project mode does not produce.
                if (name.Equals("UapAppxPackageBuildMode", StringComparison.OrdinalIgnoreCase) &&
                    (value.Equals("StoreUpload", StringComparison.OrdinalIgnoreCase) ||
                     value.Equals("CI", StringComparison.OrdinalIgnoreCase) ||
                     value.Equals("StoreAndSideload", StringComparison.OrdinalIgnoreCase)))
                {
                    return $"Store-upload packaging (-p UapAppxPackageBuildMode={value}) is not supported by " +
                           "project mode, which produces a .msix or an architecture-only .msixbundle. Run the " +
                           "native SDK packaging command directly (for example 'dotnet publish' / 'msbuild' " +
                           "with the Windows SDK MSIX targets) to produce a .msixupload archive.";
                }

                // Customizing the auto resource-package qualifiers requests language/scale resource-package
                // splitting; project-mode bundles are architecture-only.
                if (name.Equals("AppxBundleAutoResourcePackageQualifiers", StringComparison.OrdinalIgnoreCase))
                {
                    return "Resource-split bundling (-p AppxBundleAutoResourcePackageQualifiers) is not " +
                           "supported by project mode, which builds architecture-only .msixbundle artifacts. " +
                           "Run the native SDK packaging command directly to split languages/scales into " +
                           "resource packages.";
                }

                // winapp owns bundle production through --arch (each architecture is published separately and
                // composed into one .msixbundle). An explicit SDK AppxBundle request other than Never would be
                // silently overridden by winapp's per-slice AppxBundle=Never, so reject it rather than produce
                // a different artifact than asked for.
                if (name.Equals("AppxBundle", StringComparison.OrdinalIgnoreCase) &&
                    !value.Equals("Never", StringComparison.OrdinalIgnoreCase))
                {
                    return $"-p AppxBundle={value} is not supported by project mode. Use repeated --arch to " +
                           "produce an architecture .msixbundle, or run the native SDK packaging command " +
                           "directly for the SDK's own bundle behavior.";
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the value of a lone explicit <c>-p RuntimeIdentifier=&lt;rid&gt;</c>, or <c>null</c> when none
        /// is present. Property packing that could smuggle a <c>RuntimeIdentifier</c> segment is already
        /// rejected upstream by <see cref="MsBuildPropertyValidator"/>, so each property carries at most one
        /// name; a leading/trailing-space padded name is tolerated the same way the forwarding filter does.
        /// </summary>
        private static string? TryGetLoneRuntimeIdentifier(IReadOnlyList<string> properties)
        {
            foreach (var property in properties)
            {
                var separator = property.IndexOf('=');
                if (separator > 0 &&
                    property[..separator].Trim().Equals("RuntimeIdentifier", StringComparison.OrdinalIgnoreCase))
                {
                    return property[(separator + 1)..].Trim();
                }
            }

            return null;
        }
        /// <c>winapp run</c>, then package its build output (<c>TargetDir</c>) through the existing
        /// MSIX pipeline. Folder/bundle/sparse inputs never reach here.
        /// </summary>
        private async Task<int> RunProjectModeAsync(ParseResult parseResult, FileInfo csproj, CancellationToken cancellationToken)
        {
            if (!csproj.Exists)
            {
                return Fail($"Project file not found: {csproj.FullName}");
            }

            // Project-mode build inputs.
            var configuration = parseResult.GetValue(ConfigurationOption) ?? "Release";
            var archInputs = parseResult.GetValue(ArchOption) ?? [];

            // Reject a valueless --arch. Like --property it uses ZeroOrMore arity, so a bare '--arch' (no
            // value) parses without raising an arity error; detect it from the raw result (more identifier
            // tokens than captured values) so it fails loudly instead of silently defaulting to the host arch.
            if (parseResult.GetResult(ArchOption) is OptionResult archResult &&
                archResult.IdentifierTokenCount > archResult.Tokens.Count)
            {
                return Fail("A --arch option was provided without a value. Expected x64, arm64, or x86 (for example: --arch arm64).");
            }

            var noBuild = parseResult.GetValue(NoBuildOption);
            var noRestore = parseResult.GetValue(NoRestoreOption);
            var properties = parseResult.GetValue(PropertyOption) ?? [];

            // Packaging options (reused as-is by the MSIX pipeline).
            var output = parseResult.GetValue(OutputOption);
            var name = parseResult.GetValue(NameOption);
            var skipPri = parseResult.GetValue(SkipPriOption);
            var certPath = parseResult.GetValue(CertOption);
            var certPassword = parseResult.GetRequiredValue(CertPasswordOption);
            var generateCert = parseResult.GetValue(GenerateCertOption);
            var noSign = parseResult.GetValue(NoSignOption);
            // Signing is resolved to one policy: --no-sign explicitly requests an unsigned artifact and is
            // mutually exclusive with an explicit signing request (spec §6), not last-one-wins.
            if (noSign && (certPath != null || generateCert))
            {
                return Fail("--no-sign cannot be combined with --cert or --generate-cert.");
            }
            var installCert = parseResult.GetValue(InstallCertOption);
            var publisher = parseResult.GetValue(PublisherOption);
            var manifestPath = parseResult.GetValue(ManifestOption);
            var selfContainedFlag = parseResult.GetValue(SelfContainedOption);
            var executable = parseResult.GetValue(ExecutableOption);

            // A single .csproj produces one .msix; two or more architectures produce one .msixbundle. Resolve
            // the requested architectures (each canonicalized to x64/arm64/x86) and reject duplicates before
            // validating the --output extension against the artifact type.
            var resolvedArches = new List<string>();

            // A lone explicit -p RuntimeIdentifier (no --arch) is an exact-RID override: honor it unchanged
            // and derive the target architecture from it rather than defaulting to the process architecture
            // (spec §4). With --arch present it is a conflicting target selection, rejected further below.
            string? exactRid = archInputs.Length == 0 ? TryGetLoneRuntimeIdentifier(properties) : null;

            if (archInputs.Length == 0)
            {
                if (exactRid is { Length: > 0 })
                {
                    if (RunArchHelper.ArchitectureFromRid(exactRid) is not { } ridArch)
                    {
                        return Fail($"-p RuntimeIdentifier={exactRid} is not a supported Windows runtime identifier. " +
                                    "Use a win-x64, win-arm64, or win-x86 RID (a version qualifier such as win10-x64 is " +
                                    "allowed), or select the architecture with --arch.");
                    }
                    resolvedArches.Add(ridArch);
                }
                else if (!RunArchHelper.TryResolveArchitecture(null, runtimeOption: null, out var defaultArch, out var defaultErr))
                {
                    return Fail(defaultErr!);
                }
                else
                {
                    resolvedArches.Add(defaultArch);
                }
            }
            else
            {
                foreach (var archInput in archInputs)
                {
                    if (!RunArchHelper.TryResolveArchitecture(archInput, runtimeOption: null, out var resolvedArch, out var archErr))
                    {
                        return Fail(archErr!);
                    }
                    if (resolvedArches.Contains(resolvedArch))
                    {
                        return Fail($"Duplicate --arch '{resolvedArch}'. Pass each architecture at most once.");
                    }
                    resolvedArches.Add(resolvedArch);
                }
            }
            var isBundle = resolvedArches.Count >= 2;

            var outputExtensionError = ValidateOutputExtension(output, isBundle);
            if (outputExtensionError != null)
            {
                logger.LogError("{Message}", outputExtensionError);
                return 1;
            }

            // Resolve the explicit effective framework once (--framework > bare -p:TargetFramework) so the
            // build and evaluation share the same TFM.
            var framework = ProjectRunService.ResolveExplicitFramework(parseResult.GetValue(FrameworkOption), properties);

            // Reject a valueless -p/--property. The option uses ZeroOrMore arity so a bare '-p' (no
            // Name=Value) parses without a value instead of raising a System.CommandLine arity error;
            // detect it from the raw result (more identifier tokens than captured values means at least
            // one '-p' had no argument) and reject it the same way `winapp run` does.
            if (parseResult.GetResult(PropertyOption) is OptionResult propertyResult &&
                propertyResult.IdentifierTokenCount > propertyResult.Tokens.Count)
            {
                return Fail("A --property/-p option was provided without a value. Expected Name=Value (for example: -p WindowsPackageType=None).");
            }

            // Reject malformed -p values (missing '=', or ';'/',' packing that would smuggle a dedicated-flag
            // property past the name-only ForwardableProperties filter) using the same validator winapp run uses.
            if (MsBuildPropertyValidator.Validate(properties) is { } propertyError)
            {
                return Fail(propertyError);
            }

            // AppxPackageDir is winapp's internal package-staging location; the public destination selector
            // is --output. Reject a competing -p AppxPackageDir rather than silently overriding it.
            if (properties.Any(p => p.StartsWith("AppxPackageDir=", StringComparison.OrdinalIgnoreCase)))
            {
                return Fail("-p AppxPackageDir is not supported. Use --output to choose the package destination.");
            }

            // --arch is the dedicated target selector. A simultaneous explicit -p RuntimeIdentifier is a
            // conflicting target selection (a -p RuntimeIdentifier alone remains an advanced exact-RID
            // override). Use the same trimmed property-name parsing as the lone-RID path so a whitespace-
            // padded name (e.g. -p " RuntimeIdentifier=win-x64") cannot slip past the conflict check.
            if (archInputs.Length > 0 && TryGetLoneRuntimeIdentifier(properties) is not null)
            {
                return Fail("--arch conflicts with an explicit -p RuntimeIdentifier. Use --arch alone to select the architecture, or pass -p RuntimeIdentifier alone for an exact-RID override.");
            }

            // Project mode produces .msix or architecture-only .msixbundle artifacts. It does not expose the
            // SDK's Store-upload (.msixupload) or resource-split (language/scale resource packages) flows.
            // Fail an explicit request for one of these rather than quietly producing an architecture-only
            // package that looks equivalent (spec §2, §9.4, §12).
            if (DetectUnsupportedScopeRequest(properties) is { } scopeError)
            {
                return Fail(scopeError);
            }

            // Single-package mode targets one architecture; the bundle path drives each slice's own.
            var architecture = resolvedArches[0];

            // Immediate context line so the pre-build dotnet steps don't look hung.
            if (logger.IsEnabled(LogLevel.Information))
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Search} {csproj.Name}  ·  {configuration} | {(isBundle ? string.Join(", ", resolvedArches) : architecture)}");
            }

            // A capable SDK (>= 8.0.100) is required for MSBuild --getProperty.
            var workingDir = csproj.Directory ?? new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());
            var sdkError = await projectRunService.CheckSdkAsync(workingDir, cancellationToken);
            if (sdkError != null)
            {
                return Fail(sdkError);
            }

            // Discover the owning solution (if any) via the explicit-file branch so the build defines
            // $(SolutionDir); this does NOT enable .sln/directory inputs.
            FileInfo? solution;
            try
            {
                var inputResolution = await projectRunService.ResolveInputAsync(csproj, cancellationToken);
                solution = inputResolution.Solution;
            }
            catch (ProjectRunException ex)
            {
                return Fail(ex.Message);
            }

            var buildOptions = new ProjectRunOptions(configuration, architecture, framework, noBuild, noRestore, properties, Json: false, Solution: solution, ExactRuntimeIdentifier: exactRid);

            // Resolve one signing policy up front (CLI overrides project config; reject unsupported project
            // signing rather than silently ignoring it) and reuse it for the single package or every slice.
            var (resolvedPolicy, signingError) = await ResolveSigningPolicyAsync(
                csproj, buildOptions, noSign, certPath, generateCert, installCert, certPassword, cancellationToken);
            if (signingError != null)
            {
                return Fail(signingError);
            }
            var signing = resolvedPolicy!.Value;

            // Two or more architectures: publish/package each as an unsigned slice, then compose one signed
            // architecture .msixbundle (spec §8). Single-package mode continues below.
            if (isBundle)
            {
                return await RunProjectBundleModeAsync(
                    csproj, resolvedArches, buildOptions, output, name, publisher,
                    signing, selfContainedFlag, manifestPath, executable, skipPri, cancellationToken);
            }

            // Fast-fail: an unpackaged app can never be packaged. Reject before paying the publish cost
            // when the project is definitively WindowsPackageType=None (skipped under --no-build).
            if (!noBuild && await projectRunService.IsDefinitivelyUnpackagedAsync(csproj, buildOptions, cancellationToken))
            {
                return Fail(UnpackagedProjectMessage(csproj.Name));
            }

            // MSIX-tooling projects (WinUI / EnableMsixTooling): let the Windows App SDK's own MSIX targets
            // produce the package during publish, then sign and deliver it. The SDK owns file selection and
            // Native AOT native/managed filtering, so winapp never repackages the output. A native project
            // that fails to package is reported as-is — never a silent fall back to generic packaging.
            if (await projectRunService.IsNativeMsixProjectAsync(csproj, buildOptions, cancellationToken))
            {
                if (manifestPath != null)
                {
                    return Fail("--manifest is not supported for an MSIX-tooling project; configure the <AppxManifest> item in the project instead.");
                }
                if (executable != null)
                {
                    return Fail("--executable is not supported for an MSIX-tooling project; the SDK resolves the entry point from the project.");
                }
                if (skipPri)
                {
                    return Fail("--skip-pri is not supported for an MSIX-tooling project; the project's own resource build controls PRI generation.");
                }

                var packageStagingDir = new DirectoryInfo(Path.Join(Path.GetTempPath(), $"winapp-native-pack-{Guid.NewGuid():N}"));
                packageStagingDir.Create();
                try
                {
                    // --self-contained sets WindowsAppSDKSelfContained=true BEFORE publish so the SDK bundles
                    // the Windows App SDK runtime into the package (spec §4/§7). Unlike the generic path, there
                    // is no post-publish runtime injection — the SDK owns it. Absence leaves the project setting.
                    var nativeOptions = selfContainedFlag
                        ? buildOptions with { Properties = [.. properties, "WindowsAppSDKSelfContained=true"] }
                        : buildOptions;

                    NativeMsixPublishOutcome nativeOutcome;
                    try
                    {
                        nativeOutcome = await projectRunService.PublishNativeMsixAsync(csproj, nativeOptions, packageStagingDir, cancellationToken);
                    }
                    catch (ProjectRunException ex)
                    {
                        return Fail(ex.Message);
                    }

                    if (nativeOutcome.PackagePath is null)
                    {
                        // Native packaging failed — dotnet already surfaced diagnostics. Propagate its exit code.
                        return nativeOutcome.ExitCode == 0 ? 1 : nativeOutcome.ExitCode;
                    }

                    return await statusService.ExecuteWithStatusAsync("Delivering MSIX package...", async (taskContext, ct) =>
                    {
                        try
                        {
                            var result = await msixService.DeliverNativeMsixAsync(
                                nativeOutcome.PackagePath, output, name, taskContext,
                                signing.ShouldSign, signing.Cert, signing.CertPassword, signing.GenerateCert,
                                signing.InstallCert, publisher, signing.TimestampUrl, ct);

                            taskContext.AddStatusMessage($"{UiSymbols.Package} Package: {result.MsixPath}");
                            if (result.Signed)
                            {
                                taskContext.AddStatusMessage($"{UiSymbols.Lock} Package has been signed");
                            }

                            return (0, "MSIX package creation completed.");
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"Stack Trace: {ex.StackTrace}");
                            return (1, $"{UiSymbols.Error} Failed to create MSIX package: {ex.GetBaseException().Message}");
                        }
                    }, cancellationToken);
                }
                finally
                {
                    try
                    {
                        packageStagingDir.Refresh();
                        if (packageStagingDir.Exists)
                        {
                            packageStagingDir.Delete(recursive: true);
                        }
                    }
                    catch
                    {
                        // Best-effort cleanup of the native packaging scratch directory.
                    }
                }
            }

            // Generic publish-layout path: for a project without active MSIX tooling, publish and package the
            // deployment payload (PublishDir) with a resolved distribution manifest. This ALSO serves as the
            // intentional fallback when native detection is indeterminate (IsNativeMsixProjectAsync returned
            // false because the cheap evaluate could not run): the resolver's recipe-aware TargetDir handling
            // still packages an MSIX-tooling app correctly rather than failing. Do not remove that recipe
            // handling as "dead code" — it is the safety net for the detection-failure case.
            ProjectBuildOutcome outcome;
            try
            {
                outcome = await projectRunService.PublishAndResolveAsync(csproj, buildOptions, cancellationToken);
            }
            catch (ProjectRunException ex)
            {
                return Fail(ex.Message);
            }

            if (outcome.Resolution is null)
            {
                // Publish failed — dotnet already surfaced its diagnostics. Propagate its exit code.
                return outcome.ExitCode == 0 ? 1 : outcome.ExitCode;
            }

            var resolution = outcome.Resolution;

            // Authoritative packaging gate for the indeterminate case the fast-fail could not pre-judge.
            if (resolution.Packaging == ProjectPackaging.Unpackaged)
            {
                return Fail(UnpackagedProjectMessage(csproj.Name));
            }

            var targetDir = new DirectoryInfo(resolution.TargetDir);

            // Prefer an explicit --manifest, then the MSBuild-evaluated packaged manifest
            // (FinalAppxManifestName), then a manifest discovered in the packaging output. For an
            // MSIX-tooling app the evaluated manifest and its .appxrecipe live in the build output that
            // resolution.TargetDir already points at, so recipe-based staging in MsixService assembles the
            // full package (manifest + compiled XAML + source-tree assets).
            var effectiveManifest = manifestPath
                ?? (resolution.AppxManifestPath is { Length: > 0 } resolvedManifest && File.Exists(resolvedManifest)
                    ? new FileInfo(resolvedManifest)
                    : null);

            // Guardrail: packaged (per the evaluated WindowsPackageType) but no manifest anywhere is a
            // misconfiguration — surface it clearly rather than the generic pipeline error.
            if (effectiveManifest == null && !ManifestHelper.FindManifest(targetDir.FullName).Exists)
            {
                var message = noBuild
                    ? $"'{csproj.Name}' resolves to a packaged (MSIX) app but no AppxManifest.xml was found in the packaging output ({targetDir.FullName}). " +
                      "Remove --no-build to re-publish the packaged layout, or point at an up-to-date build."
                    : $"'{csproj.Name}' resolves to a packaged (MSIX) app but no AppxManifest.xml was found in the packaging output ({targetDir.FullName}). " +
                      "Ensure the project is a packaged WinUI app (EnableMsixTooling=true with a Package.appxmanifest).";
                return Fail(message);
            }

            // Self-contained model reconciliation (§6.2): the project may already be built self-contained
            // (resolution.SelfContained) and/or the user may pass --self-contained. Package the layout with
            // the correct manifest/runtime model, and never re-bundle a runtime the build already produced.
            var selfContainedModel = resolution.SelfContained || selfContainedFlag;
            var runtimeAlreadyBundled = resolution.SelfContained;
            if (resolution.SelfContained && selfContainedFlag && logger.IsEnabled(LogLevel.Information))
            {
                ansiConsole.MarkupLineInterpolated(
                    $"{UiSymbols.Info} '{csproj.Name}' is already built self-contained; --self-contained is redundant and the runtime will not be re-bundled.");
            }

            // Read the Windows App SDK dependency from the graph this build actually produced (its evaluated
            // project.assets.json + RID). Without it, package discovery re-runs `dotnet package list`, which
            // re-evaluates with the default configuration/RID and can pick the wrong graph for a project with
            // configuration/RID-conditional package references. Mirrors `winapp run` project mode.
            var packageGraph = string.IsNullOrWhiteSpace(resolution.ProjectAssetsFile)
                ? null
                : new PackageGraphSource(new FileInfo(resolution.ProjectAssetsFile), resolution.ProjectAssetsRuntimeIdentifier);

            return await statusService.ExecuteWithStatusAsync("Creating MSIX package...", async (taskContext, ct) =>
            {
                try
                {
                    var result = await msixService.CreateMsixPackageAsync(
                        targetDir, output, taskContext, name, skipPri, signing.ShouldSign, signing.Cert, signing.CertPassword,
                        signing.GenerateCert, signing.InstallCert, publisher, effectiveManifest, selfContainedModel, executable,
                        projectFile: csproj,
                        framework: resolution.Framework,
                        noRestore: resolution.NoRestore,
                        packageGraph: packageGraph,
                        targetArch: resolution.Architecture,
                        runtimeAlreadyBundled: runtimeAlreadyBundled,
                        timestampUrl: signing.TimestampUrl,
                        cancellationToken: ct);

                    taskContext.AddStatusMessage($"{UiSymbols.Package} Package: {result.MsixPath}");
                    if (result.Signed)
                    {
                        taskContext.AddStatusMessage($"{UiSymbols.Lock} Package has been signed");
                    }

                    return (0, "MSIX package creation completed.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    taskContext.AddDebugMessage($"Stack Trace: {ex.StackTrace}");
                    return (1, $"{UiSymbols.Error} Failed to create MSIX package: {ex.GetBaseException().Message}");
                }
            }, cancellationToken);
        }

        /// <summary>
        /// One resolved signing policy for the final artifact: whether to sign and with which certificate,
        /// password, and timestamp server. Bundles and single packages sign once using this.
        /// </summary>
        private readonly record struct SigningPolicy(
            bool ShouldSign, FileInfo? Cert, string CertPassword, bool GenerateCert, bool InstallCert, string? TimestampUrl);

        /// <summary>
        /// Resolves the single signing policy (spec §6): <c>--no-sign</c> → unsigned; an explicit
        /// <c>--cert</c>/<c>--generate-cert</c> → sign with the CLI certificate (overriding project config);
        /// otherwise honor the project's <c>PackageCertificateKeyFile</c>. Reports an actionable error for a
        /// selected-but-unsupported policy (certificate-store thumbprint / cloud) or an enabled policy with
        /// no usable certificate, rather than silently ignoring it or downgrading to unsigned.
        /// </summary>
        private async Task<(SigningPolicy? Policy, string? Error)> ResolveSigningPolicyAsync(
            FileInfo csproj,
            ProjectRunOptions buildOptions,
            bool noSign,
            FileInfo? certPath,
            bool generateCert,
            bool installCert,
            string certPassword,
            CancellationToken cancellationToken)
        {
            var unsigned = new SigningPolicy(false, null, certPassword, false, false, null);

            if (noSign)
            {
                return (unsigned, null);
            }
            if (certPath != null || generateCert)
            {
                // An explicit CLI signing request overrides any project signing configuration.
                return (new SigningPolicy(true, certPath, certPassword, generateCert, installCert, null), null);
            }

            var props = await projectRunService.EvaluateProjectSigningAsync(csproj, buildOptions, cancellationToken);
            if (props is null || props.SigningEnabled == false)
            {
                // Could not evaluate, or signing explicitly disabled → deliver unsigned.
                return (unsigned, null);
            }

            if (!string.IsNullOrEmpty(props.KeyFilePath))
            {
                // A project-supplied keyfile path is untrusted input. Probing a UNC / mapped-network-drive /
                // reparse-redirected path with File.Exists can trigger outbound SMB authentication, so reject
                // a network location before touching the filesystem.
                if (PathSafety.IsNetworkPath(props.KeyFilePath)
                    || PathSafety.IsNetworkDriveRoot(props.KeyFilePath)
                    || PathSafety.RedirectsToNetwork(props.KeyFilePath))
                {
                    return (null, $"The project's signing certificate (PackageCertificateKeyFile) resolves to a network location, which winapp will not probe or load: {props.KeyFilePath}. Provide a local --cert <pfx>, or use --no-sign.");
                }
                if (!File.Exists(props.KeyFilePath))
                {
                    return (null, $"The project's signing certificate (PackageCertificateKeyFile) was not found: {props.KeyFilePath}. Provide --cert <pfx>, fix the project configuration, or use --no-sign.");
                }
                // Project PFX signing: an absent PackageCertificatePassword means no password (not the CLI default).
                return (new SigningPolicy(true, new FileInfo(props.KeyFilePath), props.Password ?? string.Empty, false, installCert, props.TimestampUrl), null);
            }
            if (!string.IsNullOrEmpty(props.Thumbprint))
            {
                return (null, "This project signs with a certificate-store certificate (PackageCertificateThumbprint), which winapp project mode does not support yet. Sign with --cert <pfx>, or pass --no-sign and use your existing signing pipeline.");
            }
            if (props.SigningEnabled == true)
            {
                return (null, "AppxPackageSigningEnabled=true but no PackageCertificateKeyFile is configured. Provide --cert <pfx>, configure a project signing certificate, or use --no-sign.");
            }

            return (unsigned, null);
        }

        /// <summary>
        /// Publishes and packages every requested architecture as an unsigned slice, then composes one
        /// signed architecture .msixbundle (spec §8). Each slice uses the same publish/packaging path as
        /// single-package mode (native SDK packaging or generic publish layout).
        /// </summary>
        private async Task<int> RunProjectBundleModeAsync(
            FileInfo csproj,
            List<string> arches,
            ProjectRunOptions baseOptions,
            FileInfo? output,
            string? name,
            string? publisher,
            SigningPolicy signing,
            bool selfContainedFlag,
            FileInfo? manifestPath,
            string? executable,
            bool skipPri,
            CancellationToken cancellationToken)
        {
            var bundleStagingDir = new DirectoryInfo(Path.Join(Path.GetTempPath(), $"winapp-bundle-{Guid.NewGuid():N}"));
            bundleStagingDir.Create();
            try
            {
                var sliceMsixFiles = new List<FileInfo>();
                for (var i = 0; i < arches.Count; i++)
                {
                    var arch = arches[i];
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Package} Packaging {arch} slice ({i + 1} of {arches.Count})...");
                    }

                    var sliceDir = bundleStagingDir.CreateSubdirectory($"slice-{arch}");
                    var sliceOptions = baseOptions with { Architecture = arch };
                    var (sliceMsix, exitCode, error) = await ProduceProjectSliceAsync(
                        csproj, sliceOptions, sliceDir, selfContainedFlag, manifestPath, executable, skipPri, cancellationToken);

                    if (error != null)
                    {
                        return Fail(error);
                    }
                    if (sliceMsix == null)
                    {
                        return exitCode == 0 ? 1 : exitCode;
                    }
                    sliceMsixFiles.Add(sliceMsix);
                }

                var autoSign = signing.ShouldSign;
                return await statusService.ExecuteWithStatusAsync("Creating MSIX bundle...", async (taskContext, ct) =>
                {
                    try
                    {
                        var result = await msixService.CreateBundleFromPackagesAsync(
                            sliceMsixFiles, output, name, taskContext,
                            autoSign, signing.Cert, signing.CertPassword, signing.GenerateCert, signing.InstallCert,
                            publisher, signing.TimestampUrl, ct);

                        taskContext.AddStatusMessage($"{UiSymbols.Package} Bundle: {result.BundlePath}");
                        if (result.Signed)
                        {
                            taskContext.AddStatusMessage($"{UiSymbols.Lock} Bundle has been signed");
                        }
                        return (0, "MSIX bundle creation completed.");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        taskContext.AddDebugMessage($"Stack Trace: {ex.StackTrace}");
                        return (1, $"{UiSymbols.Error} Failed to create MSIX bundle: {ex.GetBaseException().Message}");
                    }
                }, cancellationToken);
            }
            finally
            {
                try
                {
                    bundleStagingDir.Refresh();
                    if (bundleStagingDir.Exists)
                    {
                        bundleStagingDir.Delete(recursive: true);
                    }
                }
                catch
                {
                    // Best-effort cleanup of the bundle staging directory.
                }
            }
        }

        /// <summary>
        /// Produces one UNSIGNED package for a single architecture slice into <paramref name="sliceDir"/>,
        /// using native SDK packaging for an MSIX-tooling project or the generic publish-layout path
        /// otherwise. Returns the produced .msix, or an exit code / actionable error on failure.
        /// </summary>
        private async Task<(FileInfo? Msix, int ExitCode, string? Error)> ProduceProjectSliceAsync(
            FileInfo csproj,
            ProjectRunOptions sliceOptions,
            DirectoryInfo sliceDir,
            bool selfContainedFlag,
            FileInfo? manifestPath,
            string? executable,
            bool skipPri,
            CancellationToken cancellationToken)
        {
            if (await projectRunService.IsNativeMsixProjectAsync(csproj, sliceOptions, cancellationToken))
            {
                if (manifestPath != null)
                {
                    return (null, 1, "--manifest is not supported for an MSIX-tooling project; configure the <AppxManifest> item in the project instead.");
                }
                if (executable != null)
                {
                    return (null, 1, "--executable is not supported for an MSIX-tooling project; the SDK resolves the entry point from the project.");
                }
                if (skipPri)
                {
                    return (null, 1, "--skip-pri is not supported for an MSIX-tooling project; the project's own resource build controls PRI generation.");
                }

                var nativeOptions = selfContainedFlag
                    ? sliceOptions with { Properties = [.. sliceOptions.Properties, "WindowsAppSDKSelfContained=true"] }
                    : sliceOptions;
                try
                {
                    var outcome = await projectRunService.PublishNativeMsixAsync(csproj, nativeOptions, sliceDir, cancellationToken);
                    return (outcome.PackagePath, outcome.ExitCode, null);
                }
                catch (ProjectRunException ex)
                {
                    return (null, 1, ex.Message);
                }
            }

            // Generic publish-layout slice.
            ProjectBuildOutcome outcome2;
            try
            {
                outcome2 = await projectRunService.PublishAndResolveAsync(csproj, sliceOptions, cancellationToken);
            }
            catch (ProjectRunException ex)
            {
                return (null, 1, ex.Message);
            }
            if (outcome2.Resolution is null)
            {
                return (null, outcome2.ExitCode == 0 ? 1 : outcome2.ExitCode, null);
            }
            var resolution = outcome2.Resolution;
            if (resolution.Packaging == ProjectPackaging.Unpackaged)
            {
                return (null, 1, UnpackagedProjectMessage(csproj.Name));
            }

            var targetDir = new DirectoryInfo(resolution.TargetDir);
            var effectiveManifest = manifestPath
                ?? (resolution.AppxManifestPath is { Length: > 0 } resolvedManifest && File.Exists(resolvedManifest)
                    ? new FileInfo(resolvedManifest)
                    : null);
            if (effectiveManifest == null && !ManifestHelper.FindManifest(targetDir.FullName).Exists)
            {
                return (null, 1, $"'{csproj.Name}' resolves to a packaged (MSIX) app but no AppxManifest.xml was found in the packaging output ({targetDir.FullName}).");
            }

            var selfContainedModel = resolution.SelfContained || selfContainedFlag;
            var runtimeAlreadyBundled = resolution.SelfContained;
            var packageGraph = string.IsNullOrWhiteSpace(resolution.ProjectAssetsFile)
                ? null
                : new PackageGraphSource(new FileInfo(resolution.ProjectAssetsFile), resolution.ProjectAssetsRuntimeIdentifier);

            FileInfo? producedMsix = null;
            var exit = await statusService.ExecuteWithStatusAsync($"Packaging {resolution.Architecture} slice...", async (taskContext, ct) =>
            {
                try
                {
                    var result = await msixService.CreateMsixPackageAsync(
                        targetDir, sliceDir, taskContext, packageName: null, skipPri, autoSign: false, certificatePath: null,
                        certificatePassword: "password", generateDevCert: false, installDevCert: false, publisher: null,
                        effectiveManifest, selfContainedModel, executable,
                        projectFile: csproj, framework: resolution.Framework, noRestore: resolution.NoRestore,
                        packageGraph: packageGraph, targetArch: resolution.Architecture,
                        runtimeAlreadyBundled: runtimeAlreadyBundled, cancellationToken: ct);
                    producedMsix = result.MsixPath;
                    return (0, "Slice packaged.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return (1, ex.GetBaseException().Message);
                }
            }, cancellationToken);

            return exit == 0 ? (producedMsix, 0, null) : (null, exit, null);
        }
    }
}
