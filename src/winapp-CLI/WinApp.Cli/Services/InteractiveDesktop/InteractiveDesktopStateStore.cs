// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>Outcome of reading coordination state under <c>state.lock</c>.</summary>
/// <param name="State">
/// The parsed state, or <see langword="null"/> when <paramref name="UnknownNewerVersion"/> is set.
/// </param>
/// <param name="UnknownNewerVersion">
/// A newer binary wrote a schema this build cannot interpret. Turn-participating and mutating commands
/// must fail closed; detached observations may continue without touching state (spec §12.4).
/// </param>
/// <param name="RecoveredFromCorruption">
/// Unreadable state was safely quarantined and replaced with a fresh document, which callers surface as
/// a warning and report in telemetry.
/// </param>
internal readonly record struct StateReadResult(
    InteractiveDesktopState? State,
    bool UnknownNewerVersion,
    bool RecoveredFromCorruption);

/// <summary>
/// Reads and publishes <c>interactive-desktop-{session}.state.json</c> under <c>state.lock</c>
/// (spec §7.1–§7.2).
/// </summary>
internal interface IInteractiveDesktopStateStore
{
    /// <summary>
    /// Takes <c>state.lock</c>. Callers must hold it for every read and update and must release it
    /// before waiting for <c>active.lock</c> or running UI code (spec §9).
    /// </summary>
    IDisposable AcquireStateLock(CancellationToken cancellationToken);

    /// <summary>Reads state. Must be called while holding <c>state.lock</c>.</summary>
    StateReadResult Read();

    /// <summary>Atomically publishes state. Must be called while holding <c>state.lock</c>.</summary>
    void Publish(InteractiveDesktopState state);

    /// <summary>Whether <c>active.lock</c> is currently free, tested without waiting.</summary>
    bool IsActiveLockFree();
}

