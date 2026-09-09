// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Outcome of reconciling one deployment into a guest.</summary>
/// <param name="DeploymentId">Internal deployment identity.</param>
/// <param name="Plan">What had to change.</param>
/// <param name="State">Committed state after reconciliation.</param>
internal sealed record DeploymentResult(string DeploymentId, DeploymentPlan Plan, DeploymentState State);

/// <summary>
/// Reconciles a host snapshot into a guest deployment root
/// (spec §"Deployment model", §"Exact in-place reconciliation").
/// </summary>
/// <remarks>
/// Target-neutral by construction: it talks only to <see cref="ITargetOperationExecutor"/> and knows
/// nothing about Windows Sandbox paths, IDs, or commands. That is what lets the whole sequence —
/// including the dirty-repair and epoch-invalidation paths — be verified against a fake transport.
/// <para>
/// The order matters and is the spec's: mark dirty and persist the desired state <em>before</em>
/// touching any file, so a host that dies mid-copy leaves a deployment that is provably incomplete
/// rather than one that looks finished. There is no rollback guarantee; there is a guarantee that an
/// incomplete deployment never launches.
/// </para>
/// </remarks>
internal sealed class TargetDeploymentService(IDeploymentStateStore stateStore)
{
    /// <summary>
    /// Brings the guest's copy of <paramref name="deploymentId"/> to exactly
    /// <paramref name="desired"/>.
    /// </summary>
    /// <param name="target">Target whose state root holds this deployment's record.</param>
    /// <param name="epoch">Current target generation.</param>
    /// <param name="channel">Connected guest channel.</param>
    /// <param name="deploymentId">Internal deployment identity.</param>
    /// <param name="desired">Immutable desired state captured from the host.</param>
    /// <param name="sourceRoot">Host directory the snapshot was taken from.</param>
    /// <param name="clean">
    /// True to discard the guest copy first. This is <c>run --clean</c>: the explicit clean-reinstall
    /// operation, which may clear only this deployment's own state.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="cleanRegisteredLayoutAsync">
    /// When <paramref name="clean"/> is true, an additional guest-side cleanup to run before the
    /// deployment is committed clean — used for the registration layout a deployment's package was
    /// registered from, which is guest-derived from the payload reconciled here and must not survive
    /// a clean redeploy either. Deliberately run inside the same dirty-to-clean window as the payload
    /// reconciliation above: a failure here must leave the deployment dirty for exactly the same
    /// reason a failed payload delete or write does, so a state that already claims "clean" can never
    /// describe a layout a partial cleanup left damaged. This service accepts an arbitrary callback
    /// rather than a layout path so it stays target-neutral — it has no notion of a registration
    /// layout of its own.
    /// </param>
    /// <remarks>
    /// Callers must already hold the target mutation lock: deployment synchronization mutates the
    /// guest.
    /// </remarks>
    public async Task<DeploymentResult> ReconcileAsync(
        ExecutionTargetRef target,
        ExecutionTargetEpoch epoch,
        ITargetOperationExecutor channel,
        string deploymentId,
        DeploymentSnapshot desired,
        DirectoryInfo sourceRoot,
        bool clean,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? cleanRegisteredLayoutAsync = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(sourceRoot);

        var scope = new GuestPathScope(GuestRootNames.Deployment, deploymentId);
        var existing = stateStore.Read(target, deploymentId);
        var revision = existing?.Revision ?? 0;

        // State from a previous generation describes a guest that no longer exists. Carrying its
        // package and process records forward would let a stale process ID or package registration
        // resolve against a completely different Sandbox.
        var carried = existing is not null && existing.IsForEpoch(epoch) ? existing : null;

        // Persist the desired state and the dirty flag before *any* guest mutation, including a
        // clean wipe. A crash between wiping and committing would otherwise leave state claiming a
        // complete deployment over an empty guest folder, and the next run would launch nothing.
        var dirtyState = stateStore.Commit(
            target,
            new DeploymentState
            {
                SchemaVersion = DeploymentStateStore.CurrentSchemaVersion,
                Revision = revision,
                DeploymentId = deploymentId,
                TargetEpoch = epoch.Value,
                Dirty = true,
                Desired = desired.Files,
                Package = carried?.Package,
                WasPackaged = carried is not null && (carried.WasPackaged || carried.Package is not null),

                // Whatever was running belonged to the previous layout. Keeping its ID would let a
                // later command report a process that is no longer this deployment's.
                TrackedOperationProcessId = null,
                TrackedOperationProcessStartTicksUtc = null,
            },
            revision);

        if (clean)
        {
            await channel.DeleteScopeAsync(scope, cancellationToken).ConfigureAwait(false);
        }

        var actual = clean
            ? []
            : await channel.ListFilesAsync(scope, cancellationToken).ConfigureAwait(false);

        var plan = DeploymentPlanner.CreatePlan(desired, ToDeploymentFiles(actual));

        // Removals run before writes. A path that was a file and is now a directory — or the
        // reverse — cannot be created until the old entry is gone, so writing first would fail on
        // exactly the layout changes reconciliation exists to handle.
        if (plan.Removed.Count > 0)
        {
            await channel.DeleteFilesAsync(scope, plan.Removed, cancellationToken).ConfigureAwait(false);
        }

        foreach (var file in plan.Added.Concat(plan.Changed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PutAsync(channel, scope, sourceRoot.FullName, file, cancellationToken).ConfigureAwait(false);
        }

        await VerifyAsync(channel, scope, desired, cancellationToken).ConfigureAwait(false);

        if (clean && cleanRegisteredLayoutAsync is not null)
        {
            // Still inside the dirty window committed above: an interrupted cleanup here (a locked
            // file, for instance) throws before the commit below runs, so the persisted state keeps
            // telling the truth -- dirty -- rather than a "clean" that a damaged layout contradicts.
            await cleanRegisteredLayoutAsync(cancellationToken).ConfigureAwait(false);
        }

        var cleanState = stateStore.Commit(
            target,
            dirtyState with { Dirty = false },
            dirtyState.Revision);

        return new DeploymentResult(deploymentId, plan, cleanState);
    }

    /// <summary>Records the package this deployment registered.</summary>
    /// <remarks>
    /// Committed separately from reconciliation because registration happens after files are in
    /// place, and a failure there must not undo a correct file layout.
    /// </remarks>
    public DeploymentState CommitPackage(
        ExecutionTargetRef target,
        DeploymentState state,
        PackageOwnership? package) =>
        stateStore.Commit(
            target,
            state with
            {
                Package = package,
                WasPackaged = state.WasPackaged || package is not null,
            },
            state.Revision);

    /// <summary>Every deployment recorded for a target.</summary>
    public IReadOnlyList<DeploymentState> List(ExecutionTargetRef target) => stateStore.List(target);

    /// <summary>
    /// Reads a deployment's state, but only when it describes the current target generation.
    /// </summary>
    /// <remarks>
    /// Used before a redeploy decides whether there is a previous instance to stop: state from a
    /// previous generation describes a guest that no longer exists, so its package and process
    /// records must never be treated as something currently running.
    /// </remarks>
    public DeploymentState? ReadCurrent(ExecutionTargetRef target, ExecutionTargetEpoch epoch, string deploymentId)
    {
        var existing = stateStore.Read(target, deploymentId);
        return existing is not null && existing.IsForEpoch(epoch) ? existing : null;
    }

    /// <summary>Records the process this deployment launched.</summary>
    public DeploymentState CommitProcess(
        ExecutionTargetRef target,
        DeploymentState state,
        int? processId,
        long? startTicksUtc) =>
        stateStore.Commit(
            target,
            state with
            {
                TrackedOperationProcessId = processId,
                TrackedOperationProcessStartTicksUtc = startTicksUtc,
            },
            state.Revision);

    /// <summary>Clears obsolete registration and tracked-operation claims in one revision.</summary>
    public DeploymentState ClearRegistration(ExecutionTargetRef target, DeploymentState state) =>
        stateStore.Commit(
            target,
            state with
            {
                WasPackaged = state.WasPackaged || state.Package is not null,
                Package = null,
                TrackedOperationProcessId = null,
                TrackedOperationProcessStartTicksUtc = null,
            },
            state.Revision);

    /// <summary>
    /// Fails when a deployment is dirty or belongs to a previous generation.
    /// </summary>
    /// <remarks>
    /// Called before launch and before reporting a deployment healthy. A dirty deployment is a
    /// partially applied layout: launching it would run a mixture of two builds.
    /// </remarks>
    public static void EnsureLaunchable(DeploymentState? state, ExecutionTargetEpoch epoch)
    {
        if (state is null)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.DeploymentDirty,
                "This application has not been deployed into Windows Sandbox yet.",
                userAction: "Run the command again to deploy it.",
                example: "winapp run . --on sandbox");
        }

