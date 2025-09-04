using System;
using System.CommandLine;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class CertInstallCommand : Command
{
    public CertInstallCommand()
        : base("install", "Install a certificate to the local machine store")
    {
        var certPathArgument = new Argument<string>("cert-path")
        {
            Description = "Path to the certificate file (PFX or CER)"
        };
        var passwordOption = new Option<string>("--password")
        {
            Description = "Password for the PFX file",
            DefaultValueFactory = (argumentResult) => "password",
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Force installation even if the certificate already exists",
            DefaultValueFactory = (argumentResult) => false,
        };
        Arguments.Add(certPathArgument);
        Options.Add(passwordOption);
        Options.Add(forceOption);
        Options.Add(Program.VerboseOption);

        SetAction(async (parseResult, ct) =>
        {
            var certPath = parseResult.GetRequiredValue(certPathArgument);
            var password = parseResult.GetRequiredValue(passwordOption);
            var force = parseResult.GetRequiredValue(forceOption);
            var verbose = parseResult.GetValue(Program.VerboseOption);
            try
            {
                var certificateService = new CertificateServices();
                var result = await certificateService.InstallCertificateAsync(certPath, password, force, verbose);
                if (!result)
                {
                    Console.WriteLine($"ℹ️ Certificate is already installed");
                }
                else
                {
                    Console.WriteLine($"✅ Certificate installed successfully!");
                }

                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"❌ Failed to install certificate: {error.Message}");
                return 1;
            }
        });
    }
}