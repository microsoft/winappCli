// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>How winapp came to be using the instance it is using.</summary>
/// <remarks>
/// This replaces a single "reused" flag because the four cases need different handling and only one
/// of them may take the warm-reconnect shortcut. Collapsing them into a boolean is what would let a
/// freshly adopted guest be treated as one winapp had already bootstrapped.
/// </remarks>
internal enum SandboxInstanceOrigin
{
    /// <summary>Started by this call.</summary>
    Created,

    /// <summary>
    /// Recorded by an earlier command, still listed, and still owned. The only origin whose
    /// persisted connection material describes the live guest.
    /// </summary>
    Reused,

    /// <summary>
    /// A start winapp had assigned an ID to but never confirmed, reconciled to the live instance
    /// with that exact ID.
    /// </summary>
    RecoveredStart,

    /// <summary>
    /// A Sandbox winapp did not start, taken over because <c>--on sandbox</c> asked for one and
    /// Windows allows only one at a time.
    /// </summary>
    Adopted,
}

/// <summary>Outcome of reconciling persisted state against what is actually running.</summary>
/// <param name="State">How the managed instance is currently classified.</param>
/// <param name="InstanceId">The managed instance, when one exists.</param>
/// <param name="Epoch">Generation identity of that instance.</param>
/// <param name="Revision">State revision the caller must pass when committing.</param>
internal sealed record SandboxReconciliation(
    TargetLifecycleState State,
    string? InstanceId,
    ExecutionTargetEpoch Epoch,
    long Revision);

/// <summary>Result of ensuring a managed Sandbox exists.</summary>
/// <param name="InstanceId">The managed instance.</param>
/// <param name="Epoch">Its generation identity.</param>
/// <param name="Origin">How winapp came to be using it.</param>
/// <param name="IsWarm">Whether a previous command finished bootstrapping this exact epoch.</param>
internal sealed record SandboxInstanceLease(
    string InstanceId,
    ExecutionTargetEpoch Epoch,
    SandboxInstanceOrigin Origin,
    bool IsWarm = false)
{
    /// <summary>Whether winapp took over an instance it did not start.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsWarm"/> is deliberately not derived from <see cref="Origin"/>. Ownership is
    /// committed before the guest is prepared, so a command killed part-way through its first
    /// bootstrap leaves an instance that is owned and listed but has no connected client, no
    /// Developer Mode, and no agent. Treating that as warm is what would make the next command skip
    /// <c>wsb connect</c> and then launch the agent as <c>ExistingLogin</c> into a session no client
    /// has established. The backend records the completed bootstrap only once an authenticated agent
    /// connection has actually succeeded.
    /// </para>
    /// </remarks>
    public bool IsAdopted => Origin is SandboxInstanceOrigin.Adopted;
}

