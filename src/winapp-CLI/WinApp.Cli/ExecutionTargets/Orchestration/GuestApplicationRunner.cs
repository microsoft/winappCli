// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>A deployment that is in place in the guest and ready to be launched.</summary>
/// <param name="State">Committed, clean deployment state.</param>
/// <param name="PayloadPath">Absolute guest path holding the deployed application files.</param>
/// <param name="LayoutPath">Absolute guest path the guest registers the package from.</param>
internal sealed record GuestDeployment(DeploymentState State, string PayloadPath, string LayoutPath);

/// <summary>The authoritative package registration and the managed deployment that owns it.</summary>
internal sealed record PackageOwnershipReconciliation(
    GuestPackageRegistration? Actual,
    DeploymentState? Owner,
    IReadOnlyList<DeploymentState> Claims);

/// <summary>
/// Deploys an application into an execution target and runs it there through guest winapp
/// (spec §"Deployment model", §"Package ownership").
/// </summary>
/// <remarks>
/// Target-neutral: it uses only the command channel, the reported managed root, and deployment
/// state. Nothing here knows what a Sandbox is.
/// <para>
/// The guest is asked to perform the <em>ordinary</em> <c>winapp run</c>, so registration, runtime
/// provisioning, launch, debugging, and the whole option matrix are the same code a local run uses.
/// The host's job is only to get the right files into the guest and to relay the result.
/// </para>
/// </remarks>
internal sealed class GuestApplicationRunner(TargetDeploymentService deployments)
{
    /// <summary>
    /// Content that must never be deployed, however it came to be in the source folder.
    /// </summary>
    /// <remarks>
    /// An <c>.appxrecipe</c> lists build outputs by absolute <em>host</em> path. Guest winapp would
    /// find it, prefer it over the files actually present, resolve none of those paths, and register
    /// an empty layout — a silent wrong answer rather than a failure. Materialization does not
    /// normally leave one behind; this makes it impossible for one to matter.
    /// </remarks>
    internal static bool IsExcludedFromDeployment(string relativePath) =>
        relativePath.EndsWith(".appxrecipe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reconciles <paramref name="sourceRoot"/> into the guest and returns where it landed.
    /// </summary>
    /// <param name="target">
    /// Prepared target, whose channel and epoch the deployment is fenced on, and whose mutation
    /// lease (already held by the caller) this reconciliation relies on.
    /// </param>
    /// <param name="deploymentId">Internal deployment identity.</param>
    /// <param name="sourceRoot">Host folder to deploy — a materialized layout or a build output.</param>
    /// <param name="clean">True to discard the guest copy and its registration layout first.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="target"/> was not prepared for mutation.
    /// </exception>
    public async Task<GuestDeployment> DeployAsync(
        PreparedTarget target,
        string deploymentId,
        DirectoryInfo sourceRoot,
        bool clean,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sourceRoot);

        target.RequireMutationLease();

        var payloadScope = GuestPaths.PayloadScope(deploymentId);
        var layoutScope = GuestPaths.LayoutScope(deploymentId);

        // Resolved before anything is transferred: a guest that cannot name its managed root cannot
        // be launched into, and discovering that after a multi-hundred-megabyte copy helps nobody.
        var payloadPath = GuestPaths.Resolve(target.Capabilities, payloadScope);
        var layoutPath = GuestPaths.Resolve(target.Capabilities, layoutScope);

        var snapshot = await DeploymentPlanner
            .CreateSnapshotAsync(sourceRoot, deploymentId, cancellationToken, IsExcludedFromDeployment)
            .ConfigureAwait(false);

        var existing = deployments.ReadCurrent(target.Reference, target.Epoch, deploymentId);

        // A rerun must never leave the previous launch's instance running alongside the new one,
        // and must never mutate files that instance still has open. This runs before any write,
        // delete, or the explicit --clean layout wipe below, and fails closed -- naming the app or
        // process it could not prove it stopped -- rather than risk a duplicate process or a
        // sharing violation partway through reconciliation.
        await StopPreviousInstanceAsync(target, existing, cancellationToken).ConfigureAwait(false);

        var result = await deployments.ReconcileAsync(
            target.Reference,
            target.Epoch,
            target.Operations,
            deploymentId,
            snapshot,
            sourceRoot,
            clean,
            cancellationToken,

            // Run inside reconciliation's own dirty-to-clean window rather than after it returns.
            // The registration layout is guest-derived from the payload just reconciled above, so
            // wiping it is as much a part of "clean" as the payload deletion is -- and a failure
            // here (a locked file, for instance) must leave the deployment dirty for the identical
            // reason a failed payload delete does. Committing "clean" first and only then attempting
            // this would let a partial, non-transactional directory delete leave a damaged layout
            // behind a state that calls itself clean, which is exactly what let a previous
            // interrupted `--clean` masquerade as healthy.
            clean ? ct => target.Operations.DeleteScopeAsync(layoutScope, ct) : null).ConfigureAwait(false);

        TargetDeploymentService.EnsureLaunchable(result.State, target.Epoch);

        return new GuestDeployment(result.State, payloadPath, layoutPath);
    }

