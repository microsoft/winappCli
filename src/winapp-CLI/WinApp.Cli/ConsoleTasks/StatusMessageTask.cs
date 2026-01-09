// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace WinApp.Cli.ConsoleTasks;

internal class StatusMessageTask : GroupableTask<string>
{
    public StatusMessageTask(string inProgressMessage, GroupableTask? parent, IAnsiConsole ansiConsole, ILogger logger, LiveUpdateSignal signal)
        : base(inProgressMessage, parent, null, ansiConsole, logger, signal)
    {
        IsCompleted = true;
        CompletedMessage = InProgressMessage;
    }

    public override Task<string?> ExecuteAsync(Action? onUpdate, CancellationToken cancellationToken, bool startSpinner = true)
    {
        onUpdate?.Invoke();
        return Task.FromResult<string?>(CompletedMessage);
    }
}