/// <summary>
/// Owns the Windows Sandbox singleton: which instance winapp is using, how it got it, and what to
/// do when a start half-succeeds (spec §"Sandbox lifecycle").
/// </summary>
/// <remarks>
/// <para>
/// Windows permits only one Sandbox at a time, and <c>--on sandbox</c> is explicit consent to make
/// that one usable. So a Sandbox that is already running is taken over rather than refused: asking
/// the user to close it would mean the flag they just passed could not do the thing they passed it
/// for. Taking it over does change the guest — see <see cref="AdoptRunningInstanceAsync"/> — but the
/// instance is never stopped, by this type or any other.
/// </para>
/// <para>
/// Ownership is established <em>before</em> the provider is asked to do anything. The instance ID is
/// generated here and persisted as a pending start first, so a <c>wsb start</c> that fails after
/// creating an instance can be reconciled against the exact ID winapp asked for. Recovering by
/// looking for a new entry in <c>wsb list</c> would attribute whatever appeared to winapp, including
/// a Sandbox somebody else started in the same second.
/// </para>
/// </remarks>
internal sealed class WindowsSandboxLifecycle(
    IWindowsSandboxCli cli,
    ITargetStateStore stateStore,
    ITargetProgress? progress = null)
{
    private readonly ExecutionTargetRef _target = WindowsSandboxTarget.Default;
    private readonly ITargetProgress _progress = progress ?? NullTargetProgress.Instance;

    /// <summary>How long to keep polling for an instance an unconfirmed start may have created.</summary>
    /// <remarks>
    /// <c>wsb list</c> can lag the instance it is about to report, so a single check right after a
    /// failed start would conclude nothing was created and start a second one — against a singleton.
    /// </remarks>
    internal static readonly TimeSpan StartReconciliationTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Gap between reconciliation polls.</summary>
    internal static readonly TimeSpan ReconciliationPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Progress line for an instance recovered from an unconfirmed start.</summary>
    internal const string RecoveringMessage = "Recovering the Windows Sandbox winapp had already started...";

    /// <summary>Progress line for an instance winapp did not start.</summary>
    internal const string AdoptingMessage = "Using the Windows Sandbox that is already running...";

    /// <summary>Delay seam, so reconciliation bounds are exercised without real waiting.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Clock seam, so reconciliation bounds are exercised without real time passing.</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Instance-ID generator seam; the default is cryptographically random.</summary>
    internal Func<string> NewInstanceId { get; set; } = GenerateInstanceId;

    /// <summary>
    /// Classifies the managed instance by comparing persisted state against <c>wsb list</c>.
    /// </summary>
    /// <remarks>
    /// An instance that vanished — because the user closed it or ran <c>wsb stop</c> — is reported
    /// as <see cref="TargetLifecycleState.Terminated"/> so its process, deployment, and handle state
    /// is invalidated rather than resolved against whatever is created next.
    /// </remarks>
    public async Task<SandboxReconciliation> ReconcileAsync(CancellationToken cancellationToken)
    {
        var state = stateStore.Read(_target);
        var running = await cli.ListAsync(cancellationToken).ConfigureAwait(false);
        var revision = state?.Revision ?? 0;

        if (state?.InstanceId is not { } managedId || string.IsNullOrWhiteSpace(state.BootNonce))
        {
            return new SandboxReconciliation(TargetLifecycleState.Terminated, null, ExecutionTargetEpoch.None, revision);
        }

        if (!running.Contains(managedId, StringComparer.OrdinalIgnoreCase))
        {
            // Our instance is gone. Report Terminated so the caller invalidates everything scoped to
            // the old epoch before anything new is created.
            return new SandboxReconciliation(TargetLifecycleState.Terminated, null, ExecutionTargetEpoch.None, revision);
        }

        return new SandboxReconciliation(
            TargetLifecycleState.Running,
            managedId,
            ExecutionTargetEpoch.Create(managedId, state.BootNonce),
            revision);
    }

    /// <summary>
    /// Returns a usable Sandbox: the one winapp already owns, one it can recover or take over, or a
    /// new one.
    /// </summary>
    /// <remarks>
    /// Callers must already hold the target mutation lock: creating or taking over the singleton
    /// mutates it.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">No instance could be obtained or prepared.</exception>
    public async Task<SandboxInstanceLease> EnsureInstanceAsync(CancellationToken cancellationToken)
    {
        var state = stateStore.Read(_target);
        var running = await cli.ListAsync(cancellationToken).ConfigureAwait(false);

        // Pattern-matching both members at once removes the redundant re-test of `state` that a
        // separate null-conditional access would need, while keeping the compiler's null analysis
        // satisfied.
        if (state is { InstanceId: { } managedId, BootNonce: { } bootNonce }
            && running.Contains(managedId, StringComparer.OrdinalIgnoreCase))
        {
            var epoch = ExecutionTargetEpoch.Create(managedId, bootNonce);

            // Warm only when a previous command got all the way to an authenticated agent
            // connection for this exact epoch. Ownership alone proves the instance is winapp's, not
            // that anything inside it was ever prepared.
            return new SandboxInstanceLease(
                managedId,
                epoch,
                SandboxInstanceOrigin.Reused,
                IsWarm: string.Equals(state.BootstrappedEpoch, epoch.Value, StringComparison.Ordinal));
        }

        // An unconfirmed start from this or an earlier process is resolved before anything new is
        // attempted. Starting again while that instance is alive would ask a singleton to become two.
        if (state?.PendingInstanceId is { } pendingId)
        {
            if (await TryRecoverPendingStartAsync(state, pendingId, running, cancellationToken)
                    .ConfigureAwait(false) is { } recovered)
            {
                return recovered;
            }

            // The pending record was just cleared, so ownership decisions below must not keep
            // excluding an ID that is no longer claimed.
            state = stateStore.Read(_target);
            running = await cli.ListAsync(cancellationToken).ConfigureAwait(false);
        }

        if (running.Count > 0)
        {
            return SelectAdoptionCandidate(running, state) is { } candidate
                ? await AdoptRunningInstanceAsync(state, candidate, cancellationToken).ConfigureAwait(false)
                : throw NoUsableCandidate(running);
        }

        return await StartOwnedInstanceAsync(state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a Sandbox under an ID winapp assigned and recorded first.
    /// </summary>
    /// <remarks>
    /// The pending record is committed before <c>wsb start</c> is called and is not removed when the
    /// call fails. That is what makes a partial start recoverable both within this process and after
    /// it dies: the next command finds the exact ID this one asked for and reconciles that, instead
    /// of finding an instance it cannot account for.
    /// </remarks>
    private async Task<SandboxInstanceLease> StartOwnedInstanceAsync(
        TargetState? state,
        CancellationToken cancellationToken)
    {
        var instanceId = NewInstanceId();
        var revision = MarkPending(state, instanceId);
        string reportedId;

        // Only the provider call is inside the recovery scope. Everything after it -- the identity
        // and reachability checks -- is winapp refusing an instance it will not claim, and funnelling
        // those into "maybe it worked anyway" recovery would defeat the check that raised them.
        try
        {
            reportedId = await cli.StartAsync(instanceId, configuration: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex) when (IsSingletonInUse(ex))
        {
            // The singleton is already taken. That is a reuse situation, not a broken host, so the
            // running instance is taken over rather than reported as a start failure.
            var running = await cli.ListAsync(cancellationToken).ConfigureAwait(false);
            var current = stateStore.Read(_target);

            if (SelectAdoptionCandidate(running, current) is not { } candidate)
            {
                throw;
            }

            return await AdoptRunningInstanceAsync(current, candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionTargetException)
        {
            // The instance may exist despite the failure -- 0x80070002 from `start` has been
            // observed on a host that had already created and listed one. Only the exact assigned ID
            // is reconciled, never "whatever is new in the list", and the pending record survives
            // either way so a process that dies here still leaves the next command able to finish.
            if (!await WaitForAssignedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            _progress.Report(RecoveringMessage);

            return Commit(
                instanceId,
                SandboxInstanceOrigin.RecoveredStart,
                stateStore.Read(_target)?.Revision ?? revision);
        }

        // A provider that hands back a different ID than the one it was given is not a provider
        // whose instance winapp can claim to own. Refused rather than adopted, because the instance
        // winapp asked for may also exist and preparing the wrong guest is worse than failing.
        if (!string.Equals(reportedId, instanceId, StringComparison.OrdinalIgnoreCase))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.StartFailed,
                "Windows Sandbox reported a different instance than the one winapp asked it to start.",
                userAction: "Retry the command. If it keeps failing, restart the host.",
                context: new Dictionary<string, string>
                {
                    ["requestedId"] = instanceId,
                    ["reportedId"] = reportedId,
                });
        }

        if (!await WaitForAssignedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.StartFailed,
                "Windows Sandbox started but the new instance did not become reachable.",
                userAction: "Retry the command. If it keeps failing, restart the host.",
                context: new Dictionary<string, string> { ["sandboxId"] = instanceId });
        }

        return Commit(instanceId, SandboxInstanceOrigin.Created, revision);
    }

    /// <summary>Resolves an unconfirmed start recorded by this or an earlier process.</summary>
    /// <remarks>
    /// Returns null when the pending instance is genuinely not there, which lets the caller move on
    /// to adoption or a fresh start. The pending record is cleared only when it is proven not to
    /// exist, so a transient listing gap does not discard the one piece of evidence that identifies
    /// winapp's instance.
    /// </remarks>
    private async Task<SandboxInstanceLease?> TryRecoverPendingStartAsync(
        TargetState state,
        string pendingId,
        IReadOnlyList<string> running,
        CancellationToken cancellationToken)
    {
        var ready = running.Contains(pendingId, StringComparer.OrdinalIgnoreCase)
            && await cli.IsResolvableAsync(pendingId, cancellationToken).ConfigureAwait(false);

        // Listing alone is not readiness, and neither is one unsuccessful probe: an instance created
        // moments ago is listed before its guest answers. The bounded wait covers both.
        if (!ready && !await WaitForAssignedInstanceAsync(pendingId, cancellationToken).ConfigureAwait(false))
        {
            ClearPending(state);
            return null;
        }

        _progress.Report(RecoveringMessage);

        return Commit(pendingId, SandboxInstanceOrigin.RecoveredStart, state.Revision);
    }

    /// <summary>
    /// Waits, within a bound, for one specific instance ID to be both listed and reachable.
    /// </summary>
    /// <remarks>
    /// Listing alone is not enough. An instance appears in <c>wsb list</c> before its guest can be
    /// resolved, and treating "listed" as "ready" is what would let the next step share folders into
    /// a guest that is not there yet.
    /// </remarks>
    private async Task<bool> WaitForAssignedInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        var deadline = UtcNow() + StartReconciliationTimeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var running = await cli.ListAsync(cancellationToken).ConfigureAwait(false);

            if (running.Contains(instanceId, StringComparer.OrdinalIgnoreCase) &&
                await cli.IsResolvableAsync(instanceId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (UtcNow() >= deadline)
            {
                return false;
            }

            await Delay(ReconciliationPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes over a Sandbox winapp did not start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a deliberate product decision, not a fallback. Windows allows one Sandbox, and a user
    /// who typed <c>--on sandbox</c> asked for their command to run in it; refusing because something
    /// is already there would make the flag unusable exactly when a Sandbox is available.
    /// </para>
    /// <para>
    /// Taking over is <b>not</b> read-only and is not reversible. Preparing the guest maps winapp's
    /// bootstrap folders into it, connects its client, enables Developer Mode, and adds an inbound
    /// firewall rule for the agent. Anything already running in that guest shares the session with
    /// what winapp deploys, which is the trust model <c>--on sandbox</c> documents. Nothing existing is
    /// removed and the instance is never stopped.
    /// </para>
    /// <para>
    /// Reachability is proven before ownership is recorded, so an instance that is on its way out is
    /// not claimed and then bootstrapped into.
    /// </para>
    /// </remarks>
    private async Task<SandboxInstanceLease> AdoptRunningInstanceAsync(
        TargetState? state,
        string candidate,
        CancellationToken cancellationToken)
    {
        if (!await cli.IsResolvableAsync(candidate, cancellationToken).ConfigureAwait(false))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.UnmanagedInstance,
                "A Windows Sandbox is already running but did not respond, so winapp could not use it.",
                userAction: "Wait for it to finish starting and retry, or close it if it is no longer needed.",
                context: new Dictionary<string, string> { ["sandboxId"] = candidate },
                nextCommand: new ExecutionTargetNextCommand
                {
                    Command = $"wsb stop --id {candidate}",

                    // Stopping a Sandbox winapp did not start can destroy the user's work.
                    Advisory = true,
                },
                example: "winapp run . --on sandbox");
        }

        _progress.Report(AdoptingMessage);

        return Commit(candidate, SandboxInstanceOrigin.Adopted, state?.Revision ?? 0);
    }

    /// <summary>
    /// Picks the single running instance that is a candidate for taking over, or null.
    /// </summary>
    /// <remarks>
    /// Exactly one, and never one winapp already accounts for. More than one is not a state Windows
    /// Sandbox produces today, so seeing it means the host is in a condition winapp does not
    /// understand — and picking arbitrarily from a set it cannot explain is how the wrong guest gets
    /// prepared.
    /// <para>
    /// A recorded instance is excluded only when the record actually establishes ownership, which
    /// takes both an ID and a boot nonce. A half-written record names an instance winapp cannot
    /// form an epoch for, so treating that ID as owned would exclude the one candidate on the host
    /// and report a single running Sandbox as "more than one".
    /// </para>
    /// </remarks>
    private static string? SelectAdoptionCandidate(IReadOnlyList<string> running, TargetState? state)
    {
        var owned = state is { InstanceId: { } id, BootNonce.Length: > 0 } ? id : null;

        var candidates = running
            .Where(candidate => !string.Equals(candidate, owned, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => !string.Equals(
                candidate, state?.PendingInstanceId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// Explains why none of the running instances could be taken over.
    /// </summary>
    /// <remarks>
    /// Two different situations reach this point and they need different guidance. Several running
    /// Sandboxes is a host state winapp does not understand, and the user has to reduce it to one.
    /// A single running Sandbox that is nonetheless not a candidate means it is already accounted
    /// for by state that changed underneath this command — another winapp process claimed it while
    /// this one was deciding — which retrying resolves and closing a Sandbox would not.
    /// </remarks>
    private static ExecutionTargetException NoUsableCandidate(IReadOnlyList<string> running) =>
        running.Count > 1
            ? AmbiguousInstances(running)
            : ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                "Windows Sandbox ownership changed while this command was deciding which instance to use.",
                userAction: "Retry the command.",
                context: new Dictionary<string, string>
                {
                    ["sandboxIds"] = string.Join(',', running),
                },
                example: "winapp run . --on sandbox");

    /// <summary>Refuses a host with more running Sandboxes than winapp can account for.</summary>
    internal static ExecutionTargetException AmbiguousInstances(IReadOnlyList<string> running)
    {
        ArgumentNullException.ThrowIfNull(running);

        return ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.UnmanagedInstance,
            "More than one Windows Sandbox is running, so winapp cannot tell which one to use.",
            userAction: "Close the Sandboxes you no longer need, leaving at most one, then retry.",
            context: new Dictionary<string, string>
            {
                ["sandboxIds"] = string.Join(',', running),
                ["count"] = running.Count.ToString(CultureInfo.InvariantCulture),
            },
            example: "winapp run . --on sandbox");
    }

    /// <summary>Whether a failure says the Sandbox singleton is already in use.</summary>
    private static bool IsSingletonInUse(ExecutionTargetException exception) =>
        exception.Error.Context?.GetValueOrDefault(WsbHResult.ContextKey)
            == WsbHResult.Format(WsbHResult.AppSingleUse);

    /// <summary>Records the intent to start a specific instance, before anything is started.</summary>
    private long MarkPending(TargetState? state, string instanceId)
    {
        var committed = stateStore.Commit(
            _target,
            new TargetState
            {
                SchemaVersion = state?.SchemaVersion ?? 0,
                Revision = 0,
                TargetKind = _target.Kind,
                TargetId = _target.Id,

                // Ownership of any previous instance is deliberately not carried forward: this path
                // only runs when there is nothing left to own.
                PendingInstanceId = instanceId,
                PendingStartedUtc = UtcNow(),
            },
            state?.Revision ?? 0);

        return committed.Revision;
    }

    /// <summary>Records ownership of <paramref name="instanceId"/> and clears the pending marker.</summary>
    private SandboxInstanceLease Commit(string instanceId, SandboxInstanceOrigin origin, long expectedRevision)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        stateStore.Commit(
            _target,
            new TargetState
            {
                SchemaVersion = 0,
                Revision = 0,
                TargetKind = _target.Kind,
                TargetId = _target.Id,
                InstanceId = instanceId,
                BootNonce = nonce,
                InstanceOrigin = origin.ToString(),
            },
            expectedRevision);

        return new SandboxInstanceLease(instanceId, ExecutionTargetEpoch.Create(instanceId, nonce), origin);
    }

    /// <summary>Drops a pending marker for a start that provably produced nothing.</summary>
    /// <remarks>
    /// Best effort. A pending marker that outlives its usefulness costs one extra reconciliation on
    /// the next command; failing the command over it would trade a working Sandbox for an error.
    /// </remarks>
    private void ClearPending(TargetState state)
    {
        try
        {
            stateStore.Commit(
                _target,
                state with { PendingInstanceId = null, PendingStartedUtc = null },
                state.Revision);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExecutionTargetException)
        {
            // Another process may have committed first; its view is at least as current as this one.
        }
    }

    /// <summary>
    /// Generates the instance ID winapp will claim.
    /// </summary>
    /// <remarks>
    /// Cryptographically random and shaped as a version-4 UUID. Randomness is what makes the ID
    /// unguessable, and therefore what makes "this exact ID is mine" a claim no other process can
    /// accidentally or deliberately satisfy.
    /// </remarks>
    internal static string GenerateInstanceId()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);

        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes).ToString("D", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Forgets the managed instance after it has been observed gone.
    /// </summary>
    /// <remarks>
    /// Clearing state is what makes the next command treat handles and deployments from the old
    /// epoch as stale rather than resolving them against a recreated guest.
    /// </remarks>
    public void InvalidateManagedInstance() => stateStore.Clear(_target);
}
