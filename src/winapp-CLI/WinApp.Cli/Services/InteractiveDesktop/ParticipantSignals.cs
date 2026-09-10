// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// One process's wake-up channel while it waits for the desktop.
/// </summary>
/// <remarks>
/// Auto-reset semantics matter: a signal delivered before this process starts waiting stays latched
/// and is consumed by the next wait, so a promoter that publishes and signals faster than the waiter
/// reaches its wait cannot lose the wake-up.
/// </remarks>
internal interface IParticipantSignal : IDisposable
{
    /// <summary>
    /// Waits for a wake-up, <paramref name="timeout"/>, or cancellation.
    /// </summary>
    /// <returns><see langword="true"/> when signalled, <see langword="false"/> on timeout.</returns>
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Creates this process's wake-up channel and pokes other processes' channels.
/// </summary>
/// <remarks>
/// <para>
/// The scheduler stays the sole authority. A signal is only a hint that the state <em>may</em> have
/// changed; every waiter re-reads state under <c>state.lock</c> and re-checks its own status before
/// doing anything, so a duplicate, stale or entirely spurious wake costs one lock acquisition and
/// nothing else. A <em>missing</em> wake costs a recovery deadline, never correctness.
/// </para>
/// <para>
/// Nothing about this is persisted. The channel name is derived from identity the state file already
/// carries — session, PID and process start time — so there is no schema change and no way for the
/// name to disagree with the entry it belongs to.
/// </para>
/// </remarks>
internal interface IParticipantSignals
{
    /// <summary>
    /// Opens this process's own channel. Must be called before any state entry naming this process is
    /// published, so a promoter can never look for a channel that does not exist yet.
    /// </summary>
    IParticipantSignal Create(int processId, long startTicksUtc);

    /// <summary>
    /// Best-effort wake-up for another participant. Never throws: a participant that has exited has no
    /// channel to open, and that is the normal case rather than an error.
    /// </summary>
    void Signal(int processId, long startTicksUtc);
}

/// <summary>
/// Named-event implementation. Events live in the <c>Local\</c> namespace, so they are scoped to the
/// Windows session exactly like the coordination state they mirror.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ParticipantSignals(IProcessInspector processInspector, ILogger<ParticipantSignals> logger)
    : IParticipantSignals
{
    /// <summary>
    /// Deterministic channel name for one participant.
    /// </summary>
    /// <remarks>
    /// PID alone is not identity — Windows reuses PIDs — so the process start time is included for the
    /// same reason the state entries carry it. The workflow id is deliberately absent: it is a secret
    /// the coordinator hashes before it ever touches disk, and a name is visible to any process in the
    /// session.
    /// </remarks>
    internal static string NameFor(int sessionId, int processId, long startTicksUtc)
        => string.Create(
            CultureInfo.InvariantCulture,
            $@"Local\winapp-ui-turn-{sessionId}-{processId}-{startTicksUtc}");

    private string NameFor(int processId, long startTicksUtc)
        => NameFor(processInspector.CurrentSessionId, processId, startTicksUtc);

    public IParticipantSignal Create(int processId, long startTicksUtc)
    {
        try
        {
            return new NamedEventSignal(new EventWaitHandle(
                initialState: false, EventResetMode.AutoReset, NameFor(processId, startTicksUtc)));
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException
            or UnauthorizedAccessException
            or IOException)
        {
            // Wake-ups are an optimization over state that is published either way, so failing to open
            // a channel must not fail the command. Without one this process simply falls back to its
            // recovery deadline — slower, never wrong — and nobody else is affected.
            logger.LogDebug(
                "Could not open a UI coordination wake-up channel: {Message}. This command will rely on its recovery interval.",
                ex.Message);
            return new UnsignallableParticipant();
        }
    }

    public void Signal(int processId, long startTicksUtc)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(NameFor(processId, startTicksUtc), out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // A wake-up is an optimization layered on top of published state, so failing to deliver one
            // must never fail the transaction that published it. The target falls back to its recovery
            // deadline and finds the same state a moment later.
            logger.LogDebug(
                "Could not signal winapp participant {Pid}: {Message}. It will pick the change up on its next recheck.",
                processId, ex.Message);
        }
    }

    private sealed class NamedEventSignal(EventWaitHandle handle) : IParticipantSignal
    {
        public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // RegisterWaitForSingleObject parks the handle on a shared wait thread rather than blocking
            // one thread per waiter, so a machine full of queued commands does not cost a thread each.
            var registration = ThreadPool.RegisterWaitForSingleObject(
                handle,
                (_, timedOut) => completion.TrySetResult(!timedOut),
                state: null,
                timeout,
                executeOnlyOnce: true);

            await using var cancellation = cancellationToken.Register(
                static s => ((TaskCompletionSource<bool>)s!).TrySetCanceled(), completion);

            try
            {
                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                registration.Unregister(waitObject: null);
            }
        }

        public void Dispose() => handle.Dispose();
    }

    /// <summary>
    /// Degraded channel for a process that could not open a real one: it simply sleeps out whatever
    /// recovery interval the caller asked for.
    /// </summary>
    private sealed class UnsignallableParticipant : IParticipantSignal
    {
        public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
            return false;
        }

        public void Dispose()
        {
        }
    }
}
