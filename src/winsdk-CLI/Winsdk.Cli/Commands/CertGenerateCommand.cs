using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class CertGenerateCommand : Command
{
    public static Option<string> PublisherOption { get; }
    public static Option<string> ManifestOption { get; }
    public static Option<string> OutputOption { get; }
    public static Option<string> PasswordOption { get; }
    public static Option<int> ValidDaysOption { get; }
    public static Option<bool> InstallOption { get; }
    public static Option<IfExists> IfExistsOption { get; }

    internal enum IfExists
    {
        Error,
        Overwrite,
        Skip
    }

    static CertGenerateCommand()
    {
        PublisherOption = new Option<string>("--publisher")
        {
            Description = "Publisher name for the generated certificate. If not specified, will be inferred from manifest."
        };
        ManifestOption = new Option<string>("--manifest")
        {
            Description = "Path to appxmanifest.xml file to extract publisher information from"
        };
        ManifestOption.AcceptLegalFilePathsOnly();
        OutputOption = new Option<string>("--output")
        {
            Description = "Output path for the generated PFX file",
            DefaultValueFactory = (argumentResult) => CertificateService.DefaultCertFileName
        };
        OutputOption.AcceptLegalFileNamesOnly();
        PasswordOption = new Option<string>("--password")
        {
            Description = "Password for the generated PFX file",
            DefaultValueFactory = (argumentResult) => "password",
        };
        ValidDaysOption = new Option<int>("--valid-days")
        {
            Description = "Number of days the certificate is valid",
            DefaultValueFactory = (argumentResult) => 365,
        };
        InstallOption = new Option<bool>("--install")
        {
            Description = "Install the certificate to the local machine store after generation",
            DefaultValueFactory = (argumentResult) => false,
        };
        IfExistsOption = new Option<IfExists> ("--if-exists")
        {
            Description = "Skip generation if the certificate file already exists",
            DefaultValueFactory = (argumentResult) => IfExists.Error,
        };
    }

    public CertGenerateCommand()
        : base("generate", "Generate a new development certificate")
    {
        Options.Add(PublisherOption);
        Options.Add(ManifestOption);
        Options.Add(OutputOption);
        Options.Add(PasswordOption);
        Options.Add(ValidDaysOption);
        Options.Add(InstallOption);
        Options.Add(IfExistsOption);
    }

    public class Handler(ICertificateService certificateService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var publisher = parseResult.GetValue(PublisherOption);
            var manifestPath = parseResult.GetValue(ManifestOption);
            var output = parseResult.GetRequiredValue(OutputOption);
            var password = parseResult.GetRequiredValue(PasswordOption);
            var validDays = parseResult.GetRequiredValue(ValidDaysOption);
            var install = parseResult.GetRequiredValue(InstallOption);
            var ifExists = parseResult.GetRequiredValue(IfExistsOption);
            var verbose = parseResult.GetValue(WinSdkRootCommand.VerboseOption);

            // Check if certificate file already exists
            if (File.Exists(output))
            {
                Console.Error.WriteLine($"❌ Certificate file already exists: {output}");
                if (ifExists == IfExists.Error)
                {
                    Console.Error.WriteLine("Please specify a different output path or remove the existing file.");
                    return 1;
                }
                else if (ifExists == IfExists.Skip)
                {
                    return 0;
                }
                else if (ifExists == IfExists.Overwrite)
                {
                    Console.WriteLine($"⚠️ Overwriting existing certificate file: {output}");
                }
            }

            // Use the consolidated certificate generation method with all console output and error handling
            await certificateService.GenerateDevCertificateWithInferenceAsync(
                outputPath: output,
                explicitPublisher: publisher,
                manifestPath: manifestPath,
                password: password,
                validDays: validDays,
                skipIfExists: false,
                updateGitignore: true,
                install: install,
                quiet: false,
                verbose: verbose,
                cancellationToken: cancellationToken);

            return 0;
        }
    }
}
