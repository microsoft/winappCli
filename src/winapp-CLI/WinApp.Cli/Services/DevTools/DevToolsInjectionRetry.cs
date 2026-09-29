// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// Retry only the cold-start race where a live WinUI app has a window before its XAML tree exists.
/// Any other HRESULT, target exit, or exhausted budget remains a terminal attach failure.
/// </summary>
internal static class DevToolsInjectionRetry
{
    /// <summary>
    /// <c>HRESULT_FROM_WIN32(ERROR_NOT_FOUND)</c> == <c>0x80070490</c>. Returned by
    /// <c>InitializeXamlDiagnosticsEx</c> against a live process when the XAML visual tree has not yet come up.
    /// This is the one — and only — HRESULT treated as "retry: the tree isn't ready yet".
    /// </summary>
    public const int ErrorNotFound = unchecked((int)0x80070490);

    public static bool IsTreeNotReady(int hr) => hr == ErrorNotFound;

    /// <summary>
    /// Retries <see cref="ErrorNotFound"/> only while the target stays alive.
    /// The injected delegates keep the policy testable without a live WinUI target.
    /// </summary>
    public static async Task<InjectionOutcome> RunAsync(
        Func<int> attemptInject,
        Func<bool> isTargetAlive,
        int maxAttempts,
        TimeSpan retryDelay,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attemptInject);
        ArgumentNullException.ThrowIfNull(isTargetAlive);
        ArgumentNullException.ThrowIfNull(delayAsync);
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "At least one attempt is required.");
        }

        var lastHr = ErrorNotFound;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // If the target died before this attempt (or before the first), stop: injecting into a dead
            // process would just surface a different, more confusing HRESULT. Report the exit and fail fast.
            if (!isTargetAlive())
            {
                return new InjectionOutcome(lastHr, attempt - 1, TargetExited: true);
            }

            lastHr = attemptInject();

            // S_OK, or any terminal failure that is NOT "tree not ready", is the final answer — return now.
            if (!IsTreeNotReady(lastHr))
            {
                return new InjectionOutcome(lastHr, attempt, TargetExited: false);
            }

            // Retryable ("tree not up yet"): back off before the next attempt, unless this was the last one.
            if (attempt < maxAttempts)
            {
                await delayAsync(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        // Budget exhausted while still ERROR_NOT_FOUND: the tree never came up.
        return new InjectionOutcome(lastHr, maxAttempts, TargetExited: false);
    }
}

internal readonly record struct InjectionOutcome(int Hr, int Attempts, bool TargetExited)
{
    public bool Succeeded => Hr == 0 && !TargetExited;
}