    /// <summary>
    /// Stops the previous run's tracked instance for this deployment, if any, before its layout is
    /// mutated.
    /// </summary>
    /// <remarks>
    /// Package identity is preferred whenever a package was registered: it is resolved to whatever
    /// full name the guest currently has registered and terminates exactly that package's
    /// processes, which needs no process ID at all and so has nothing that can go stale or be
    /// reused by an unrelated process. The process-ID path exists for the unpackaged direct-launch
    /// case, where PID plus start time is the only identity available, and is verified the same way
    /// on the guest side before anything is touched.
    /// <para>
    /// This deployment's own recorded <see cref="PackageOwnership.RegisteredLocation"/> is sent
    /// alongside the family name, because the family name alone does not prove this deployment owns
    /// what is currently registered under it: two deployments built from different source paths can
    /// share the same package identity, and only one of them can be genuinely registered at a time.
    /// The guest verifies the currently registered package's own install location against this
    /// value before terminating anything, refusing rather than stopping a different deployment's
    /// legitimately running application.
    /// </para>
    /// </remarks>
    private static async Task StopPreviousInstanceAsync(
        PreparedTarget target,
        DeploymentState? existing,
        CancellationToken cancellationToken)
    {
        if (existing is null)
        {
            // Nothing recorded for this deployment in the current generation, so there is nothing
            // that could still be running from a previous run of it.
            return;
        }

        if (existing.Package is { PackageFamilyName: { } familyName, RegisteredLocation: { } registeredLocation })
        {
            await target.Operations
                .StopPackageProcessesAsync(familyName, registeredLocation, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (existing.TrackedOperationProcessId is { } processId &&
            existing.TrackedOperationProcessStartTicksUtc is { } startTicksUtc)
        {
            await target.Operations.StopTrackedProcessAsync(processId, startTicksUtc, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one guest winapp command for a deployment and relays its streams and exit code.
    /// </summary>
    /// <remarks>
    /// The started process is committed to deployment state before the command completes, so a host
    /// that dies mid-run still leaves a record of what it launched rather than a deployment that
    /// claims nothing is running.
    /// <para>
    /// That commit advances the stored revision, which is why the caller is handed the record back.
    /// A caller that kept its own pre-launch <paramref name="state"/> and committed against it later
    /// would be one revision behind, and every such commit is refused — silently, because a lost
    /// commit must never fail a running application. Clearing package ownership after a successful
    /// unregister is exactly that kind of later commit.
    /// </para>
    /// </remarks>
    /// <returns>The command's exit code and the deployment record as it now stands.</returns>
    public async Task<GuestRunOutcome> RunAsync(
        PreparedTarget target,
        DeploymentState state,
        GuestExecRequest request,
        GuestExecCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(state);

        var started = false;
        var current = state;

        var result = await target.Operations.ExecuteAsync(
            request,
            callbacks with
            {
                OnStarted = process =>
                {
                    if (started)
                    {
                        return;
                    }

                    started = true;
                    current = TryCommitProcess(target, current, process);
                    callbacks.OnStarted?.Invoke(process);
                },
            },
            cancellationToken).ConfigureAwait(false);

        return new GuestRunOutcome(result.ExitCode, current);
    }

    /// <summary>Records which guest package this deployment owns.</summary>
    public DeploymentState CommitPackage(ExecutionTargetRef target, DeploymentState state, PackageOwnership package) =>
        deployments.CommitPackage(target, state, package);

    /// <summary>
    /// Reconciles host ownership claims with the package Windows actually has registered.
    /// </summary>
    /// <remarks>
    /// Windows has one current registration for a package family. Multiple current-generation host
    /// records for that identity are therefore claims about that one registration, not evidence that
    /// several registrations coexist. A confirmed absence clears all stale claims. A present package
    /// is returned only when it is a development registration rooted at a location one or more
    /// current-generation records prove winapp managed; otherwise the operation fails closed without
    /// changing either the package or the records.
    /// </remarks>
    public async Task<PackageOwnershipReconciliation> ReconcilePackageForUnregisterAsync(
        PreparedTarget target,
        string packageName,
        string publisher,
        string packageFamilyName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.RequireMutationLease();

        var claims = FindPackageClaims(target.Reference, target.Epoch, packageName, publisher, packageFamilyName);
        var actual = await target.Operations.GetRegisteredPackageAsync(
            packageName,
            publisher,
            packageFamilyName,
            cancellationToken).ConfigureAwait(false);

        if (actual is null)
        {
            ClearPackageClaims(target.Reference, claims);
            return new PackageOwnershipReconciliation(null, null, claims);
        }

        var matching = FindMatchingClaims(target, claims, actual);
        if (!actual.IsDevelopmentMode || matching.Count == 0)
        {
            throw UnownedRegistration(packageName, packageFamilyName, actual);
        }

        return new PackageOwnershipReconciliation(
            actual,
            matching.OrderBy(state => state.DeploymentId, StringComparer.Ordinal).First(),
            claims);
    }

    /// <summary>Repairs a stale pre-registration journal before redeploying its files.</summary>
    public async Task ReconcilePackageBeforeRegistrationAsync(
        PreparedTarget target,
        string packageName,
        string publisher,
        string packageFamilyName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.RequireMutationLease();

        var reconciliation = await ReconcilePackageForUnregisterAsync(
            target,
            packageName,
            publisher,
            packageFamilyName,
            cancellationToken).ConfigureAwait(false);

        if (reconciliation is { Actual: { } actual, Owner: { } owner })
        {
            TransferPackageOwnership(target.Reference, owner, reconciliation.Claims, actual);
        }
    }

    /// <summary>
    /// Unregisters one proven winapp-owned package and clears its claims only after Windows confirms
    /// that no registration remains.
    /// </summary>
    public async Task<GuestPackageRegistration?> UnregisterOwnedPackageAsync(
        PreparedTarget target,
        string packageName,
        string publisher,
        string packageFamilyName,
        string? requiredDeploymentId,
        long? requiredRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.RequireMutationLease();

        var reconciliation = await ReconcilePackageForUnregisterAsync(
            target,
            packageName,
            publisher,
            packageFamilyName,
            cancellationToken).ConfigureAwait(false);

        if (reconciliation is not { Actual: { } actual, Owner: { } owner })
        {
            return null;
        }

        if (requiredDeploymentId is not null &&
            !string.Equals(owner.DeploymentId, requiredDeploymentId, StringComparison.Ordinal))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.PackageConflict,
                $"Package ownership moved from deployment '{requiredDeploymentId}' to '{owner.DeploymentId}'.",
                userAction: "Retry the command.");
        }

        if (requiredRevision is not null && owner.Revision != requiredRevision)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.PackageConflict,
                "A newer run replaced this deployment's package registration.",
                userAction: "Leave the newer registration in place, or unregister it explicitly.");
        }

        await target.Operations
            .UnregisterPackageAsync(
                packageFamilyName,
                actual.FullName,
                actual.RegisteredLocation!,
                cancellationToken)
            .ConfigureAwait(false);

        var remaining = await target.Operations.GetRegisteredPackageAsync(
            packageName,
            publisher,
            packageFamilyName,
            cancellationToken).ConfigureAwait(false);
        if (remaining is not null)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.PackageConflict,
                $"Windows still reports package '{remaining.FullName}' as registered after the removal attempt.",
                userAction: "Close the application in Windows Sandbox, then retry.",
                context: new Dictionary<string, string>
                {
                    ["packageFamilyName"] = packageFamilyName,
                    ["packageFullName"] = remaining.FullName,
                    ["registeredLocation"] = remaining.RegisteredLocation ?? "(unknown)",
                });
        }

