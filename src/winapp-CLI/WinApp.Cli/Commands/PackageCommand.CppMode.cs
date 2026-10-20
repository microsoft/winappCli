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
        /// C++ (<c>.vcxproj</c>) project mode: builds with Visual Studio's MSBuild (the same build
        /// <c>winapp run</c> uses), then packages the build output folder. A packaged WinUI project supplies
        /// its generated <c>AppxManifest.xml</c> and <c>.appxrecipe</c>; any other application project is
        /// packaged with an <c>appxmanifest.xml</c> from <c>--manifest</c>, the project folder, the build
        /// output, or the current directory, like <c>winapp package &lt;folder&gt;</c>.
        /// </summary>
        private async Task<int> RunCppProjectModeAsync(ParseResult parseResult, FileInfo project, CancellationToken cancellationToken)
        {
            if (!project.Exists)
            {
                return Fail($"Project file not found: {project.FullName}");
            }

            if (parseResult.GetResult(FrameworkOption) is { Implicit: false })
            {
                return Fail($"--framework applies to .NET projects and can't be used with a C++ project ({project.Name}).");
            }

            var archInputs = parseResult.GetValue(ArchOption) ?? [];
            if (parseResult.GetResult(ArchOption) is OptionResult archResult &&
                archResult.IdentifierTokenCount > archResult.Tokens.Count)
            {
                return Fail("A --arch option was provided without a value. Expected x64, arm64, or x86 (for example: --arch arm64).");
            }
            if (archInputs.Length > 1)
            {
                return Fail($"Multi-architecture bundles aren't supported for C++ projects yet. Package one architecture at a time (for example: winapp package {project.Name} --arch arm64), " +
                            "or build each architecture with MSBuild and bundle the output folders: winapp package <x64-folder> <arm64-folder>.");
            }

            var properties = parseResult.GetValue(PropertyOption) ?? [];
            if (parseResult.GetResult(PropertyOption) is OptionResult propertyResult &&
                propertyResult.IdentifierTokenCount > propertyResult.Tokens.Count)
            {
                return Fail("A --property/-p option was provided without a value. Expected Name=Value (for example: -p Platform=ARM64).");
            }
            if (MsBuildPropertyValidator.Validate(properties) is { } propertyError)
            {
                return Fail(propertyError);
            }

            // -p Configuration=Release is how Visual Studio users pick the configuration, so treat it like -c.
            // Both are allowed only when they name the same configuration.
            var configuration = parseResult.GetValue(ConfigurationOption) ?? "Release";
            if (ProjectRunService.CppConfigurationFromProperties(properties) is { } propertyConfiguration)
            {
                if (parseResult.GetResult(ConfigurationOption) is { Implicit: false }
                    && !string.Equals(propertyConfiguration, configuration, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail($"-p Configuration={propertyConfiguration} conflicts with -c {configuration}. Pass only one of them.");
                }
                configuration = propertyConfiguration;
            }

            // --arch wins; otherwise -p Platform=ARM64 selects the architecture, as in Visual Studio. A
            // conflicting --arch and -p Platform pair is rejected by the build.
            string architecture;
            if (archInputs.Length == 0 && ProjectRunService.CppArchitectureFromProperties(properties) is { } platformArch)
            {
                architecture = platformArch;
            }
            else if (!RunArchHelper.TryResolveArchitecture(archInputs.FirstOrDefault(), runtimeOption: null, out architecture, out var archError))
            {
                return Fail(archError!);
            }

            var output = parseResult.GetValue(OutputOption);
            if (ValidateOutputExtension(output, isBundle: false) is { } outputExtensionError)
            {
                logger.LogError("{Message}", outputExtensionError);
                return 1;
            }

            var certPath = parseResult.GetValue(CertOption);
            var generateCert = parseResult.GetValue(GenerateCertOption);
            var noSign = parseResult.GetValue(NoSignOption);
            if (noSign && (certPath != null || generateCert))
            {
                return Fail("--no-sign cannot be combined with --cert or --generate-cert.");
            }

            var name = parseResult.GetValue(NameOption);
            var skipPri = parseResult.GetValue(SkipPriOption);
            var certPassword = parseResult.GetRequiredValue(CertPasswordOption);
            var installCert = parseResult.GetValue(InstallCertOption);
            var publisher = parseResult.GetValue(PublisherOption);
            var manifestPath = parseResult.GetValue(ManifestOption);
            var selfContainedFlag = parseResult.GetValue(SelfContainedOption);
            var executable = parseResult.GetValue(ExecutableOption);
            var noBuild = parseResult.GetValue(NoBuildOption);
            var noRestore = parseResult.GetValue(NoRestoreOption);

            if (logger.IsEnabled(LogLevel.Information))
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Search} {project.Name}  ·  {configuration} | {architecture}");
            }

            ProjectBuildOutcome outcome;
            try
            {
                // The owning solution defines $(SolutionDir), which C++ projects commonly use for output paths.
                var solution = (await projectRunService.ResolveInputAsync(project, cancellationToken)).Solution;
                var buildOptions = new ProjectRunOptions(configuration, architecture, Framework: null, noBuild, noRestore, properties, Json: false, Solution: solution);
                outcome = await projectRunService.BuildAndResolveAsync(project, buildOptions, cancellationToken);
            }
            catch (ProjectRunException ex)
            {
                return Fail(ex.Message);
            }

            if (outcome.Resolution is null)
            {
                // MSBuild already surfaced its diagnostics. Propagate its exit code.
                return outcome.ExitCode == 0 ? 1 : outcome.ExitCode;
            }

            var resolution = outcome.Resolution;
            if (NetworkOutputDirError(project.Name, resolution.TargetDir) is { } targetDirError)
            {
                return Fail(targetDirError);
            }

            var targetDir = new DirectoryInfo(resolution.TargetDir);
            if (!targetDir.Exists)
            {
                return Fail(noBuild
                    ? $"The build output folder for '{project.Name}' was not found: {targetDir.FullName}. Remove --no-build so the project is built first."
                    : $"The build output folder for '{project.Name}' was not found: {targetDir.FullName}.");
            }

            FileInfo? effectiveManifest = manifestPath;
            FileInfo? appxRecipe = null;
            if (resolution.Packaging == ProjectPackaging.Packaged)
            {
                // A packaged (MSIX) C++ project generates its own manifest and file list during the build.
                effectiveManifest ??= resolution.AppxManifestPath is { Length: > 0 } generated && File.Exists(generated)
                    ? new FileInfo(generated)
                    : null;
                appxRecipe = string.IsNullOrWhiteSpace(resolution.AppxRecipePath) ? null : new FileInfo(resolution.AppxRecipePath);
                if (effectiveManifest == null && !ManifestHelper.FindManifest(targetDir.FullName).Exists)
                {
                    return Fail(noBuild
                        ? $"'{project.Name}' is a packaged (MSIX) project but no AppxManifest.xml was found in its build output ({targetDir.FullName}). Remove --no-build so the project is built first."
                        : $"'{project.Name}' is a packaged (MSIX) project but the build did not produce an AppxManifest.xml in {targetDir.FullName}.");
                }
            }
            else
            {
                // An unpackaged app (Win32 / console) has no generated manifest: use the one 'winapp init'
                // created next to the project, then the build output and the current directory.
                if (effectiveManifest == null && project.Directory is { } projectDirectory
                    && ManifestHelper.FindManifest(projectDirectory.FullName) is { Exists: true } projectManifest)
                {
                    effectiveManifest = projectManifest;
                }
                if (effectiveManifest == null
                    && !ManifestHelper.FindManifest(targetDir.FullName).Exists
                    && !ManifestHelper.FindManifest(currentDirectoryProvider.GetCurrentDirectory()).Exists)
                {
                    return Fail($"No appxmanifest.xml was found for '{project.Name}'. winapp looked in the project folder, the build output ({targetDir.FullName}), and the current folder. " +
                                $"Create one by running 'winapp init' in the project folder, or pass --manifest <path>.");
                }

                // The built .exe fills a $targetnametoken$ placeholder in the manifest.
                if (executable == null && resolution.RunCommand is { } exePath)
                {
                    executable = Path.GetRelativePath(targetDir.FullName, exePath);
                }
            }

            var shouldSign = !noSign && (certPath != null || generateCert);
            return await statusService.ExecuteWithStatusAsync("Creating MSIX package...", async (taskContext, ct) =>
            {
                try
                {
                    var result = await msixService.CreateMsixPackageAsync(
                        targetDir, output, taskContext, name, skipPri, shouldSign, certPath, certPassword,
                        generateCert, installCert, publisher, effectiveManifest, resolution.SelfContained || selfContainedFlag, executable,
                        targetArch: resolution.Architecture,
                        runtimeAlreadyBundled: resolution.SelfContained,
                        appxRecipe: appxRecipe,
                        windowsAppSdkPackageDirectory: resolution.WindowsAppSdkPackageDir is { } sdkDir ? new DirectoryInfo(sdkDir) : null,
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
