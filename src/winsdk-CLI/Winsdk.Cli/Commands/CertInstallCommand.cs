using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class CertInstallCommand : Command
{
    public static Argument<string> CertPathArgument { get; }
    public static Option<string> PasswordOption { get; }
    public static Option<bool> ForceOption { get; }

    static CertInstallCommand()
    {
        CertPathArgument = new Argument<string>("cert-path")
        {
            Description = "Path to the certificate file (PFX or CER)"
        };
        PasswordOption = new Option<string>("--password")
        {
            Description = "Password for the PFX file",
            DefaultValueFactory = (argumentResult) => "password",
        };
        ForceOption = new Option<bool>("--force")
        {
            Description = "Force installation even if the certificate already exists",
            DefaultValueFactory = (argumentResult) => false,
        };
    }

    public CertInstallCommand()
        : base("install", "Install a certificate to the local machine store")
    {
        Arguments.Add(CertPathArgument);
        Options.Add(PasswordOption);
        Options.Add(ForceOption);
    }

    public class Handler(ICertificateService certificateService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var certPath = parseResult.GetRequiredValue(CertPathArgument);
            var password = parseResult.GetRequiredValue(PasswordOption);
            var force = parseResult.GetRequiredValue(ForceOption);
            var verbose = parseResult.GetValue(WinSdkRootCommand.VerboseOption);

            try
            {
                var result = await certificateService.InstallCertificateAsync(certPath, password, force, verbose, cancellationToken);
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
        }
    }
}
