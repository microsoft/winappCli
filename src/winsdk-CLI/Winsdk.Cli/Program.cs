using Microsoft.Extensions.DependencyInjection;
using System.Text;
using Winsdk.Cli.Commands;
using Winsdk.Cli.Helpers;

namespace Winsdk.Cli;

internal static class Program
{
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

        var services = new ServiceCollection()
            .ConfigureServices()
            .ConfigureCommands();

        using var serviceProvider = services.BuildServiceProvider();

        var rootCommand = serviceProvider.GetRequiredService<WinSdkRootCommand>();

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