/// <inheritdoc cref="IInteractiveDesktopStateStore"/>
internal sealed class InteractiveDesktopStateStore(
    IInteractiveDesktopPaths paths,
    IParticipantRegistry participants,
    IMonotonicClock clock,
    ILogger<InteractiveDesktopStateStore> logger) : IInteractiveDesktopStateStore
{
    /// <summary>
    /// How long a publish keeps retrying transient sharing/access failures before giving up (spec §7.2).
    /// Antivirus and search indexers routinely hold a just-written file open for a few milliseconds.
    /// </summary>
    private const int PublishRetryBudgetMs = 1_000;

    /// <summary>
    /// Spin attempts before yielding the thread while waiting for <c>state.lock</c>. The critical
    /// section is a small read plus an optional small write, so contention almost always clears within
    /// the spin and never pays a timer-resolution sleep.
    /// </summary>
    private const int StateLockSpinAttempts = 64;

    public IDisposable AcquireStateLock(CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();

        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(
                    paths.StateLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException ex) when (CoordinationLockIo.IsContention(ex))
            {
                // Held by another coordinator mid-transition. Spin briefly, then yield.
            }
            catch (IOException ex)
            {
                // Not contention: retrying would hang forever on a failure that will never clear.
                throw CoordinationLockIo.CannotOpen(paths.StateLockPath, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UiCoordinationException(
                    UiCoordinationErrorCodes.Unavailable,
                    $"The UI coordination state lock '{paths.StateLockPath}' could not be opened: {ex.Message}",
                    "Check that the current user can write to the coordination directory.");
            }

            attempt++;
            if (attempt <= StateLockSpinAttempts)
            {
                Thread.SpinWait(20 * attempt);
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }

    public StateReadResult Read()
    {
        string? raw;
        var fileExists = File.Exists(paths.StatePath);
        try
        {
            raw = fileExists ? File.ReadAllText(paths.StatePath) : null;
        }
        catch (IOException ex)
        {
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.Unavailable,
                $"The UI coordination state could not be read: {ex.Message}",
                "Retry the command. If it keeps failing, close other winapp ui processes and retry.");
        }

        if (!fileExists)
        {
            // Missing state is the ordinary first command on this desktop — but only when nothing on
            // disk still proves a turn is in progress. An external deletion (AV, manual cleanup, a
            // stray rmdir) while a recording or a queued waiter is live would otherwise mint a second
            // owner for the same desktop and let two agents drive it at once.
            if (HasLiveTurnEvidence())
            {
                logger.LogDebug("UI coordination state is missing while a participant is still live; failing closed.");
                throw CannotRebuildState("missing");
            }

            return new StateReadResult(InteractiveDesktopState.CreateFresh(), false, false);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            // A file that exists but holds nothing is NOT a fresh start — atomic publication never
            // produces one, so it means a torn write or external truncation while other processes may
            // still be relying on the state it replaced. Take the guarded recovery path.
            logger.LogDebug("UI coordination state file exists but is empty; treating as corrupt.");
            return RecoverCorruptState();
        }

        // The schema version is read from the raw document BEFORE any typed deserialization. A newer
        // binary may change field *shapes*, not just add fields — if `owner` became an object here and a
        // string there, strong deserialization throws JsonException and the catch below would classify a
        // perfectly valid newer state as corruption, quarantine it, and replace it with a version 1
        // document. That is exactly the downgrade spec §12.4 forbids.
        if (TryReadDeclaredVersion(raw, out var declaredVersion)
            && declaredVersion > InteractiveDesktopState.CurrentVersion)
        {
            // Not corruption — a newer binary owns this file. Leave the bytes untouched.
            return new StateReadResult(null, UnknownNewerVersion: true, RecoveredFromCorruption: false);
        }

        InteractiveDesktopState? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(raw, InteractiveDesktopJsonContext.Default.InteractiveDesktopState);
        }
        catch (JsonException ex)
        {
            logger.LogDebug("UI coordination state is not valid JSON: {Message}", ex.Message);
            return RecoverCorruptState();
        }

        if (parsed is null)
        {
            return RecoverCorruptState();
        }

        if (parsed.Version > InteractiveDesktopState.CurrentVersion)
        {
            // Reached when the version survived typed deserialization (an additive-only newer schema).
            // TryReadDeclaredVersion above already caught the shape-incompatible case.
            return new StateReadResult(null, UnknownNewerVersion: true, RecoveredFromCorruption: false);
        }

        if (!IsStructurallyValid(parsed))
        {
            logger.LogDebug("UI coordination state failed version {Version} structural validation.", parsed.Version);
            return RecoverCorruptState();
        }

        parsed.OwnerCommands ??= [];
        parsed.Waiters ??= [];
        return new StateReadResult(parsed, false, false);
    }

    /// <summary>
    /// Reads only the root <c>version</c> number, without binding any other field to a type.
    /// </summary>
    /// <remarks>
    /// Deliberately tolerant: a document that is not valid JSON, has no object root, omits
    /// <c>version</c>, or carries a non-numeric <c>version</c> returns <see langword="false"/> so the
    /// caller falls through to typed deserialization and the existing corruption-recovery semantics.
    /// This method only ever *diverts* a document that positively declares a version newer than this
    /// binary understands.
    /// </remarks>
    private static bool TryReadDeclaredVersion(string raw, out int version)
    {
        version = 0;

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out var versionElement)
                && versionElement.ValueKind == JsonValueKind.Number
                && versionElement.TryGetInt32(out version);
        }
        catch (JsonException)
        {
            // Malformed JSON is genuine corruption; let the typed path report it.
            return false;
        }
    }

    /// <summary>
    /// Rejects documents that parse as JSON but describe scheduling state that cannot be reasoned about.
    /// </summary>
    /// <remarks>
    /// The checks here are the ones whose violation would corrupt scheduling rather than merely look
    /// odd: duplicate tickets would make the forward barrier and FIFO order ambiguous, a
    /// <c>nextTicket</c> at or below a live ticket would hand a second command the same barrier
    /// position, owner commands without an owner would let a turn run unattributed, and an out-of-range
    /// enum would silently behave as <c>Observe</c> or <c>Waiting</c>.
    /// </remarks>
    private static bool IsStructurallyValid(InteractiveDesktopState state)
    {
        if (state.Version < 1 || state.NextTicket < 1 || state.TurnId < 0)
        {
            return false;
        }

        if (state.Owner is { } owner
            && (string.IsNullOrWhiteSpace(owner.Key) || !Enum.IsDefined(owner.Kind)))
        {
            return false;
        }

        var commands = state.OwnerCommands ?? [];
        var waiters = state.Waiters ?? [];

        // Commands can only belong to an owner. An orphaned set means the owner record was lost.
        if (state.Owner is null && commands.Count > 0)
        {
            return false;
        }

        // Tickets order the owner-local forward barrier AND the global queue, so they must be unique
        // across both lists, not just within one.
        var tickets = new HashSet<long>();
        var highestTicket = 0L;

        foreach (var command in commands)
        {
            if (command.Pid <= 0
                || !Enum.IsDefined(command.Mode)
                || !Enum.IsDefined(command.Status))
            {
                return false;
            }

            if (command.Mode == UiTurnMode.Observe)
            {
                // Observations never serialize as barriers, so they carry no ticket.
                if (command.Ticket is not null)
                {
                    return false;
                }

                continue;
            }

            if (command.Ticket is not { } commandTicket || commandTicket < 1 || !tickets.Add(commandTicket))
            {
                return false;
            }

            highestTicket = Math.Max(highestTicket, commandTicket);
        }

        foreach (var waiter in waiters)
        {
            if (waiter.Pid <= 0
                || waiter.Ticket < 1
                || string.IsNullOrWhiteSpace(waiter.OwnerKey)
                || !Enum.IsDefined(waiter.Mode)
                || !Enum.IsDefined(waiter.OwnerKind)
                || waiter.Mode == UiTurnMode.Observe
                || !tickets.Add(waiter.Ticket))
            {
                return false;
            }

            highestTicket = Math.Max(highestTicket, waiter.Ticket);
        }

        // The next allocation must not collide with a ticket already in use.
        return state.NextTicket > highestTicket;
    }

    /// <summary>
    /// Quarantines unreadable state and starts fresh, but only when it is provably safe: no
    /// <c>active.lock</c> holder and no live participant lease. Otherwise a live workflow would silently
    /// lose its turn and two processes could drive the desktop at once (spec §12.3).
    /// </summary>
    private StateReadResult RecoverCorruptState()
    {
        if (HasLiveTurnEvidence())
        {
            throw CannotRebuildState("unreadable");
        }

        var quarantinePath = System.IO.Path.Combine(
            paths.LockDirectory,
            $"state.corrupt-{clock.UtcNow.ToString("yyyyMMdd'T'HHmmss'.'fff'Z'", CultureInfo.InvariantCulture)}.json");

        try
        {
            File.Move(paths.StatePath, quarantinePath, overwrite: true);
            logger.LogWarning(
                "{Symbol} UI coordination state was unreadable and has been rebuilt. The previous file was kept at {Path}.",
                Helpers.UiSymbols.Warning,
                quarantinePath);
        }
        catch (IOException ex)
        {
            // Quarantining is best effort: the important part is that no live participant exists, so
            // overwriting with a fresh document below is already safe.
            logger.LogDebug("Corrupt UI coordination state could not be quarantined: {Message}", ex.Message);
        }

        return new StateReadResult(InteractiveDesktopState.CreateFresh(), false, RecoveredFromCorruption: true);
    }

    /// <summary>
    /// Whether anything on disk still proves a turn is in progress: a held <c>active.lock</c>, or any
    /// live participant lease. Checked before rebuilding state that is missing or unreadable, so a
    /// rebuild can never invent a second owner alongside a running one.
    /// </summary>
    private bool HasLiveTurnEvidence()
        => !IsActiveLockFree() || participants.AnyLiveParticipant();

    private static UiCoordinationException CannotRebuildState(string condition)
        => new(
            UiCoordinationErrorCodes.Unavailable,
            $"UI coordination state is {condition} and another winapp ui process is still active, so it cannot be safely rebuilt.",
            "Wait for the other winapp ui commands to finish (or stop them) and retry.");

    /// <summary>
    /// Human-readable idle deadline, written for diagnostics only and never read back.
    /// </summary>
    /// <remarks>
    /// The delta between the stored deadline and the current uptime is unbounded in principle — a
    /// state file written before a reboot carries a deadline from the previous boot — and
    /// <see cref="DateTime.AddMilliseconds(double)"/> throws once the result leaves the representable
    /// range. A diagnostic string must never be able to fail a publish, so an unrepresentable value
    /// is simply omitted.
    /// </remarks>
    private string? TryFormatIdleExpiry(InteractiveDesktopState state)
    {
        if (state.IdleExpiresTick64 <= 0)
        {
            return null;
        }

        try
        {
            return clock.UtcNow
                .AddMilliseconds(state.IdleExpiresTick64 - clock.NowTicks64)
                .ToString("O", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public void Publish(InteractiveDesktopState state)
    {
        state.DiagnosticIdleExpiresUtc = TryFormatIdleExpiry(state);

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            state, InteractiveDesktopJsonContext.Default.InteractiveDesktopState);

        var tempPath = paths.StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var stopwatch = Stopwatch.StartNew();
        Exception? lastFailure = null;

        while (stopwatch.ElapsedMilliseconds <= PublishRetryBudgetMs)
        {
            try
            {
                // Same directory so the replace below is a rename on one volume, and flushed to disk so a
                // crash between write and rename cannot publish a truncated document.
                using (var stream = new FileStream(
                    tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.WriteThrough))
                {
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, paths.StatePath, overwrite: true);
                SweepStaleTempFiles();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastFailure = ex;
                Thread.Sleep(10);
            }
        }

        TryDeleteTemp(tempPath);
        throw new UiCoordinationException(
            UiCoordinationErrorCodes.Unavailable,
            $"UI coordination state could not be published: {lastFailure?.Message ?? "unknown error"}",
            "Retry the command. If it keeps failing, check that the coordination directory is on a local writable drive and not being scanned by another tool.");
    }

    public bool IsActiveLockFree()
    {
        try
        {
            using var probe = new FileStream(
                paths.ActiveLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Unable to prove it is free, so report "held" — the caller only ever uses a free answer to
            // authorize a destructive rebuild.
            return false;
        }
    }

    /// <summary>
    /// Removes publish temp files orphaned by a crash between write and rename. Best effort and only
    /// under <c>state.lock</c>, so a live publisher's temp file is never removed underneath it.
    /// </summary>
    private void SweepStaleTempFiles()
    {
        try
        {
            var pattern = System.IO.Path.GetFileName(paths.StatePath) + ".*.tmp";
            foreach (var stale in Directory.EnumerateFiles(paths.LockDirectory, pattern))
            {
                TryDeleteTemp(stale);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            logger.LogDebug("Stale UI coordination temp files could not be swept: {Message}", ex.Message);
        }
    }

    private static void TryDeleteTemp(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .tmp is harmless: it is uniquely named and swept on a later publish.
        }
    }
}
