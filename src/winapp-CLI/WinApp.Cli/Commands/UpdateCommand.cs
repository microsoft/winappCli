// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Commands;

internal class UpdateCommand : Command, IShortDescription
{
    public string ShortDescription => "Update packages in winapp.yaml";

    public UpdateCommand() : base("update", "Check for and install newer SDK versions. Updates winapp.yaml with latest versions and reinstalls packages. Requires existing winapp.yaml (created by 'init'). Use --setup-sdks preview for preview SDKs. To reinstall current versions without updating, use 'restore' instead.")
    {
        Options.Add(InitCommand.SetupSdksOption);
    }

    public class Handler(
        IConfigService configService,
        INugetService nugetService,
        IWinappDirectoryService winappDirectoryService,
        IPackageInstallationService packageInstallationService,
        IBuildToolsService buildToolsService,
        IWindowsAppRuntimeService windowsAppRuntimeService,
        IStatusService statusService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IProjectContextDetector projectContextDetector) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var setupSdks = parseResult.GetValue(InitCommand.SetupSdksOption) ?? SdkInstallMode.Stable;

            ProjectContextEvent.Log(
                "update",
                () => projectContextDetector.DetectDirectory(
                    currentDirectoryProvider.GetCurrentDirectoryInfo(),
                    ProjectTargetKind.Workspace));

            return await statusService.ExecuteWithStatusAsync("Updating packages and build tools...", async (taskContext, cancellationToken) =>
            {
                try
                {
                    // Tracks packages whose latest-version lookup failed closed (e.g. a feed outage or auth
                    // failure). GetLatestVersionAsync now throws rather than returning a stale answer, and a
                    // lookup failure must not be reported as an authoritative "up to date" result — so record
                    // them and fail the command (non-zero) at the end instead of emitting the success message.
                    var lookupFailures = new List<string>();

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
                        else if (setupSdks == SdkInstallMode.None)
                        {
                            // --setup-sdks none means "skip SDK package work entirely", so there is no channel to
                            // resolve a latest version against; GetLatestVersionAsync rejects None outright. Skip
                            // the package loop and leave the pinned versions alone rather than asking for a
                            // version we then report as a lookup failure, which made the documented option always
                            // exit non-zero on a non-empty config. Build tools and the runtime are skipped below.
                            taskContext.AddStatusMessage($"{UiSymbols.Skip} SDK updates skipped (--setup-sdks none); pinned versions in winapp.yaml are unchanged");
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

                                        // Only advance to a strictly greater version. Comparing by value
                                        // (not string inequality) avoids two hazards: a normalized-but-equal
                                        // version (e.g. "1.0" vs "1.0.0") spuriously counting as an update,
                                        // and a lower "latest" ever silently downgrading the pinned version.
                                        if (NugetService.CompareVersions(latestVersion, package.Version) > 0)
                                        {
                                            taskContext.AddStatusMessage($"{UiSymbols.Rocket} {package.Name}: {package.Version} → {latestVersion}");
                                            updatedConfig.SetVersion(package.Name, latestVersion);
                                            hasUpdates = true;
                                        }
                                        else
                                        {
                                            taskContext.AddDebugMessage($"{UiSymbols.Check} {package.Name}: already up to date ({package.Version})");
                                            updatedConfig.SetVersion(package.Name, package.Version);
                                        }
                                    }
                                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                                    {
                                        // A user cancellation (Ctrl+C) must abort the whole command, not be
                                        // recorded as an ordinary lookup failure that would let the loop keep
                                        // checking packages and then proceed to install / build-tool work.
                                        throw;
                                    }
                                    catch (Exception ex)
                                    {
                                        taskContext.AddStatusMessage($"{UiSymbols.Warning} Failed to check {package.Name}: {NugetErrorMessage.Redact(ex.Message)}");
                                        // Keep current version on error, but remember the failure so the
                                        // command exits non-zero and does not claim everything is up to date.
                                        updatedConfig.SetVersion(package.Name, package.Version);
                                        lookupFailures.Add(package.Name);
                                    }
                                }

                                return 0;
                            }, cancellationToken);

                            // The package-check subtask runs inside a task wrapper that catches and swallows
                            // exceptions, so a cancellation rethrown mid-loop stops the loop but does not
                            // propagate on its own. Surface it here before acting on the (partial) results, so
                            // Ctrl+C aborts the command instead of falling through to install and build-tool work.
                            cancellationToken.ThrowIfCancellationRequested();

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
                                // Only assert everything is current when every lookup actually succeeded. A
                                // failed lookup (recorded above) is not evidence of being up to date.
                                if (lookupFailures.Count == 0)
                                {
                                    taskContext.AddStatusMessage($"{UiSymbols.Check} All packages are already up to date");
                                }
                            }
                        }
                    }
                    else
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Note} No winapp.yaml found");
                    }

                    // Step 2: Ensure build tools are installed/updated in cache
                    // `--setup-sdks none` means "skip SDK installation" (its documented meaning, and what
                    // `init` does — WorkspaceSetupService skips both the SDK packages and the runtime under
                    // None). Downloading build tools and installing the runtime MSIX are exactly that kind of
                    // work, and the runtime install modifies the machine, so honor the option here too rather
                    // than only skipping the version checks above.
                    if (setupSdks == SdkInstallMode.None)
                    {
                        taskContext.AddStatusMessage($"{UiSymbols.Skip} Build tools and Windows App Runtime skipped (--setup-sdks none)");
                    }
                    else
                    {
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
                        // Find MSIX directory using WindowsAppRuntimeService logic
                        var msixDir = windowsAppRuntimeService.FindWindowsAppSdkMsixDirectory();

                        if (msixDir != null)
                        {
                            taskContext.AddStatusMessage($"{UiSymbols.Wrench} Installing Windows App Runtime...");

                            await windowsAppRuntimeService.InstallWindowsAppRuntimeAsync(msixDir, taskContext, cancellationToken);

                            taskContext.AddStatusMessage($"{UiSymbols.Check} Windows App Runtime installation complete");
                        }
                        else
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Windows App SDK packages not found, skipping runtime installation");
                        }
                    }

                    // A version lookup that failed closed (feed outage / auth failure) must fail the command:
                    // returning 0 here would report a feed error as a successful, authoritative update.
                    if (lookupFailures.Count > 0)
                    {
                        return (1, $"{UiSymbols.Error} Update failed: could not determine the latest version for {lookupFailures.Count} package(s): {string.Join(", ", lookupFailures)}. Their pinned versions in winapp.yaml were left unchanged.");
                    }

                    return (0, "Update completed successfully!");
                }
                catch (Exception error)
                {
                    if (error.StackTrace != null)
                    {
                        taskContext.AddDebugMessage(error.StackTrace);
                    }
                    return (1, $"{UiSymbols.Error} Update command failed: {error.GetBaseException().Message}");
                }
            }, cancellationToken);
        }
    }
}
