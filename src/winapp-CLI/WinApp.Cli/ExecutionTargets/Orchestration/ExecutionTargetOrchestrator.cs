// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>A prepared execution target: a live executor, its identity, epoch, and how it was obtained.</summary>
/// <param name="Reference">
/// Which target this is. Carried on the prepared target so shared orchestration names the selected
/// target rather than reaching for a hard-coded one, and so every result can report its own scope.
/// </param>
/// <param name="Operations">What this target can be asked to do, above the provider boundary.</param>
/// <param name="Epoch">Generation identity every request and result is fenced against.</param>
/// <param name="Capabilities">What the target reported it can do.</param>
/// <param name="Reused">True when an existing instance was reused, driving the progress line.</param>
/// <param name="MutationLease">
/// Non-null when this target was prepared with <see cref="PrepareTargetOptions.RequiresMutation"/>
/// set. This is <em>not</em> released by <see cref="ExecutionTargetOrchestrator.PrepareAsync"/> --
/// it stays held across everything the caller still has to do to mutate the guest (runtime
/// provisioning, deployment reconciliation, package registration), and the caller must call
/// <see cref="ReleaseMutationLease"/> once that work is done and before anything that can run for a
/// long time, such as launching an application. <see cref="DisposeAsync"/> releases it too, as a
/// fail-safe for a caller that forgets or that fails before reaching its own release point -- never
/// as the primary release path, because that would hold the lock for the target's entire lifetime,
/// including a running application.
/// </param>
/// <remarks>
/// Deliberately owns no <em>connection</em> lock. The connection lock covers establishing a channel,
/// not using one, so a prepared target — which lives for as long as the command that holds it,
/// including a foreground application — never keeps another winapp process from connecting. The
/// mutation lock is a separate concern with a separate lifetime: it covers the window in which this
/// target changes guest state, and is released before the application runs.
/// </remarks>
internal sealed record PreparedTarget(
    ExecutionTargetRef Reference,
    ITargetOperationExecutor Operations,
    ExecutionTargetEpoch Epoch,
    ExecutionTargetCapabilities Capabilities,
    bool Reused,
    TargetMutationLease? MutationLease = null) : IAsyncDisposable
{
    /// <summary>Which target, and which incarnation of it, produced a result.</summary>
    public ExecutionTargetScope Scope => ExecutionTargetScope.For(Reference, Epoch);

    /// <summary>
    /// Releases the mutation lock now, before the channel itself is torn down.
    /// </summary>
    /// <remarks>
    /// Every mutating caller must call this once runtime provisioning, deployment reconciliation,
    /// and package registration have all finished, and before launching or otherwise running the
    /// deployed application -- the lock must never be held across a running app, which would block
    /// every other winapp workflow. Safe to call when this target was not prepared for mutation
    /// (a no-op) and safe to call more than once: <see cref="TargetMutationLease.Dispose"/> is
    /// idempotent, and <see cref="DisposeAsync"/> calling it again afterward is harmless.
    /// </remarks>
    public void ReleaseMutationLease() => MutationLease?.Dispose();

    /// <summary>
    /// Asserts that this target still holds its mutation lease, failing fast on the programming
    /// error of a guest mutation running without the lock rather than letting it run unprotected.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// This target was not prepared with <see cref="PrepareTargetOptions.RequiresMutation"/> set,
    /// or the lease was already released (via <see cref="ReleaseMutationLease"/> or
    /// <see cref="DisposeAsync"/>).
    /// </exception>
    internal void RequireMutationLease()
    {
        // Checked for release, not just presence: a reference to a disposed lease is not proof of
        // exclusive access, and asserting only non-null would let a mutation run unprotected the
        // instant it happened to execute after the caller's own ReleaseMutationLease() call.
        if (MutationLease is not { IsReleased: false })
        {
            throw new InvalidOperationException(
                "This operation mutates the guest and requires a target prepared with " +
                $"{nameof(PrepareTargetOptions)}.{nameof(PrepareTargetOptions.Mutating)}, whose " +
                "mutation lease has not already been released.");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            // The executor is the target's own resource above the provider boundary, so tearing it
            // down is a provider concern expressed through IAsyncDisposable rather than something
            // this record knows how to do for a specific transport.
            if (Operations is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            // A fail-safe, not the intended release path: a well-behaved caller has already called
            // ReleaseMutationLease() by the time the target itself is disposed. Dispose is
            // idempotent, so this is a no-op in that case.
            MutationLease?.Dispose();
        }
    }
}

/// <summary>What a command needs from the target before it will run.</summary>
/// <param name="RequireInteractiveDesktop">
/// True for real input or screen capture, which need a connected interactive client. False for
/// read-only work such as UI Automation inspection.
/// </param>
/// <param name="RequiresMutation">
/// True when the command will change guest state, which is what the mutation lock covers.
/// </param>
internal sealed record PrepareTargetOptions(bool RequireInteractiveDesktop, bool RequiresMutation)
{
    /// <summary>Deployment, registration, and runtime installation.</summary>
    public static PrepareTargetOptions Mutating { get; } = new(true, true);

    /// <summary>Foreground-sensitive commands that change nothing in the guest.</summary>
    public static PrepareTargetOptions Interactive { get; } = new(true, false);

    /// <summary>Read-only inspection, which needs neither the desktop nor the lock.</summary>
    public static PrepareTargetOptions ReadOnly { get; } = new(false, false);
}

/// <summary>What an inspect-only look at a target found.</summary>
/// <param name="Running">True when the target winapp manages is running right now.</param>
/// <param name="Epoch">
/// Which generation is running. <see cref="ExecutionTargetEpoch.None"/> when nothing is.
/// </param>
/// <param name="Target">
/// A prepared channel to the running agent, or null when the target is not running or its agent
/// did not answer. The caller owns it and must dispose it.
/// </param>
internal sealed record TargetInspection(
    bool Running,
    ExecutionTargetEpoch Epoch,
    PreparedTarget? Target);

/// <summary>
/// The single entry point every targeted command goes through
/// (spec §"Ensure and reuse", §"Shared orchestration").
/// </summary>
/// <remarks>
/// Ordering here is the contract. Support is probed before anything is built or mutated, so a
/// missing prerequisite fails in seconds rather than after a long build and never falls back
/// silently to local execution. The mutation lock is taken only when the command will actually
/// change guest state, so a read-only inspection never blocks behind a deployment — and, equally,
/// never blocks one.
/// <para>
/// Agent version negotiation happens before the first real operation but after the channel exists,
/// because the decision needs what the guest actually reports rather than what the host assumes.
/// </para>
/// </remarks>
internal sealed class ExecutionTargetOrchestrator(
    IExecutionTargetBackend backend,
    ITargetMutationLock mutationLock,
    ITargetConnectionLock connectionLock,
    ITargetProgress? progress = null)
{
    internal const string PrepareProgressMessage = "Preparing Windows Sandbox...";

    private readonly ITargetProgress _progress = progress ?? NullTargetProgress.Instance;

    /// <summary>How long to wait for another winapp process to finish mutating this target.</summary>
    internal static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Resolves the window on this machine that the target's whole desktop is drawn into.
    /// </summary>
    /// <remarks>
    /// The only route from a command to a target's rendered desktop, and deliberately a thin one:
    /// which window belongs to which target is a fact only the backend can establish, and a backend
    /// that renders nothing here says so rather than being asked to invent an answer.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">
    /// This target does not render on this machine, or its client window cannot be identified.
    /// </exception>
    public TargetDesktopSurface ResolveDesktopSurface(TargetDesktopUse use) =>
        HostRendered().ResolveDesktopSurface(use);

    /// <summary>
    /// Answers the same question as <see cref="ResolveDesktopSurface"/>, writing nothing.
    /// </summary>
    /// <remarks>
    /// What <c>winapp target snapshot</c> uses, so reporting where a desktop is rendered never
    /// becomes a change to the state being reported.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">
    /// This target does not render on this machine, or its client window cannot be identified.
    /// </exception>
    public TargetDesktopSurface InspectDesktopSurface() =>
        HostRendered().InspectDesktopSurface();

    /// <summary>The backend as a host-rendered target, or a failure explaining that it is not one.</summary>
    private IHostRenderedTarget HostRendered()
    {
        if (backend is not IHostRenderedTarget rendered)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.Unsupported,
                $"The '{Target.Selector}' target does not draw a desktop on this machine, so it cannot be captured.",
                userAction:
                    "Capture from inside the target instead, with 'winapp ui screenshot --on " +
                    $"{Target.Selector}' or 'winapp ui record --on {Target.Selector}'.",
                example: $"winapp ui screenshot -a MyApp --on {Target.Selector} -o .\\shot.png");
        }

        return rendered;
    }

    /// <summary>
    /// Reports what the target looks like right now, without creating or repairing anything.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="PrepareAsync"/> for commands that only report. It deliberately
    /// skips every step that could change what it is about to describe: no support probe (which may
    /// enable a Windows feature or ask for elevation), no instance creation, no client reconnect, no
    /// agent repair, and no lock. A target that is not running is a result, not an error.
    /// <para>
    /// The caller owns <see cref="TargetInspection.Target"/> and must dispose it when it is not null.
    /// </para>
    /// </remarks>
    /// <exception cref="ExecutionTargetException">
    /// This target cannot be inspected without preparing it, or the host could not be asked at all.
    /// </exception>
    public async Task<TargetInspection> InspectAsync(CancellationToken cancellationToken)
    {
        if (backend is not IInspectableTarget inspectable)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.Unsupported,
                $"The '{Target.Selector}' target cannot be inspected without preparing it first.",
                userAction: $"Run a command that prepares the target, such as 'winapp run . --on {Target.Selector}'.");
        }

        var attachment = await inspectable.TryAttachAsync(cancellationToken).ConfigureAwait(false);

        if (attachment.Connection is not { } connection)
        {
            return new TargetInspection(attachment.Running, attachment.Epoch, null);
        }

        GuestCommandChannel? channel = null;

        try
        {
            channel = new GuestCommandChannel(connection.Transport, connection.Epoch);
            channel.Start();

            var capabilities = await channel.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);

            var prepared = new PreparedTarget(
                backend.Target, channel, connection.Epoch, capabilities, Reused: true, MutationLease: null);

            channel = null;
            return new TargetInspection(true, connection.Epoch, prepared);
        }
        finally
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Cheaply checks that this host can use the target at all, before any build or mutation.
    /// </summary>
    /// <exception cref="ExecutionTargetException">The target is not usable on this host.</exception>
    public async Task EnsureSupportedAsync(CancellationToken cancellationToken)
    {
        var support = await backend.ProbeSupportAsync(cancellationToken).ConfigureAwait(false);

        if (!support.IsSupported)
        {
            throw new ExecutionTargetException(support.Error ?? new ExecutionTargetErrorInfo
            {
                Code = ExecutionTargetErrorCodes.Unsupported,
                Message = "This host cannot run commands in an execution target.",
            });
        }
    }

    /// <summary>
    /// Ensures a running target with a compatible agent and returns a ready command channel.
    /// </summary>
    /// <remarks>
    /// The caller owns the returned <see cref="PreparedTarget"/> and must dispose it. When
    /// <paramref name="options"/> requires mutation, the caller also owns
    /// <see cref="PreparedTarget.MutationLease"/> and must release it explicitly, via
    /// <see cref="PreparedTarget.ReleaseMutationLease"/>, once every guest mutation it is about to
    /// perform (runtime provisioning, deployment reconciliation, package registration) has finished
    /// — never inside this method, which only probes, connects, and negotiates capabilities. Holding
    /// the lease for the life of a running application would mean one long-running app blocked every
    /// other workflow, which is exactly what the spec excludes from the lock's scope; but releasing
    /// it here, before the caller has done any of the mutating work the lock exists to protect, would
    /// leave that work completely unprotected.
    /// <para>
    /// The connection lock, unlike the mutation lock, never outlives this method. It is released the
    /// moment the channel exists, because its job is to make establishment safe, not use: it spans
    /// the reconnect attempt and the bootstrap that follows a failed one, so two hosts starting at
    /// once cannot both create an instance, rewrite connection material, or replace the agent.
    /// Whichever gets in first bootstraps; the second finds a healthy agent and reconnects to it.
    /// </para>
    /// </remarks>
    public async Task<PreparedTarget> PrepareAsync(
        PrepareTargetOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        // One setup line covers support probing, instance preparation, and agent connection. The
        // provider reports exceptional setup work separately, while routine internal transitions
        // stay quiet so a successful run is readable.
        _progress.Report(PrepareProgressMessage);

        await EnsureSupportedAsync(cancellationToken).ConfigureAwait(false);

        GuestCommandChannel? channel = null;
        TargetMutationLease? mutationLease = null;

        try
        {
            TargetConnection connection;

            using (AcquireConnection(cancellationToken))
            {
                connection = await backend.EnsureConnectedAsync(
                    new EnsureTargetOptions(options.RequireInteractiveDesktop),
                    cancellationToken).ConfigureAwait(false);

                channel = new GuestCommandChannel(connection.Transport, connection.Epoch);
                channel.Start();
            }

            // Negotiated before any lock is taken, not after. A guest that is refusing this channel
            // says so here, and waiting up to the lock timeout first would turn an immediate,
            // actionable "the agent is busy" into a stale channel and a confusing transport error.
            var capabilities = await channel.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);

            mutationLease = options.RequiresMutation ? AcquireLock(cancellationToken) : null;

            if (mutationLease?.WasAbandoned == true)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "The previous winapp process did not release the {0} lock cleanly; reconciling.",
                    backend.Target.Id);
            }

            EnsureCapable(options, capabilities);

            var prepared = new PreparedTarget(
                backend.Target,
                channel,
                connection.Epoch,
                capabilities,
                connection.Reused,
                mutationLease);

            channel = null;
            mutationLease = null;
            return prepared;
        }
        catch
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            // Only the mutation lease needs cleaning up here: the connection lease is scoped to the
            // establishment block above. On the success path this is a no-op, because ownership of
            // the lease has already transferred to the PreparedTarget and the local was nulled.
            mutationLease?.Dispose();
        }
    }

    /// <summary>Provider diagnostics for a failure envelope.</summary>
    public IReadOnlyDictionary<string, string> DescribeForDiagnostics() => backend.DescribeForDiagnostics();

    /// <summary>
    /// The one non-local target this build's registered provider serves.
    /// </summary>
    /// <remarks>
    /// Exposed so a command can prove the selector it was given names a target that actually exists
    /// before it prepares anything. It is the provider's own reference, not a constant, so nothing
    /// above the provider boundary has to know which kind is registered.
    /// </remarks>
    public ExecutionTargetRef Target => backend.Target;

    /// <summary>
    /// Acquires a fresh mutation lease against this target's own connection, without going through
    /// <see cref="PrepareAsync"/> again.
    /// </summary>
    /// <remarks>
    /// For a caller that already holds a live, still-connected <see cref="PreparedTarget"/> whose
    /// own mutation lease it already released (because the mutating work it was protecting has
    /// finished) but that later needs a second, independent mutating window against the very same
    /// target -- for example, a host-orchestrated cleanup phase that must run only after a launched
    /// application has fully exited, an unbounded time later. That target's channel is still live
    /// precisely because <see cref="PrepareAsync"/> released only the connection lock, never the
    /// channel, so the second window has an established connection to reuse and needs nothing from
    /// the connection lock at all -- re-preparing would merely re-establish a connection that
    /// already exists, and under a new epoch rather than the one this deployment's state is fenced
    /// on. Only the mutation lock is taken, which is exactly the lock a second mutating window
    /// needs to be exclusive against every other mutating command.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">Another command is still mutating this target.</exception>
    public TargetMutationLease AcquireMutationLease(CancellationToken cancellationToken) =>
        AcquireLock(cancellationToken);

    private TargetMutationLease AcquireLock(CancellationToken cancellationToken)
    {
        var lease = mutationLock.TryAcquire(backend.Target, LockTimeout, cancellationToken);

        return lease ?? throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TargetAmbiguous,
            "Another winapp command is still changing this Windows Sandbox.",
            userAction: "Wait for the other command to finish, then retry.",
            context: new Dictionary<string, string> { ["targetId"] = backend.Target.Id });
    }

    private TargetConnectionLease AcquireConnection(CancellationToken cancellationToken)
    {
        return connectionLock.TryAcquire(backend.Target, LockTimeout, cancellationToken)
            ?? throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                "Another winapp command is still starting or repairing this Windows Sandbox.",
                userAction: "Wait for the other command to finish, then retry.",
                context: new Dictionary<string, string> { ["targetId"] = backend.Target.Id });
    }

    /// <summary>
    /// Refuses a command whose required capability the guest does not have.
    /// </summary>
    /// <remarks>
    /// Checked against what the guest reports rather than inferred from the provider name, which is
    /// what lets a future backend reuse this unchanged.
    /// <para>
    /// The capability consulted is <see cref="ExecutionTargetCapabilities.SupportsRealInput"/>, not
    /// <see cref="ExecutionTargetCapabilities.SupportsInteractiveDesktop"/>. Those differ in exactly
    /// the case that matters: a closed Sandbox client leaves the guest session and UI Automation
    /// working — so an interactive desktop is still reported — while real input and Windows Graphics
    /// Capture stop. Gating on the wrong one would admit foreground commands that then report input
    /// they never delivered.
    /// </para>
    /// <para>
    /// This is still capability, not readiness. A command that delivers input re-verifies
    /// immediately beforehand, because the user can disconnect between this check and the keystroke.
    /// </para>
    /// </remarks>
    private static void EnsureCapable(PrepareTargetOptions options, ExecutionTargetCapabilities capabilities)
    {
        if (!options.RequireInteractiveDesktop)
        {
            return;
        }

        if (capabilities.SupportsRealInput)
        {
            return;
        }

        throw ExecutionTargetException.Create(
            capabilities.SupportsInteractiveDesktop
                ? ExecutionTargetErrorCodes.InputNotReady
                : ExecutionTargetErrorCodes.NoInteractiveSession,
            capabilities.SupportsInteractiveDesktop
                ? "The Windows Sandbox window is disconnected, so real input and screen capture are unavailable."
                : "The execution target has no interactive desktop, so this command cannot run there.",
            userAction: "Reconnect the Sandbox window, then retry.",
            nextCommand: new ExecutionTargetNextCommand
            {
                Command = "wsb connect",

                // Reconnecting changes what is on screen, so it needs a user decision.
                Advisory = true,
            });
    }
}
