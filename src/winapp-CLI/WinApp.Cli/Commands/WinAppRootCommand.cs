// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

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

    public WinAppRootCommand(
        InitCommand initCommand,
        RestoreCommand restoreCommand,
        PackageCommand packageCommand,
        ManifestCommand manifestCommand,
        UpdateCommand updateCommand,
        CreateDebugIdentityCommand createDebugIdentityCommand,
        GetWinappPathCommand getWinappPathCommand,
        CacheCommand cacheCommand,
        CertCommand certCommand,
        SignCommand signCommand,
        ToolCommand toolCommand) : base("Windows App Development CLI tool")
    {
        Subcommands.Add(initCommand);
        Subcommands.Add(restoreCommand);
        Subcommands.Add(packageCommand);
        Subcommands.Add(manifestCommand);
        Subcommands.Add(updateCommand);
        Subcommands.Add(createDebugIdentityCommand);
        Subcommands.Add(getWinappPathCommand);
        Subcommands.Add(cacheCommand);
        Subcommands.Add(certCommand);
        Subcommands.Add(signCommand);
        Subcommands.Add(toolCommand);
    }
}
