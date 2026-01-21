// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;
using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

/// <summary>
/// Service for managing Spectre.Console status displays with ILogger integration.
/// Uses Spectre.Console Live display for automatic terminal handling.
/// </summary>
internal class StatusService(IAnsiConsole ansiConsole, ILogger<StatusService> logger) : IStatusService
{
    public async Task<int> ExecuteWithStatusAsync<T>(string inProgressMessage, Func<TaskContext, CancellationToken, Task<(int ReturnCode, T CompletedMessage)>> taskFunc, CancellationToken cancellationToken)
    {
        var renderLock = new Lock();
        GroupableTask<(int ReturnCode, T CompletedMessage)> task = new(inProgressMessage, null, taskFunc, ansiConsole, logger, renderLock);

        // Start the task execution
        var taskExecution = task.ExecuteAsync(null, cancellationToken);

        IRenderable rendered;

        (int ReturnCode, T CompletedMessage)? result = null;
        if (Environment.UserInteractive && !Console.IsOutputRedirected)
        {
            rendered = task.Render();
            // Run the Live display until task completes
            await ansiConsole.Live(rendered)
                .AutoClear(true)
                .Overflow(VerticalOverflow.Crop)
                .Cropping(VerticalOverflowCropping.Top)
                .StartAsync(async ctx =>
                {
                    while (!taskExecution.IsCompleted)
                    {
                        lock (renderLock)
                        {
                            rendered = task.Render();
                            ctx.UpdateTarget(rendered);
                        }
                        ctx.Refresh();

                        // Wait for animation refresh (100ms) or task completion
                        await Task.WhenAny(taskExecution, Task.Delay(100, cancellationToken));
                    }
                });
        }
        else
        {
            // if output is redirected, just wait for the task to complete without live rendering
            try
            {
                result = await taskExecution;
            }
            catch (OperationCanceledException)
            {
            }
        }

        // Final render to show completed state
        lock (renderLock)
        {
            rendered = task.Render();
        }

        ansiConsole.Write(rendered);

        // Get the result
        try
        {
            result = await taskExecution;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }

        if (result != null)
        {
            if (result.Value.ReturnCode != 0)
            {
                logger.LogError("Task failed with return code {ReturnCode}, message: {CompletedMessage}", result.Value.ReturnCode, result.Value.CompletedMessage);
            }
            else
            {
                logger.LogDebug("Task completed successfully with message: {CompletedMessage}", result.Value.CompletedMessage);
            }
            return result.Value.ReturnCode;
        }

        return 1;
    }
}
