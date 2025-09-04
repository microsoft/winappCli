using System;
using System.CommandLine;

namespace Winsdk.Cli.Commands;

internal class MsixPackageCommand : Command
{

    public MsixPackageCommand()
        : base("package", "Create an MSIX package from a prepared package directory")
    {
        var inputFolderArgument = new Argument<string>("input-folder")
        {
            Description = "Input folder with package layout"
        };
        var outputFolderArgument = new Argument<string>("output-folder")
        {
            Description = "Output folder for the generated package",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = (argumentResult) => Directory.GetCurrentDirectory()
        };

        Arguments.Add(inputFolderArgument);
        Arguments.Add(outputFolderArgument);

        var nameOption = new Option<string?>("--name")
        {
            Description = "Package name (default: from manifest)"
        };
        var skipPriOption = new Option<bool>("--skip-pri")
        {
            Description = "Skip PRI file generation"
        };
        var certOption = new Option<string?>("--cert")
        {
            Description = "Path to signing certificate (will auto-sign if provided)"
        };
        var certPasswordOption = new Option<string>("--cert-password")
        {
            Description = "Certificate password (default: password)",
            DefaultValueFactory = (argumentResult) => "password"
        };
        var generateCertOption = new Option<bool>("--generate-cert")
        {
            Description = "Generate a new development certificate"
        };
        var installCertOption = new Option<bool>("--install-cert")
        {
            Description = "Install certificate to machine"
        };
        var publisherOption = new Option<string?>("--publisher")
        {
            Description = "Publisher name for certificate generation"
        };

        Options.Add(nameOption);
        Options.Add(skipPriOption);
        Options.Add(certOption);
        Options.Add(certPasswordOption);
        Options.Add(generateCertOption);
        Options.Add(installCertOption);
        Options.Add(publisherOption);
        Options.Add(Program.VerboseOption);

        SetAction(async (parseResult, ct) =>
        {
            var inputFolder = parseResult.GetRequiredValue(inputFolderArgument);
            var outputFolder = parseResult.GetRequiredValue(outputFolderArgument);
            var name = parseResult.GetValue(nameOption);
            var skipPri = parseResult.GetValue(skipPriOption);
            var certPath = parseResult.GetValue(certOption);
            var certPassword = parseResult.GetRequiredValue(certPasswordOption);
            var generateCert = parseResult.GetValue(generateCertOption);
            var installCert = parseResult.GetValue(installCertOption);
            var publisher = parseResult.GetValue(publisherOption);
            var verbose = parseResult.GetValue(Program.VerboseOption);

            try
            {
                // Auto-sign if certificate is provided or if generate-cert is specified
                var autoSign = !string.IsNullOrEmpty(certPath) || generateCert;

                var msix = new MsixService();

                var result = await msix.CreateMsixPackageAsync(inputFolder, outputFolder, name, skipPri, autoSign, certPath, certPassword, generateCert, installCert, publisher, verbose, ct);

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
                return 1;
            }
        });
    }
}