// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class CacheGetPathCommand : Command
{
    public CacheGetPathCommand() : base("get-path", "Get the current packages cache path")
    {
    }

    public class Handler(IWinappDirectoryService winappDirectoryService, ILogger<CacheGetPathCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            try
            {
                var packagesDir = winappDirectoryService.GetPackagesCacheDirectory();

                // Output the path
                logger.LogInformation("{PackagesDir}", packagesDir.FullName);

                if (packagesDir.Exists)
                {
                    logger.LogDebug("{UISymbol} Packages cache directory exists: {PackagesDir}", UiSymbols.Folder, packagesDir.FullName);
                }
                else
                {
                    logger.LogDebug("{UISymbol} Packages cache directory does not exist yet: {PackagesDir}", UiSymbols.Warning, packagesDir.FullName);
                }

                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error getting packages cache path: {ErrorMessage}", UiSymbols.Error, ex.Message);
                return Task.FromResult(1);
            }
        }
    }
}
