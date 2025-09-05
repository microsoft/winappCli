using System.CommandLine;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class MsixSignCommand : Command
{
    public MsixSignCommand() : base("sign", "Sign an MSIX package with a certificate")
    {
        var msixPathArgument = new Argument<string>("msix-path")
        {
            Description = "Path to the MSIX package to sign"
        };
        var certPathArgument = new Argument<string>("cert-path")
        {
            Description = "Path to the certificate file (PFX format)"
        };
        var passwordOption = new Option<string>("--password")
        {
            Description = "Certificate password",
            DefaultValueFactory = (argumentResult) => "password"
        };
        var timestampOption = new Option<string>("--timestamp")
        {
            Description = "Timestamp server URL"
        };

        Arguments.Add(msixPathArgument);
        Arguments.Add(certPathArgument);
        Options.Add(passwordOption);
        Options.Add(timestampOption);
        Options.Add(Program.VerboseOption);

        SetAction(async (parseResult, ct) =>
        {
            var msixPath = parseResult.GetRequiredValue(msixPathArgument);
            var certPath = parseResult.GetRequiredValue(certPathArgument);
            var password = parseResult.GetValue(passwordOption);
            var timestamp = parseResult.GetValue(timestampOption);
            var verbose = parseResult.GetValue(Program.VerboseOption);

            var certificateService = new CertificateServices();
            try
            {
                await certificateService.SignMsixPackageAsync(msixPath, certPath, password, timestamp, verbose, ct);

                Console.WriteLine($"🔐 Signed package: {msixPath}");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"❌ Failed to sign MSIX package: {error.Message}");
                return 1;
            }
        });
    }
}