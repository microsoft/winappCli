// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.ConsoleTasks;

/// <summary>
/// State of a confirmation prompt.
/// </summary>
internal enum PromptState
{
    /// <summary>
    /// Waiting for user input.
    /// </summary>
    WaitingForInput,

    /// <summary>
    /// User confirmed (Y/yes).
    /// </summary>
    Confirmed,

    /// <summary>
    /// User declined (N/no).
    /// </summary>
    Declined,

    /// <summary>
    /// Prompt was cancelled.
    /// </summary>
    Cancelled
}

/// <summary>
/// A task that displays a confirmation prompt and waits for Y/N input.
/// Updates its display state as the user interacts with it.
/// </summary>
internal class PromptConfirmationTask : GroupableTask<bool>
{
    private readonly TaskCompletionSource<bool> _resultTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action? _onUpdate;
    private PromptState _state = PromptState.WaitingForInput;
    private string _typedInput = string.Empty;

    public PromptState State
    {
        get => _state;
        private set
        {
            _state = value;
            UpdateDisplayMessage();
        }
    }

    public string PromptText { get; }

    public PromptConfirmationTask(string promptText, GroupableTask? parent, IAnsiConsole ansiConsole, ILogger logger, Lock renderLock, Action? onUpdate)
        : base(FormatPromptMessage(promptText, PromptState.WaitingForInput, string.Empty), parent, null, ansiConsole, logger, renderLock)
    {
        PromptText = promptText;
        _onUpdate = onUpdate;
        EscapeInProgressMessage = false;
    }

    private static string FormatPromptMessage(string promptText, PromptState state, string typedInput)
    {
        return state switch
        {
            PromptState.WaitingForInput => string.IsNullOrEmpty(typedInput) 
                ? $"{promptText} [blue][[Y/n]][/] [green](y)[/]: "
                : $"{promptText} [blue][[Y/n]][/] [green](y)[/]: {typedInput}",
            PromptState.Confirmed => $"{UiSymbols.Check} {promptText} Yes",
            PromptState.Declined => $"{UiSymbols.Error} {promptText} No",
            PromptState.Cancelled => $"{UiSymbols.Warning} {promptText} (cancelled)",
            _ => promptText
        };
    }

    private void UpdateDisplayMessage()
    {
        InProgressMessage = FormatPromptMessage(PromptText, _state, _typedInput);
        if (_state != PromptState.WaitingForInput)
        {
            IsCompleted = true;
            CompletedMessage = _state == PromptState.Confirmed;
        }
        _onUpdate?.Invoke();
    }

    /// <summary>
    /// Wait for user input and return the result.
    /// </summary>
    public async Task<bool> WaitForInputAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Read keys until we get Enter, Escape, or cancellation
            while (!cancellationToken.IsCancellationRequested)
            {
                var keyInfo = await AnsiConsole.Input.ReadKeyAsync(intercept: true, cancellationToken: cancellationToken);
                if (keyInfo != null)
                {
                    // Enter key confirms based on typed input (default to Yes)
                    if (keyInfo.Value.Key == ConsoleKey.Enter)
                    {
                        // Check what was typed - 'N' or 'n' means decline, anything else (including empty) means confirm
                        bool confirmed = !_typedInput.Equals("N", StringComparison.OrdinalIgnoreCase);
                        State = confirmed ? PromptState.Confirmed : PromptState.Declined;
                        _resultTcs.TrySetResult(confirmed);
                        return confirmed;
                    }

                    // Escape key declines
                    if (keyInfo.Value.Key == ConsoleKey.Escape)
                    {
                        State = PromptState.Declined;
                        _resultTcs.TrySetResult(false);
                        return false;
                    }

                    // Backspace removes last character
                    if (keyInfo.Value.Key == ConsoleKey.Backspace)
                    {
                        if (_typedInput.Length > 0)
                        {
                            _typedInput = _typedInput[..^1];
                            UpdateDisplayMessage();
                        }
                        continue;
                    }

                    // Only accept Y or N as valid input characters
                    char typed = char.ToUpperInvariant(keyInfo.Value.KeyChar);
                    if (typed == 'Y' || typed == 'N')
                    {
                        _typedInput = typed.ToString();
                        UpdateDisplayMessage();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }

        State = PromptState.Cancelled;
        _resultTcs.TrySetResult(false);
        return false;
    }

    public override Task<bool> ExecuteAsync(Action? onUpdate, CancellationToken cancellationToken, bool startSpinner = true)
    {
        // This task is driven by WaitForInputAsync, not ExecuteAsync
        return Task.FromResult(false);
    }
}
