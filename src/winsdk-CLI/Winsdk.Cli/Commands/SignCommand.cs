using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class SignCommand : Command
{
    public static Argument<string> FilePathArgument { get; }
    public static Argument<string> CertPathArgument { get; }
    public static Option<string> PasswordOption { get; }
    public static Option<string> TimestampOption { get; }

    static SignCommand()
    {
        FilePathArgument = new Argument<string>("file-path")
        {
            Description = "Path to the file/package to sign"
        };
        CertPathArgument = new Argument<string>("cert-path")
        {
            Description = "Path to the certificate file (PFX format)"
        };
        PasswordOption = new Option<string>("--password")
        {
            Description = "Certificate password",
            DefaultValueFactory = (argumentResult) => "password"
        };
        TimestampOption = new Option<string>("--timestamp")
        {
            Description = "Timestamp server URL"
        };
    }

    public SignCommand() : base("sign", "Sign a file/package with a certificate")
    {
        Arguments.Add(FilePathArgument);
        Arguments.Add(CertPathArgument);
        Options.Add(PasswordOption);
        Options.Add(TimestampOption);
    }

    public class Handler(ICertificateService certificateService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var filePath = parseResult.GetRequiredValue(FilePathArgument);
            var certPath = parseResult.GetRequiredValue(CertPathArgument);
            var password = parseResult.GetValue(PasswordOption);
            var timestamp = parseResult.GetValue(TimestampOption);
            var verbose = parseResult.GetValue(WinSdkRootCommand.VerboseOption);

            try
            {
                await certificateService.SignFileAsync(filePath, certPath, password, timestamp, verbose, cancellationToken);

                Console.WriteLine($"🔐 Signed file: {filePath}");
                return 0;
            }
            catch (InvalidOperationException error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"❌ Failed to sign file: {error.Message}");
                return 1;
            }
        }
    }
}
