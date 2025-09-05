using System.CommandLine;

namespace Winsdk.Cli.Commands;

internal class MsixCertCommand : Command
{
    public MsixCertCommand() : base("cert", "Generate or install development certificates")
    {
        Subcommands.Add(new MsixCertGenerateCommand());
        Subcommands.Add(new MsixCertInstallCommand());
    }
}