        if (!state.IsForEpoch(epoch))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetStale,
                "This application was deployed into a Windows Sandbox that no longer exists.",
                userAction: "Run the command again to redeploy it.",
                example: "winapp run . --on sandbox");
        }

        if (state.Dirty)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.DeploymentDirty,
                "The previous deployment did not finish, so the guest copy is incomplete.",
                userAction: "Run the command again to redeploy it completely.",
                context: new Dictionary<string, string> { ["deploymentId"] = state.DeploymentId },
                example: "winapp run . --on sandbox");
        }
    }

    /// <summary>Streams one file from the host snapshot into the guest.</summary>
    private static async Task PutAsync(
        ITargetOperationExecutor channel,
        GuestPathScope scope,
        string sourceRoot,
        DeploymentFile file,
        CancellationToken cancellationToken)
    {
        var path = DeploymentPlanner.ResolveContainedPath(sourceRoot, file.RelativePath);

        await using var content = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);

        await channel.PutFileAsync(
            scope,
            new GuestFileInfo(file.RelativePath, file.Size, file.LastWriteUtc.UtcTicks, file.Sha256),
            content,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-reads the guest and proves it now matches the desired state exactly.
    /// </summary>
    /// <remarks>
    /// Each individual transfer is already hash-verified by the guest, but that does not prove the
    /// <em>set</em> is right: a delete that silently failed, or a file written by something else
    /// between operations, is only visible by comparing the whole layout afterwards.
    /// </remarks>
    private static async Task VerifyAsync(
        ITargetOperationExecutor channel,
        GuestPathScope scope,
        DeploymentSnapshot desired,
        CancellationToken cancellationToken)
    {
        var actual = await channel.ListFilesAsync(scope, cancellationToken).ConfigureAwait(false);
        var remaining = DeploymentPlanner.CreatePlan(desired, ToDeploymentFiles(actual));

        if (remaining.IsEmpty)
        {
            return;
        }

        throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.DeploymentDirty,
            "The application's files in Windows Sandbox do not match what was deployed.",
            userAction: "Run the command again to redeploy it completely.",
            context: new Dictionary<string, string>
            {
                ["missing"] = remaining.Added.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["mismatched"] = remaining.Changed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["unexpected"] = remaining.Removed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    private static List<DeploymentFile> ToDeploymentFiles(IReadOnlyList<GuestFileInfo> files) =>
        [.. files.Select(f => new DeploymentFile(
            f.RelativePath,
            f.Size,
            new DateTimeOffset(f.LastWriteUtcTicks, TimeSpan.Zero),
            f.Sha256))];
}
