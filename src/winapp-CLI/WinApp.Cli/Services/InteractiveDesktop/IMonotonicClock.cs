// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// The monotonic time source behind every coordination deadline. Backed by
/// <see cref="Environment.TickCount64"/> in production (spec §10): UTC timestamps in
/// <c>state.json</c> are diagnostic only, because wall-clock time can jump backwards across DST,
/// NTP corrections and manual clock changes, which would either strand a turn or hand it off early.
/// </summary>
/// <remarks>
/// <see cref="Environment.TickCount64"/> counts milliseconds since the current boot, so a state file
/// written before a reboot carries deadlines from a different epoch. That is safe here because
/// prior-boot state is only ever acted upon when no participant lease is live, and such state is
/// treated as stale (spec §12.3).
/// </remarks>
internal interface IMonotonicClock
{
    /// <summary>Milliseconds since boot. Strictly non-decreasing within one boot.</summary>
    long NowTicks64 { get; }

    /// <summary>Wall-clock UTC, used only for the diagnostic fields in <c>state.json</c>.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production <see cref="IMonotonicClock"/> over <see cref="Environment.TickCount64"/>.</summary>
internal sealed class TickCountClock : IMonotonicClock
{
    public long NowTicks64 => Environment.TickCount64;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
