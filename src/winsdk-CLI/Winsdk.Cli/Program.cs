using System.Text;
using System.CommandLine;
using Winsdk.Cli.Commands;

namespace Winsdk.Cli;

internal static class Program
{
    internal static Option<bool> VerboseOption = new Option<bool>("--verbose", "-v")
    {
        Description = "Enable verbose output"
    };

    static async Task<int> Main(string[] args)
    {
        // Ensure UTF-8 I/O for emoji-capable terminals; fall back silently if not supported
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
            // ignore
        }

        RootCommand rootCommand = new("Windows SDK CLI tool")
        {
            new InitCommand(),
            new RestoreCommand(),
            new PackageCommand(),
            new ManifestCommand(),
            new UpdateCommand(),
            new CreateDebugIdentityCommand(),
            new GetGlobalWinsdkCommand(),
            new CertCommand(),
            new SignCommand(),
            new ToolCommand()
        };

        return await rootCommand.Parse(args).InvokeAsync();
    }

    internal static bool PromptYesNo(string message)
    {
        Console.Write(message);
        var input = Console.ReadLine()?.Trim() ?? "";
        return input.Equals("y", StringComparison.OrdinalIgnoreCase) ||
               input.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
