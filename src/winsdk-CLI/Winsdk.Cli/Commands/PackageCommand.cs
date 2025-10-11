using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class PackageCommand : Command
{
    public static Argument<string> InputFolderArgument { get; }
    public static Option<string> OutputFolderOption { get; }
    public static Option<string?> NameOption { get; }
    public static Option<bool> SkipPriOption { get; }
    public static Option<string?> CertOption { get; }
    public static Option<string> CertPasswordOption { get; }
    public static Option<bool> GenerateCertOption { get; }
    public static Option<bool> InstallCertOption { get; }
    public static Option<string?> PublisherOption { get; }
    public static Option<string?> ManifestOption { get; }
    public static Option<bool> SelfContainedOption { get; }

    static PackageCommand()
    {
        InputFolderArgument = new Argument<string>("input-folder")
        {
            Description = "Input folder with package layout (default: .winsdk folder in current project)",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = (argumentResult) =>
            {
                // Try to find .winsdk directory in current project
                var projectManifest = MsixService.FindProjectManifest();
                if (projectManifest != null)
                {
                    return Path.GetDirectoryName(projectManifest)!; // Return manifest directory
                }
                return Directory.GetCurrentDirectory(); // Fallback to current directory
            }
        };
        OutputFolderOption = new Option<string>("--output-folder")
        {
            Description = "Output folder for the generated package",
            DefaultValueFactory = (argumentResult) => Directory.GetCurrentDirectory()
        };

        NameOption = new Option<string?>("--name")
        {
            Description = "Package name (default: from manifest)"
        };
        SkipPriOption = new Option<bool>("--skip-pri")
        {
            Description = "Skip PRI file generation"
        };
        CertOption = new Option<string?>("--cert")
        {
            Description = "Path to signing certificate (will auto-sign if provided)"
        };
        CertPasswordOption = new Option<string>("--cert-password")
        {
            Description = "Certificate password (default: password)",
            DefaultValueFactory = (argumentResult) => "password"
        };
        GenerateCertOption = new Option<bool>("--generate-cert")
        {
            Description = "Generate a new development certificate"
        };
        InstallCertOption = new Option<bool>("--install-cert")
        {
            Description = "Install certificate to machine"
        };
        PublisherOption = new Option<string?>("--publisher")
        {
            Description = "Publisher name for certificate generation"
        };
        ManifestOption = new Option<string?>("--manifest")
        {
            Description = "Path to AppX manifest file (default: auto-detect from input folder or current directory)"
        };
        SelfContainedOption = new Option<bool>("--self-contained")
        {
            Description = "Bundle Windows App SDK runtime for self-contained deployment"
        };
    }

    public PackageCommand()
        : base("package", "Create an MSIX package from a prepared package directory")
    {
        Arguments.Add(InputFolderArgument);
        Options.Add(OutputFolderOption);
        Options.Add(NameOption);
        Options.Add(SkipPriOption);
        Options.Add(CertOption);
        Options.Add(CertPasswordOption);
        Options.Add(GenerateCertOption);
        Options.Add(InstallCertOption);
        Options.Add(PublisherOption);
        Options.Add(ManifestOption);
        Options.Add(SelfContainedOption);
        Options.Add(WinSdkRootCommand.VerboseOption);
    }

    public class Handler(IMsixService msixService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var inputFolder = parseResult.GetValue(InputFolderArgument) ?? 
                              (MsixService.FindProjectManifest() != null ? 
                               Path.GetDirectoryName(MsixService.FindProjectManifest()!)! : 
                               Directory.GetCurrentDirectory());
            var outputFolder = parseResult.GetRequiredValue(OutputFolderOption);
            var name = parseResult.GetValue(NameOption);
            var skipPri = parseResult.GetValue(SkipPriOption);
            var certPath = parseResult.GetValue(CertOption);
            var certPassword = parseResult.GetRequiredValue(CertPasswordOption);
            var generateCert = parseResult.GetValue(GenerateCertOption);
            var installCert = parseResult.GetValue(InstallCertOption);
            var publisher = parseResult.GetValue(PublisherOption);
            var manifestPath = parseResult.GetValue(ManifestOption);
            var selfContained = parseResult.GetValue(SelfContainedOption);
            var verbose = parseResult.GetValue(WinSdkRootCommand.VerboseOption);

            try
            {
                // Auto-sign if certificate is provided or if generate-cert is specified
                var autoSign = !string.IsNullOrEmpty(certPath) || generateCert;

                var result = await msixService.CreateMsixPackageAsync(inputFolder, outputFolder, name, skipPri, autoSign, certPath, certPassword, generateCert, installCert, publisher, manifestPath, selfContained, verbose, cancellationToken);

                Console.WriteLine("✅ MSIX package created successfully!");

                Console.WriteLine($"📦 Package: {result.MsixPath}");
                if (result.Signed)
                {
                    Console.WriteLine($"🔐 Package has been signed");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to create MSIX package: {ex.Message}");
                if (verbose)
                {
                    Console.Error.WriteLine(ex.StackTrace);
                }
                return 1;
            }
        }
    }
}
