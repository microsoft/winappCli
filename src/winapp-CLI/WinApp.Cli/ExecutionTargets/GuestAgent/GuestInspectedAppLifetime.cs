// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

internal sealed record GuestInspectedAppFrame(
    string Phase, GuestProcessStart? Process = null, int? NodeCount = null, bool OverlayShown = false, string? Error = null)
{
    internal const int MaximumErrorLength = 2048;
}
internal sealed record GuestInspectedAppControl(string Kind);

internal static class GuestInspectedAppLifetime
{
    internal sealed record OwnedIdentity(
        uint ProcessId, long StartTicksUtc, string ExecutablePath, string? PackageFamilyName, string? ApplicationUserModelId);

    internal static OwnedIdentity CaptureIdentity(ILaunchedProcess app)
    {
        if (app.StartTicksUtc is not long start || start <= 0 || app.ExecutablePath is not { Length: > 0 } path ||
            !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("Guest cleanup cannot verify the retained application's start time and executable.");
        }
        return new(app.ProcessId, start, path, app.PackageFamilyName, app.ApplicationUserModelId);
    }

    internal static async Task<bool> CloseOwnedAsync(
        ILaunchedProcess app, OwnedIdentity identity, TimeSpan? closeTimeout = null)
    {
        if (app.HasExited) { return false; }
        VerifyIdentity();
        if (app.RequestClose())
        {
            using var graceful = new CancellationTokenSource(closeTimeout ?? TimeSpan.FromSeconds(2));
            try { await app.WaitForExitAsync(graceful.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (graceful.IsCancellationRequested)
            {
                // Only the bounded normal-close wait expired; the retained identity is checked again below.
            }
        }
        if (app.HasExited) { return false; }
        VerifyIdentity();
        await Console.Error.WriteLineAsync(
            $"Guest DevTools normal close did not complete for PID {identity.ProcessId}; terminating the retained app process. The enclosing operation job also terminates remaining owned descendants during teardown.").ConfigureAwait(false);
        app.KillProcessOnly();
        using var terminated = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await app.WaitForExitAsync(terminated.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (terminated.IsCancellationRequested)
        {
            throw new IOException("The retained guest application did not confirm exit after root-only termination.");
        }
        return true;

        void VerifyIdentity()
        {
            if (app.ProcessId != identity.ProcessId || app.StartTicksUtc != identity.StartTicksUtc ||
                !string.Equals(app.ExecutablePath, identity.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(app.PackageFamilyName, identity.PackageFamilyName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(app.ApplicationUserModelId, identity.ApplicationUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The retained guest application identity changed; no close or termination was requested.");
            }
        }
    }

    internal static async Task<int> RunAsync(
        ILaunchedProcess app, GuestProcessStart identity, IDevToolsService devTools,
        Stream hostInput, Stream hostOutput, bool showOverlay, CancellationToken cancellationToken,
        Action? prepareComments = null, OwnedIdentity? ownedIdentity = null)
    {
        var owned = ownedIdentity ?? CaptureIdentity(app);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stage = "launch identity";
        string? failure = null;
        Exception? firstFailure = null;
        try
        {
            if (identity.ProcessId != owned.ProcessId || identity.StartTicksUtc != owned.StartTicksUtc)
            {
                throw new InvalidOperationException("Guest launch did not establish the owned application's identity.");
            }
            stage = "host readiness";
            await SendAsync(new("started", identity), stop.Token).ConfigureAwait(false);
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(60));
                var bytes = await GuestCommentFrames.ReadAsync(hostInput, startup.Token).ConfigureAwait(false);
                var control = JsonSerializer.Deserialize(bytes, GuestCommentsJsonContext.Default.GuestInspectedAppControl);
                if (control?.Kind != "owner-ready" || app.HasExited)
                {
                    throw new IOException("The host comment owner was not ready for this live application.");
                }
            }
            stage = "comment binding";
            prepareComments?.Invoke();
            stage = "inspector connection";
            var connection = await devTools.ConnectAsync(app.ProcessId, showOverlay, DevToolsAccess.Mutation, stop.Token).ConfigureAwait(false);
            if (!connection.Connected || (showOverlay && !connection.OverlayShown) || app.HasExited)
            {
                if (connection.Connected && !app.HasExited && showOverlay && !connection.OverlayShown)
                {
                    stage = "overlay startup";
                    failure = $"Protocol attached, but the requested overlay did not open. {connection.OverlayError} " +
                        "The owned guest application will be stopped; use --no-overlay for a headless launch.";
                }
                else
                {
                    failure = connection.Error ?? "The guest application exited before inspection completed.";
                }
                throw new InvalidOperationException(failure);
            }
            stage = "application lifetime";
            await SendAsync(new("ready", identity, connection.NodeCount, connection.OverlayShown), stop.Token).ConfigureAwait(false);
            var exited = app.WaitForExitAsync(stop.Token);
            var controlInput = GuestCommentFrames.ReadAsync(hostInput, stop.Token);
            try
            {
                var completed = await Task.WhenAny(exited, controlInput).ConfigureAwait(false);
                if (completed == exited || app.HasExited)
                {
                    await exited.ConfigureAwait(false);
                    return app.ExitCode;
                }
                _ = await controlInput.ConfigureAwait(false);
                throw new OperationCanceledException("The host stopped its owned guest application.", stop.Token);
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(exited, controlInput).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    // No pending stream reader may outlive this operation.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or JsonException)
        {
            firstFailure = ex;
            await ReportFailureAsync(hostOutput, identity,
                $"Guest DevTools {stage} failed: {failure ?? ex.GetType().Name}.", stop.Token).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            firstFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                await CloseOwnedAsync(app, owned).ConfigureAwait(false);
            }
            catch (Exception ex) when (firstFailure is not null &&
                ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                await Console.Error.WriteLineAsync(
                    $"Guest DevTools cleanup also failed for retained PID {owned.ProcessId} ({ex.GetType().Name}); " +
                    "the original failure is retained. The enclosing operation job still owns the remaining processes.").ConfigureAwait(false);
            }
        }

        Task SendAsync(GuestInspectedAppFrame frame, CancellationToken token) =>
            GuestCommentFrames.WriteAsync(hostOutput, frame, GuestCommentsJsonContext.Default.GuestInspectedAppFrame, token);
    }

    internal static async Task ReportFailureAsync(
        Stream output, GuestProcessStart? identity, string message, CancellationToken cancellationToken)
    {
        if (message.Length > GuestInspectedAppFrame.MaximumErrorLength)
        {
            message = message[..GuestInspectedAppFrame.MaximumErrorLength];
        }
        try
        {
            await GuestCommentFrames.WriteAsync(output, new GuestInspectedAppFrame("failed", identity, Error: message),
                GuestCommentsJsonContext.Default.GuestInspectedAppFrame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Guest DevTools could not deliver its failure frame; the host channel is closed.").ConfigureAwait(false);
        }
    }
}
