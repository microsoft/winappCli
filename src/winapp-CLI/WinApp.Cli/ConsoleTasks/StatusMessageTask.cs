// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Models;

namespace WinApp.Cli.ConsoleTasks;

internal class StatusMessageTask : GroupableTask<string>
{
    public StatusMessageTask(string inProgressMessage, GroupableTask? parent, AnsiConsoleContext ansiConsoleContext, ILogger logger)
        : base(inProgressMessage, parent, null, ansiConsoleContext, logger)
    {
        IsCompleted = true;
        CompletedMessage = InProgressMessage;
    }

    public override Task<string?> ExecuteAsync(Action? onUpdate = null, bool startSpinner = true)
    {
        return Task.FromResult<string?>(CompletedMessage);
    }
}
