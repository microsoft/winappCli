// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;

namespace WinApp.Cli.ConsoleTasks;

/// <summary>
/// Represents the type of signal sent from the task thread to the main Live display thread.
/// </summary>
internal enum LiveUpdateType
{
    /// <summary>
    /// Request a refresh of the Live display.
    /// </summary>
    Refresh,

    /// <summary>
    /// Request to show a prompt to the user. The Live display will be stopped,
    /// the prompt shown, and then a new Live display started.
    /// </summary>
    Prompt
}

/// <summary>
/// Represents a pending prompt request from the task thread.
/// </summary>
internal class PendingPromptRequest
{
    private readonly TaskCompletionSource<object?> _resultTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The prompt to display to the user.
    /// </summary>
    public required IPrompt<object> Prompt { get; init; }

    /// <summary>
    /// The cancellation token for the prompt operation.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Sets the result of the prompt operation.
    /// </summary>
    public void SetResult(object? result) => _resultTcs.TrySetResult(result);

    /// <summary>
    /// Sets the prompt as cancelled.
    /// </summary>
    public void SetCancelled() => _resultTcs.TrySetCanceled();

    /// <summary>
    /// Sets an exception on the prompt operation.
    /// </summary>
    public void SetException(Exception ex) => _resultTcs.TrySetException(ex);

    /// <summary>
    /// Gets the task that completes when the prompt result is available.
    /// </summary>
    public Task<object?> ResultTask => _resultTcs.Task;
}

/// <summary>
/// Manages communication between the task execution thread and the main Live display thread.
/// The task thread signals when it needs a refresh or a prompt, and the main thread responds.
/// </summary>
internal class LiveUpdateSignal : IDisposable
{
    private readonly Lock _lock = new();
    private readonly AutoResetEvent _signalEvent = new(false);
    private PendingPromptRequest? _pendingPrompt;
    private volatile bool _hasPromptPending;

    public Lock Lock => _lock;

    /// <summary>
    /// Signal a refresh request from the task thread.
    /// </summary>
    public void SignalRefresh()
    {
        _signalEvent.Set();
    }

    /// <summary>
    /// Signal a prompt request from the task thread and wait for the result.
    /// </summary>
    /// <typeparam name="T">The type of the prompt result.</typeparam>
    /// <param name="prompt">The prompt to display.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result from the prompt.</returns>
    public async Task<T?> SignalPromptAsync<T>(IPrompt<T> prompt, CancellationToken cancellationToken)
    {
        PendingPromptRequest request;

        lock (_lock)
        {
            // Create a wrapper prompt that boxes the result
            var wrappedPrompt = new PromptWrapper<T>(prompt);
            request = new PendingPromptRequest
            {
                Prompt = wrappedPrompt,
                CancellationToken = cancellationToken
            };
            _pendingPrompt = request;
            _hasPromptPending = true;
        }

        // Signal the main thread that something needs attention
        _signalEvent.Set();

        // Wait for the main thread to complete the prompt
        var result = await request.ResultTask;
        return result is T typed ? typed : default;
    }

    /// <summary>
    /// Wait for a signal from the task thread and return what type it is.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The type of signal to handle.</returns>
    public async Task<LiveUpdateType> WaitForSignalAsync(CancellationToken cancellationToken)
    {
        // Wait for a signal or timeout (100ms for spinner animation refresh)
        await Task.Run(() =>
        {
            WaitHandle.WaitAny([_signalEvent, cancellationToken.WaitHandle], 100);
        }, cancellationToken);

        // Check if there's a prompt pending (priority over refresh)
        if (_hasPromptPending)
        {
            return LiveUpdateType.Prompt;
        }

        return LiveUpdateType.Refresh;
    }

    /// <summary>
    /// Get the pending prompt request and clear the pending flag.
    /// </summary>
    /// <returns>The pending prompt request, or null if there is none.</returns>
    public PendingPromptRequest? GetPendingPromptAndReset()
    {
        lock (_lock)
        {
            var prompt = _pendingPrompt;
            _pendingPrompt = null;
            _hasPromptPending = false;
            return prompt;
        }
    }

    public void Dispose()
    {
        _signalEvent.Dispose();
    }

    /// <summary>
    /// Wrapper class to convert IPrompt&lt;T&gt; to IPrompt&lt;object&gt; for storage.
    /// </summary>
    public class PromptWrapper<T>(IPrompt<T> inner) : IPrompt<object>
    {
        public object Show(IAnsiConsole console) => inner.Show(console)!;

        public async Task<object> ShowAsync(IAnsiConsole console, CancellationToken cancellationToken)
        {
            var result = await inner.ShowAsync(console, cancellationToken);
            return result!;
        }
    }
}
