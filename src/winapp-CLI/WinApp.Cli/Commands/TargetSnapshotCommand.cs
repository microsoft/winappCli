// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

/// <summary>
/// Reports what an execution target looks like right now, in one command.
/// </summary>
/// <remarks>
/// Answers the question an agent asks before it decides what to do next: is the target up, can it
/// take input, what did winapp deploy there, and what is on its desktop. Everything reported is
/// already known to winapp or is one read-only guest query, so the answer is cheap and repeatable.
/// <para>
/// Inspect-only, in the strong sense: it starts nothing, connects no Sandbox client, and repairs no
/// agent. "Is a target running?" would otherwise be a question that starts one — and every later
/// answer would describe the target the question created, not the one the caller asked about.
/// A target that is not running is reported as such, and the command still exits 0.
/// </para>
/// <para>
/// Deliberately produces no files and embeds no image data. A snapshot is something a caller reads
/// and discards; anything worth keeping is what <c>winapp target screenshot</c> and <c>winapp target
/// record</c> exist to produce.
/// </para>
/// </remarks>
internal class TargetSnapshotCommand : Command, IShortDescription
{
    /// <summary>Most guest windows a snapshot lists before it says it truncated.</summary>
    /// <remarks>
    /// A guest desktop can carry hundreds of top-level windows, most of them invisible shells. An
    /// unbounded list would bury the handful that matter and blow up an agent's context for nothing,
    /// so the most useful ones are listed and the total is always reported alongside.
    /// </remarks>
    internal const int MaxWindows = 50;

    /// <inheritdoc/>
    public string ShortDescription => "Report the current state of an execution target";

    /// <summary>Which target to describe.</summary>
    public static Argument<string> SelectorArgument { get; } = TargetVerb.NewSelectorArgument();

    /// <summary>Creates the command.</summary>
    public TargetSnapshotCommand()
        : base(
            "snapshot",
            "Report an execution target's readiness, capabilities, deployments, and top-level guest windows. " +
            "Inspects only: never starts, connects, or repairs a target, and reports plainly when none is running. " +
            "Writes only to stdout: no screenshots and no files.")
    {
        Arguments.Add(SelectorArgument);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    /// <summary>Collects the report and renders it.</summary>
    public class Handler(
        ExecutionTargetOrchestrator orchestrator,
        IDeploymentStateStore deployments,
        IAnsiConsole console) : AsynchronousCommandLineAction
    {
        /// <inheritdoc/>
        public override async Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            ExecutionTargetRef reference;

            try
            {
                reference = TargetVerb.Resolve(orchestrator, parseResult.GetValue(SelectorArgument));
            }
            catch (ExecutionTargetException ex)
            {
                return TargetOutput.RejectSelection(console, json, ex.Error);
            }

            try
            {
                // Inspect-only: never creates, starts, reconnects, or repairs. A command whose whole
                // job is to report state must not be the reason that state exists — an agent asking
                // "is a Sandbox up?" would otherwise start one by asking, and then be told yes.
                var inspection = await orchestrator.InspectAsync(cancellationToken).ConfigureAwait(false);
                await using var target = inspection.Target;

                var output = new TargetSnapshotOutput
                {
                    ExecutionTarget = ExecutionTargetScope.For(reference, inspection.Epoch),
                    Running = inspection.Running,
                    Attached = target is not null,
                    Capabilities = target?.Capabilities,
                    Desktop = DescribeDesktop(inspection.Running),
                    Deployments = [],
                };

                output.Desktop.EffectiveInputReady =
                    target?.Capabilities.SupportsRealInput == true &&
                    output.Desktop.Rendered &&
                    !output.Desktop.Minimized;
                output.Desktop.EffectiveCaptureReady =
                    target?.Capabilities.SupportsScreenCapture == true &&
                    output.Desktop.Rendered &&
                    !output.Desktop.Minimized;

                if (inspection.Running)
                {
                    output.Deployments = await DescribeDeploymentsAsync(
                        reference, inspection.Epoch, target, cancellationToken).ConfigureAwait(false);
                }

                if (target is not null)
                {
                    var windows = await ListGuestWindowsAsync(target, cancellationToken).ConfigureAwait(false);

                    if (windows is not null)
                    {
                        output.WindowCount = windows.Length;
                        output.Windows = [.. Rank(windows).Take(MaxWindows)];
                        output.WindowsTruncated = windows.Length > MaxWindows;
                    }
                }

                if (json)
                {
                    console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(
                        output, TargetJsonContext.Default.TargetSnapshotOutput));
                }
                else
                {
                    Render(console, reference, output);
                }

                return 0;
            }
            catch (ExecutionTargetException ex)
            {
                return TargetOutput.Fail(console, json, ex.Error);
            }
        }

