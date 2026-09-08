// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Finds the app window that automation calls will act on, from a process name, window title, PID,
/// or window handle. Use <see cref="UiTarget.FromWindowHandle"/> directly when you already have a
/// handle and need no discovery.
/// </summary>
public interface IUiTargetResolver
{
    /// <summary>
    /// Resolves the target window. When several windows match, the foreground one wins, otherwise
    /// the largest.
    /// </summary>
    /// <param name="app">Process name, window title, or PID. Required unless <paramref name="hwnd"/> is set.</param>
    /// <param name="hwnd">A specific window handle. Takes precedence over <paramref name="app"/> and marks the result as an explicit target, so element lookups stay scoped to that window.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>The process and, when known, window that matched the request.</returns>
    /// <exception cref="AppNotFoundException">No running app matched.</exception>
    /// <exception cref="InvalidOperationException">Neither argument was supplied, or several windowed processes matched and the choice is ambiguous.</exception>
    Task<UiTarget> ResolveAsync(string? app, long? hwnd, CancellationToken ct);
}
