// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Helpers;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class GetWinsdkPathCommand : Command
{
    public static Option<bool> GlobalOption { get; }

    static GetWinsdkPathCommand()
    {
        GlobalOption = new Option<bool>("--global")
        {
            Description = "Get the global .winsdk directory instead of local"
        };
    }

    public GetWinsdkPathCommand() : base("get-winsdk-path", "Get the path to the .winsdk directory (local by default, global with --global)")
    {
        Options.Add(GlobalOption);
    }

    public class Handler(IWinsdkDirectoryService winsdkDirectoryService, ILogger<GetWinsdkPathCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var global = parseResult.GetValue(GlobalOption);

            try
            {
                DirectoryInfo winsdkDir;
                string directoryType;
                
                if (global)
                {
                    // Get the global .winsdk directory
                    winsdkDir = winsdkDirectoryService.GetGlobalWinsdkDirectory();
                    directoryType = "Global";
                }
                else
                {
                    // Get the local .winsdk directory
                    winsdkDir = winsdkDirectoryService.GetLocalWinsdkDirectory();
                    directoryType = "Local";
                }
                
                // For global directories, check if they exist
                if (global && !winsdkDir.Exists)
                {
                    logger.LogError("{UISymbol} {DirectoryType} .winsdk directory not found: {WinsdkDir}", UiSymbols.Error, directoryType, winsdkDir);
                    logger.LogError("   Make sure to run 'winsdk init' first");
                    return Task.FromResult(1);
                }

                // Output just the path for easy consumption by scripts
                logger.LogInformation("{WinSdkDir}", winsdkDir);

                var status = winsdkDir.Exists ? "exists" : "does not exist";
                logger.LogDebug("{UISymbol} {DirectoryType} .winsdk directory: {WinsdkDir} ({Status})", UiSymbols.Folder, directoryType, winsdkDir, status);

                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error getting {DirectoryType} winsdk directory: {ErrorMessage}", UiSymbols.Error, (global ? "global" : "local"), ex.Message);
                return Task.FromResult(1);
            }
        }
    }
}
