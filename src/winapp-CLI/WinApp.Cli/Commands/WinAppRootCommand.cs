// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

internal class WinAppRootCommand : RootCommand
{
    internal static Option<bool> VerboseOption = new Option<bool>("--verbose", "-v")
    {
        Description = "Enable verbose output"
    };

    internal static Option<bool> QuietOption = new Option<bool>("--quiet", "-q")
    {
        Description = "Suppress progress messages"
    };

    internal static readonly Option<bool> CliSchemaOption = new("--cli-schema")
    {
        Description = "Outputs the CLI command schema in JSON format",
        Arity = ArgumentArity.Zero,
        Recursive = true,
        Hidden = true,
        Action = new PrintCliSchemaAction()
    };

    private class PrintCliSchemaAction : SynchronousCommandLineAction
    {
        public override bool Terminating => true;

        public override int Invoke(ParseResult parseResult)
        {
            CliSchema.PrintCliSchema(parseResult.CommandResult, parseResult.InvocationConfiguration.Output);
            return 0;
        }
    }

    public WinAppRootCommand(
        InitCommand initCommand,
        RestoreCommand restoreCommand,
        PackageCommand packageCommand,
        ManifestCommand manifestCommand,
        UpdateCommand updateCommand,
        CreateDebugIdentityCommand createDebugIdentityCommand,
        GetWinappPathCommand getWinappPathCommand,
        CertCommand certCommand,
        SignCommand signCommand,
        ToolCommand toolCommand,
        MSStoreCommand msStoreCommand) : base("Setup Windows SDK and Windows App SDK for use in your app, create MSIX packages, generate manifests and certificates, and use build tools.")
    {
        Subcommands.Add(initCommand);
        Subcommands.Add(restoreCommand);
        Subcommands.Add(packageCommand);
        Subcommands.Add(manifestCommand);
        Subcommands.Add(updateCommand);
        Subcommands.Add(createDebugIdentityCommand);
        Subcommands.Add(getWinappPathCommand);
        Subcommands.Add(certCommand);
        Subcommands.Add(signCommand);
        Subcommands.Add(toolCommand);
        Subcommands.Add(msStoreCommand);

        Options.Add(CliSchemaOption);
    }
}
