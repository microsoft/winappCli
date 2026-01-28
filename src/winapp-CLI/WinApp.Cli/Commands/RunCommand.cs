// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class RunCommand : Command
{
    public static Option<FileInfo?> ManifestOption { get; }
    public static Option<DirectoryInfo?> OutputAppXDirectoryOption { get; }
    public static Option<string> ArgsOption { get; }
    public static Option<bool> NoBuildOption { get; }

    static RunCommand()
    {
        ManifestOption = new Option<FileInfo?>("--manifest")
        {
            Description = "Path to the appxmanifest.xml"
        };

        OutputAppXDirectoryOption = new Option<DirectoryInfo?>("--output-appx-directory")
        {
            Description = "Output directory for the loose layout package. If not specified, A directory named AppX inside the appxmanifest.xml's directory will be used."
        };

        ArgsOption = new Option<string>("--args")
        {
            Description = "Command-line arguments to pass to the application"
        };

        NoBuildOption = new Option<bool>("--no-build")
        {
            Description = "Skip the build step before launching"
        };
    }

    public RunCommand() : base("run", "Create debug identity and launch the packaged application. Returns the process ID for debugger attachment.")
    {
        Options.Add(ManifestOption);
        Options.Add(OutputAppXDirectoryOption);
        Options.Add(ArgsOption);
        Options.Add(NoBuildOption);
    }

    public class Handler(
        IMsixService msixService,
        IAppLauncherService appLauncherService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IStatusService statusService,
        IProjectInformationProviderResolver projectInformationProviderResolver,
        ILogger<RunCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var manifest = parseResult.GetValue(ManifestOption);
            if (manifest != null && !manifest.Exists)
            {
                logger.LogError("Manifest not found: {ManifestPath}. Use --manifest to specify the path.", manifest.FullName);
                return 1;
            }

            DirectoryInfo GetDefaultAppXDirectory(FileInfo? manifest)
            {
                var directoryName = manifest?.DirectoryName;
                directoryName ??= currentDirectoryProvider.GetCurrentDirectory();
                return new DirectoryInfo(Path.Combine(directoryName, "AppX"));
            }

            var outputAppXDirectory = parseResult.GetValue(OutputAppXDirectoryOption);
            var appArgs = parseResult.GetValue(ArgsOption);
            var noBuild = parseResult.GetValue(NoBuildOption);

            return await statusService.ExecuteWithStatusAsync("Launching packaged application...", async (taskContext, cancellationToken) =>
            {
                try
                {
                    var currentDirectoryInfo = currentDirectoryProvider.GetCurrentDirectoryInfo();
                    DirectoryInfo? outputDirectory = null;

                    if (!noBuild)
                    {
                        // Step 1: Build the project
                        taskContext.AddStatusMessage($"{UiSymbols.Tools} Building project...");

                        var projectInformationProvider = await projectInformationProviderResolver.Resolve(currentDirectoryInfo, cancellationToken);

                        if (projectInformationProvider == null)
                        {
                            throw new Exception("No supported project found in the current directory.");
                        }

                        outputDirectory = await projectInformationProvider.BuildAsync(currentDirectoryInfo, cancellationToken);

                        if (outputDirectory == null)
                        {
                            throw new Exception("Build failed or output directory not found.");
                        }

                        taskContext.AddStatusMessage($"{UiSymbols.Check} Build succeeded.");
                    }
                    else
                    {
                        taskContext.AddStatusMessage($"{UiSymbols.Skip} Skipping build (--no-build)");
                    }

                    DirectoryInfo inputDirectory;

                    if (manifest != null)
                    {
                        inputDirectory = manifest.Directory!;
                    }
                    else if (outputDirectory != null)
                    {
                        inputDirectory = outputDirectory;

                        manifest = new FileInfo(Path.Combine(inputDirectory.FullName, "AppxManifest.xml"));
                        if (!manifest.Exists)
                        {
                            var fileInfo = new FileInfo(Path.Combine(currentDirectoryInfo.FullName, "appxmanifest.xml"));
                            if (fileInfo.Exists)
                            {
                                manifest = fileInfo;
                                if (outputAppXDirectory == null)
                                {
                                    outputAppXDirectory ??= new DirectoryInfo(Path.Combine(inputDirectory.FullName, "AppX"));
                                }
                            }
                        }
                        if (!manifest.Exists)
                        {
                            var fileInfo = new FileInfo(Path.Combine(currentDirectoryInfo.FullName, "Package.AppxManifest"));
                            if (!fileInfo.Exists)
                            {
                                throw new Exception("AppxManifest.xml not found in the build output or current directory. Use --manifest to specify the path.");
                            }
                            manifest = fileInfo;
                            if (outputAppXDirectory == null)
                            {
                                outputAppXDirectory ??= new DirectoryInfo(Path.Combine(inputDirectory.FullName, "AppX"));
                            }
                        }
                    }
                    else
                    {
                        // No build and no manifest specified - try to find manifest in current directory
                        inputDirectory = currentDirectoryInfo;

                        manifest = new FileInfo(Path.Combine(currentDirectoryInfo.FullName, "appxmanifest.xml"));
                        if (!manifest.Exists)
                        {
                            manifest = new FileInfo(Path.Combine(currentDirectoryInfo.FullName, "AppxManifest.xml"));
                        }
                        if (!manifest.Exists)
                        {
                            manifest = new FileInfo(Path.Combine(currentDirectoryInfo.FullName, "Package.AppxManifest"));
                        }
                        if (!manifest.Exists)
                        {
                            throw new Exception("AppxManifest.xml not found in the current directory. Use --manifest to specify the path, or remove --no-build to build the project first.");
                        }
                    }

                    outputAppXDirectory ??= GetDefaultAppXDirectory(manifest);

                    // Step 2: Create and register the debug identity
                    taskContext.AddStatusMessage($"{UiSymbols.Package} Creating debug identity...");
                    var identityResult = await msixService.AddLooseLayoutIdentityAsync(
                        manifest,
                        inputDirectory,
                        outputAppXDirectory,
                        taskContext,
                        cancellationToken);

                    taskContext.AddStatusMessage($"{UiSymbols.Package} Package: {identityResult.PackageName}");
                    taskContext.AddStatusMessage($"{UiSymbols.User} Publisher: {identityResult.Publisher}");
                    taskContext.AddStatusMessage($"{UiSymbols.Id} App ID: {identityResult.ApplicationId}");

                    // Step 3: Compute the AUMID (Application User Model ID)
                    var packageFamilyName = appLauncherService.ComputePackageFamilyName(
                        identityResult.PackageName,
                        identityResult.Publisher);
                    var aumid = $"{packageFamilyName}!{identityResult.ApplicationId}";

                    taskContext.AddStatusMessage($"{UiSymbols.Link} AUMID: {aumid}");

                    // Step 4: Launch the application using IApplicationActivationManager
                    taskContext.AddStatusMessage($"{UiSymbols.Rocket} Launching application...");
                    var processId = appLauncherService.LaunchByAumid(aumid, appArgs);

                    taskContext.AddStatusMessage($"{UiSymbols.Check} Process ID: {processId}");

                    // Print PID to stdout (for debugger integration)
                    Console.WriteLine(processId);

                    return (0, $"Application launched successfully (PID: {processId})");
                }
                catch (Exception error)
                {
                    return (1, $"{UiSymbols.Error} Failed to launch application: {error.Message}");
                }
            }, cancellationToken);
        }
    }
}
