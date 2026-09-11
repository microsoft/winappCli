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
        /// Materializes a packaged app on the host, then registers and launches it in the guest.
        /// </summary>
        /// <remarks>
        /// Materialization must not install runtimes or register packages on this machine.
        /// </remarks>
        private async Task<int> ExecutePackagedTargetRunAsync(
            DirectoryInfo inputFolder,
            FileInfo? manifest,
            LayoutOutput layoutOutput,
            string? appArgs,
            bool noLaunch,
            AliasLaunchDecision aliasDecision,
            bool debugOutput,
            bool unregisterOnExit,
            bool detach,
            bool clean,
            bool useSymbols,
            string? executable,
            bool isJson,
            string? runtimeArch,
            FileInfo? projectFile,
            string? framework,
            bool noRestore,
            bool selfContained,
            PackageGraphSource? packageGraph,
            CancellationToken cancellationToken)
        {
            FileInfo resolvedManifest;
            DirectoryInfo layout;
            MsixIdentityResult? identity = null;

            // Prevent concurrent runs rewriting the host layout until guest deployment consumes it.
            LayoutLease? layoutLease = null;

            try
            {
                try
                {
                    resolvedManifest = ResolveManifestForSandbox(inputFolder, manifest);

                    // LayoutOutput retains whether this host directory is generated or user-supplied.
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
                                    executable, projectFile, framework, noRestore,
                                    selfContained, aliasDecision.UseAlias, packageGraph, ct);
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

                var options = new GuestLaunchOptions(
                    aliasDecision.UseAlias, debugOutput, detach, isJson, appArgs,
                    AliasIsExplicit: aliasDecision.Explicit, Symbols: useSymbols);

                // Registration and exit cleanup own mutation leases; this launch-only request does not.
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
                    forwardStandardInput: aliasDecision.UseAlias,
                    layoutLease: layoutLease,
                    applicationArchitecture: runtimeArch,
                    packageGraph: selfContained ? null : packageGraph,
                    framework: framework);
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
                GuestRunPlanner.EnsureSupportedForUnpackaged(debugOutput);
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
                guestProducesRunResult: false,
                applicationArchitecture: resolution.Architecture,
                packageGraph: !resolution.SelfContained && resolution.ProjectAssetsFile is { } assetsFile
                    ? new PackageGraphSource(new FileInfo(assetsFile), resolution.ProjectAssetsRuntimeIdentifier)
                    : null,
                framework: resolution.Framework);
        }

        /// <summary>
        /// Prepares and deploys under a mutation lease, then releases it before running the app.
        /// </summary>
        /// <param name="sourceRoot">Host folder to reconcile into the guest.</param>
        /// <param name="deploymentId">Internal deployment identity.</param>
        /// <param name="clean">Whether to discard the guest copy first.</param>
        /// <param name="isJson">Whether the invoking command is in machine-readable mode.</param>
        /// <param name="requiresRealInput">Whether the guest command needs a usable input desktop.</param>
        /// <param name="identity">Package identity to record ownership for, when there is one.</param>
        /// <param name="noLaunch">Return after registration instead of starting the app.</param>
        /// <param name="unregisterOnExit">Remove this run's registration after guest termination.</param>
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
            LayoutLease? layoutLease = null,
            string? applicationArchitecture = null,
            PackageGraphSource? packageGraph = null,
            string? framework = null)
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

                var provisioning = await ProvisionRuntimesAsync(
                    target, sourceRoot, applicationArchitecture, packageGraph, framework, cancellationToken);

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

                    // Even --no-launch registers under the mutation lease.
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
                            target.ReleaseMutationLease();

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

                // A running app must not retain either the guest mutation lease or host layout lease.
                target.ReleaseMutationLease();

                layoutLease?.Dispose();

                var ownerEnvironment = GuestOwnerContext.WithWorkflow(
                    environment: null,
                    GuestOwnerContext.ResolveGuestToken(
                        target.Reference.StateKey, target.Epoch.Value));

                // Apphosts need the verified per-user runtime root as well as the workflow context.
                foreach (var (name, value) in provisioning.LaunchEnvironment)
                {
                    ownerEnvironment[name] = value;
                }

                // Merge target metadata into guest JSON; direct executables get a host-built envelope.
                using var capturedOutput = isJson && guestProducesRunResult ? new MemoryStream() : null;
                var request = buildRequest(deployment, ownerEnvironment);

                WriteProgress(isJson, "Starting the application in the Windows Sandbox...");

                GuestRunOutcome run;
                try
                {
                    run = await guestApplicationRunner.RunAsync(
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
                        cancellationToken,
                        onStateChanged: updated => state = updated);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (identity is not null && unregisterOnExit)
                    {
                        await UnregisterDeploymentAfterExitAsync(target, identity, state);
                    }

                    throw;
                }

                // Cleanup rechecks this run's revision under a fresh mutation lease.
                if (identity is not null && unregisterOnExit)
                {
                    await UnregisterDeploymentAfterExitAsync(target, identity, run.State);
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
        /// Registers under the caller's mutation lease. The later guest-launch command can only
        /// verify and launch this registration, never repair a mismatch by registering again.
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
                Arguments = GuestRunPlanner.BuildRegistrationArguments(
                    deployment.PayloadPath,
                    deployment.LayoutPath,
                    clean, isJson),
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
        /// Rechecks ownership under a fresh mutation lease after confirmed guest termination.
        /// </summary>
        /// <remarks>
        /// Reuse the live channel and its epoch. The recorded revision rejects cleanup from a run
        /// superseded while it was waiting. Cleanup has its own deadline and preserves the app's exit code.
        /// </remarks>
        private async Task UnregisterDeploymentAfterExitAsync(
            PreparedTarget target,
            MsixIdentityResult identity,
            DeploymentState state)
        {
            try
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var cleanupToken = cleanupTimeout.Token;
                using var mutationLease = executionTargetOrchestrator.AcquireMutationLease(cleanupToken);
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
                    cancellationToken: cleanupToken).ConfigureAwait(false);
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
            string? applicationArchitecture,
            PackageGraphSource? packageGraph,
            string? framework,
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
                            sourceRoot,
                            applicationArchitecture,
                            new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory()),
                            taskContext,
                            ct,
                            windowsAppRuntimeVersion: ResolveRestoredWindowsAppRuntimeVersion(packageGraph, framework));

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

        internal static string? ResolveRestoredWindowsAppRuntimeVersion(PackageGraphSource? graph, string? framework)
        {
            if (graph is null)
            {
                return null;
            }

            var packages = ProjectAssetsFileReader.TryRead(graph.AssetsFile, graph.RuntimeIdentifier)
                ?? throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.RuntimeProvisionFailed,
                    $"The restored package graph at '{graph.AssetsFile.FullName}' could not be read.",
                    userAction: "Restore and rebuild the project, then retry.");
            var versions = packages.Projects
                .SelectMany(project => project.Frameworks)
                .Where(target => framework is null || string.Equals(target.Framework, framework, StringComparison.OrdinalIgnoreCase))
                .SelectMany(target => target.TopLevelPackages.Concat(target.TransitivePackages))
                .Where(package => string.Equals(package.Id, "Microsoft.WindowsAppSDK.Runtime", StringComparison.OrdinalIgnoreCase))
                .Select(package => package.ResolvedVersion)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return versions.Length switch
            {
                0 => null,
                1 => versions[0],
                _ => throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.RuntimeProvisionFailed,
                    "The restored graph contains different Windows App Runtime versions for multiple target frameworks.",
                    userAction: "Pass --framework to select the framework you built."),
            };
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
