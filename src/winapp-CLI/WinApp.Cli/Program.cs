// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Telemetry;
using WinApp.Cli.Telemetry.Events;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace WinApp.Cli;

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
        
        var minimumLogLevel = LogLevel.Information;
        bool quiet = false;
        bool verbose = false;

        if (args.Contains(WinAppRootCommand.VerboseOption.Name) || args.Any(WinAppRootCommand.VerboseOption.Aliases.Contains))
        {
            minimumLogLevel = LogLevel.Debug;
            verbose = true;
        }
        else if (args.Contains(WinAppRootCommand.QuietOption.Name) || args.Any(WinAppRootCommand.QuietOption.Aliases.Contains))
        {
            minimumLogLevel = LogLevel.Warning;
            quiet = true;
        }

        if (quiet && verbose)
        {
            Console.Error.WriteLine($"Cannot specify both --quiet and --verbose options together.");
            return 1;
        }

        var services = new ServiceCollection()
            .ConfigureServices()
            .ConfigureCommands()
            .AddLogging(b =>
            {
                b.ClearProviders();
                b.AddTextWriterLogger([Console.Out], [Console.Error]);
                b.SetMinimumLevel(minimumLogLevel);
            });

        using var serviceProvider = services.BuildServiceProvider();

        var firstRunService = serviceProvider.GetRequiredService<IFirstRunService>();
        firstRunService.CheckAndDisplayFirstRunNotice();

        var rootCommand = serviceProvider.GetRequiredService<WinAppRootCommand>();

        // If no arguments provided, display banner and show help
        if (args.Length == 0)
        {
            BannerHelper.DisplayBanner();
            // Show help by invoking with --help
            await rootCommand.Parse(["--help"]).InvokeAsync();
            return 0;
        }

        var parseResult = rootCommand.Parse(args);

        try
        {
            CommandInvokedEvent.Log(parseResult.CommandResult);

            var returnCode = await parseResult.InvokeAsync();

            CommandCompletedEvent.Log(parseResult.CommandResult, returnCode);

            return returnCode;
        }
        catch (Exception ex)
        {
            TelemetryFactory.Get<ITelemetry>().LogException(parseResult.CommandResult.Command.Name, ex);
            Console.Error.WriteLine($"An unexpected error occurred: {ex.Message}");
            return 1;
        }
    }

    internal static bool PromptYesNo(string message)
    {
        Console.Write(message);
        var input = Console.ReadLine()?.Trim() ?? "";
        return input.Equals("y", StringComparison.OrdinalIgnoreCase) ||
               input.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
