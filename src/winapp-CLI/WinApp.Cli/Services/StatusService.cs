// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;
using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

/// <summary>
/// Service for managing Spectre.Console status displays with ILogger integration.
/// Uses manual rendering with cursor manipulation instead of Live display.
/// </summary>
internal class StatusService(IAnsiConsole ansiConsole, ILogger<StatusService> logger) : IStatusService
{
    public async Task<int> ExecuteWithStatusAsync<T>(string inProgressMessage, Func<TaskContext, CancellationToken, Task<(int ReturnCode, T CompletedMessage)>> taskFunc, CancellationToken cancellationToken)
    {
        using var signal = new LiveUpdateSignal();
        GroupableTask<(int ReturnCode, T CompletedMessage)> task = new(inProgressMessage, null, taskFunc, ansiConsole, logger, signal);

        (int ReturnCode, T CompletedMessage)? result = default;

        // Start the task execution on a separate thread
        var taskExecution = Task.Run(async () =>
        {
            return await task.ExecuteAsync(signal.SignalRefresh, cancellationToken);
        }, cancellationToken);

        // Track rendered line count for cursor restoration
        var lastLineCount = 0;

        // Hide cursor during animation
        if (!Console.IsOutputRedirected)
        {
            ansiConsole.Cursor.Hide();
        }

        try
        {
            // Main loop: render and handle signals
            while (!taskExecution.IsCompleted)
            {
                IRenderable rendered;
                int lineCount;
                // Render current state
                lock (signal.Lock)
                {
                    (rendered, lineCount) = task.Render();
                }

                // Move cursor up to overwrite previous render (if any)
                if (lastLineCount > 0 && ansiConsole.Profile.Capabilities.Ansi)
                {
                    ansiConsole.Cursor.MoveUp(lastLineCount);
                    //console.Write("\x1b[J"); // Clear from cursor to end of screen
                }

                int beforeRenderTop = 0;
                if (!Console.IsOutputRedirected)
                {
                    beforeRenderTop = Console.CursorTop;
                }

                // Write the new render
                ansiConsole.Write(rendered);
                int afterRenderTop = 0;
                if (!Console.IsOutputRedirected)
                {
                    afterRenderTop = Console.CursorTop;
                }
                else
                {
                    // For test environments without a real console, estimate cursor position
                    afterRenderTop = lineCount;
                }

                // There is something very weird going on with Spectre.Console rendering where sometimes the cursor ends up in the wrong place.
                if (afterRenderTop != lineCount + beforeRenderTop)
                {
                    var diff = afterRenderTop - (lineCount + beforeRenderTop);
                    ansiConsole.Cursor.MoveUp(diff);
                }

                ansiConsole.Write("\x1b[J"); // Clear from cursor to end of screen

                //console.Profile.Out.Writer.Flush();
                lastLineCount = lineCount;

                // Wait for a signal or timeout
                var signalType = await signal.WaitForSignalAsync(cancellationToken);

                // Handle prompt if requested
                if (signalType == LiveUpdateType.Prompt && !taskExecution.IsCompleted)
                {
                    var promptRequest = signal.GetPendingPromptAndReset();
                    if (promptRequest != null)
                    {
                        try
                        {
                            // Show cursor for user input
                            if (!Console.IsOutputRedirected)
                            {
                                ansiConsole.Cursor.Show();
                            }

                            int beforePromptTop;
                            if (!Console.IsOutputRedirected)
                            {
                                beforePromptTop = Console.CursorTop;
                            }
                            else
                            {
                                beforePromptTop = 0;
                            }

                            // Execute the prompt
                            var promptResult = await promptRequest.Prompt.ShowAsync(ansiConsole, promptRequest.CancellationToken);
                            promptRequest.SetResult(promptResult);

                            if (!Console.IsOutputRedirected)
                            {
                                var afterPromptTop = Console.CursorTop;

                                // Move cursor up once to overwrite prompt result line
                                var diff = afterPromptTop - beforePromptTop;
                                ansiConsole.Cursor.MoveUp(diff);

                                // Hide cursor again
                                ansiConsole.Cursor.Hide();
                            }

                            // Move cursor back up past the prompt result line and current render
                            if (ansiConsole.Profile.Capabilities.Ansi)
                            {
                                ansiConsole.Cursor.MoveUp(lastLineCount);
                                //console.Write("\x1b[J");
                            }

                            lastLineCount = 0; // Reset since we cleared
                        }
                        catch (OperationCanceledException)
                        {
                            promptRequest.SetCancelled();
                        }
                        catch (Exception ex)
                        {
                            promptRequest.SetException(ex);
                        }
                    }
                }
            }
        }
        finally
        {
            // Always show cursor when done
            if (!Console.IsOutputRedirected)
            {
                ansiConsole.Cursor.Show();
            }
        }

        // Get the final result
        try
        {
            result = await taskExecution;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }

        // Clear last in-progress render and show final state
        if (lastLineCount > 0 && ansiConsole.Profile.Capabilities.Ansi)
        {
            ansiConsole.Cursor.MoveUp(lastLineCount);
            //console.Write("\x1b[J");
        }

        // Final render to show completed state
        ansiConsole.Write(task.Render().Item1);

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
