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
        Description = "Output the complete CLI command structure as JSON for tooling, scripting, and LLM integration. Includes all commands, options, arguments, and their descriptions.",
        Arity = ArgumentArity.Zero,
        Recursive = true,
        Hidden = false,
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
        ToolCommand toolCommand) : base("CLI for generating and managing appxmanifest.xml, image assets, test certificates, Windows (App) SDK projections, package identity, and packaging. For use with any app framework targeting Windows")
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

        Options.Add(CliSchemaOption);
    }
}
