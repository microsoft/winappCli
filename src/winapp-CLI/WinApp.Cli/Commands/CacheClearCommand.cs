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
    public CacheClearCommand() : base("clear", "Clear the packages cache")
    {
    }

    public class Handler(IWinappDirectoryService winappDirectoryService, ILogger<CacheClearCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            try
            {
                var cacheDir = winappDirectoryService.GetPackagesCacheDirectory();

                if (!cacheDir.Exists)
                {
                    logger.LogInformation("{UISymbol} Cache directory does not exist: {CacheDir}", UiSymbols.Info, cacheDir.FullName);
                    return Task.FromResult(0);
                }

                logger.LogWarning("{UISymbol} This will delete all packages from: {CacheDir}", UiSymbols.Warning, cacheDir.FullName);
                
                if (!Program.PromptYesNo("Continue? (y/n): "))
                {
                    logger.LogError("{UISymbol} Operation cancelled", UiSymbols.Error);
                    return Task.FromResult(1);
                }

                // Delete all contents of the cache directory
                logger.LogInformation("{UISymbol} Clearing cache...", UiSymbols.Package);
                
                foreach (var item in cacheDir.GetFileSystemInfos())
                {
                    if (item is DirectoryInfo dir)
                    {
                        dir.Delete(recursive: true);
                        logger.LogDebug("  Deleted directory: {ItemName}", item.Name);
                    }
                    else if (item is FileInfo file)
                    {
                        file.Delete();
                        logger.LogDebug("  Deleted file: {ItemName}", item.Name);
                    }
                }

                logger.LogInformation("{UISymbol} Cache cleared successfully", UiSymbols.Check);

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