        /// <summary>
        /// The host window the target's desktop is drawn into, when it has one.
        /// </summary>
        /// <remarks>
        /// A target that renders nowhere on this machine, or whose client window cannot be
        /// identified, is reported as such rather than failing the snapshot: the rest of the report
        /// is exactly what a caller needs to work out why.
        /// </remarks>
        private TargetSnapshotDesktop DescribeDesktop(bool running)
        {
            if (!running)
            {
                // Resolving a client window for a target that is not running would find whatever
                // remote-session window happens to be open on this desktop and report it as this
                // target's.
                return new TargetSnapshotDesktop
                {
                    Rendered = false,
                    Unavailable = ExecutionTargetErrorCodes.TargetStale,
                };
            }

            try
            {
                var surface = orchestrator.InspectDesktopSurface();

                return new TargetSnapshotDesktop
                {
                    Rendered = true,
                    WindowHandle = surface.WindowHandle,
                    ProcessId = surface.ProcessId,
                    ProcessName = surface.ProcessName,
                    Adopted = surface.Adopted,
                    Minimized = surface.IsMinimized,
                };
            }
            catch (ExecutionTargetException ex)
            {
                return new TargetSnapshotDesktop { Rendered = false, Unavailable = ex.Error.Code };
            }
        }

        /// <summary>What winapp has deployed to this generation of the target.</summary>
        /// <remarks>
        /// Filtered to the current epoch. A record from a previous generation describes files and a
        /// registration that no longer exist, and reporting it as present would be worse than
        /// reporting nothing.
        /// </remarks>
        private async Task<TargetSnapshotDeployment[]> DescribeDeploymentsAsync(
            ExecutionTargetRef reference,
            ExecutionTargetEpoch epoch,
            PreparedTarget? target,
            CancellationToken cancellationToken)
        {
            try
            {
                var reported = new List<TargetSnapshotDeployment>();

                foreach (var state in deployments.List(reference)
                    .Where(state => state.IsForEpoch(epoch))
                    .OrderBy(state => state.DeploymentId, StringComparer.Ordinal))
                {
                    var registrationStatus = state.Package is null ? "none" : "unknown";
                    var operationStatus =
                        state.TrackedOperationProcessId is not null &&
                        state.TrackedOperationProcessStartTicksUtc is not null
                            ? "unknown"
                            : "none";
                    int? runningOperationProcessId = null;

                    if (target is not null)
                    {
                        if (state.Package is { } package)
                        {
                            try
                            {
                                var actual = await target.Operations.GetRegisteredPackageAsync(
                                    package.PackageName,
                                    package.Publisher,
                                    package.PackageFamilyName,
                                    cancellationToken).ConfigureAwait(false);
                                registrationStatus =
                                    actual is not null &&
                                    actual.IsDevelopmentMode &&
                                    actual.RegisteredLocation is not null &&
                                    package.Owns(actual.FullName, actual.RegisteredLocation)
                                        ? "registered"
                                        : "missing";
                            }
                            catch (ExecutionTargetException)
                            {
                                registrationStatus = "unknown";
                            }
                        }

                        if (state.TrackedOperationProcessId is { } processId &&
                            state.TrackedOperationProcessStartTicksUtc is { } startTicksUtc)
                        {
                            try
                            {
                                var running = await target.Operations.IsTrackedProcessRunningAsync(
                                    processId, startTicksUtc, cancellationToken).ConfigureAwait(false);
                                operationStatus = running ? "running" : "exited";
                                runningOperationProcessId = running ? processId : null;
                            }
                            catch (ExecutionTargetException)
                            {
                                operationStatus = "unknown";
                            }
                        }
                    }

                    // Do not combine a guest answer with a record that changed while that answer was
                    // in flight. Omitting the raced deployment is the only truthful read-only result.
                    var current = deployments.Read(reference, state.DeploymentId);
                    if (current is null ||
                        current.Revision != state.Revision ||
                        !current.IsForEpoch(epoch))
                    {
                        continue;
                    }

                    reported.Add(new TargetSnapshotDeployment
                    {
                        DeploymentId = state.DeploymentId,
                        Dirty = state.Dirty,
                        Kind = state.WasPackaged || state.Package is not null ? "packaged" : "unpackaged",
                        RegistrationStatus = registrationStatus,
                        PackageFullName = registrationStatus == "registered"
                            ? state.Package?.PackageFullName
                            : null,
                        PackageFamilyName = registrationStatus == "registered"
                            ? state.Package?.PackageFamilyName
                            : null,
                        Aumid = registrationStatus == "registered" ? state.Package?.Aumid : null,
                        TrackedOperationStatus = operationStatus,
                        TrackedOperationProcessId = runningOperationProcessId,
                        TrackedOperationKind = operationStatus == "none"
                            ? null
                            : state.WasPackaged || state.Package is not null
                                ? "package-launcher"
                                : "application",
                        RetainedLayout =
                            !state.Dirty &&
                            registrationStatus is "none" or "missing" &&
                            operationStatus is "none" or "exited",
                    });
                }

                return [.. reported];
            }
            catch (ExecutionTargetException)
            {
                // One unreadable deployment record is not a reason to withhold everything else a
                // caller asked for.
                return [];
            }
        }

