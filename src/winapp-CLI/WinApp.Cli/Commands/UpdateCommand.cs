// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UpdateCommand : Command
{
    public UpdateCommand() : base("update", "Update packages in winapp.yaml and install/update build tools in cache")
    {
        Options.Add(InitCommand.SetupSdksOption);
    }

    public class Handler(
        IConfigService configService,
        INugetService nugetService,
        IWinappDirectoryService winappDirectoryService,
        IPackageInstallationService packageInstallationService,
        IBuildToolsService buildToolsService,
        IWorkspaceSetupService workspaceSetupService,
        IStatusService statusService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var setupSdks = parseResult.GetValue(InitCommand.SetupSdksOption) ?? SdkInstallMode.Stable;

            return await statusService.ExecuteWithStatusAsync("Updating packages and build tools...", async (taskContext, cancellationToken) =>
            {
                try
                {
                    // Step 1: Find yaml config file
                    taskContext.AddDebugMessage($"{UiSymbols.Note} Checking for winapp.yaml configuration...");

                    if (configService.Exists())
                    {
                        // Step 1.1: Update packages in yaml config
                        var config = configService.Load();

                        if (config.Packages.Count == 0)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} winapp.yaml found but contains no packages");
                        }
                        else
                        {
                            taskContext.AddStatusMessage($"{UiSymbols.Package} Found winapp.yaml with {config.Packages.Count} packages, checking for updates...");

                            var updatedConfig = new WinappConfig();
                            bool hasUpdates = false;
                            await taskContext.AddSubTaskAsync("Checking for package updates", async (taskContext, cancellationToken) =>
                            {
                                foreach (var package in config.Packages)
                                {
                                    taskContext.AddDebugMessage($"{UiSymbols.Bullet} Checking {package.Name} (current: {package.Version})");

                                    try
                                    {
                                        var latestVersion = await nugetService.GetLatestVersionAsync(package.Name, setupSdks, cancellationToken);

                                        if (latestVersion != package.Version)
                                        {
                                            taskContext.AddStatusMessage($"{UiSymbols.Rocket} {package.Name}: {package.Version} → {latestVersion}");
                                            updatedConfig.SetVersion(package.Name, latestVersion);
                                            hasUpdates = true;
                                        }
                                        else
                                        {
                                            taskContext.AddDebugMessage($"{UiSymbols.Check} {package.Name}: already latest ({latestVersion})");
                                            updatedConfig.SetVersion(package.Name, package.Version);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        taskContext.AddStatusMessage($"{UiSymbols.Warning} Failed to check {package.Name}: {ex.Message}");
                                        // Keep current version on error
                                        updatedConfig.SetVersion(package.Name, package.Version);
                                    }
                                }

                                return 0;
                            }, cancellationToken);

                            if (hasUpdates)
                            {
                                configService.Save(updatedConfig);
                                taskContext.AddStatusMessage($"{UiSymbols.Save} Updated winapp.yaml with latest versions");

                                // Install the updated packages
                                taskContext.AddStatusMessage($"{UiSymbols.Package} Installing updated packages...");
                                var packageNames = updatedConfig.Packages.Select(p => p.Name).ToArray();

                                var globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();

                                var installedVersions = await packageInstallationService.InstallPackagesAsync(
                                    globalWinappDir,
                                    packageNames,
                                    taskContext,
                                    sdkInstallMode: setupSdks,
                                    ignoreConfig: false, // Use the updated config
                                    cancellationToken: cancellationToken
                                );

                                taskContext.AddStatusMessage($"{UiSymbols.Check} Package installation completed");
                            }
                            else
                            {
                                taskContext.AddStatusMessage($"{UiSymbols.Check} All packages are already up to date");
                            }
                        }
                    }
                    else
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Note} No winapp.yaml found");
                    }

                    // Step 2: Ensure build tools are installed/updated in cache
                    taskContext.AddDebugMessage($"{UiSymbols.Wrench} Checking build tools in cache...");

                    var buildToolsPath = await buildToolsService.EnsureBuildToolsAsync(taskContext, forceLatest: true, cancellationToken: cancellationToken);

                    if (buildToolsPath != null)
                    {
                        taskContext.AddStatusMessage($"{UiSymbols.Check} Build tools are up to date");
                        taskContext.AddDebugMessage($"{UiSymbols.Check} Build tools are available at: {buildToolsPath}");
                    }
                    else
                    {
                        return (1, $"{UiSymbols.Error} Failed to install/update build tools");
                    }

                    // Step 3: Install Windows App SDK runtime if available
                    // Find MSIX directory using WorkspaceSetupService logic
                    var msixDir = workspaceSetupService.FindWindowsAppSdkMsixDirectory();

                    if (msixDir != null)
                    {
                        taskContext.AddStatusMessage($"{UiSymbols.Wrench} Installing Windows App Runtime...");

                        await workspaceSetupService.InstallWindowsAppRuntimeAsync(msixDir, taskContext, cancellationToken);

                        taskContext.AddStatusMessage($"{UiSymbols.Check} Windows App Runtime installation complete");
                    }
                    else
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Note} Windows App SDK packages not found, skipping runtime installation");
                    }

                    return (0, $"{UiSymbols.Party} Update completed successfully!");
                }
                catch (Exception error)
                {
                    if (error.StackTrace != null)
                    {
                        taskContext.AddDebugMessage(error.StackTrace);
                    }
                    return (1, $"{UiSymbols.Error} Update command failed: {error.Message}");
                }
            }, cancellationToken);
        }
    }
}
