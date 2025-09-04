using System.CommandLine;

namespace Winsdk.Cli.Commands;

internal class CertCommand : Command
{
    public CertCommand() : base("cert", "Generate or install development certificates")
    {
        Subcommands.Add(new CertGenerateCommand());
        Subcommands.Add(new CertInstallCommand());
    }
}
