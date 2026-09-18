// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class GetWinappPathCommand : Command, IShortDescription
{
    public string ShortDescription => "Print the path to the global .winapp directory";

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

    public class Handler(IWinappDirectoryService winappDirectoryService, IAnsiConsole console, ILogger<GetWinappPathCommand> logger) : AsynchronousCommandLineAction
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

                    // Fall back to the global cache (which is also where 'winapp install' would
                    // put packages without a local .winapp) and warn the user. See #475.
                    if (!winappDir.Exists)
                    {
                        var globalDir = winappDirectoryService.GetGlobalWinappDirectory();
                        // Write the warning to stderr so stdout stays script-friendly
                        // (callers do `path = $(winapp get-winapp-path)`). Going through
                        // ILogger.LogWarning would route via the static AnsiConsole and
                        // pollute stdout (TextWriterLogger only sends >= Error to stderr).
                        parseResult.InvocationConfiguration.Error.WriteLine(
                            $"{UiSymbols.Warning} No local .winapp directory found; falling back to the global cache at {globalDir.FullName}.");
                        winappDir = globalDir;
                        directoryType = "Global (fallback)";
                    }
                }

                // For global directories, check if they exist
                if (global)
                {
                    try
                    {
                        if (!File.GetAttributes(winappDir.FullName).HasFlag(FileAttributes.Directory))
                        {
                            logger.LogError("{UISymbol} The global cache path is not a directory: {WinappDir}",
                                UiSymbols.Error, winappDir);
                            return Task.FromResult(1);
                        }
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        logger.LogError("{UISymbol} {DirectoryType} .winapp directory not found: {WinappDir}", UiSymbols.Error, directoryType, winappDir);
                        logger.LogError("Choose an existing, permitted directory with WINAPP_CLI_CACHE_DIRECTORY.");
                        return Task.FromResult(1);
                    }
                }

                // Output just the path for easy consumption by scripts. Use IAnsiConsole
                // directly (rather than ILogger) so the path lands cleanly on stdout without
                // any logger formatting and so tests can capture it via TestAnsiConsole.
                console.Profile.Out.Writer.WriteLine(winappDir.FullName);

                var status = winappDir.Exists ? "exists" : "does not exist";
                logger.LogDebug("{UISymbol} {DirectoryType} .winapp directory: {WinappDir} ({Status})", UiSymbols.Folder, directoryType, winappDir, status);

                return Task.FromResult(0);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogError("{UISymbol} Cannot access the {DirectoryType} winapp directory: {ErrorMessage}. Allow access or set WINAPP_CLI_CACHE_DIRECTORY to a permitted directory.",
                    UiSymbols.Error, global ? "global" : "local", ex.Message);
                return Task.FromResult(1);
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error getting {DirectoryType} winapp directory: {ErrorMessage}", UiSymbols.Error, (global ? "global" : "local"), ex.Message);
                return Task.FromResult(1);
            }
        }
    }
}
