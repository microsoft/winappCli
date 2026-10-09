// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// Named-pipe connect is a TOCTOU: a free instance can be taken before <c>CreateFile</c>.
/// Keep this policy transport-free; standalone clients carry pinned copies.
/// </summary>
internal static class DevToolsConnectRetry
{
    public const int ErrorFileNotFound = 2;

    public const int ErrorPipeBusy = 231;

    public const int BackoffStartMs = 1;

    public const int BackoffMaxMs = 20;

    /// <summary>
    /// Retry busy pipes, and retry not-found only after busy proved the pipe existed.
    /// Access denied and other terminal errors must surface immediately.
    /// </summary>
    public static bool ShouldRetry(int error, bool sawBusy) =>
        error == ErrorPipeBusy || (error == ErrorFileNotFound && sawBusy);

    public static int NextBackoff(int currentMs) => Math.Min(currentMs * 2, BackoffMaxMs);

    public static T Run<T>(
        DevToolsConnectAttempt<T> attempt,
        int timeoutMs,
        Func<int, Exception> fail,
        Func<long>? now = null,
        Action<int>? sleep = null)
        where T : class
    {
        now ??= () => Environment.TickCount64;
        sleep ??= Thread.Sleep;

        var deadline = now() + Math.Max(0, timeoutMs);
        var sawBusy = false;
        var backoffMs = BackoffStartMs;

        while (true)
        {
            var remaining = (int)Math.Clamp(deadline - now(), 0, int.MaxValue);
            var result = attempt(remaining, out var error);
            if (result is not null)
            {
                return result;
            }

            if (!ShouldRetry(error, sawBusy))
            {
                throw fail(error);
            }

            if (error == ErrorPipeBusy)
            {
                sawBusy = true;
            }

            // Checked after the retry decision so a non-transient error is always reported as itself rather
            // than as a timeout, and before sleeping so an exhausted budget does not pay one more backoff.
            if (now() >= deadline)
            {
                throw fail(error);
            }

            sleep(backoffMs);
            backoffMs = NextBackoff(backoffMs);
        }
    }
}

internal delegate T? DevToolsConnectAttempt<T>(int remainingMs, out int error)
    where T : class;
