// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
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
        /// The actionable error when a project resolves to a packageable-layout dead end: it builds
        /// as an unpackaged (<c>WindowsPackageType=None</c>) app with no MSIX manifest to package.
        /// </summary>
        private static string UnpackagedProjectMessage(string csprojName)
            => $"'{csprojName}' builds as an unpackaged app (WindowsPackageType=None), which has no MSIX " +
               "manifest to package. To create an MSIX, configure the project as a packaged WinUI app " +
               "(EnableMsixTooling=true with a Package.appxmanifest), or package a pre-built layout folder " +
               "that contains an AppxManifest.xml.";

        /// <summary>
        /// Project mode: build the resolved <c>.csproj</c> using the same infrastructure as
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
            var configuration = parseResult.GetValue(ConfigurationOption) ?? "Debug";
            var archOption = parseResult.GetValue(ArchOption);
            var runtimeOption = parseResult.GetValue(RuntimeOption);
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
            var installCert = parseResult.GetValue(InstallCertOption);
            var publisher = parseResult.GetValue(PublisherOption);
            var manifestPath = parseResult.GetValue(ManifestOption);
            var selfContainedFlag = parseResult.GetValue(SelfContainedOption);
            var executable = parseResult.GetValue(ExecutableOption);

            // A single .csproj produces one .msix; reject a .msixbundle --output before building.
            var outputExtensionError = ValidateOutputExtension(output, isBundle: false);
            if (outputExtensionError != null)
            {
                logger.LogError("{Message}", outputExtensionError);
                return 1;
            }

            // Resolve the explicit effective framework once (--framework > bare -p:TargetFramework) so the
            // build and evaluation share the same TFM.
            var framework = ProjectRunService.ResolveExplicitFramework(parseResult.GetValue(FrameworkOption), properties);

            // Reject malformed -p values early so they never become a nonsensical MSBuild argument.
            foreach (var property in properties)
            {
                if (property.Contains(';'))
                {
                    var offendingName = property[..property.IndexOfAny(['=', ';'])];
                    return Fail(
                        $"Invalid --property '{offendingName}'. A single -p cannot pack multiple properties with ';'. " +
                        "Pass one property per repeatable -p (for example: -p A=1 -p B=2), or escape a literal ';' in a value as '%3B'.");
                }

                var separator = property.IndexOf('=');
                if (separator <= 0 || string.IsNullOrWhiteSpace(property[..separator]))
                {
                    var shown = separator > 0 ? property[..separator] : (separator == 0 ? "(empty)" : property);
                    return Fail($"Invalid --property '{shown}'. Expected Name=Value (for example: -p Configuration=Release).");
                }
            }

            // Resolve the target architecture: --runtime's arch beats --arch; else the process arch.
            if (!RunArchHelper.TryResolveArchitecture(archOption, runtimeOption, out var architecture, out var archError))
            {
                return Fail(archError!);
            }

            // Immediate context line so the pre-build dotnet steps don't look hung.
            if (logger.IsEnabled(LogLevel.Information))
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Search} {csproj.Name}  ·  {configuration} | {architecture}");
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

            var buildOptions = new ProjectRunOptions(configuration, architecture, framework, noBuild, noRestore, properties, Json: false, Solution: solution);

            // Fast-fail: an unpackaged app can never be packaged. Reject before paying the build cost
            // when the project is definitively WindowsPackageType=None (skipped under --no-build).
            if (!noBuild && await projectRunService.IsDefinitivelyUnpackagedAsync(csproj, buildOptions, cancellationToken))
            {
                return Fail(UnpackagedProjectMessage(csproj.Name));
            }

            ProjectBuildOutcome outcome;
            try
            {
                outcome = await projectRunService.BuildAndResolveAsync(csproj, buildOptions, cancellationToken);
            }
            catch (ProjectRunException ex)
            {
                return Fail(ex.Message);
            }

            if (outcome.Resolution is null)
            {
                // Build failed — dotnet already surfaced its diagnostics. Propagate its exit code.
                return outcome.ExitCode == 0 ? 1 : outcome.ExitCode;
            }

            var resolution = outcome.Resolution;

            // Authoritative packaging gate for the indeterminate case the fast-fail could not pre-judge.
            if (resolution.Packaging == ProjectPackaging.Unpackaged)
            {
                return Fail(UnpackagedProjectMessage(csproj.Name));
            }

            var targetDir = new DirectoryInfo(resolution.TargetDir);

            // Guardrail: packaged (per the evaluated WindowsPackageType) but no manifest in the build
            // output is a misconfiguration — surface it clearly rather than the generic pipeline error.
            if (manifestPath == null && !ManifestHelper.FindManifest(targetDir.FullName).Exists)
            {
                var message = noBuild
                    ? $"'{csproj.Name}' resolves to a packaged (MSIX) app but no AppxManifest.xml was found in the build output ({targetDir.FullName}). " +
                      "Remove --no-build to rebuild the packaged layout, or point at an up-to-date packaged build."
                    : $"'{csproj.Name}' resolves to a packaged (MSIX) app but no AppxManifest.xml was found in the build output ({targetDir.FullName}). " +
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

            return await statusService.ExecuteWithStatusAsync("Creating MSIX package...", async (taskContext, ct) =>
            {
                try
                {
                    var autoSign = certPath != null || generateCert;

                    var result = await msixService.CreateMsixPackageAsync(
                        targetDir, output, taskContext, name, skipPri, autoSign, certPath, certPassword,
                        generateCert, installCert, publisher, manifestPath, selfContainedModel, executable,
                        projectFile: csproj,
                        framework: resolution.Framework,
                        noRestore: resolution.NoRestore,
                        targetArch: resolution.Architecture,
                        runtimeAlreadyBundled: runtimeAlreadyBundled,
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
    }
}