        /// <summary>
        /// Asks the guest's own winapp what is on its desktop.
        /// </summary>
        /// <returns>The guest's windows, or null when it could not answer.</returns>
        /// <remarks>
        /// Declared as needing no real input, so it neither reconnects the client nor fails when the
        /// Sandbox window happens to be closed. That is also why a failure returns null instead of
        /// throwing: the window list is the one part of a snapshot that depends on a live desktop,
        /// and losing it must not cost the caller the readiness and deployment facts that explain
        /// why it is missing.
        /// </remarks>
        private static async Task<WindowInfo[]?> ListGuestWindowsAsync(
            PreparedTarget target,
            CancellationToken cancellationToken)
        {
            var stdout = new MemoryStream();

            try
            {
                var result = await target.Operations.ExecuteAsync(
                    new GuestExecRequest
                    {
                        UseGuestWinapp = true,
                        Arguments = ["ui", "list-windows", "--json"],
                        Environment = GuestOwnerContext.WithOwner(
                            environment: null,
                            GuestOwnerContext.ResolveGuestToken(
                                target.Reference.StateKey, target.Epoch.Value)),
                    },
                    new GuestExecCallbacks(
                        OnStandardOutput: data => stdout.Write(data.Span),

                        // Swallowed rather than relayed. The guest's own diagnostics would land on
                        // this command's stderr next to nothing that explains them, and the report
                        // already says plainly when the window list is missing.
                        OnStandardError: _ => { }),
                    cancellationToken).ConfigureAwait(false);

                return result.ExitCode == 0
                    ? JsonSerializer.Deserialize(
                        Encoding.UTF8.GetString(stdout.ToArray()), UiJsonContext.Default.WindowInfoArray)
                    : null;
            }
            catch (Exception ex) when (ex is ExecutionTargetException or JsonException)
            {
                return null;
            }
            finally
            {
                stdout.Dispose();
            }
        }

        /// <summary>Foreground window first, then largest, so the truncated list is the useful one.</summary>
        private static IEnumerable<WindowInfo> Rank(IEnumerable<WindowInfo> windows) =>
            windows
                .OrderByDescending(window => window.IsForeground)
                .ThenByDescending(window => (long)window.Width * window.Height);

