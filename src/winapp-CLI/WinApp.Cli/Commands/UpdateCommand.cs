// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UpdateCommand : Command
{
    public static Option<bool> PrereleaseOption { get; }

    static UpdateCommand()
    {
        PrereleaseOption = new Option<bool>("--prerelease")
        {
            Description = "Include prerelease versions when checking for updates"
        };
    }

    public UpdateCommand() : base("update", "Update packages in winapp.yaml and install/update build tools in cache")
    {
        Options.Add(PrereleaseOption);
    }

    public class Handler(
        IConfigService configService,
        INugetService nugetService,
        IWinappDirectoryService winappDirectoryService,
        IPackageInstallationService packageInstallationService,
        IBuildToolsService buildToolsService,
        IWorkspaceSetupService workspaceSetupService,
        ILogger<UpdateCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var prerelease = parseResult.GetValue(PrereleaseOption);

            try
            {
                // Step 1: Find yaml config file
                logger.LogDebug("{UISymbol} Checking for winapp.yaml configuration...", UiSymbols.Note);

                if (configService.Exists())
                {
                    // Step 1.1: Update packages in yaml config
                    var config = configService.Load();
                    
                    if (config.Packages.Count == 0)
                    {
                        logger.LogDebug("{UISymbol} winapp.yaml found but contains no packages", UiSymbols.Note);
                    }
                    else
                    {
                        logger.LogInformation("{UISymbol} Found winapp.yaml with {PackageCount} packages, checking for updates...", UiSymbols.Package, config.Packages.Count);

                        var updatedConfig = new WinappConfig();
                        bool hasUpdates = false;
                        using (logger.BeginScope("PackageUpdates"))
                        {
                            foreach (var package in config.Packages)
                            {
                                logger.LogDebug("{UISymbol} Checking {PackageName} (current: {PackageVersion})", UiSymbols.Bullet, package.Name, package.Version);

                                try
                                {
                                    var latestVersion = await nugetService.GetLatestVersionAsync(package.Name, prerelease, cancellationToken);

                                    if (latestVersion != package.Version)
                                    {
                                        logger.LogInformation("{UISymbol} {PackageName}: {CurrentVersion} → {LatestVersion}", UiSymbols.Rocket, package.Name, package.Version, latestVersion);
                                        updatedConfig.SetVersion(package.Name, latestVersion);
                                        hasUpdates = true;
                                    }
                                    else
                                    {
                                        logger.LogDebug("{UISymbol} {PackageName}: already latest ({LatestVersion})", UiSymbols.Check, package.Name, latestVersion);
                                        updatedConfig.SetVersion(package.Name, package.Version);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger.LogInformation("{UISymbol} Failed to check {PackageName}: {ErrorMessage}", UiSymbols.Warning, package.Name, ex.Message);
                                    // Keep current version on error
                                    updatedConfig.SetVersion(package.Name, package.Version);
                                }
                            }
                        }

                        if (hasUpdates)
                        {
                            configService.Save(updatedConfig);
                            logger.LogInformation("{UISymbol} Updated winapp.yaml with latest versions", UiSymbols.Save);
                            
                            // Install the updated packages
                            logger.LogInformation("{UISymbol} Installing updated packages...", UiSymbols.Package);
                            var packageNames = updatedConfig.Packages.Select(p => p.Name).ToArray();
                            
                            var globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();

                            var installedVersions = await packageInstallationService.InstallPackagesAsync(
                                globalWinappDir,
                                packageNames,
                                includeExperimental: prerelease,
                                ignoreConfig: false, // Use the updated config
                                cancellationToken: cancellationToken
                            );

                            logger.LogInformation("{UISymbol} Package installation completed", UiSymbols.Check);
                        }
                        else
                        {
                            logger.LogInformation("{UISymbol} All packages are already up to date", UiSymbols.Check);
                        }
                    }
                }
                else
                {
                    logger.LogDebug("{UISymbol} No winapp.yaml found", UiSymbols.Note);
                }

                // Step 2: Ensure build tools are installed/updated in cache
                logger.LogDebug("{UISymbol} Checking build tools in cache...", UiSymbols.Wrench);

                var buildToolsPath = await buildToolsService.EnsureBuildToolsAsync(forceLatest: true, cancellationToken: cancellationToken);
                
                if (buildToolsPath != null)
                {
                    logger.LogInformation("{UISymbol} Build tools are up to date", UiSymbols.Check);
                    logger.LogDebug("{UISymbol} Build tools are available at: {BuildToolsPath}", UiSymbols.Check, buildToolsPath);
                }
                else
                {
                    logger.LogError("{UISymbol} Failed to install/update build tools", UiSymbols.Error);
                    return 1;
                }

                // Step 3: Install Windows App SDK runtime if available
                // Find MSIX directory using WorkspaceSetupService logic
                var msixDir = workspaceSetupService.FindWindowsAppSdkMsixDirectory();

                if (msixDir != null)
                {
                    logger.LogInformation("{UISymbol} Installing Windows App Runtime...", UiSymbols.Wrench);

                    await workspaceSetupService.InstallWindowsAppRuntimeAsync(msixDir, cancellationToken);

                    logger.LogInformation("{UISymbol} Windows App Runtime installation complete", UiSymbols.Check);
                }
                else
                {
                    logger.LogDebug("{UISymbol} Windows App SDK packages not found, skipping runtime installation", UiSymbols.Note);
                }

                logger.LogInformation("{UISymbol} Update completed successfully!", UiSymbols.Party);
                return 0;
            }
                catch (Exception error)
            {
                logger.LogError("{UISymbol} Update failed: {ErrorMessage}", UiSymbols.Error, error.Message);
                logger.LogDebug("{ErrorStackTrace}", error.StackTrace);
                return 1;
            }
        }
    }
}
