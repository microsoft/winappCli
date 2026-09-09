// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Telemetry;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>What one provisioning pass did, for the caller's progress line and for tests.</summary>
/// <param name="Requirements">The constraints that were discovered.</param>
/// <param name="AlreadySatisfied">True when the guest needed nothing installed.</param>
/// <param name="Report">The guest's verdict, when the guest was asked for one.</param>
internal sealed record RuntimeProvisionResult(
    RuntimeRequirements Requirements,
    bool AlreadySatisfied,
    RuntimeProvisionReport? Report)
{
    /// <summary>
    /// Environment the application must be launched with, so its apphost resolves what was
    /// installed.
    /// </summary>
    /// <remarks>
    /// A per-user .NET root is discoverable to an apphost through <c>DOTNET_ROOT</c> and nothing
    /// else without machine-wide registration, so the value has to reach the launched process
    /// itself. Empty when the guest satisfied every framework on its own, which leaves the guest's
    /// ordinary resolution untouched.
    /// </remarks>
    public IReadOnlyDictionary<string, string> LaunchEnvironment =>
        Report?.DotNetRoot is { Length: > 0 } root
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["DOTNET_ROOT"] = root }
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Provisions the shared runtimes an application needs inside an execution target
/// (spec §"Runtime provisioning").
/// </summary>
/// <remarks>
/// Target-neutral like the rest of orchestration: it uses the command channel, the guest's reported
/// managed root, and host state, and knows nothing about Windows Sandbox. The whole sequence is
/// therefore verifiable against a fake transport.
/// <para>
/// Nothing about the host machine is inspected for readiness or changed. Payloads are read from
/// caches the host already has and are installed in the guest — a <c>--on sandbox</c> run must never
/// register a runtime on the developer's machine, which is a large part of why the flag exists.
/// </para>
/// <para>
/// Installation happens in the guest, through the ordinary guest winapp child process the agent runs
/// for every other operation. The agent implements no package semantics of its own, so the version
/// comparison, the "already present" skip, and the refusal to remove anything are the same code in
/// the guest as on a local machine.
/// </para>
/// </remarks>
internal sealed class TargetRuntimeService(
    IRuntimeProvisionStateStore stateStore,
    IRuntimePayloadResolver payloadResolver,
    IRuntimeFrameworkResolver frameworkResolver)
{
    /// <summary>Telemetry event names for the phases the spec requires timings for.</summary>
    /// <remarks>
    /// Constants, never interpolated. A phase name carries no path, package identity, argument, or
    /// anything else the telemetry rules exclude — which is what lets these be recorded at all.
    /// </remarks>
    internal const string DiscoveryPhase = "SandboxRuntimeDiscovery";
    internal const string CacheResolutionPhase = "SandboxRuntimeCacheResolution";
    internal const string TransferPhase = "SandboxRuntimeTransfer";
    internal const string InstallationPhase = "SandboxRuntimeInstallation";
    internal const string VerificationPhase = "SandboxRuntimeVerification";

    /// <summary>The hidden guest verb that installs staged payloads and verifies the graph.</summary>
    internal const string GuestVerb = "guest-runtime";

    /// <summary>Option naming the staged plan for the guest verb.</summary>
    internal const string GuestPlanOption = "--plan";

    /// <summary>Folder under the guest's managed root that holds the per-user .NET installation.</summary>
    internal const string DotNetRootFolderName = "dotnet";

    /// <summary>Largest guest report the host will buffer.</summary>
    /// <remarks>
    /// A report is a few kilobytes even for an application with many dependencies. The bound exists
    /// so a guest that printed something unexpected cannot make the host allocate without limit.
    /// </remarks>
    internal const int MaxReportBytes = 512 * 1024;

    /// <summary>
    /// Ensures every runtime <paramref name="sourceRoot"/> needs is present in the guest.
    /// </summary>
    /// <param name="target">
    /// Prepared target, whose channel and epoch the work is fenced on, and whose mutation lease
    /// (already held by the caller from <see cref="ExecutionTargetOrchestrator.PrepareAsync"/>)
    /// this call relies on rather than reacquiring.
    /// </param>
    /// <param name="targetRef">Target whose state root holds the provisioning record.</param>
    /// <param name="sourceRoot">Host folder about to be deployed — a layout or a build output.</param>
    /// <param name="projectRoot">Workspace root, used only when a payload has to be acquired.</param>
    /// <param name="taskContext">Status and debug sink.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="ExecutionTargetException">
    /// The required graph could not be satisfied without removing or downgrading a shared runtime.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="target"/> was not prepared for mutation.
    /// </exception>
    /// <remarks>
    /// The graph is verified before every launch, never inferred from a previous pass. A clean
    /// journal proves what winapp did, not what the guest currently has: <c>sandbox exec</c> gives
    /// any caller a way to install, remove, or replace packages and runtimes inside the same
    /// generation, and a deployment that trusted the record would launch into a guest whose runtime
    /// had been changed underneath it. The journal's job is narrower and unchanged — it says whether
    /// a previous pass was interrupted, and therefore whether the staged area can be trusted.
    /// <para>
    /// This no longer acquires the mutation lock itself: the caller already holds it for the whole
    /// mutating sequence (runtime provisioning, deployment reconciliation, package registration), via
    /// <paramref name="target"/>'s <see cref="PreparedTarget.MutationLease"/>. Reacquiring the same
    /// file-backed lock here would deadlock against the caller's own held lease rather than nest.
    /// </para>
    /// </remarks>
    public async Task<RuntimeProvisionResult> EnsureAsync(
        PreparedTarget target,
        ExecutionTargetRef targetRef,
        DirectoryInfo sourceRoot,
        DirectoryInfo projectRoot,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetRef);
        ArgumentNullException.ThrowIfNull(sourceRoot);

        var discoveryWatch = Stopwatch.StartNew();
        var requirements = RuntimeRequirementDiscovery.Discover(sourceRoot, target.Capabilities.Architecture);
        Record(DiscoveryPhase, discoveryWatch);

        if (requirements.IsEmpty)
        {
            // A self-contained or native application declares nothing shared. Provisioning it would
            // be work with no subject, and staging an empty plan into the guest would still cost a
            // round trip on every run.
            return new RuntimeProvisionResult(requirements, AlreadySatisfied: true, Report: null);
        }

        target.RequireMutationLease();

        var planId = requirements.PlanId;

        var existing = stateStore.Read(targetRef);

        // Only an unfinished pass makes the staged area untrustworthy. A clean record for a
        // different plan, or one from a previous generation, leaves nothing misleading behind — and
        // wiping the scope for those would re-transfer tens of megabytes for no reason.
        var repair = existing?.Dirty == true;

        var resolutionWatch = Stopwatch.StartNew();

        var packages = await payloadResolver
            .ResolveAsync(requirements, projectRoot, taskContext, cancellationToken)
            .ConfigureAwait(false);

        var frameworks = await ResolveFrameworksAsync(requirements, projectRoot, taskContext, cancellationToken)
            .ConfigureAwait(false);

        Record(CacheResolutionPhase, resolutionWatch);

        var dotNetRoot = TargetPathSafety.CombineInsideRoot(
            target.Capabilities.ManagedRoot ?? throw MissingManagedRoot(), DotNetRootFolderName);

        var plan = BuildPlan(requirements, planId, dotNetRoot, packages, frameworks);

        // Journalled before the first guest mutation, exactly as deployment is: a host that dies
        // between installing the first package and the last must leave a record that says so.
        var dirty = stateStore.Commit(
            targetRef,
            new RuntimeProvisionState
            {
                SchemaVersion = RuntimeProvisionStateStore.CurrentSchemaVersion,
                Revision = existing?.Revision ?? 0,
                TargetEpoch = target.Epoch.Value,
                PlanId = planId,
                Dirty = true,
            },
            existing?.Revision ?? 0);

        var scope = new GuestPathScope(GuestRootNames.Runtimes, planId);

        var transferWatch = Stopwatch.StartNew();
        await RuntimeStaging.StageAsync(target, scope, plan, packages, frameworks, repair, cancellationToken)
            .ConfigureAwait(false);
        Record(TransferPhase, transferWatch);

        var report = await InvokeGuestAsync(target, scope, planId, taskContext, cancellationToken).ConfigureAwait(false);

        Record(InstallationPhase, report.InstallMilliseconds);
        Record(VerificationPhase, report.VerifyMilliseconds);

        EnsureSatisfied(report);

        stateStore.Commit(targetRef, dirty with { Dirty = false }, dirty.Revision);

        return new RuntimeProvisionResult(
            requirements,
            AlreadySatisfied: !report.Items.Any(item => item.Installed),
            report);
    }

    /// <summary>Resolves a portable layout for every shared framework requirement that has one.</summary>
    private async Task<Dictionary<string, RuntimeFrameworkPayload>> ResolveFrameworksAsync(
        RuntimeRequirements requirements,
        DirectoryInfo projectRoot,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var payloads = new Dictionary<string, RuntimeFrameworkPayload>(StringComparer.OrdinalIgnoreCase);

        foreach (var requirement in requirements.Frameworks)
        {
            var payload = await frameworkResolver
                .ResolveAsync(requirement, projectRoot, taskContext, cancellationToken)
                .ConfigureAwait(false);

            if (payload is null)
            {
                // Not a failure here. The guest may already have it, and only an unsatisfied guest
                // verification is grounds for refusing to launch.
                continue;
            }

            payloads[requirement.Name] = payload;
        }

        return payloads;
    }

    /// <summary>Builds the plan the guest reads, naming the staged file for each resolved payload.</summary>
    /// <remarks>
    /// Payload file names are flattened to the package identity so two cached copies with the same
    /// file name cannot collide in the staging scope, and so the staged name is a value the host
    /// derived rather than one it inherited from a folder it does not own.
    /// </remarks>
    private static RuntimeProvisionPlan BuildPlan(
        RuntimeRequirements requirements,
        string planId,
        string dotNetRoot,
        IReadOnlyList<ResolvedRuntimePackage> packages,
        Dictionary<string, RuntimeFrameworkPayload> frameworks) =>
        new()
        {
            SchemaVersion = RuntimeProvisionPlan.CurrentSchemaVersion,
            PlanId = planId,
            Architecture = requirements.Architecture,
            DotNetRoot = dotNetRoot,
            Packages =
            [
                .. packages.Select(entry => new RuntimePackageRequirement
                {
                    Name = entry.Requirement.Name,
                    MinVersion = entry.Requirement.MinVersion,
                    Architecture = entry.Requirement.Architecture,
                    Publisher = entry.Requirement.Publisher,
                    Derived = entry.Requirement.Derived,
                    PayloadFile = entry.Payload is { } payload ? RuntimeStaging.StagedFileName(payload) : null,
                }),
            ],
            Frameworks =
            [
                .. requirements.Frameworks.Select(requirement => new RuntimeFrameworkRequirement
                {
                    Name = requirement.Name,
                    MinVersion = requirement.MinVersion,
                    Architecture = requirement.Architecture,
                    PayloadFile = frameworks.TryGetValue(requirement.Name, out var payload)
                        ? RuntimeStaging.StagedFileName(payload)
                        : null,
                    PayloadVersion = frameworks.TryGetValue(requirement.Name, out var resolved)
                        ? resolved.Version
                        : null,
                }),
            ],
        };

    /// <summary>Runs the guest verb over the staged plan and reads back its report.</summary>
    /// <remarks>
    /// The verdict is fetched from the staging scope rather than parsed off standard output. Output
    /// frames and the exit notification are independent sends, so a report read from the stream
    /// could be truncated by a guest process that exited first — and the host must never treat a
    /// truncated verdict as a verified graph.
    /// </remarks>
    private static async Task<RuntimeProvisionReport> InvokeGuestAsync(
        PreparedTarget target,
        GuestPathScope scope,
        string planId,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var scopePath = GuestPaths.Resolve(target.Capabilities, scope);
        var planPath = TargetPathSafety.CombineInsideRoot(scopePath, RuntimeProvisionPlan.FileName);

        var diagnostics = new StringBuilder();

        var result = await target.Operations.ExecuteAsync(
            new GuestExecRequest
            {
                UseGuestWinapp = true,
                Arguments = [GuestVerb, GuestPlanOption, planPath],
                WorkingDirectory = scopePath,
            },
            new GuestExecCallbacks(
                OnStandardError: data =>
                {
                    if (diagnostics.Length < MaxReportBytes)
                    {
                        diagnostics.Append(Encoding.UTF8.GetString(data.Span));
                    }
                }),
            cancellationToken).ConfigureAwait(false);

        var report = await TryReadReportAsync(target, scope, cancellationToken).ConfigureAwait(false);

        // A report for another plan is a leftover from a previous pass, not this one's verdict.
        if (report is not null && string.Equals(report.PlanId, planId, StringComparison.Ordinal))
        {
            return report;
        }

        // No readable verdict means the graph is unverified, whatever the exit code said. Launching
        // on the strength of a report nobody could read is exactly the silent wrong answer this
        // whole step exists to prevent.
        taskContext.AddDebugMessage(
            $"{UiSymbols.Note} The guest runtime provisioning report could not be read (exit code {result.ExitCode}).");

        var context = new Dictionary<string, string>
        {
            ["exitCode"] = result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        // The guest's own first line of diagnostics, when it produced one. Without it the only clue
        // a user gets is an exit code, which names nothing they can act on.
        if (FirstLine(diagnostics.ToString()) is { Length: > 0 } detail)
        {
            context["guestError"] = detail;
        }

        throw Failed(
            "winapp could not verify the shared runtimes the app needs inside Windows Sandbox.",
            "Retry the command. If it keeps failing, close Windows Sandbox so a fresh guest is created.",
            context);
    }

    /// <summary>Fetches the guest's report, or null when it did not publish a readable one.</summary>
    private static async Task<RuntimeProvisionReport?> TryReadReportAsync(
        PreparedTarget target,
        GuestPathScope scope,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();

        try
        {
            await target.Operations
                .GetFileAsync(scope, RuntimeProvisionReport.FileName, buffer, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ExecutionTargetException)
        {
            // The guest never wrote one. The caller reports that as an unverified graph, with the
            // exit code and the guest's own diagnostics attached.
            return null;
        }

        if (buffer.Length is 0 or > MaxReportBytes)
        {
            return null;
        }

        return RuntimeProvisionReport.TryParse(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    /// <summary>First non-empty line of guest diagnostics, bounded for an error envelope.</summary>
    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed.Length <= 500 ? trimmed : trimmed[..500];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Refuses to launch when any requirement is unmet, naming the requirement rather than the
    /// symptom.
    /// </summary>
    /// <remarks>
    /// The unsatisfied constraint is the whole value of this step: the alternative is an application
    /// that starts and dies with a missing-dependency error nobody can act on. Nothing is removed or
    /// downgraded to make a constraint fit — an unsatisfiable graph is reported, not forced.
    /// </remarks>
    private static void EnsureSatisfied(RuntimeProvisionReport report)
    {
        if (report.Satisfied)
        {
            return;
        }

        var unsatisfied = report.Items.Where(item => !item.Satisfied).ToList();

        var summary = string.Join(
            ", ",
            unsatisfied.Select(item => $"{item.Name} {item.RequiredVersion} or newer"));

        throw Failed(
            $"Windows Sandbox is missing a runtime the app requires: {summary}.",
            "Publish the app self-contained, or install the missing runtime in the Sandbox and retry.",
            context: new Dictionary<string, string>
            {
                ["unsatisfied"] = string.Join(", ", unsatisfied.Select(item => item.Name)),
                ["detail"] = string.Join(
                    "; ",
                    unsatisfied.Where(item => item.Detail is not null).Select(item => item.Detail!)),
            },
            example: "winapp target exec sandbox -- winget install Microsoft.WindowsAppRuntime");
    }

    private static ExecutionTargetException MissingManagedRoot() =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.AgentIncompatible,
            "The guest agent did not report where it stores deployed applications.",
            userAction: "Update winapp on this machine, then retry so the guest agent is replaced.",
            nextCommand: new ExecutionTargetNextCommand { Command = "winapp update", Advisory = false });

    private static ExecutionTargetException Failed(
        string message,
        string userAction,
        Dictionary<string, string>? context = null,
        string? example = null) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.RuntimeProvisionFailed,
            message,
            userAction: userAction,
            context: context,
            example: example);

    private static void Record(string phase, Stopwatch watch) =>
        Record(phase, watch.ElapsedMilliseconds);

    private static void Record(string phase, long milliseconds) =>
        TelemetryFactory.Get<ITelemetry>().LogTimeTaken(
            phase,
            (uint)Math.Clamp(milliseconds, 0, uint.MaxValue),
            TelemetryCorrelation.CurrentId);
}
