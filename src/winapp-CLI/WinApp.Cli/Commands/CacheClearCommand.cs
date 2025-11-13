// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class CacheClearCommand : Command
{
    public static Option<bool> ForceOption { get; }

    static CacheClearCommand()
    {
        ForceOption = new Option<bool>("--force", "-f")
        {
            Description = "Skip confirmation prompt"
        };
    }

    public CacheClearCommand() : base("clear", "Clear the packages cache")
    {
        Options.Add(ForceOption);
    }

    public class Handler(IWinappDirectoryService winappDirectoryService, ILogger<CacheClearCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var force = parseResult.GetValue(ForceOption);

            try
            {
                var packagesDir = winappDirectoryService.GetPackagesCacheDirectory();

                if (!packagesDir.Exists)
                {
                    logger.LogInformation("{UISymbol} Packages cache does not exist: {PackagesDir}", UiSymbols.Info, packagesDir.FullName);
                    return Task.FromResult(0);
                }

                // Confirm deletion unless --force is used
                if (!force)
                {
                    logger.LogWarning("{UISymbol} This will delete all cached packages from: {PackagesDir}",
                        UiSymbols.Warning, packagesDir.FullName);
                    var confirm = Program.PromptYesNo("Are you sure you want to continue? (y/n): ");
                    if (!confirm)
                    {
                        logger.LogInformation("Operation cancelled by user");
                        return Task.FromResult(1);
                    }
                }

                // Delete the packages directory
                logger.LogInformation("{UISymbol} Clearing packages cache...", UiSymbols.Package);
                try
                {
                    packagesDir.Delete(true);
                    logger.LogInformation("{UISymbol} Packages cache cleared successfully", UiSymbols.Check);
                }
                catch (Exception ex)
                {
                    logger.LogError("{UISymbol} Failed to clear cache: {ErrorMessage}", UiSymbols.Error, ex.Message);
                    return Task.FromResult(1);
                }

                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error clearing cache: {ErrorMessage}", UiSymbols.Error, ex.Message);
                return Task.FromResult(1);
            }
        }
    }
}