        ClearPackageClaims(target.Reference, reconciliation.Claims);
        return actual;
    }

    /// <summary>
    /// Resolves the optimistic registration journal entry after the guest registration attempt.
    /// </summary>
    /// <remarks>
    /// The attempted deployment is journaled before the guest mutation so a host crash can never
    /// leave a winapp-created package with no ownership evidence. After the call, the guest OS is
    /// authoritative: success transfers the one live registration to the matching deployment and
    /// clears every stale same-identity claim; failure clears a disproven attempted claim or restores
    /// ownership to the deployment whose registration actually survived. A contradictory success
    /// response followed by confirmed absence preserves the optimistic journal rather than discarding
    /// the only recovery evidence. Commits are intentionally ordered owner-first, stale-second. A
    /// crash can therefore leave duplicate evidence, which this same deterministic reconciliation
    /// repairs, but never leave a live winapp registration with no evidence.
    /// </remarks>
    public async Task<DeploymentState?> ReconcileRegistrationAttemptAsync(
        PreparedTarget target,
        string attemptedDeploymentId,
        string packageName,
        string publisher,
        string packageFamilyName,
        bool registrationSucceeded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.RequireMutationLease();

        var claims = FindPackageClaims(target.Reference, target.Epoch, packageName, publisher, packageFamilyName);
        var actual = await target.Operations.GetRegisteredPackageAsync(
            packageName,
            publisher,
            packageFamilyName,
            cancellationToken).ConfigureAwait(false);

        if (actual is null)
        {
            if (registrationSucceeded)
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.PackageConflict,
                    $"The guest reported that '{packageName}' registered successfully, but Windows reports no current registration.",
                    userAction: "Retry the command.");
            }

            ClearPackageClaims(target.Reference, claims);
            return null;
        }

        var matching = actual.IsDevelopmentMode ? FindMatchingClaims(target, claims, actual) : [];
        var attempted = matching.FirstOrDefault(state =>
            string.Equals(state.DeploymentId, attemptedDeploymentId, StringComparison.Ordinal));

        if (registrationSucceeded)
        {
            if (attempted is null)
            {
                throw UnownedRegistration(packageName, packageFamilyName, actual);
            }

            return TransferPackageOwnership(target.Reference, attempted, claims, actual);
        }

        if (matching.Count > 0)
        {
            var survivingOwner = matching
                .OrderBy(state => state.DeploymentId, StringComparer.Ordinal)
                .First();
            return TransferPackageOwnership(target.Reference, survivingOwner, claims, actual);
        }

        // The failed attempt demonstrably did not create the registration Windows reports. Remove
        // only that false claim; unrelated historical evidence remains available for diagnosis.
        var falseAttempt = claims.FirstOrDefault(state =>
            string.Equals(state.DeploymentId, attemptedDeploymentId, StringComparison.Ordinal));
        if (falseAttempt is not null)
        {
            deployments.CommitPackage(target.Reference, falseAttempt, package: null);
        }

        return null;
    }

    /// <summary>Forgets the package a deployment owned, after it has been unregistered.</summary>
    public DeploymentState ClearPackage(ExecutionTargetRef target, DeploymentState state) =>
        ClearRegistrationClaim(target, state);

    /// <summary>Clears every current-generation claim for one identity after confirmed removal.</summary>
    public void ClearPackageClaims(ExecutionTargetRef target, IReadOnlyList<DeploymentState> claims)
    {
        foreach (var claim in claims)
        {
            ClearRegistrationClaim(target, claim);
        }
    }

    private DeploymentState ClearRegistrationClaim(ExecutionTargetRef target, DeploymentState state) =>
        deployments.ClearRegistration(target, state);

    private List<DeploymentState> FindPackageClaims(
        ExecutionTargetRef target,
        ExecutionTargetEpoch epoch,
        string packageName,
        string publisher,
        string packageFamilyName) =>
        [
            .. deployments.List(target)
                .Where(state => state.IsForEpoch(epoch)
                    && state.Package is { } package
                    && string.Equals(package.PackageName, packageName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(package.Publisher, publisher, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        package.PackageFamilyName,
                        packageFamilyName,
                        StringComparison.OrdinalIgnoreCase)),
        ];

    private static List<DeploymentState> FindMatchingClaims(
        PreparedTarget target,
        IReadOnlyList<DeploymentState> claims,
        GuestPackageRegistration actual)
    {
        if (string.IsNullOrWhiteSpace(actual.RegisteredLocation))
        {
            return [];
        }

        return
        [
            .. claims.Where(state =>
                state.Package!.Owns(actual.FullName, actual.RegisteredLocation) &&
                TargetPathSafety.PathsEqual(
                    state.Package.RegisteredLocation,
                    GuestPaths.Resolve(target.Capabilities, GuestPaths.LayoutScope(state.DeploymentId)))),
        ];
    }

    private DeploymentState TransferPackageOwnership(
        ExecutionTargetRef target,
        DeploymentState owner,
        IReadOnlyList<DeploymentState> claims,
        GuestPackageRegistration actual)
    {
        var committedOwner = deployments.CommitPackage(
            target,
            owner,
            owner.Package! with
            {
                PackageFullName = actual.FullName,
                RegisteredLocation = actual.RegisteredLocation!,
            });

        foreach (var stale in claims)
        {
            if (!string.Equals(stale.DeploymentId, owner.DeploymentId, StringComparison.Ordinal))
            {
                deployments.CommitPackage(target, stale, package: null);
            }
        }

        return committedOwner;
    }

    private static ExecutionTargetException UnownedRegistration(
        string packageName,
        string packageFamilyName,
        GuestPackageRegistration actual) =>
        ExecutionTargetException.Create(
            actual.IsDevelopmentMode
                ? ExecutionTargetErrorCodes.PackageConflict
                : ExecutionTargetErrorCodes.ProvisionedPackageConflict,
            actual.IsDevelopmentMode
                ? $"The package currently registered as '{packageName}' is not rooted in a deployment winapp owns."
                : $"The package currently registered as '{packageName}' is not a development package.",
            userAction: "Remove or unregister that package in Windows Sandbox, then retry.",
            context: new Dictionary<string, string>
            {
                ["packageFamilyName"] = packageFamilyName,
                ["packageFullName"] = actual.FullName,
                ["registeredLocation"] = actual.RegisteredLocation ?? "(unknown)",
                ["isDevelopmentMode"] = actual.IsDevelopmentMode.ToString(),
            });

    /// <summary>
    /// Commits the launched process without letting a state race fail a running application.
    /// </summary>
    /// <remarks>
    /// The process is already running by this point. A concurrent commit from another host process
    /// means the record is stale, which is a diagnostics loss, not a reason to report a successful
    /// launch as a failure — so the caller gets its previous record back and carries on.
    /// </remarks>
    /// <returns>The committed record, or the one passed in when the commit was refused.</returns>
    private DeploymentState TryCommitProcess(
        PreparedTarget target,
        DeploymentState state,
        GuestProcessStart process)
    {
        try
        {
            return deployments.CommitProcess(
                target.Reference,
                state,
                process.ProcessId,
                process.StartTicksUtc);
        }
        catch (ExecutionTargetException ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not record the launched guest process for deployment {0}: {1}",
                state.DeploymentId,
                ex.Message);

            return state;
        }
    }
}

/// <summary>What a guest run produced: the application's exit code, and the record it left behind.</summary>
/// <param name="ExitCode">The guest application's own exit code.</param>
/// <param name="State">
/// The deployment record as it now stands. Committing anything later — clearing package ownership
/// after an unregister, for example — must use this rather than the record the caller started with,
/// because launching the process advanced the stored revision.
/// </param>
internal sealed record GuestRunOutcome(int ExitCode, DeploymentState State);
