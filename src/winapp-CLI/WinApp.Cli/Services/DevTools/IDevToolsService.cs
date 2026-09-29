// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools;

internal interface IDevToolsService
{
    /// <summary>
    /// Injects the agent into <paramref name="targetPid"/>, waits for its pipe to come up, and
    /// reads the live node count. When <paramref name="showOverlay"/> is <c>true</c> (the interactive
    /// <c>--devtools</c> path) it also asks the agent to build the in-app overlay; the headless
    /// <c>--json</c> path passes <c>false</c> so no overlay is ever created there. The in-app Inspect
    /// button opens the in-process inspector window (a second WinUI window on the target's own UI thread,
    /// shipping no extra process). Never throws — failures are returned as <see cref="DevToolsConnection"/>
    /// with <see cref="DevToolsConnection.Connected"/> == <c>false</c>.
    /// </summary>
    Task<DevToolsConnection> ConnectAsync(
        uint targetPid,
        bool showOverlay,
        DevToolsAccess requestedAccess,
        CancellationToken cancellationToken);
}

internal enum DevToolsAccess
{
    Read,
    Ui,
    Mutation,
}

internal static class DevToolsAccessExtensions
{
    public static string ToProtocolValue(this DevToolsAccess access) => access switch
    {
        DevToolsAccess.Read => "read",
        DevToolsAccess.Ui => "ui",
        DevToolsAccess.Mutation => "mutation",
        _ => throw new ArgumentOutOfRangeException(nameof(access)),
    };
}

internal sealed record DevToolsConnection(bool Connected, int NodeCount, string? Error, bool OverlayShown = false,
    string? OverlayError = null)
{
    public static DevToolsConnection Ok(int nodeCount, bool overlayShown = false, string? overlayError = null) =>
        new(true, nodeCount, null, overlayShown, overlayError);

    public static DevToolsConnection Fail(string error) => new(false, 0, error);
}
