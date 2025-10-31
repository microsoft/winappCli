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

    public class Handler(IMsixService msixService, ICurrentDirectoryProvider currentDirectoryProvider, ILogger<CreateDebugIdentityCommand> logger) : AsynchronousCommandLineAction
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

            try
            {
                var result = await msixService.AddMsixIdentityAsync(entryPointPath?.ToString(), manifest, noInstall, cancellationToken);

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
