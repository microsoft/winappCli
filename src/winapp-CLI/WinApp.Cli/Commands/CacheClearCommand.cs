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
    public CacheClearCommand() : base("clear", "Clear all contents of the package cache")
    {
    }

    public class Handler(ICacheService cacheService, ILogger<CacheClearCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            try
            {
                // Confirm before clearing
                logger.LogWarning("This will delete all cached packages. Are you sure?");
                if (!Program.PromptYesNo("Continue? (y/n): "))
                {
                    logger.LogInformation("Operation cancelled");
                    return 1;
                }

                await cacheService.ClearCacheAsync(cancellationToken);
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error clearing cache: {ErrorMessage}", UiSymbols.Error, ex.Message);
                logger.LogDebug("Stack Trace: {StackTrace}", ex.StackTrace);
                return 1;
            }
        }
    }
}
