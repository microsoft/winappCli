// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// In-memory stand-in for the named-event wake-up channels, so queue behavior can be tested without
/// real cross-process events.
/// </summary>
/// <remarks>
/// Models the two properties the real primitive is chosen for: a signal delivered before anyone waits
/// stays latched and is consumed by the next wait, and one signal releases exactly one wait. It also
/// records every timeout a waiter asked for, which is how the recovery-deadline schedule is asserted
/// without sleeping through it.
/// </remarks>
internal sealed class FakeParticipantSignals : IParticipantSignals
{
    private readonly Dictionary<(int Pid, long Start), FakeSignal> _channels = [];
    private readonly Lock _sync = new();

    /// <summary>Every participant that was woken, in order, including repeats.</summary>
    public List<(int Pid, long StartTicksUtc)> Signalled { get; } = [];

    /// <summary>Timeouts requested by waits, in order, so a test can assert the recovery schedule.</summary>
    public List<TimeSpan> RequestedTimeouts { get; } = [];

    /// <summary>Signals delivered to participants that never opened a channel.</summary>
    public List<(int Pid, long StartTicksUtc)> SignalledWithoutChannel { get; } = [];

    public IParticipantSignal Create(int processId, long startTicksUtc)
    {
        lock (_sync)
        {
            var signal = new FakeSignal(this);
            _channels[(processId, startTicksUtc)] = signal;
            return signal;
        }
    }

    public void Signal(int processId, long startTicksUtc)
    {
        FakeSignal? channel;
        lock (_sync)
        {
            Signalled.Add((processId, startTicksUtc));
            if (!_channels.TryGetValue((processId, startTicksUtc), out channel))
            {
                SignalledWithoutChannel.Add((processId, startTicksUtc));
                return;
            }
        }

        channel.Set();
    }

    /// <summary>How many times <paramref name="participant"/> was woken.</summary>
    public int SignalCountFor(UiParticipantIdentity participant)
    {
        lock (_sync)
        {
            return Signalled.Count(s => s.Pid == participant.ProcessId && s.StartTicksUtc == participant.StartTicksUtc);
        }
    }

    /// <summary>Wakes a participant directly, standing in for another process's promotion.</summary>
    public void SignalDirect(UiParticipantIdentity participant)
        => Signal(participant.ProcessId, participant.StartTicksUtc);

    internal sealed class FakeSignal(FakeParticipantSignals owner) : IParticipantSignal
    {
        // Capacity one: the latch either holds a pending wake-up or it does not, exactly like an
        // auto-reset event. Releasing twice cannot bank two wake-ups.
        private readonly SemaphoreSlim _latch = new(0, 1);

        public bool Disposed { get; private set; }

        public void Set()
        {
            try
            {
                _latch.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already latched; a second signal before anyone waits is not two wake-ups.
            }
        }

        public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (owner._sync)
            {
                owner.RequestedTimeouts.Add(timeout);
            }

            return await _latch.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            Disposed = true;
            _latch.Dispose();
        }
    }
}
