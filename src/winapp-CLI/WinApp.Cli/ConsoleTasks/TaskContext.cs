// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.ConsoleTasks;

internal class TaskContext(GroupableTask task, Action? onUpdate, AnsiConsoleContext ansiConsoleContext, ILogger logger)
{
    public async Task<T?> AddSubTaskAsync<T>(string inProgressMessage, Func<TaskContext, Task<T>> taskFunc)
    {
        var subTask = new GroupableTask<T>(inProgressMessage, task, taskFunc, ansiConsoleContext, logger);
        task.SubTasks.Add(subTask);
        return await subTask.ExecuteAsync(onUpdate, startSpinner: false);
    }

    public void AddStatusMessage(string message)
    {
        if (!char.IsPunctuation(message, 0) && !char.IsSymbol(message, 0))
        {
            message = $"{UiSymbols.Info} {message}";
        }
        var subTask = new StatusMessageTask(message, task, ansiConsoleContext, logger);
        task.SubTasks.Add(subTask);
    }

    public void AddDebugMessage(string message)
    {
        // Only update status and log if verbose logging is enabled
        if (logger.IsEnabled(LogLevel.Debug))
        {
            if (!char.IsPunctuation(message, 0) && !char.IsSymbol(message, 0))
            { 
                message = $"{UiSymbols.Verbose} {message}";
            }
            AddStatusMessage(message);
        }
    }

    public void StatusError(string message, params object?[] args)
    {
#pragma warning disable CA2254 // Template should be a static expression
        logger.LogError(message, args);
#pragma warning restore CA2254 // Template should be a static expression
    }

    public async Task<T?> PromptAsync<T>(IPrompt<T> prompt)
    {
        var subTask = new PromptTask<T>(prompt, task, ansiConsoleContext, logger);
        task.SubTasks.Add(subTask);

        // Find the root task with the spinner
        var spinnerTask = task.FindTaskWithSpinner();
        spinnerTask?.PauseSpinner();

        var result = await subTask.ExecuteAsync(onUpdate, startSpinner: false);

        task.SubTasks.Remove(subTask);
        spinnerTask?.ResumeSpinner();

        return result;
    }

    internal void UpdateSubStatus(string? subStatus)
    {
        task.SubStatus = subStatus;
    }
}
