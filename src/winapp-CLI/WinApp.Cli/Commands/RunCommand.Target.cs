// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class RunCommand
{
    public partial class Handler
    {
        /// <summary>
        /// Runs a packaged app in the execution target: materialize on the host, reconcile into the
        /// guest, then let guest winapp register, launch, and honour the option matrix.
        /// </summary>
        /// <remarks>
        /// Nothing about this machine changes. Materialization stops before runtime provisioning and
        /// registration, and the guest performs both — so a <c>--on sandbox</c> run leaves no package
        /// registered here and installs no runtime here, which is the entire point of the flag.
        /// <para>
        /// The guest is asked to perform the ordinary <c>winapp run</c>. Every option in the matrix
        /// is therefore the same implementation users already rely on locally rather than a second
        /// one that can drift.
        /// </para>
        /// </remarks>
        private async Task<int> ExecutePackagedTargetRunAsync(
            DirectoryInfo inputFolder,
            FileInfo? manifest,
            LayoutOutput layoutOutput,
            string? appArgs,
            bool noLaunch,
            bool withAlias,
            bool debugOutput,
            bool unregisterOnExit,
            bool detach,
            bool clean,
            string? executable,
            bool isJson,
            FileInfo? projectFile,
            string? framework,
            bool noRestore,
            CancellationToken cancellationToken)
        {
            FileInfo resolvedManifest;
            DirectoryInfo layout;
            MsixIdentityResult? identity = null;

            // Held from materialization until the guest deployment has finished consuming the layout.
            // Releasing it after materialization would leave a window in which a second run could
            // rewrite the directory, and this run would then deploy the other run's files.
            //
            // It is released at the same points as the guest mutation lease, and for the same reason:
            // once the layout has been copied into the guest and registered there, nothing this run
            // does afterward reads the host directory again, and a long-running app must never keep
            // another winapp workflow waiting on it.
            LayoutLease? layoutLease = null;

            try
            {
                try
                {
                    resolvedManifest = ResolveManifestForSandbox(inputFolder, manifest);

                    // Ownership travels with the path from the call site (see LayoutOutput). The
                    // guest registration layout arrives here as WinappManaged: the host created it
                    // beside the deployed payload, so it is winapp's to keep matching the build,
                    // exactly like the generated AppX directory.
                    layout = layoutOutput.Resolve(() => new DirectoryInfo(
                        TargetPathSafety.CombineInsideRoot(inputFolder.FullName, "AppX")));

                    LongPathHelper.ValidatePathLength(resolvedManifest.FullName);
                    LongPathHelper.ValidatePathLength(layout.FullName);

                    layoutLease = LayoutLease.Acquire(
                        winappDirectoryService.GetGlobalWinappDirectory(),
                        layout,
                        cancellationToken);

                    var materializeError = (string?)null;
                    var materialized = await statusService.ExecuteWithStatusAsync(
                        "Preparing application layout...",
                        async (taskContext, ct) =>
                        {
                            try
                            {
                                identity = await msixService.MaterializeLooseLayoutAsync(
                                    resolvedManifest, inputFolder, layout, taskContext, layoutOutput.Reconciliation,
                                    executable, projectFile, framework, noRestore, ct);
                                return (0, $"{identity.PackageName} ready to deploy");
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                materializeError = ex.Message;
                                return (1, $"{UiSymbols.Error} Failed to prepare the application: {ex.Message}");
                            }
                        },
                        cancellationToken);

                    if (materialized != 0 || identity is null)
                    {
                        return Fail(materializeError ?? "Failed to prepare the application.", isJson);
                    }
                }
                catch (OperationCanceledException)
                {
                    return -1;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FileNotFoundException or TimeoutException)
                {
                    return Fail(ex.Message, isJson);
                }

            var options = new GuestRunOptions(
                noLaunch, withAlias, debugOutput, unregisterOnExit, detach, clean, isJson, appArgs);

            // When this run also launches, RunInGuestAsync pulls registration out into its own
            // locked call (see RegisterPackageAsync) so the mutation lease never has to keep
            // covering the launch/wait that follows. The general guest `winapp run` is never used
            // for that second, unlocked call: after releasing the lease, a different deployment
            // sharing this package identity could register in the gap, and the general `run` would
            // then see a mismatched install location and silently fall through to an unlocked
            // unregister+register of its own -- reintroducing exactly the mutation this split exists
            // to prevent, and disturbing the other deployment's registration in the process. The
            // hidden guest-launch verb is structurally incapable of that: it has no code path that
            // registers or unregisters anything, so a mismatch is refused outright instead of
            // "repaired". See GuestLaunchPlanner/GuestLaunchCommand.
            //
            // --unregister-on-exit is likewise never forwarded to guest-launch (it has no such
            // option at all): it is instead honored as a third, separate, host-orchestrated phase
            // after the guest-launch call returns -- see UnregisterDeploymentAfterExitAsync.
            //
            // When noLaunch is requested there is no launch phase to split off, so registration
            // (still under the same locked call inside RunInGuestAsync) is the whole operation.
            return await RunInGuestAsync(
                layout,
                DeploymentIdFor(inputFolder, identity),
                clean,
                isJson,
                requiresRealInput: !noLaunch,
                identity,
                noLaunch,
                unregisterOnExit,
                (deployment, ownerEnvironment) => new GuestExecRequest
                {
                    UseGuestWinapp = true,
                    Arguments = GuestLaunchPlanner.BuildLaunchArguments(
                        identity.PackageName,
                        identity.Publisher,
                        identity.ApplicationId,
                        deployment.LayoutPath,
                        deployment.PayloadPath,
                        executionTargetOrchestrator.Target.Selector,
                        options),

                    // The payload folder, so a guest app that resolves files relative to its working
                    // directory sees its own deployment rather than the agent's location.
                    WorkingDirectory = deployment.PayloadPath,
                    Environment = ownerEnvironment,
                    RequiresRealInput = !noLaunch,
                },
                cancellationToken,
                // --with-alias is documented as running the app "in the current terminal with
                // inherited stdin/stdout/stderr". Output already came back; without this, stdin did
                // not, so a console app launched this way could never be driven.
                forwardStandardInput: withAlias,
                layoutLease: layoutLease);
            }
            finally
            {
                layoutLease?.Dispose();
            }
        }

        /// <summary>
        /// Runs an unpackaged app in the execution target by starting its apphost there directly.
        /// </summary>
        /// <remarks>
        /// There is no package to register, so this deploys the build output and starts the
        /// executable — the guest analogue of what an unpackaged local run does. The host's working
        /// directory is deliberately not reproduced: it names a location that does not exist in the
        /// guest, so the deployment folder is used and documented instead of silently substituting a
        /// path the app was never pointed at.
        /// </remarks>
        private async Task<int> ExecuteUnpackagedSandboxRunAsync(
            ProjectRunResolution resolution,
            FileInfo csproj,
            string? appArgs,
            bool debugOutput,
            bool detach,
            bool isJson,
            CancellationToken cancellationToken)
        {
            var targetDir = new DirectoryInfo(resolution.TargetDir);

            string executableRelativePath;

            try
            {
                GuestRunPlanner.EnsureSupportedForUnpackaged(new GuestRunOptions(DebugOutput: debugOutput));
                executableRelativePath = ResolveGuestRelativeExecutable(targetDir, resolution.RunCommand!, csproj);
            }
            catch (ExecutionTargetException ex)
            {
                return TargetOutput.Fail(ansiConsole, isJson, ex.Error);
            }

            // A non-apphost RunCommand (for example dotnet) carries leading arguments that must
            // precede the user's, exactly as they do locally.
            var launchArguments = WindowsCommandLine.SplitArguments(
                CombineLaunchArguments(resolution.RunArguments, appArgs) ?? string.Empty);

            return await RunInGuestAsync(
                targetDir,
                DeploymentPlanner.CreateDeploymentId(Path.GetFullPath(targetDir.FullName), originalPackageIdentity: null),
                clean: false,
                isJson,
                requiresRealInput: true,
                identity: null,

                // No package, so no registration phase applies regardless of these two values.
                noLaunch: false,
                unregisterOnExit: false,
                (deployment, ownerEnvironment) => new GuestExecRequest
                {
                    Executable = TargetPathSafety.CombineInsideRoot(deployment.PayloadPath, executableRelativePath),
                    Arguments = [.. launchArguments],
                    WorkingDirectory = deployment.PayloadPath,
                    Environment = ownerEnvironment,
                    RequiresRealInput = true,
                    Detach = detach,
                },
                cancellationToken,
                guestProducesRunResult: false);
        }

        /// <summary>
        /// The shared half of every <c>run --on sandbox</c>: prepare, deploy, run, relay.
        /// </summary>
        /// <param name="sourceRoot">Host folder to reconcile into the guest.</param>
        /// <param name="deploymentId">Internal deployment identity.</param>
        /// <param name="clean">Whether to discard the guest copy first.</param>
        /// <param name="isJson">Whether the invoking command is in machine-readable mode.</param>
        /// <param name="requiresRealInput">Whether the guest command needs a usable input desktop.</param>
        /// <param name="identity">Package identity to record ownership for, when there is one.</param>
        /// <param name="noLaunch">
        /// True when the caller asked only to deploy and register, never to launch. Irrelevant when
        /// <paramref name="identity"/> is null (an unpackaged run has no registration phase at all).
        /// For a packaged run, registration (see <see cref="RegisterPackageAsync"/>) always happens
        /// first, under the mutation lease, regardless of this value -- it is the only guest package
        /// mutation any packaged run performs, so it can never be skipped or deferred to an unlocked
        /// call. This flag only decides what happens *after* registration succeeds: true means
        /// registration was the whole operation and its own result is published as final; false
        /// means <paramref name="buildRequest"/> builds a further, unlocked launch-only call.
        /// </param>
        /// <param name="unregisterOnExit">
        /// True when the caller asked the deployment unregistered once its application exits.
        /// Applies only when <paramref name="identity"/> is not null and <paramref name="noLaunch"/>
        /// is false (the combination with <paramref name="noLaunch"/> is already rejected before
        /// this method is reached). Honored as a third, separate, host-orchestrated phase after
        /// <paramref name="buildRequest"/>'s call returns -- see
        /// <see cref="UnregisterDeploymentAfterExitAsync"/> -- never inside the unlocked launch call
        /// itself, which has no unregister capability at all.
        /// </param>
        /// <param name="buildRequest">Builds the guest request once the guest paths are known.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <param name="guestProducesRunResult">
        /// True when the request runs guest winapp, whose stdout is a <see cref="RunCommandResult"/>.
        /// False for a direct unpackaged executable, whose arbitrary stdout is suppressed under JSON
        /// and replaced with a host-built result envelope.
        /// </param>
        /// <param name="forwardStandardInput">
        /// Whether this process's standard input is streamed to the guest process. Set for
        /// <c>--with-alias</c>, which promises an inherited-stdio console run.
        /// </param>
        private async Task<int> RunInGuestAsync(
            DirectoryInfo sourceRoot,
            string deploymentId,
            bool clean,
            bool isJson,
            bool requiresRealInput,
            MsixIdentityResult? identity,
            bool noLaunch,
            bool unregisterOnExit,
            Func<GuestDeployment, Dictionary<string, string>, GuestExecRequest> buildRequest,
            CancellationToken cancellationToken,
            bool guestProducesRunResult = true,
            bool forwardStandardInput = false,
            LayoutLease? layoutLease = null)
        {
            try
            {
                await using var target = await executionTargetOrchestrator.PrepareAsync(
                    PrepareTargetOptions.Mutating with { RequireInteractiveDesktop = requiresRealInput },
                    cancellationToken);

                if (identity is not null)
                {
                    var familyName = appLauncherService.ComputePackageFamilyName(
                        identity.PackageName,
                        identity.Publisher);
                    await guestApplicationRunner.ReconcilePackageBeforeRegistrationAsync(
                        target,
                        identity.PackageName,
                        identity.Publisher,
                        familyName,
                        cancellationToken);
                }

                var provisioning = await ProvisionRuntimesAsync(target, sourceRoot, cancellationToken);

                WriteProgress(isJson, "Deploying the application into the Windows Sandbox...");

                var deployment = await guestApplicationRunner.DeployAsync(
                    target, deploymentId, sourceRoot, clean, cancellationToken);

                var state = deployment.State;

                if (identity is not null)
                {
                    var familyName = appLauncherService.ComputePackageFamilyName(identity.PackageName, identity.Publisher);

                    state = guestApplicationRunner.CommitPackage(target.Reference, state, new PackageOwnership
                    {
                        PackageName = identity.PackageName,
                        Publisher = identity.Publisher,
                        PackageFamilyName = familyName,
                        RegisteredLocation = deployment.LayoutPath,
                        Aumid = $"{familyName}!{identity.ApplicationId}",
                    });

                    // Registration is the only guest package mutation any packaged sandbox run ever
                    // performs, and it always happens here, under the mutation lease, whether or not
                    // the caller also asked to launch. A --no-launch run has no further guest call to
                    // make at all -- registration IS the whole operation -- so it must never be sent
                    // unlocked "because nothing else needs the lock afterward": the registration
                    // mutation itself is exactly what the lock exists to protect.
                    WriteProgress(isJson, "Registering the application in the Windows Sandbox...");

                    var registration = await RegisterPackageAsync(target, deployment, clean, isJson, cancellationToken);

                    try
                    {
                        var reconciled = await guestApplicationRunner.ReconcileRegistrationAttemptAsync(
                            target,
                            deployment.State.DeploymentId,
                            identity.PackageName,
                            identity.Publisher,
                            familyName,
                            registration.ExitCode == 0,
                            cancellationToken);

                        if (reconciled is not null &&
                            string.Equals(
                                reconciled.DeploymentId,
                                deployment.State.DeploymentId,
                                StringComparison.Ordinal))
                        {
                            state = reconciled;
                        }

                        if (registration.ExitCode != 0)
                        {
                            // Registration itself failed, so there is no launch phase to follow --
                            // whether or not one was requested -- and this call's own result is the
                            // only one the caller gets.
                            if (isJson && registration.CapturedOutput is not null)
                            {
                                PublishGuestJson(registration.CapturedOutput, target);
                            }

                            return registration.ExitCode;
                        }

                        if (noLaunch)
                        {
                            // Registration succeeded and there is no launch phase to follow: release
                            // the lease now and publish this call's own result as the final one,
                            // exactly as an unsplit call's success would have been.
                            target.ReleaseMutationLease();

                            // The layout has been deployed and registered -- consumed -- so this run
                            // no longer depends on the host directory holding still.
                            layoutLease?.Dispose();

                            if (isJson && registration.CapturedOutput is not null)
                            {
                                PublishGuestJson(registration.CapturedOutput, target);
                            }

                            return registration.ExitCode;
                        }
                    }
                    finally
                    {
                        registration.CapturedOutput?.Dispose();
                    }
                }

                // Every guest mutation this run needed -- runtime provisioning, deployment
                // reconciliation, and (for a packaged run) package registration -- is done. What is
                // left is starting and running the application, which the mutation lock must never
                // cover: a long-running app would otherwise block every other winapp workflow
                // against this target.
                target.ReleaseMutationLease();

                // Same boundary for the host layout: deployment copied it into the guest and
                // registration consumed it there. Holding it across the application's lifetime would
                // block every other run against this build output for as long as the app stays open.
                layoutLease?.Dispose();

                var ownerEnvironment = GuestOwnerContext.WithOwner(
                    environment: null,
                    GuestOwnerContext.ResolveGuestToken(
                        target.Reference.StateKey, target.Epoch.Value));

                // A per-user .NET installation is discoverable to an apphost through DOTNET_ROOT and
                // nothing else without machine-wide registration, so the root provisioning created
                // has to reach the launched process itself. Merged rather than assigned, so the
                // owner context this launch also depends on is not lost — and only present at all
                // when the guest reported that the managed root is what satisfies a framework.
                foreach (var (name, value) in provisioning.LaunchEnvironment)
                {
                    ownerEnvironment[name] = value;
                }

                // Under --json the guest's payload is captured rather than relayed, so the additive
                // execution-target members can be merged into the document the caller parses. It is
                // never reformatted blind: anything that does not parse is written through exactly
                // as the guest produced it, because corrupting a result is worse than omitting a
                // field.
                using var capturedOutput = isJson && guestProducesRunResult ? new MemoryStream() : null;
                var request = buildRequest(deployment, ownerEnvironment);

                // Registration and activation happen inside the guest and can take several seconds
                // for a packaged app, so the last silent stretch gets a line too.
                WriteProgress(isJson, "Starting the application in the Windows Sandbox...");

                var run = await guestApplicationRunner.RunAsync(
                    target,
                    state,
                    request,
                    new GuestExecCallbacks(
                        OnOperationId: forwardStandardInput
                            ? GuestStandardInputPump.Attach(target.Operations, cancellationToken)
                            : null,
                        OnStandardOutput: data =>
                        {
                            if (isJson && !guestProducesRunResult)
                            {
                                return;
                            }

                            if (capturedOutput is not null)
                            {
                                CaptureBounded(capturedOutput, data);
                                return;
                            }

                            WriteRawToConsole(Console.OpenStandardOutput(), data);
                        },
                        OnStandardError: data =>
                        {
                            if (!isJson || guestProducesRunResult)
                            {
                                WriteRawToConsole(Console.OpenStandardError(), data);
                            }
                        }),
                    cancellationToken);

                // The application has now fully exited (the guest-launch call above does not
                // return until it does). --unregister-on-exit is honored only now, as a third,
                // separate, host-orchestrated phase -- never inside the launch call itself, and
                // never covering any part of the application's own lifetime.
                if (identity is not null && unregisterOnExit)
                {
                    await UnregisterDeploymentAfterExitAsync(target, identity, run.State, cancellationToken);
                }

                if (isJson && guestProducesRunResult && capturedOutput is not null)
                {
                    PublishGuestJson(capturedOutput, target);
                }
                else if (isJson)
                {
                    PublishDirectGuestJson(target);
                }

                return run.ExitCode;
            }
            catch (ExecutionTargetException ex)
            {
                return TargetOutput.Fail(ansiConsole, isJson, ex.Error);
            }
        }

        /// <summary>
        /// Registers the packaged application in the guest without launching it -- the only guest
        /// package mutation any packaged sandbox run performs -- while the mutation lease from
        /// <see cref="ExecutionTargetOrchestrator.PrepareAsync"/> is still held.
        /// </summary>
        /// <param name="target">Prepared target whose mutation lease this call relies on.</param>
        /// <param name="deployment">The deployment just reconciled into the guest.</param>
        /// <param name="clean">
        /// Whether to clear the guest package's application data, forwarded from the run's own
        /// <c>--clean</c>. Applied here, in the locked phase, only.
        /// </param>
        /// <param name="isJson">Whether the invoking command is in machine-readable mode.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>
        /// The outcome. Ownership of <see cref="GuestPackagePhaseResult.CapturedOutput"/> passes to
        /// the caller, which must dispose it once done with it -- whether or not it was published.
        /// </returns>
        /// <remarks>
        /// Guest <c>winapp run --no-launch</c>: the same production code every local
        /// registration-only run already uses, never a bespoke reimplementation. This call always
        /// happens, whether or not the caller also asked to launch -- a <c>--no-launch</c> run has no
        /// further guest call to make at all, so its registration cannot be sent unlocked "because
        /// nothing else needs the lock afterward"; the registration mutation itself is exactly what
        /// the lock exists to protect, and the caller publishes this call's own result as final in
        /// that case.
        /// <para>
        /// When the caller also launches, a further call follows once registration succeeds and the
        /// lease is released -- deliberately <em>not</em> the general guest <c>run</c>, which
        /// registers and launches inseparably: if a different deployment sharing this package
        /// identity registers in the gap after this call returns, the general <c>run</c> would see
        /// the now-mismatched install location and silently fall through to an unlocked
        /// unregister+register, disturbing the other deployment's registration. That further call is
        /// instead the hidden guest-launch verb (<see cref="GuestLaunchPlanner"/>/
        /// <see cref="GuestLaunchCommand"/>), which has no code path that registers or unregisters
        /// anything: it verifies the currently registered package is installed from exactly this
        /// call's layout and refuses to launch otherwise, rather than "repairing" the mismatch. In
        /// that case this call's own success result is not published -- the launch call's is.
        /// </para>
        /// <para>
        /// Non-JSON output streams live exactly as a single, unsplit call already would. The started
        /// guest process here is the short-lived registration-only <c>winapp.exe</c>, not the
        /// application, so it is never committed as the deployment's running process.
        /// </para>
        /// </remarks>
        private static async Task<GuestPackagePhaseResult> RegisterPackageAsync(
            PreparedTarget target,
            GuestDeployment deployment,
            bool clean,
            bool isJson,
            CancellationToken cancellationToken)
        {
            target.RequireMutationLease();

            var request = new GuestExecRequest
            {
                UseGuestWinapp = true,
                Arguments = GuestRunPlanner.BuildRunArguments(
                    deployment.PayloadPath,
                    deployment.LayoutPath,
                    new GuestRunOptions(NoLaunch: true, Clean: clean, Json: isJson)),
                WorkingDirectory = deployment.PayloadPath,
            };

            var capturedOutput = isJson ? new MemoryStream() : null;

            var result = await target.Operations.ExecuteAsync(
                request,
                new GuestExecCallbacks(
                    OnStandardOutput: data =>
                    {
                        if (capturedOutput is not null)
                        {
                            CaptureBounded(capturedOutput, data);
                            return;
                        }

                        WriteRawToConsole(Console.OpenStandardOutput(), data);
                    },
                    OnStandardError: data => WriteRawToConsole(Console.OpenStandardError(), data)),
                cancellationToken).ConfigureAwait(false);

            return new GuestPackagePhaseResult(result.ExitCode, result.ProcessId, capturedOutput);
        }

        /// <summary>Outcome of the locked, register-only guest call.</summary>
        /// <param name="ExitCode">The guest's own exit code for the register-only call.</param>
        /// <param name="ProcessId">The short-lived registration-only <c>winapp.exe</c>'s process ID.</param>
        /// <param name="CapturedOutput">
        /// The guest's captured stdout, present only under <c>--json</c>. Ownership passes to the
        /// caller, which must dispose it once done -- whether it publishes it (on failure, or on a
        /// <c>--no-launch</c> success) or not (a launching success, whose own final result comes
        /// from the launch phase instead).
        /// </param>
        private readonly record struct GuestPackagePhaseResult(int ExitCode, int ProcessId, MemoryStream? CapturedOutput);

        /// <summary>
        /// Honors <c>--unregister-on-exit</c> for a packaged sandbox run as a third, separate,
        /// host-orchestrated phase -- run only after the application has fully exited.
        /// </summary>
        /// <param name="target">
        /// This run's own prepared target. Its channel is still live -- <see
        /// cref="ExecutionTargetOrchestrator.PrepareAsync"/> released only the connection lock, not
        /// the connection -- even though this run's own mutation lease was already released before
        /// the launch phase. This phase reacquires only a fresh mutation lease, over that same live
        /// connection.
        /// </param>
        /// <param name="identity">The package identity whose current ownership must be re-proven.</param>
        /// <param name="state">This deployment's current host-side record.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <remarks>
        /// The hidden guest-launch verb (the unlocked launch phase) has no unregister capability at
        /// all, by design (see <see cref="GuestLaunchCommand"/>/<see cref="GuestLaunchPlanner"/>): if
        /// a different deployment sharing this package identity registered from a different layout
        /// while this run's application was still executing, unregistering by name alone -- the
        /// guest's original, pre-existing <c>UnregisterDevPackageAsync</c> behavior that the local
        /// (non-sandbox) run still uses -- would remove that OTHER deployment's registration instead.
        /// This phase closes that gap by querying Windows again under the fresh lease, requiring that
        /// the actual registration still belongs to this deployment, removing only its exact package
        /// full name, and clearing ownership only after a final query proves absence.
        /// <para>
        /// A fresh mutation lease is acquired here rather than reusing the one this run already
        /// released: by the time the application exits, an unbounded amount of time may have passed,
        /// and the lease must never be held across that window. Acquiring, using, and releasing it
        /// only now -- strictly after the wait -- is what keeps this phase from reintroducing the
        /// hazard the registration/launch split exists to avoid.
        /// </para>
        /// <para>
        /// Deliberately <see cref="ExecutionTargetOrchestrator.AcquireMutationLease"/> rather than a
        /// second <see cref="ExecutionTargetOrchestrator.PrepareAsync"/> call: <paramref name="target"/>
        /// itself is still alive at this point (its own <c>await using</c> scope has not exited yet),
        /// so its channel is already established and this phase needs nothing from the connection
        /// lock at all. Re-preparing would re-establish a connection that already exists, and would
        /// do so under a new epoch rather than the one this deployment's own state is fenced on.
        /// Reusing <paramref name="target"/>'s live channel and acquiring only a fresh mutation lease
        /// keeps both the channel and the epoch identical.
        /// </para>
        /// <para>
        /// Best-effort and silent on the primary output: its outcome is never published to stdout and
        /// never affects this run's own exit code, matching the pre-existing local
        /// <c>UnregisterDevPackageAsync</c>'s behavior exactly. The application already ran to
        /// completion, and a failed best-effort cleanup afterward is not a reason to report the run
        /// itself as failed.
        /// </para>
        /// </remarks>
        private async Task UnregisterDeploymentAfterExitAsync(
            PreparedTarget target,
            MsixIdentityResult identity,
            DeploymentState state,
            CancellationToken cancellationToken)
        {
            try
            {
                using var mutationLease = executionTargetOrchestrator.AcquireMutationLease(cancellationToken);
                var cleanupTarget = target with { MutationLease = mutationLease };

                var familyName = appLauncherService.ComputePackageFamilyName(
                    identity.PackageName,
                    identity.Publisher);
                await guestApplicationRunner.UnregisterOwnedPackageAsync(
                    cleanupTarget,
                    identity.PackageName,
                    identity.Publisher,
                    familyName,
                    state.DeploymentId,
                    state.Revision,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Best-effort, matching the pre-existing local UnregisterDevPackageAsync: the
                // application already ran to completion, so a failed cleanup here -- including
                // cancellation -- is not a reason to report the run itself as failed.
                logger.LogDebug("Could not unregister the sandbox deployment on exit: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// The deployment identity for a resolved input, derived from its canonical path and
        /// original package identity so two projects sharing an identity stay distinct.
        /// </summary>
        private static string DeploymentIdFor(DirectoryInfo inputFolder, MsixIdentityResult identity) =>
            DeploymentPlanner.CreateDeploymentId(
                Path.GetFullPath(inputFolder.FullName),
                $"{identity.PackageName}_{identity.Publisher}");

        /// <summary>
        /// Installs and verifies the shared runtimes the deployment needs, before it is deployed.
        /// </summary>
        /// <remarks>
        /// Ahead of deployment rather than after it, matching the spec's order: a guest missing the
        /// Windows App Runtime cannot register the package that is about to be copied there, and
        /// discovering that after transferring hundreds of megabytes helps nobody.
        /// <para>
        /// The structured failure is re-thrown rather than returned, because the status wrapper
        /// swallows exceptions from its task body and only the caller's envelope knows how to render
        /// an execution-target error under <c>--json</c>.
        /// </para>
        /// </remarks>
        private async Task<RuntimeProvisionResult> ProvisionRuntimesAsync(
            PreparedTarget target,
            DirectoryInfo sourceRoot,
            CancellationToken cancellationToken)
        {
            ExecutionTargetException? failure = null;
            RuntimeProvisionResult? provisioned = null;

            await statusService.ExecuteWithStatusAsync(
                "Checking runtimes in the Windows Sandbox...",
                async (taskContext, ct) =>
                {
                    try
                    {
                        var result = await targetRuntimeService.EnsureAsync(
                            target,
                            target.Reference,
                            sourceRoot,
                            new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory()),
                            taskContext,
                            ct);

                        provisioned = result;

                        return (0, DescribeProvisioning(result));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Every failure is captured, not just the structured ones. An unexpected
                        // exception swallowed here would let the run continue to deploy and launch
                        // as though the runtime graph had been verified, which is the one outcome
                        // this step exists to make impossible.
                        failure = ex as ExecutionTargetException ?? ExecutionTargetException.Create(
                            ExecutionTargetErrorCodes.RuntimeProvisionFailed,
                            $"The shared runtimes the app needs could not be provisioned in Windows Sandbox: {ex.Message}",
                            userAction: "Retry the command. If it keeps failing, close Windows Sandbox so a fresh guest is created.",
                            innerException: ex);

                        return (1, $"{UiSymbols.Error} {failure.Error.Message}");
                    }
                },
                cancellationToken);

            // Cancellation is swallowed by the status wrapper, so it is re-observed here rather than
            // left to look like a successful provisioning pass.
            cancellationToken.ThrowIfCancellationRequested();

            if (failure is not null)
            {
                throw failure;
            }

            // A null result with no captured failure would mean the status body neither completed
            // nor reported why. Treating that as "nothing to provision" would launch on an
            // unverified graph, so it fails instead.
            return provisioned ?? throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.RuntimeProvisionFailed,
                "winapp could not verify the shared runtimes the app needs inside Windows Sandbox.",
                userAction: "Retry the command. If it keeps failing, close Windows Sandbox so a fresh guest is created.");
        }

        /// <summary>Summarises one provisioning pass for the progress line.</summary>
        private static string DescribeProvisioning(RuntimeProvisionResult result)
        {
            if (result.Requirements.IsEmpty)
            {
                return "No shared runtimes required";
            }

            var installed = result.Report?.Items.Count(item => item.Installed) ?? 0;

            return installed == 0
                ? "Shared runtimes verified"
                : $"Installed {installed} shared runtime component(s)";
        }

        /// <summary>Resolves the manifest for a sandbox run using the same precedence as a local one.</summary>
        private FileInfo ResolveManifestForSandbox(DirectoryInfo inputFolder, FileInfo? manifest)
        {
            if (manifest is not null)
            {
                if (!manifest.Exists)
                {
                    throw new FileNotFoundException($"Manifest file not found: {manifest.FullName}");
                }

                return manifest;
            }

            var folderManifest = FindManifest(inputFolder.FullName);
            if (folderManifest.Exists)
            {
                return folderManifest;
            }

            var cwdManifest = FindManifest(currentDirectoryProvider.GetCurrentDirectory());
            if (cwdManifest.Exists)
            {
                return cwdManifest;
            }

            throw new FileNotFoundException(
                $"Manifest file not found. Searched in: input folder ({inputFolder.FullName}), current directory ({currentDirectoryProvider.GetCurrentDirectory()}). Use --manifest to specify the path.");
        }

        /// <summary>
        /// Expresses the resolved apphost as a path relative to the deployed folder.
        /// </summary>
        /// <remarks>
        /// The host's absolute path means nothing in the guest, and an executable outside the folder
        /// being deployed would simply not be there — so that is refused rather than turned into a
        /// launch failure the user cannot interpret.
        /// </remarks>
        private static string ResolveGuestRelativeExecutable(DirectoryInfo targetDir, string executablePath, FileInfo csproj)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir.FullName));
            var full = Path.GetFullPath(executablePath);

            if (!TargetPathSafety.IsInsideRoot(root, full))
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.Unsupported,
                    $"'{csproj.Name}' launches an executable outside its build output, which cannot be deployed into Windows Sandbox.",
                    userAction: "Publish the app as self-contained, or run it without --on sandbox.",
                    context: new Dictionary<string, string> { ["executable"] = Path.GetFileName(full) });
            }

            return full[(root.Length + 1)..];
        }

        /// <summary>Writes a progress line without disturbing a machine-readable stdout.</summary>
        private void WriteProgress(bool isJson, string message)
        {
            if (isJson)
            {
                Console.Error.WriteLine(message);
                return;
            }

            ansiConsole.MarkupLineInterpolated($"{message}");
        }

        /// <summary>Largest guest JSON payload captured for augmentation before it is relayed as-is.</summary>
        /// <remarks>
        /// A run result is a few hundred bytes. The bound exists so a guest that produced something
        /// unexpected on a machine-readable stream cannot make the host buffer without limit.
        /// </remarks>
        internal const int MaxCapturedJsonBytes = 1024 * 1024;

        /// <summary>Captures guest output up to the bound, discarding the excess.</summary>
        private static void CaptureBounded(MemoryStream buffer, ReadOnlyMemory<byte> data)
        {
            var remaining = MaxCapturedJsonBytes - (int)buffer.Length;
            if (remaining <= 0)
            {
                return;
            }

            buffer.Write(data.Span[..Math.Min(remaining, data.Length)]);
        }

        /// <summary>
        /// Emits the guest's machine-readable result with the execution-target members merged in.
        /// </summary>
        internal void PublishGuestJson(MemoryStream captured, PreparedTarget target)
        {
            ArgumentNullException.ThrowIfNull(captured);
            ArgumentNullException.ThrowIfNull(target);

            var bytes = captured.ToArray();
            if (bytes.Length == 0)
            {
                return;
            }

            var augmented = TryAugmentGuestJson(
                bytes,
                new ExecutionTargetInfo
                {
                    Kind = target.Reference.Kind,
                    Id = target.Reference.Id,
                    Architecture = target.Capabilities.Architecture,
                    Epoch = target.Epoch.Value,
                });

            if (augmented is null)
            {
                WriteRawToConsole(Console.OpenStandardOutput(), bytes);
                return;
            }

            ansiConsole.Profile.Out.Writer.WriteLine(augmented);
        }

        /// <summary>Publishes the additive result for a directly launched unpackaged target app.</summary>
        internal void PublishDirectGuestJson(PreparedTarget target)
        {
            var result = CreateDirectGuestResult(
                target.Reference,
                target.Capabilities.Architecture,
                target.Epoch.Value);

            ansiConsole.Profile.Out.Writer.WriteLine(
                JsonSerializer.Serialize(result, RunCommandJsonContext.Default.RunCommandResult));
        }

        internal static RunCommandResult CreateDirectGuestResult(
            ExecutionTargetRef reference,
            string architecture,
            string epoch)
        {
            ArgumentNullException.ThrowIfNull(reference);

            // The agent starts a containment barrier first, so its ExecStarted PID is the barrier,
            // not the app. Omitting a process target is safer than publishing a copyable PID for the
            // wrong process; discovery remains `ui list-windows --on <target>` or an explicit app
            // name.
            return new RunCommandResult
            {
                Sandbox = true,
                ProcessScope = reference.Selector,
                ExecutionTarget = new ExecutionTargetInfo
                {
                    Kind = reference.Kind,
                    Id = reference.Id,
                    Architecture = architecture,
                    Epoch = epoch,
                },
            };
        }

        /// <summary>
        /// Merges the additive execution-target members into a guest run result.
        /// </summary>
        /// <returns>
        /// The augmented document, or null when the payload must be relayed byte-for-byte instead.
        /// </returns>
        /// <remarks>
        /// A payload that does not parse — or that overran the capture bound — is relayed unchanged.
        /// Losing an additive field is recoverable; handing a caller a truncated or re-encoded
        /// document is not.
        /// </remarks>
        internal static string? TryAugmentGuestJson(
            byte[] payload,
            ExecutionTargetInfo executionTarget)
        {
            ArgumentNullException.ThrowIfNull(payload);

            if (payload.Length is 0 or >= MaxCapturedJsonBytes)
            {
                return null;
            }

            RunCommandResult? result;

            try
            {
                result = JsonSerializer.Deserialize(payload, RunCommandJsonContext.Default.RunCommandResult);
            }
            catch (JsonException)
            {
                return null;
            }

            if (result is null)
            {
                return null;
            }

            result.Sandbox = true;
            result.ExecutionTarget = executionTarget;

            var selector = string.Equals(executionTarget.Id, ExecutionTargetRef.DefaultId, StringComparison.Ordinal)
                ? executionTarget.Kind
                : $"{executionTarget.Kind}:{executionTarget.Id}";

            result.ProcessScope = selector;

            // Only the guest's own reported application process is a valid UI target. The host-side
            // exec process is the short-lived winapp launcher, especially for --no-launch.
            if (result.ProcessId is { } pid)
            {
                // Emitted as the two arguments together, never as a bare PID and never as a value
                // that hides the target inside it. A number on its own would resolve against this
                // desktop if it were pasted into a UI command without the selector.
                result.UiTargetArgs =
                    $"--on {selector} -a {pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }

            return JsonSerializer.Serialize(result, RunCommandJsonContext.Default.RunCommandResult);
        }

        /// <summary>Relays a guest stream chunk verbatim.</summary>
        /// <remarks>
        /// Bytes rather than decoded text: guest output can be binary or split mid-character at a
        /// chunk boundary, and decoding per chunk would corrupt both.
        /// </remarks>
        private static void WriteRawToConsole(Stream stream, ReadOnlyMemory<byte> data)
        {
            stream.Write(data.Span);
            stream.Flush();
        }
    }
}
