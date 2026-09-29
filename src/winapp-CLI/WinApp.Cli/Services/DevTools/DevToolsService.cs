// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Services.DevTools;

internal sealed class DevToolsService(ILogger<DevToolsService> logger, ICommentPusher commentPusher,
    ExecutionTargets.GuestAgent.GuestCommentContext? guestComments = null) : IDevToolsService
{
    internal const string MutationPolicyEnvironmentVariable = "WINAPP_DEVTOOLS_MUTATION";

    // How long to wait for the app to put up its main window before injecting, and how long to wait
    // for the agent's pipe to answer after injection.
    private static readonly TimeSpan UiReadyTimeout = TimeSpan.FromSeconds(20);
    private const int PipeReadyTimeoutMs = 30_000;

    // Cold-start injection retry: on a first launch after a rebuild the app's main window handle can
    // appear before its XAML visual tree, so InitializeXamlDiagnosticsEx returns ERROR_NOT_FOUND. That is
    // transient — a warm retry (and attach --pid against the same live process) succeeds. Retry ONLY that
    // HRESULT, while the target is alive, for ~10s (interval * (attempts - 1)): enough to cover packaged
    // identity registration + JIT on a cold app, while a non-WinUI target still fails fast (it returns a
    // different, terminal HRESULT on the first attempt, which is not retried).
    private const int InjectMaxAttempts = 21;
    private static readonly TimeSpan InjectRetryInterval = TimeSpan.FromMilliseconds(500);

    public async Task<DevToolsConnection> ConnectAsync(
        uint targetPid,
        bool showOverlay,
        DevToolsAccess requestedAccess,
        CancellationToken cancellationToken)
    {
        Process target;
        try
        {
            target = Process.GetProcessById(unchecked((int)targetPid));
        }
        catch (ArgumentException)
        {
            return DevToolsConnection.Fail($"Process {targetPid} is not running.");
        }
        using var targetLifetime = target;
        if (guestComments?.Process is { } expected &&
            (expected.ProcessId != targetPid || target.HasExited ||
                target.StartTime.ToUniversalTime().Ticks != expected.StartTicksUtc))
        {
            return DevToolsConnection.Fail("The bound guest application lifetime expired. Relaunch or rediscover it.");
        }

        if (DevToolsPipeDiscovery.EnumerateInjectedPids().Contains((int)targetPid))
        {
            return CompleteConnection(target, targetPid, showOverlay, cancellationToken);
        }

        string agentPath;
        try
        {
            agentPath = DevToolsArtifacts.StageTap();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            logger.LogError(ex, "Could not prepare the DevTools agent.");
            return DevToolsConnection.Fail($"Could not prepare the DevTools agent: {ex.Message}");
        }

        // 2. Give the app a moment to bring up its XAML/window. This must happen BEFORE we resolve the
        //    framework UDK: a freshly-launched process hasn't loaded its Windows App Runtime modules yet,
        //    so resolving from the target's module list (the version/architecture-correct source) only
        //    works once the app is up. Injecting also needs the XAML tree live so diagnostics can bind.
        if (!await WaitForUiReadyAsync(target, cancellationToken))
        {
            logger.LogDebug(
                "DevTools: target {Pid} showed no main window within {Timeout}s; continuing anyway.",
                targetPid, (int)UiReadyTimeout.TotalSeconds);
        }

        // 3. Locate the framework UDK that exports InitializeXamlDiagnosticsEx (from the target's own
        //    loaded modules where possible — see FrameworkUdkLocator).
        string? udkPath;
        try
        {
            udkPath = FrameworkUdkLocator.Resolve(target, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            logger.LogError(ex, "Could not locate the target's Windows App Runtime.");
            return DevToolsConnection.Fail($"Could not locate the target's Windows App Runtime: {ex.Message}");
        }
        if (udkPath is null)
        {
            return DevToolsConnection.Fail(
                "Could not locate Microsoft.Internal.FrameworkUdk.dll (Windows App Runtime). " +
                $"Set {FrameworkUdkLocator.OverrideEnvironmentVariable} to its full path.");
        }

        // 4. Inject (pure Win32, MTA thread, no Windows App SDK bootstrap), retrying the cold-start race:
        //    on a fresh launch the app's window handle can appear before its XAML visual tree exists, so the
        //    first InitializeXamlDiagnosticsEx returns ERROR_NOT_FOUND. Retry that — and only that — while the
        //    target is alive, so the first --devtools on a cold app succeeds where today it fails on attempt one.
        InjectionOutcome outcome;
        try
        {
            var initializationData = BuildInitializationData(
                requestedAccess, Environment.GetEnvironmentVariable(MutationPolicyEnvironmentVariable),
                guestComments, stagedTapPath: agentPath);
            outcome = await DevToolsInjectionRetry.RunAsync(
                attemptInject: () => XamlDiagnosticsInjector.Inject(
                    targetPid,
                    agentPath,
                    udkPath,
                    initializationData),
                isTargetAlive: () => !HasExited(target),
                maxAttempts: InjectMaxAttempts,
                retryDelay: InjectRetryInterval,
                delayAsync: Task.Delay,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DevToolsConnection.Fail($"DevTools injection failed: {ex.Message}");
        }

        if (outcome.TargetExited)
        {
            return DevToolsConnection.Fail(
                $"Target process {targetPid} exited before the DevTools agent could attach " +
                "(the app closed or crashed on startup).");
        }

        if (outcome.Hr != 0)
        {
            if (DevToolsInjectionRetry.IsTreeNotReady(outcome.Hr))
            {
                var budgetSeconds = (int)((InjectMaxAttempts - 1) * InjectRetryInterval.TotalSeconds);
                return DevToolsConnection.Fail(
                    $"DevTools could not attach: the app's XAML visual tree did not appear within " +
                    $"{budgetSeconds}s (InitializeXamlDiagnosticsEx hr=0x{outcome.Hr:X8}). " +
                    $"Is process {targetPid} a WinUI 3 app?");
            }

            return DevToolsConnection.Fail($"InitializeXamlDiagnosticsEx failed (hr=0x{outcome.Hr:X8}).");
        }

        // Injection bound. If it took more than one attempt we lost the cold-start race and the retry
        // recovered it — surface how much of the budget that consumed at Information level (the warm,
        // single-attempt path stays quiet). This is the number that says whether the budget is generous
        // or tight: a success on attempt 18 of 21 is a pass on this machine but a fail waiting for a
        // slower one, so it is worth seeing without --verbose.
        if (outcome.Attempts > 1)
        {
            logger.LogInformation(
                "DevTools injection succeeded on attempt {Attempts} of {MaxAttempts} (recovered the cold-start race).",
                outcome.Attempts, InjectMaxAttempts);
        }

        return CompleteConnection(target, targetPid, showOverlay, cancellationToken);
    }

    private DevToolsConnection CompleteConnection(Process target, uint targetPid, bool showOverlay, CancellationToken cancellationToken)
    {
        var tap = new VisualTreeTap(targetPid);
        if (!tap.WaitReady(PipeReadyTimeoutMs, cancellationToken))
        {
            return DevToolsConnection.Fail(
                $"Injected into {targetPid}, but the DevTools agent pipe did not come up within " +
                $"{PipeReadyTimeoutMs / 1000}s.");
        }

        var hello = tap.Hello(cancellationToken: cancellationToken);
        if (hello.Error is { } error)
        {
            // A peer's message can contain arbitrary data. Preserve its code and known category, not its payload.
            var category = error.Token switch
            {
                "no-response" or "unauthorized" or "parse-error" or "internal" or "bad-args" or
                "no-tree" or "not-found" or "refused-unsafe" => error.Token,
                _ => "protocol-error",
            };
            return DevToolsConnection.Fail($"DevTools negotiation failed ({category}, code {error.Code}).");
        }
        using var capabilities = hello.TryParseResult();
        if (capabilities?.RootElement.ValueKind != JsonValueKind.Object)
        {
            return DevToolsConnection.Fail("DevTools negotiation returned invalid capabilities; expected an object.");
        }
        if (guestComments?.Process is not null &&
            capabilities.RootElement.TryGetProperty("guestInitializationError", out var initializationError) &&
            initializationError.ValueKind != JsonValueKind.Null)
        {
            return DevToolsConnection.Fail("The guest inspector could not establish its bundled comment writer. Relaunch using a complete matching CLI and engine bundle.");
        }
        var agentComments = capabilities.RootElement.TryGetProperty("guestComments", out var metadata) ? metadata : default;
        if (guestComments?.Process is not null && guestComments.AgentMismatch(agentComments) is { } mismatch)
        {
            return DevToolsConnection.Fail($"The injected guest inspector does not prove this host comment binding: {mismatch}. Restart the app through its host-owned launch.");
        }
        var hasGuestComments = agentComments.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);

        var nodeCount = tap.TryGetNodeCount(cancellationToken);
        if (nodeCount is null || nodeCount < 0)
        {
            // The agent is loaded and answering, but its AdviseVisualTreeChange subscription did not
            // bind, so there is no live tree. Surface that instead of reporting a misleading 0 nodes.
            logger.LogDebug("DevTools attached to {Pid}, but the visual tree service did not bind.", targetPid);
            return DevToolsConnection.Fail(tap.LastConnectionError ??
                "The agent did not report a valid visual tree count. No live tree is available.");
        }

        // 6. Interactive --devtools only: ask the agent to build the in-app overlay now. The headless
        //    --json path passes showOverlay=false, so no overlay elements are ever created there (the
        //    oracle stays deterministic by construction, not merely by self-exclusion holding). Best-effort:
        //    a failed overlay must not fail the DevTools connection itself.
        var overlayShown = false;
        string? overlayError = null;
        if (showOverlay)
        {
            overlayShown = tap.ShowOverlay(cancellationToken);
            if (!overlayShown)
            {
                overlayError = tap.LastConnectionError ?? "The agent did not confirm the requested overlay.";
                logger.LogDebug("DevTools connected to {Pid}, but the overlay did not open: {Error}", targetPid, overlayError);
            }
        }

        // 7. Interactive only: hand the app the comments already in its store, so the toolbar's count and its
        //    markers describe THIS APP's comments rather than only the ones typed since this attach. The
        //    overlay places what it can by anchor; comments whose element isn't realized right now still count.
        //    Best-effort — an app with no store, or one whose comments no longer anchor, is not an error.
        if (showOverlay && (!hasGuestComments || guestComments is { Process: not null, HostCommentsUnavailable: false }))
        {
            var pushed = commentPusher.Push(targetPid, CommentStoreRootFor(target), cancellationToken: cancellationToken);
            if (pushed is { } p)
            {
                logger.LogDebug(
                    "DevTools pushed {Total} open comment(s) to {Pid}; {Placed} landed on elements realized right now.",
                    p.Total, targetPid, p.Placed);
            }
        }

        logger.LogDebug("DevTools connected to {Pid}: {Count} nodes.", targetPid, nodeCount);
        return DevToolsConnection.Ok(nodeCount.Value, overlayShown, overlayError);
    }

    internal static string BuildInitializationData(DevToolsAccess requestedAccess, string? mutationPolicy,
        ExecutionTargets.GuestAgent.GuestCommentContext? guestComments = null,
        string? cliPath = null, string? stagedTapPath = null)
    {
        cliPath ??= DevToolsArtifacts.CliExecutablePath;
        stagedTapPath ??= DevToolsArtifacts.TapPath;
        var effectiveAccess = ApplyMutationFloor(requestedAccess, mutationPolicy);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("posture", effectiveAccess.ToProtocolValue());
            if (guestComments?.Process is { } process)
            {
                writer.WriteString("guestCommentStart", process.StartTicksUtc.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (guestComments.Session is { } session)
                {
                    writer.WriteString("guestCommentBinding", session.BindingId);
                    writer.WriteString("guestCommentEpoch", session.Epoch);
                }
                if (string.IsNullOrEmpty(cliPath) || !File.Exists(cliPath) ||
                    !string.Equals(Path.GetFileName(cliPath), "winapp.exe", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFullPath(stagedTapPath), Path.GetFullPath(DevToolsArtifacts.TapPath), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetDirectoryName(Path.GetFullPath(cliPath)),
                        Path.GetDirectoryName(Path.GetFullPath(stagedTapPath)), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Guest DevTools requires the running CLI and native agent in the same complete bundle.");
                }
                writer.WriteBoolean("cliSibling", true);
            }
            else if (!string.IsNullOrEmpty(cliPath) && File.Exists(cliPath))
            {
                writer.WriteString("cliExe", cliPath);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static DevToolsAccess ApplyMutationFloor(DevToolsAccess requestedAccess, string? policy) =>
        requestedAccess == DevToolsAccess.Mutation &&
        string.Equals(policy, "deny", StringComparison.OrdinalIgnoreCase)
            ? DevToolsAccess.Ui
            : requestedAccess;

    /// <summary>
    /// Where to look for the comment store when the app itself can't say (it wasn't launched by
    /// <c>winapp run</c>, so it carries no <c>WINAPP_DEVTOOLS_SOURCE_ROOT</c>): the app's own executable
    /// directory. The store lookup walks UP from here, so a build output under <c>&lt;project&gt;\bin\...</c>
    /// finds <c>&lt;project&gt;\.winapp</c> — which is the store the developer means, and not necessarily the
    /// one under whatever directory winapp happens to have been invoked from. Falls back to that cwd when the
    /// app's path can't be read (a protected process, or one that exited under us).
    /// </summary>
    private static string CommentStoreRootFor(Process target)
    {
        try
        {
            var exe = target.MainModule?.FileName;
            var dir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                return dir;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Fall through to the cwd — an unreadable module list is not a reason to skip the push.
        }

        return Environment.CurrentDirectory;
    }

    // Liveness probe for the injection retry: Refresh() then HasExited, treating a process we can no longer
    // query (exited/raced away) as not alive so the retry stops rather than injecting into a dead target.
    private static bool HasExited(Process target)
    {
        try
        {
            target.Refresh();
            return target.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static async Task<bool> WaitForUiReadyAsync(Process target, CancellationToken cancellationToken)
    {
        var until = Environment.TickCount64 + (long)UiReadyTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < until)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (target.HasExited)
                {
                    return false;
                }

                target.Refresh();
                if (target.MainWindowHandle != nint.Zero)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }
}
