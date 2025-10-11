using System.CommandLine;

namespace Winsdk.Cli.Commands;

internal class ManifestCommand : Command
{
    public ManifestCommand(ManifestGenerateCommand manifestGenerateCommand)
        : base("manifest", "AppxManifest.xml management")
    {
        Subcommands.Add(manifestGenerateCommand);
    }
}
