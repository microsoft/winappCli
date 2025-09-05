using System;
using System.CommandLine;

namespace Winsdk.Cli.Commands;

internal class MsixAddIdentityCommand : Command
{
    public MsixAddIdentityCommand() : base("add-identity-to-exe", "Add MSIX identity to an existing executable")
    {
        var executableArgument = new Argument<string>("executable")
        {
            Description = "Path to the executable file",
            Arity = ArgumentArity.ExactlyOne

        };
        var manifestPathArgument = new Argument<string>("manifest-path")
        {
            Description = "Path to the application manifest file",
            Arity = ArgumentArity.ExactlyOne
        };
        var tempDirOption = new Option<string>("--temp-dir")
        {
            Description = "Directory for temporary files"
        };

        Arguments.Add(executableArgument);
        Arguments.Add(manifestPathArgument);
        Options.Add(tempDirOption);
        Options.Add(Program.VerboseOption);

        SetAction(async (parseResult, ct) =>
        {
            var executablePath = parseResult.GetRequiredValue(executableArgument);
            var manifestPath = parseResult.GetRequiredValue(manifestPathArgument);
            var tempDir = parseResult.GetValue(tempDirOption);
            var verbose = parseResult.GetValue(Program.VerboseOption);

            if (!File.Exists(executablePath))
            {
                Console.Error.WriteLine($"Executable not found: {executablePath}");
                return 1;
            }

            try
            {
                var msix = new MsixService();
                var result = await msix.AddMsixIdentityToExeAsync(executablePath, manifestPath, tempDir, verbose, ct);

                Console.WriteLine("✅ MSIX identity added successfully!");
                Console.WriteLine($"📦 Package: {result.PackageName}");
                Console.WriteLine($"👤 Publisher: {result.Publisher}");
                Console.WriteLine($"🆔 App ID: {result.ApplicationId}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"❌ Failed to add MSIX identity: ${error.Message}");
                return 1;
            }

            return 0;
        });
    }
}