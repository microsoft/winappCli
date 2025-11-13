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
    public CacheGetPathCommand() : base("get-path", "Get the current package cache directory path")
    {
    }

    public class Handler(ICacheService cacheService, ILogger<CacheGetPathCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            try
            {
                var cacheDir = cacheService.GetCacheDirectory();
                var customLocation = cacheService.GetCustomCacheLocation();
                
                if (!string.IsNullOrEmpty(customLocation))
                {
                    logger.LogInformation("{UISymbol} Package cache location (custom): {Path}", UiSymbols.Folder, cacheDir.FullName);
                }
                else
                {
                    logger.LogInformation("{UISymbol} Package cache location (default): {Path}", UiSymbols.Folder, cacheDir.FullName);
                }

                if (!cacheDir.Exists)
                {
                    logger.LogInformation("{UISymbol} Cache directory does not exist yet", UiSymbols.Note);
                }

                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error getting cache path: {ErrorMessage}", UiSymbols.Error, ex.Message);
                logger.LogDebug("Stack Trace: {StackTrace}", ex.StackTrace);
                return Task.FromResult(1);
            }
        }
    }
}
