using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class ManifestGenerateCommand : Command
{
    public static Argument<string> DirectoryArgument { get; }
    public static Option<string> PackageNameOption { get; }
    public static Option<string> PublisherNameOption { get; }
    public static Option<string> VersionOption { get; }
    public static Option<string> DescriptionOption { get; }
    public static Option<string?> ExecutableOption { get; }
    public static Option<bool> SparseOption { get; }
    public static Option<string?> LogoPathOption { get; }
    public static Option<bool> YesOption { get; }

    static ManifestGenerateCommand()
    {
        DirectoryArgument = new Argument<string>("directory")
        {
            Description = "Directory to generate manifest in",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = (argumentResult) => Directory.GetCurrentDirectory()
        };

        PackageNameOption = new Option<string>("--package-name")
        {
            Description = "Package name (default: folder name)"
        };

        PublisherNameOption = new Option<string>("--publisher-name")
        {
            Description = "Publisher CN (default: CN=<current user>)"
        };

        VersionOption = new Option<string>("--version")
        {
            Description = "Version",
            DefaultValueFactory = (argumentResult) => "1.0.0.0"
        };

        DescriptionOption = new Option<string>("--description")
        {
            Description = "Description",
            DefaultValueFactory = (argumentResult) => "My Application"
        };

        ExecutableOption = new Option<string?>("--executable")
        {
            Description = "Executable path/name (default: <package-name>.exe)"
        };

        SparseOption = new Option<bool>("--sparse")
        {
            Description = "Generate sparse package manifest"
        };

        LogoPathOption = new Option<string?>("--logo-path")
        {
            Description = "Path to logo image file"
        };

        YesOption = new Option<bool>("--yes", "--y")
        {
            Description = "Skip interactive prompts and use default values"
        };
    }

    public ManifestGenerateCommand() : base("generate", "Generate a manifest in directory")
    {
        Arguments.Add(DirectoryArgument);
        Options.Add(PackageNameOption);
        Options.Add(PublisherNameOption);
        Options.Add(VersionOption);
        Options.Add(DescriptionOption);
        Options.Add(ExecutableOption);
        Options.Add(SparseOption);
        Options.Add(LogoPathOption);
        Options.Add(YesOption);
        Options.Add(WinSdkRootCommand.VerboseOption);
    }

    public class Handler(IManifestService manifestService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var directory = parseResult.GetRequiredValue(DirectoryArgument);
            var packageName = parseResult.GetValue(PackageNameOption);
            var publisherName = parseResult.GetValue(PublisherNameOption);
            var version = parseResult.GetRequiredValue(VersionOption);
            var description = parseResult.GetRequiredValue(DescriptionOption);
            var executable = parseResult.GetValue(ExecutableOption);
            var sparse = parseResult.GetValue(SparseOption);
            var logoPath = parseResult.GetValue(LogoPathOption);
            var yes = parseResult.GetValue(YesOption);
            var verbose = parseResult.GetValue(WinSdkRootCommand.VerboseOption);

            try
            {
                await manifestService.GenerateManifestAsync(
                    directory,
                    packageName,
                    publisherName,
                    version,
                    description,
                    executable,
                    sparse,
                    logoPath,
                    yes,
                    verbose,
                    cancellationToken);

                Console.WriteLine($"Manifest generated successfully in: {directory}");

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"❌ Error generating manifest: {ex.Message}");
                if (verbose)
                {
                    Console.Error.WriteLine(ex.StackTrace);
                }
                return 1;
            }
        }
    }
}
