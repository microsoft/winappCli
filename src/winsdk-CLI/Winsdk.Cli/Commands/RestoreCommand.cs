using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class RestoreCommand : Command
{
    public static Argument<string> BaseDirectoryArgument { get; }
    public static Option<string> ConfigDirOption { get; }
    public static Option<bool> QuietOption { get; }

    static RestoreCommand()
    {
        BaseDirectoryArgument = new Argument<string>("base-directory")
        {
            Description = "Base/root directory for the winsdk workspace",
            Arity = ArgumentArity.ZeroOrOne
        };

        ConfigDirOption = new Option<string>("--config-dir")
        {
            Description = "Directory to read configuration from (default: current directory)",
            DefaultValueFactory = (argumentResult) => Directory.GetCurrentDirectory()
        };

        QuietOption = new Option<bool>("--quiet", "-q")
        {
            Description = "Suppress progress messages"
        };
    }

    public RestoreCommand() : base("restore", "Restore packages from winsdk.yaml and ensure workspace is ready")
    {
        Arguments.Add(BaseDirectoryArgument);
        Options.Add(ConfigDirOption);
        Options.Add(QuietOption);
    }

    public class Handler(IWorkspaceSetupService workspaceSetupService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var baseDirectory = parseResult.GetValue(BaseDirectoryArgument);
            var configDir = parseResult.GetRequiredValue(ConfigDirOption);
            var quiet = parseResult.GetValue(QuietOption);
            var verbose = parseResult.GetValue(WinSdkRootCommand.VerboseOption);

            if (quiet && verbose)
            {
                Console.Error.WriteLine($"Cannot specify both --quiet and --verbose options together.");
                return 1;
            }

            var options = new WorkspaceSetupOptions
            {
                BaseDirectory = baseDirectory ?? Directory.GetCurrentDirectory(),
                ConfigDir = configDir,
                Quiet = quiet,
                Verbose = verbose,
                RequireExistingConfig = true,
                ForceLatestBuildTools = false // Will be determined from config
            };

            return await workspaceSetupService.SetupWorkspaceAsync(options, cancellationToken);
        }
    }
}
