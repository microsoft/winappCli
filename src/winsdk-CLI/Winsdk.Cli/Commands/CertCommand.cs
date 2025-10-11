using System.CommandLine;

namespace Winsdk.Cli.Commands;

internal class CertCommand : Command
{
    public CertCommand(CertGenerateCommand certGenerateCommand, CertInstallCommand certInstallCommand)
        : base("cert", "Generate or install development certificates")
    {
        Subcommands.Add(certGenerateCommand);
        Subcommands.Add(certInstallCommand);
    }
}