        private static void Render(IAnsiConsole console, ExecutionTargetRef reference, TargetSnapshotOutput output)
        {
            if (!output.Running)
            {
                console.MarkupLineInterpolated($"{reference.Selector}: not running");
                console.MarkupLineInterpolated(
                    $"  Start one with: winapp run . --on {reference.Selector}");
                return;
            }

            console.MarkupLineInterpolated(
                $"{reference.Selector}: running, epoch {output.ExecutionTarget.Epoch ?? "none"}");

            if (output.Capabilities is { } capabilities)
            {
                console.MarkupLineInterpolated($"  Architecture: {TerminalText.Sanitize(capabilities.Architecture)}");
                console.MarkupLineInterpolated(
                    $"  Guest support: real input {Yes(capabilities.SupportsRealInput)}, screen capture {Yes(capabilities.SupportsScreenCapture)}, interactive desktop {Yes(capabilities.SupportsInteractiveDesktop)}");
            }
            else
            {
                console.MarkupLine("  Agent: not answering, so capabilities and windows are unknown");
            }

            if (output.Desktop.Rendered)
            {
                console.MarkupLineInterpolated(
                    $"  Desktop: HWND {output.Desktop.WindowHandle} ({output.Desktop.ProcessName}, PID {output.Desktop.ProcessId}){(output.Desktop.Adopted ? ", adopted" : "")}{(output.Desktop.Minimized ? ", minimized" : "")}");
                console.MarkupLineInterpolated(
                    $"  Effective readiness: real input {Yes(output.Desktop.EffectiveInputReady)}, screen capture {Yes(output.Desktop.EffectiveCaptureReady)}");
            }
            else
            {
                console.MarkupLineInterpolated(
                    $"  Desktop: not capturable on this machine ({output.Desktop.Unavailable})");
            }

            if (output.Deployments.Length == 0)
            {
                console.MarkupLine("  Deployments: none for this epoch");
            }
            else
            {
                console.MarkupLineInterpolated($"  Deployments: {output.Deployments.Length}");
                foreach (var deployment in output.Deployments)
                {
                    var description = DescribeDeploymentState(deployment);
                    console.MarkupLineInterpolated(
                        $"    {TerminalText.Sanitize(deployment.PackageFullName ?? deployment.DeploymentId)} ({description})");
                }
            }

            if (output.Windows is null)
            {
                console.MarkupLine(output.Attached
                    ? "  Windows: unavailable (the guest did not report its desktop)"
                    : "  Windows: unavailable (no agent to ask)");
                return;
            }

            console.MarkupLineInterpolated(
                $"  Windows: {output.WindowCount}{(output.WindowsTruncated ? $" (showing {output.Windows.Length})" : "")}");

            foreach (var window in output.Windows)
            {
                // Guest-chosen text, printed to the caller's terminal: sanitized so a window title
                // cannot repaint the report it appears in. JSON carries the original.
                console.MarkupLineInterpolated(
                    $"    HWND {window.Hwnd} {TerminalText.Sanitize(window.ProcessName)} (PID {window.ProcessId}) {window.Width}x{window.Height}{(window.IsForeground ? " [foreground]" : "")} \"{TerminalText.Sanitize(window.Title)}\"");
            }
        }

        private static string Yes(bool value) => value ? "yes" : "no";

        private static string DescribeDeploymentState(TargetSnapshotDeployment deployment)
        {
            if (deployment.Dirty)
            {
                return "dirty";
            }

            var states = new List<string>();
            if (deployment.RegistrationStatus == "registered")
            {
                states.Add("registered");
            }

            if (deployment.TrackedOperationStatus == "running")
            {
                states.Add(
                    $"{deployment.TrackedOperationKind} running, PID {deployment.TrackedOperationProcessId}");
            }
            else if (deployment.TrackedOperationStatus == "exited")
            {
                states.Add($"{deployment.TrackedOperationKind} exited");
            }

            if (deployment.RetainedLayout)
            {
                states.Add("retained layout");
            }

            return states.Count == 0 ? "state unknown" : string.Join(", ", states);
        }
    }
}

