using System.CommandLine;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class CertGenerateCommand : Command
{
    public CertGenerateCommand()
        : base("generate", "Generate a new development certificate")
    {
        var publisherOption = new Option<string>("--publisher")
        {
            Description = "Publisher name for the generated certificate",
            Required = true
        };
        var outputOption = new Option<string>("--output")
        {
            Description = "Output path for the generated PFX file",
            DefaultValueFactory = (argumentResult) => "dev-devcert.pfx"
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

        Options.Add(publisherOption);
        Options.Add(outputOption);
        Options.Add(passwordOption);
        Options.Add(validDaysOption);
        Options.Add(Program.VerboseOption);

        SetAction(async (parseResult, ct) =>
        {
            var publisher = parseResult.GetRequiredValue(publisherOption);
            var output = parseResult.GetRequiredValue(outputOption);
            var password = parseResult.GetRequiredValue(passwordOption);
            var validDays = parseResult.GetRequiredValue(validDaysOption);
            var verbose = parseResult.GetValue(Program.VerboseOption);

            try
            {
                var certificateService = new CertificateServices();

                var result = await certificateService.GenerateDevCertificateAsync(publisher, output, password, validDays, verbose);

                Console.WriteLine("✅ Certificate generated successfully!");
                Console.WriteLine($"🔐 Certificate: {result.CertificatePath}");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"❌ Failed to generate certificate: {error.Message}");
                return 1;
            }
        });
    }
}