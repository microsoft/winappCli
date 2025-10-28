// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Helpers;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class CreateDebugIdentityCommand : Command
{
    public static Option<string> EntryPointOption { get; }
    public static Option<string> ManifestOption { get; }
    public static Option<bool> NoInstallOption { get; }
    public static Option<string> LocationOption { get; }

    static CreateDebugIdentityCommand()
    {
        EntryPointOption = new Option<string>("--entrypoint")
        {
            Description = "Path to the .exe that will need to run with identity, or entrypoint script."
        };
        ManifestOption = new Option<string>("--manifest")
        {
            Description = "Path to the appxmanifest.xml",
            DefaultValueFactory = (argumentResult) => ".\\appxmanifest.xml"
        };
        NoInstallOption = new Option<bool>("--no-install")
        {
            Description = "Do not install the package after creation."
        };
        LocationOption = new Option<string>("--location")
        {
            Description = "Root path of the application. Default is parent directory of the executable."
        };
    }

    public CreateDebugIdentityCommand() : base("create-debug-identity", "Create and install a temporary package for debugging. Must be called every time the appxmanifest.xml is modified for changes to take effect.")
    {
        Options.Add(EntryPointOption);
        Options.Add(ManifestOption);
        Options.Add(NoInstallOption);
        Options.Add(LocationOption);
    }

    public class Handler(IMsixService msixService, ILogger<CreateDebugIdentityCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var entryPointPath = parseResult.GetValue(EntryPointOption);
            var manifest = parseResult.GetRequiredValue(ManifestOption);
            var noInstall = parseResult.GetValue(NoInstallOption);
            var location = parseResult.GetValue(LocationOption);

            if (entryPointPath != null && !File.Exists(entryPointPath))
            {
                logger.LogError("EntryPoint/Executable not found: {EntryPointPath}", entryPointPath);
                return 1;
            }

            try
            {
                var result = await msixService.AddMsixIdentityAsync(entryPointPath, manifest, noInstall, location, cancellationToken);

                logger.LogInformation("{UISymbol} MSIX identity added successfully!", UiSymbols.Check);
                logger.LogInformation("{UISymbol} Package: {PackageName}", UiSymbols.Package, result.PackageName);
                logger.LogInformation("{UISymbol} Publisher: {Publisher}", UiSymbols.User, result.Publisher);
                logger.LogInformation("{UISymbol} App ID: {ApplicationId}", UiSymbols.Id, result.ApplicationId);
            }
            catch (Exception error)
            {
                logger.LogError("{UISymbol} Failed to add MSIX identity: {ErrorMessage}", UiSymbols.Error, error.Message);
                return 1;
            }

            return 0;
        }
    }
}
