// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Models;

namespace WinApp.Cli.ConsoleTasks;

internal class PromptTask<T>(IPrompt<T> prompt, GroupableTask parent, AnsiConsoleContext ansiConsoleContext, ILogger logger)
    : GroupableTask<T>(string.Empty, parent, null, ansiConsoleContext, logger)
{
    public override async Task<T?> ExecuteAsync(Action? onUpdate = null, bool startSpinner = true)
    {
        onUpdate?.Invoke();

        AnsiConsoleContext.NonExclusiveAnsiConsole.Cursor.MoveUp(1);

        AnsiConsoleContext.NonExclusiveAnsiConsole.WriteLine();
        AnsiConsoleContext.NonExclusiveAnsiConsole.Cursor.MoveUp(1);
        var result = await prompt.ShowAsync(AnsiConsoleContext.NonExclusiveAnsiConsole, CancellationToken.None);

        AnsiConsoleContext.NonExclusiveAnsiConsole.Cursor.MoveUp(1);

        return result;
    }
}