/// <summary>Machine-readable snapshot of one execution target.</summary>
internal sealed class TargetSnapshotOutput
{
    /// <summary>Which target, and which incarnation of it, was described.</summary>
    public required ExecutionTargetScope ExecutionTarget { get; init; }

    /// <summary>True when the target winapp manages is running right now.</summary>
    /// <remarks>
    /// False is an ordinary answer, not a failure: the command exits 0 and reports what it found.
    /// A snapshot never starts a target to be able to describe one.
    /// </remarks>
    public required bool Running { get; init; }

    /// <summary>True when the guest agent answered, so the report includes what only it knows.</summary>
    public required bool Attached { get; init; }

    /// <summary>What the live guest reports it can do, or null when no agent answered.</summary>
    public ExecutionTargetCapabilities? Capabilities { get; init; }

    /// <summary>Where this target's desktop is drawn on this machine, if anywhere.</summary>
    public required TargetSnapshotDesktop Desktop { get; init; }

    /// <summary>What winapp has deployed to this generation of the target.</summary>
    public required TargetSnapshotDeployment[] Deployments { get; set; }

    /// <summary>
    /// Top-level guest windows, most useful first, or null when the guest did not answer.
    /// </summary>
    public WindowInfo[]? Windows { get; set; }

    /// <summary>How many top-level windows the guest reported, before truncation.</summary>
    public int WindowCount { get; set; }

    /// <summary>True when <see cref="Windows"/> holds fewer entries than the guest reported.</summary>
    public bool WindowsTruncated { get; set; }
}

/// <summary>Where a target's desktop is rendered on this machine.</summary>
internal sealed class TargetSnapshotDesktop
{
    /// <summary>True when this machine draws the target's desktop and winapp found the window.</summary>
    public required bool Rendered { get; init; }

    /// <summary>Host window handle of the client, when there is one.</summary>
    public long? WindowHandle { get; init; }

    /// <summary>Host process that owns the client window.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Host process name that owns the client window.</summary>
    public string? ProcessName { get; init; }

    /// <summary>True when winapp recognised the client rather than recording that it created it.</summary>
    public bool Adopted { get; init; }

    /// <summary>True when the host client window is minimized right now.</summary>
    public bool Minimized { get; init; }

    /// <summary>Whether a routed real-input operation could start without restoring the client.</summary>
    public bool EffectiveInputReady { get; set; }

    /// <summary>Whether a pixel-capture operation could start without restoring the client.</summary>
    public bool EffectiveCaptureReady { get; set; }

    /// <summary>Error code explaining why no client window is available, when there is none.</summary>
    public string? Unavailable { get; init; }
}

/// <summary>One deployment winapp has recorded on this generation of the target.</summary>
internal sealed class TargetSnapshotDeployment
{
    /// <summary>Stable identity of the deployment.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>True when the last reconciliation did not finish, so the guest copy is unreliable.</summary>
    public required bool Dirty { get; init; }

    /// <summary>Package registered in the guest, when the deployment registered one.</summary>
    public string? PackageFullName { get; init; }

    /// <summary>Package family, when the deployment registered a package.</summary>
    public string? PackageFamilyName { get; init; }

    /// <summary>Application user model ID, when the deployment registered one.</summary>
    public string? Aumid { get; init; }

    /// <summary>Whether this is a packaged or unpackaged deployment.</summary>
    public required string Kind { get; init; }

    /// <summary>Whether the recorded package registration is active, missing, absent, or unverified.</summary>
    public required string RegistrationStatus { get; init; }

    /// <summary>Whether the tracked operation is running, exited, absent, or unverified.</summary>
    public required string TrackedOperationStatus { get; init; }

    /// <summary>Live guest operation PID, present only after PID/start-time validation.</summary>
    public int? TrackedOperationProcessId { get; init; }

    /// <summary>Whether the tracked operation is the application or a package launcher.</summary>
    public string? TrackedOperationKind { get; init; }

    /// <summary>True when only a clean reusable layout remains.</summary>
    public bool RetainedLayout { get; init; }
}
