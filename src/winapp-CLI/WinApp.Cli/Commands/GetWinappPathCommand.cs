// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class GetWinappPathCommand : Command
{
    public static Option<bool> GlobalOption { get; }

    static GetWinappPathCommand()
    {
        GlobalOption = new Option<bool>("--global")
        {
            Description = "Get the global .winapp directory instead of local"
        };
    }

    public GetWinappPathCommand() : base("get-winapp-path", "Print the path to the .winapp directory. Use --global for the shared cache location, or omit for the project-local .winapp folder. Useful for build scripts that need to reference installed packages.")
    {
        Options.Add(GlobalOption);
    }

    public class Handler(IWinappDirectoryService winappDirectoryService, ILogger<GetWinappPathCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var global = parseResult.GetValue(GlobalOption);

            try
            {
                DirectoryInfo winappDir;
                string directoryType;
                
                if (global)
                {
                    // Get the global .winapp directory
                    winappDir = winappDirectoryService.GetGlobalWinappDirectory();
                    directoryType = "Global";
                }
                else
                {
                    // Get the local .winapp directory
                    winappDir = winappDirectoryService.GetLocalWinappDirectory();
                    directoryType = "Local";
                }
                
                // For global directories, check if they exist
                if (global && !winappDir.Exists)
                {
                    logger.LogError("{UISymbol} {DirectoryType} .winapp directory not found: {WinappDir}", UiSymbols.Error, directoryType, winappDir);
                    logger.LogError("   Make sure to run 'winapp init' first");
                    return Task.FromResult(1);
                }

                // Output just the path for easy consumption by scripts
                logger.LogInformation("{WinappDir}", winappDir);

                var status = winappDir.Exists ? "exists" : "does not exist";
                logger.LogDebug("{UISymbol} {DirectoryType} .winapp directory: {WinappDir} ({Status})", UiSymbols.Folder, directoryType, winappDir, status);

                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error getting {DirectoryType} winapp directory: {ErrorMessage}", UiSymbols.Error, (global ? "global" : "local"), ex.Message);
                return Task.FromResult(1);
            }
        }
    }
}
