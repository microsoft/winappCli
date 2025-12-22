// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class CreateDebugIdentityCommand : Command
{
    public static Argument<FileInfo> EntryPointArgument { get; }
    public static Option<FileInfo> ManifestOption { get; }
    public static Option<bool> NoInstallOption { get; }

    static CreateDebugIdentityCommand()
    {
        EntryPointArgument = new Argument<FileInfo>("entrypoint")
        {
            Description = "Path to the .exe that will need to run with identity, or entrypoint script.",
            Arity = ArgumentArity.ZeroOrOne
        };
        EntryPointArgument.AcceptExistingOnly();
        ManifestOption = new Option<FileInfo>("--manifest")
        {
            Description = "Path to the appxmanifest.xml"
        };
        ManifestOption.AcceptExistingOnly();
        NoInstallOption = new Option<bool>("--no-install")
        {
            Description = "Do not install the package after creation."
        };
    }

    public CreateDebugIdentityCommand() : base("create-debug-identity", "Create and install a temporary package for debugging. Must be called every time the appxmanifest.xml is modified for changes to take effect.")
    {
        Arguments.Add(EntryPointArgument);
        Options.Add(ManifestOption);
        Options.Add(NoInstallOption);
    }

    public class Handler(IMsixService msixService, ICurrentDirectoryProvider currentDirectoryProvider, IStatusService statusService, ILogger<CreateDebugIdentityCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var entryPointPath = parseResult.GetValue(EntryPointArgument);
            var manifest = parseResult.GetValue(ManifestOption) ?? new FileInfo(Path.Combine(currentDirectoryProvider.GetCurrentDirectory(), "appxmanifest.xml"));
            var noInstall = parseResult.GetValue(NoInstallOption);

            if (entryPointPath != null && !entryPointPath.Exists)
            {
                logger.LogError("EntryPoint/Executable not found: {EntryPointPath}", entryPointPath);
                return 1;
            }

            return await statusService.ExecuteWithStatusAsync("Creating MSIX Debug identity...", async (taskContext, cancellationToken) =>
            {
                try
                {
                    var result = await msixService.AddMsixIdentityAsync(entryPointPath?.ToString(), manifest, noInstall, taskContext, cancellationToken);

                    taskContext.AddStatusMessage($"{UiSymbols.Check} MSIX identity added successfully!");
                    taskContext.AddStatusMessage($"{UiSymbols.Package} Package: {result.PackageName}");
                    taskContext.AddStatusMessage($"{UiSymbols.User} Publisher: {result.Publisher}");
                    taskContext.AddStatusMessage($"{UiSymbols.Id} App ID: {result.ApplicationId}");
                }
                catch (Exception error)
                {
                    return (1, $"{UiSymbols.Error} Failed to add MSIX identity: {error.Message}");
                }

                return (0, "{UiSymbols.Check} MSIX identity created successfully.");
            }, cancellationToken);
        }
    }
}
