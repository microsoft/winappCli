// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Service for managing Spectre.Console status displays with ILogger integration.
/// Uses a special logger scope to trigger status updates.
/// </summary>
internal class StatusService(AnsiConsoleContext ansiConsoleContext, ILogger<StatusService> logger) : IStatusService
{
    public async Task<int> ExecuteWithStatusAsync<T>(string inProgressMessage, Func<TaskContext, Task<(int ReturnCode, T CompletedMessage)>> taskFunc)
    {
        GroupableTask<(int ReturnCode, T CompletedMessage)> task = new(inProgressMessage, null, taskFunc, ansiConsoleContext, logger);

        (int ReturnCode, T CompletedMessage)? result = default;

        await ansiConsoleContext.AnsiConsole.Live(task.Render())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Visible)
            .Cropping(VerticalOverflowCropping.Top)
            .StartAsync(async liveCtx =>
            {
                result = await task.ExecuteAsync(() =>
                {
                    liveCtx.UpdateTarget(task.Render());
                    liveCtx.Refresh();
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);

        if (result != null)
        {
            if (result.Value.ReturnCode != 0)
            {
                logger.LogError("Task failed with return code {ReturnCode}, message: {CompletedMessage}", result.Value.ReturnCode, result.Value.CompletedMessage);
            }
            else
            {
                logger.LogInformation("Task completed successfully with message: {CompletedMessage}", result.Value.CompletedMessage);
            }
            return result.Value.ReturnCode;
        }

        return 1;
    }
}
