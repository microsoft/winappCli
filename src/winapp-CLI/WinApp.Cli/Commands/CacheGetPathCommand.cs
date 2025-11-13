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
    public CacheGetPathCommand() : base("get-path", "Get the current path of the packages cache directory")
    {
    }

    public class Handler(IWinappDirectoryService winappDirectoryService, ILogger<CacheGetPathCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            try
            {
                var packagesDir = winappDirectoryService.GetPackagesCacheDirectory();

                // Output just the path for easy consumption by scripts
                logger.LogInformation("{PackagesDir}", packagesDir.FullName);

                var status = packagesDir.Exists ? "exists" : "does not exist";
                logger.LogDebug("{UISymbol} Packages cache directory: {PackagesDir} ({Status})", UiSymbols.Folder, packagesDir.FullName, status);

                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error getting packages cache directory: {ErrorMessage}", UiSymbols.Error, ex.Message);
                return Task.FromResult(1);
            }
        }
    }
}
