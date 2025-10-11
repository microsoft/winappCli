using System.CommandLine;

namespace Winsdk.Cli.Commands;

internal class WinSdkRootCommand : RootCommand
{
    internal static Option<bool> VerboseOption = new Option<bool>("--verbose", "-v")
    {
        Description = "Enable verbose output"
    };

    public WinSdkRootCommand(
        InitCommand initCommand,
        RestoreCommand restoreCommand,
        PackageCommand packageCommand,
        ManifestCommand manifestCommand,
        UpdateCommand updateCommand,
        CreateDebugIdentityCommand createDebugIdentityCommand,
        GetWinsdkPathCommand getWinsdkPathCommand,
        CertCommand certCommand,
        SignCommand signCommand,
        ToolCommand toolCommand) : base("Windows SDK CLI tool")
    {
        Subcommands.Add(initCommand);
        Subcommands.Add(restoreCommand);
        Subcommands.Add(packageCommand);
        Subcommands.Add(manifestCommand);
        Subcommands.Add(updateCommand);
        Subcommands.Add(createDebugIdentityCommand);
        Subcommands.Add(getWinsdkPathCommand);
        Subcommands.Add(certCommand);
        Subcommands.Add(signCommand);
        Subcommands.Add(toolCommand);
    }
}
