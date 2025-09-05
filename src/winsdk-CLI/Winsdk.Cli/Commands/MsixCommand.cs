using System.CommandLine;

namespace Winsdk.Cli.Commands;

internal class MsixCommand : Command
{
    public MsixCommand() : base("msix", "MSIX package management")
    {
        Subcommands.Add(new MsixInitCommand());
        Subcommands.Add(new MsixSignCommand());
        Subcommands.Add(new MsixAddIdentityCommand());
        Subcommands.Add(new MsixPackageCommand());
        Subcommands.Add(new MsixCertCommand());
    }
}
