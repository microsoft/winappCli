// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using Spectre.Console;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

internal class WinAppRootCommand : RootCommand, IShortDescription
{
    public string ShortDescription => "Tools for Windows app development, package identity, packaging, and the Windows (App) SDK";
    internal static Option<bool> VerboseOption = new Option<bool>("--verbose", "-v")
    {
        Description = "Enable verbose output"
    };

    internal static Option<bool> QuietOption = new Option<bool>("--quiet", "-q")
    {
        Description = "Suppress progress messages"
    };

    internal static Option<bool> JsonOption = new Option<bool>("--json")
    {
        Description = "Format output as JSON"
    };

    internal static readonly Option<bool> CliSchemaOption = new("--cli-schema")
    {
        Description = "Output the complete CLI command structure as JSON for tooling, scripting, and LLM integration. Includes all commands, options, arguments, and their descriptions.",
        Arity = ArgumentArity.Zero,
        Recursive = true,
        Hidden = false,
        Action = new PrintCliSchemaAction()
    };

    internal static readonly Option<string?> CallerOption = new("--caller")
    {
        Description = "Identifies the caller (e.g., nuget-package, npm). Used for telemetry.",
        Recursive = true,
        Hidden = true
    };

    internal static readonly Option<string?> ProjectFrameworkOption = new("--project-framework")
    {
        Description = "Allow-listed project framework supplied by an integrated caller. Used for telemetry.",
        Recursive = true,
        Hidden = true
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
        NewCommand newCommand,
        RestoreCommand restoreCommand,
        PackageCommand packageCommand,
        ManifestCommand manifestCommand,
        UpdateCommand updateCommand,
        CreateDebugIdentityCommand createDebugIdentityCommand,
        EmbedIdentityCommand embedIdentityCommand,
        RunCommand runCommand,
        UnregisterCommand unregisterCommand,
        GetWinappPathCommand getWinappPathCommand,
        CertCommand certCommand,
        SignCommand signCommand,
        AzSignCommand azSignCommand,
        ToolCommand toolCommand,
        MSStoreCommand msStoreCommand,
        IAnsiConsole ansiConsole,
        CreateExternalCatalogCommand createExternalCatalogCommand,
        CompleteCommand completeCommand,
        FindUiCommand findUiCommand,
        UiCommand uiCommand) : base("CLI for Windows app development, including package identity, packaging, managing Package.appxmanifest, test certificates, Windows (App) SDK projections, and more. For use with any app framework targeting Windows")
    {
        Subcommands.Add(initCommand);
        Subcommands.Add(newCommand);
        Subcommands.Add(restoreCommand);
        Subcommands.Add(packageCommand);
        Subcommands.Add(manifestCommand);
        Subcommands.Add(updateCommand);
        Subcommands.Add(createDebugIdentityCommand);
        Subcommands.Add(embedIdentityCommand);
        Subcommands.Add(runCommand);
        Subcommands.Add(unregisterCommand);
        Subcommands.Add(getWinappPathCommand);
        Subcommands.Add(certCommand);
        Subcommands.Add(signCommand);
        Subcommands.Add(azSignCommand);
        Subcommands.Add(toolCommand);
        Subcommands.Add(msStoreCommand);
        Subcommands.Add(createExternalCatalogCommand);
        Subcommands.Add(findUiCommand);
        Subcommands.Add(uiCommand);
        Subcommands.Add(completeCommand);

        Options.Add(CliSchemaOption);
        Options.Add(CallerOption);
        Options.Add(ProjectFrameworkOption);

        // Reject unknown options/arguments so typos and removed flags fail loudly
        TreatUnmatchedTokensAsErrors = true;

        // Replace the default help with a custom categorized help screen
        var helpOption = Options.OfType<HelpOption>().First();
        helpOption.Action = new CustomHelpAction(this, ansiConsole,
            ("Setup", [typeof(InitCommand), typeof(NewCommand), typeof(RestoreCommand), typeof(UpdateCommand)]),
            ("Packaging & Signing", [typeof(PackageCommand), typeof(SignCommand), typeof(AzSignCommand), typeof(CertCommand), typeof(ManifestCommand), typeof(EmbedIdentityCommand), typeof(CreateExternalCatalogCommand)]),
            ("Development Tools", [typeof(CreateDebugIdentityCommand), typeof(MSStoreCommand), typeof(ToolCommand), typeof(GetWinappPathCommand), typeof(RunCommand), typeof(UnregisterCommand)]),
            ("Discovery", [typeof(FindUiCommand)]),
            ("UI Automation", [typeof(UiCommand)])
        );
    }
}
