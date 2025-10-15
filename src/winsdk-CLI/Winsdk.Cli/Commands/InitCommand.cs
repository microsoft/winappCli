using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class InitCommand : Command
{
    public static Argument<string> BaseDirectoryArgument { get; }
    public static Option<string> ConfigDirOption { get; }
    public static Option<bool> PrereleaseOption { get; }
    public static Option<bool> IgnoreConfigOption { get; }
    public static Option<bool> NoGitignoreOption { get; }
    public static Option<bool> QuietOption { get; }
    public static Option<bool> YesOption { get; }
    public static Option<bool> NoCertOption { get; }
    public static Option<bool> ConfigOnlyOption { get; }

    static InitCommand()
    {
        BaseDirectoryArgument = new Argument<string>("base-directory")
        {
            Description = "Base/root directory for the winsdk workspace, for consumption or installation.",
            Arity = ArgumentArity.ZeroOrOne
        };
        ConfigDirOption = new Option<string>("--config-dir")
        {
            Description = "Directory to read/store configuration (default: current directory)",
            DefaultValueFactory = (argumentResult) => Directory.GetCurrentDirectory()
        };
        PrereleaseOption = new Option<bool>("--prerelease")
        {
            Description = "Include prerelease packages from NuGet"
        };
        IgnoreConfigOption = new Option<bool>("--ignore-config", "--no-config")
        {
            Description = "Don't use configuration file for version management"
        };
        NoGitignoreOption = new Option<bool>("--no-gitignore")
        {
            Description = "Don't update .gitignore file"
        };
        QuietOption = new Option<bool>("--quiet", "-q")
        {
            Description = "Suppress progress messages"
        };
        YesOption = new Option<bool>("--yes", "--no-prompt")
        {
            Description = "Assume yes to all prompts"
        };
        NoCertOption = new Option<bool>("--no-cert")
        {
            Description = "Skip development certificate generation"
        };
        ConfigOnlyOption = new Option<bool>("--config-only")
        {
            Description = "Only handle configuration file operations (create if missing, validate if exists). Skip package installation, certificate generation, and other workspace setup steps."
        };
    }

    public InitCommand() : base("init", "Initializes a directory with required assets (manifest, certs, libraries) for building a modern Windows app. ")
    {
        Arguments.Add(BaseDirectoryArgument);
        Options.Add(ConfigDirOption);
        Options.Add(PrereleaseOption);
        Options.Add(IgnoreConfigOption);
        Options.Add(NoGitignoreOption);
        Options.Add(QuietOption);
        Options.Add(YesOption);
        Options.Add(NoCertOption);
        Options.Add(ConfigOnlyOption);
    }

    public class Handler(IWorkspaceSetupService workspaceSetupService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var baseDirectory = parseResult.GetValue(BaseDirectoryArgument);
            var configDir = parseResult.GetRequiredValue(ConfigDirOption);
            var prerelease = parseResult.GetValue(PrereleaseOption);
            var ignoreConfig = parseResult.GetValue(IgnoreConfigOption);
            var noGitignore = parseResult.GetValue(NoGitignoreOption);
            var quiet = parseResult.GetValue(QuietOption);
            var assumeYes = parseResult.GetValue(YesOption);
            var noCert = parseResult.GetValue(NoCertOption);
            var configOnly = parseResult.GetValue(ConfigOnlyOption);
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
                IncludeExperimental = prerelease,
                IgnoreConfig = ignoreConfig,
                NoGitignore = noGitignore,
                AssumeYes = assumeYes,
                RequireExistingConfig = false,
                ForceLatestBuildTools = true,
                NoCert = noCert,
                ConfigOnly = configOnly
            };

            return await workspaceSetupService.SetupWorkspaceAsync(options, cancellationToken);
        }
    }
}
