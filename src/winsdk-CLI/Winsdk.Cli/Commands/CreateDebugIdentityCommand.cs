using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class CreateDebugIdentityCommand : Command
{
    public static Argument<string> ExecutableArgument { get; }
    public static Option<string> ManifestOption { get; }
    public static Option<bool> NoInstallOption { get; }
    public static Option<string> LocationOption { get; }

    static CreateDebugIdentityCommand()
    {
        ExecutableArgument = new Argument<string>("executable")
        {
            Description = "Path to the .exe that will need to run with identity"
        };
        ManifestOption = new Option<string>("--manifest")
        {
            Description = "Path to the appxmanifest.xml",
            DefaultValueFactory = (argumentResult) => ".\\appxmanifest.xml"
        };
        NoInstallOption = new Option<bool>("--no-install")
        {
            Description = "Do not install the package after creation."
        };
        LocationOption = new Option<string>("--location")
        {
            Description = "Root path of the application. Default is parent directory of the executable."
        };
    }

    public CreateDebugIdentityCommand() : base("create-debug-identity", "Create and install a temporary package for debugging. Must be called every time the appxmanifest.xml is modified for changes to take effect.")
    {
        Arguments.Add(ExecutableArgument);
        Options.Add(ManifestOption);
        Options.Add(NoInstallOption);
        Options.Add(LocationOption);
        Options.Add(WinSdkRootCommand.VerboseOption);
    }

    public class Handler(IMsixService msixService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var executablePath = parseResult.GetRequiredValue(ExecutableArgument);
            var manifest = parseResult.GetRequiredValue(ManifestOption);
            var noInstall = parseResult.GetValue(NoInstallOption);
            var location = parseResult.GetValue(LocationOption);
            var verbose = parseResult.GetValue(WinSdkRootCommand.VerboseOption);

            if (!File.Exists(executablePath))
            {
                Console.Error.WriteLine($"Executable not found: {executablePath}");
                return 1;
            }

            try
            {
                var result = await msixService.AddMsixIdentityToExeAsync(executablePath, manifest, noInstall, location, verbose, cancellationToken);

                Console.WriteLine("✅ MSIX identity added successfully!");
                Console.WriteLine($"📦 Package: {result.PackageName}");
                Console.WriteLine($"👤 Publisher: {result.Publisher}");
                Console.WriteLine($"🆔 App ID: {result.ApplicationId}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"❌ Failed to add MSIX identity: {error.Message}");
                return 1;
            }

            return 0;
        }
    }
}
