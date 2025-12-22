// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Text;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.ConsoleTasks;

internal class TaskContext
{
    private readonly GroupableTask _task;
    private readonly Action? _onUpdate;
    private readonly IAnsiConsole _ansiConsole;
    private readonly ILogger _logger;
    private readonly LiveUpdateSignal _signal;

    public TaskContext(GroupableTask task, Action? onUpdate, IAnsiConsole ansiConsole, ILogger logger, LiveUpdateSignal signal)
    {
        _task = task;
        _onUpdate = onUpdate;
        _ansiConsole = ansiConsole;
        _logger = logger;
        _signal = signal;
    }

    public async Task<T?> AddSubTaskAsync<T>(string inProgressMessage, Func<TaskContext, CancellationToken, Task<T>> taskFunc, CancellationToken cancellationToken)
    {
        var subTask = new GroupableTask<T>(inProgressMessage, _task, taskFunc, _ansiConsole, _logger, _signal);
        lock (_signal.Lock)
        {
            _task.SubTasks.Add(subTask, cancellationToken);
        }

        return await subTask.ExecuteAsync(_onUpdate, cancellationToken, startSpinner: false);
    }

    public void AddStatusMessage(string message)
    {
        AddStatusMessageInternal(message, UiSymbols.Info);
    }

    public void AddDebugMessage(string message)
    {
        // Only update status and log if verbose logging is enabled
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            AddStatusMessageInternal(message, UiSymbols.Verbose);
        }
    }

    private void AddStatusMessageInternal(string message, string symbol)
    {
        if (!StartsWithSymbolOrPunctuation(message))
        {
            message = $"{symbol} {message}";
        }
        var subTask = new StatusMessageTask(message, _task, _ansiConsole, _logger, _signal);
        lock (_signal.Lock)
        {
            _task.SubTasks.Add(subTask);
        }

        _onUpdate?.Invoke();
    }

    private static bool StartsWithSymbolOrPunctuation(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        // Check if message starts with a symbol/punctuation/emoji using Rune for proper Unicode handling
        if (Rune.TryGetRuneAt(message, 0, out var firstRune))
        {
            // Only treat ASCII letters (A-Z, a-z) and digits (0-9) as "normal text"
            // Everything else (including Unicode symbols like ℹ U+2139 which .NET classifies as LowercaseLetter)
            // should be treated as a symbol/emoji prefix
            int value = firstRune.Value;
            bool isAsciiLetterOrDigit = (value >= 'A' && value <= 'Z') ||
                                        (value >= 'a' && value <= 'z') ||
                                        (value >= '0' && value <= '9');
            return !isAsciiLetterOrDigit;
        }

        // Fallback to char-based checks if Rune parsing fails
        return char.IsPunctuation(message, 0) || char.IsSymbol(message, 0);
    }

    public void StatusError(string message, params object?[] args)
    {
#pragma warning disable CA2254 // Template should be a static expression
        _logger.LogError(message, args);
#pragma warning restore CA2254 // Template should be a static expression
    }

    public async Task<T?> PromptAsync<T>(IPrompt<T> prompt, CancellationToken cancellationToken)
    {
        // Use the signal-based approach: signal the main thread to handle the prompt
        return await _signal.SignalPromptAsync(prompt, cancellationToken);
    }

    internal void UpdateSubStatus(string? subStatus)
    {
        _task.SubStatus = subStatus;
    }
}
