// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class CacheMoveCommand : Command
{
    public static Argument<string> PathArgument { get; }

    static CacheMoveCommand()
    {
        PathArgument = new Argument<string>("path")
        {
            Description = "The new path for the package cache"
        };
    }

    public CacheMoveCommand() : base("move", "Move the package cache to a new location")
    {
        Arguments.Add(PathArgument);
    }

    public class Handler(ICacheService cacheService, ILogger<CacheMoveCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var newPath = parseResult.GetRequiredValue(PathArgument);

            try
            {
                await cacheService.MoveCacheAsync(newPath, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Operation cancelled");
                return 1;
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error moving cache: {ErrorMessage}", UiSymbols.Error, ex.Message);
                logger.LogDebug("Stack Trace: {StackTrace}", ex.StackTrace);
                return 1;
            }
        }
    }
}
