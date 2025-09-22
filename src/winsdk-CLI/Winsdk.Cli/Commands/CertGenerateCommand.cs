using System.CommandLine;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class CertGenerateCommand : Command
{
    private readonly CertificateServices _certificateService;
    private readonly MsixService _msixService;

    public CertGenerateCommand()
        : base("generate", "Generate a new development certificate")
    {
        var configService = new ConfigService(Directory.GetCurrentDirectory());
        var buildToolsService = new BuildToolsService(configService);
        _certificateService = new CertificateServices(buildToolsService);
        _msixService = new MsixService(buildToolsService);
        
        var publisherOption = new Option<string>("--publisher")
        {
            Description = "Publisher name for the generated certificate. If not specified, will be inferred from manifest."
        };
        var manifestOption = new Option<string>("--manifest")
        {
            Description = "Path to appxmanifest.xml file to extract publisher information from"
        };
        manifestOption.AcceptLegalFilePathsOnly();
        var outputOption = new Option<string>("--output")
        {
            Description = "Output path for the generated PFX file",
            DefaultValueFactory = (argumentResult) => CertificateServices.DefaultCertFileName
        };
        outputOption.AcceptLegalFileNamesOnly();
        var passwordOption = new Option<string>("--password")
        {
            Description = "Password for the generated PFX file",
            DefaultValueFactory = (argumentResult) => "password",
        };
        var validDaysOption = new Option<int>("--valid-days")
        {
            Description = "Number of days the certificate is valid",
            DefaultValueFactory = (argumentResult) => 365,
        };
        var installOption = new Option<bool>("--install")
        {
            Description = "Install the certificate to the local machine store after generation",
            DefaultValueFactory = (argumentResult) => false,
        };

        Options.Add(publisherOption);
        Options.Add(manifestOption);
        Options.Add(outputOption);
        Options.Add(passwordOption);
        Options.Add(validDaysOption);
        Options.Add(installOption);
        Options.Add(Program.VerboseOption);

        SetAction(async (parseResult, ct) =>
        {
            var publisher = parseResult.GetValue(publisherOption);
            var manifestPath = parseResult.GetValue(manifestOption);
            var output = parseResult.GetRequiredValue(outputOption);
            var password = parseResult.GetRequiredValue(passwordOption);
            var validDays = parseResult.GetRequiredValue(validDaysOption);
            var install = parseResult.GetRequiredValue(installOption);
            var verbose = parseResult.GetValue(Program.VerboseOption);

            // Check if certificate file already exists
            if (File.Exists(output))
            {
                Console.Error.WriteLine($"❌ Certificate file already exists: {output}");
                Console.Error.WriteLine("Please specify a different output path or remove the existing file.");
                return 1;
            }

            // Use the consolidated certificate generation method with all console output and error handling
            await _certificateService.GenerateDevCertificateWithInferenceAsync(
                outputPath: output,
                explicitPublisher: publisher,
                manifestPath: manifestPath,
                password: password,
                validDays: validDays,
                skipIfExists: false, // We already checked above
                updateGitignore: true,
                install: install,
                quiet: false,
                verbose: verbose,
                cancellationToken: ct);

            return 0;
        });
    }

}
