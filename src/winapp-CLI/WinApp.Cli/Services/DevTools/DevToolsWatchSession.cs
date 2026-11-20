// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// Keeps DevTools attached to an app that <c>dotnet watch</c> runs: each time <c>dotnet watch</c> starts the app (the
/// first launch, and every restart after an edit Hot Reload can't apply), the new process is attached with the same
/// overlay posture. Hot Reload edits keep the same process, so they need no new attach.
/// </summary>
internal sealed class DevToolsWatchSession(
    Func<IReadOnlyList<int>> findApps,
    Func<int, CancellationToken, Task<DevToolsConnection>> connect,
    Func<bool> watcherExited,
    Func<int, bool> processAlive)
{
    internal const int MaxAttempts = 3;

    private readonly Dictionary<int, int> attempts = [];

    /// <summary>Called after each attach attempt with the process, its result and whether it was a restart.</summary>
    public Action<int, DevToolsConnection, bool>? Attached { get; init; }

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>How many app processes DevTools attached to.</summary>
    public int AttachedCount { get; private set; }

    /// <summary>Runs until <c>dotnet watch</c> exits or <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !watcherExited())
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            await Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var pid in findApps())
        {
            var tries = attempts.GetValueOrDefault(pid);
            if (tries < 0 || tries >= MaxAttempts || !processAlive(pid))
            {
                continue;
            }
            attempts[pid] = tries + 1;
            var connection = await connect(pid, cancellationToken).ConfigureAwait(false);
            if (connection.Connected)
            {
                // Negative marks a process that is attached for good.
                attempts[pid] = -1;
                Attached?.Invoke(pid, connection, AttachedCount++ > 0);
            }
            else if (tries + 1 >= MaxAttempts || !processAlive(pid))
            {
                Attached?.Invoke(pid, connection, AttachedCount > 0);
            }
        }
    }
}
