// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Turns a managed root and scope into the absolute guest path a guest command can be given.
/// </summary>
/// <remarks>
/// File transfer never needs this: the host names a root and a relative path, and the guest resolves
/// both. Launching does, because the deployed folder has to appear in an argument the guest's own
/// winapp parses. The root therefore comes from what the guest reported about itself rather than
/// from a constant on the host — a constant would put a Windows Sandbox path in target-neutral
/// orchestration and would silently produce a wrong path for any other backend.
/// </remarks>
internal static class GuestPaths
{
    /// <summary>Scope suffix for the guest-side registration layout of a deployment.</summary>
    /// <remarks>
    /// A sibling of the payload scope, never a folder inside it. Nested, it would be enumerated by
    /// the next reconciliation, found absent from the host's desired state, and deleted — so every
    /// rerun would silently destroy the layout the previous run registered from.
    /// </remarks>
    internal const string LayoutScopeSuffix = "-layout";

    /// <summary>The guest folder a deployment's payload is reconciled into.</summary>
    public static GuestPathScope PayloadScope(string deploymentId) =>
        new(GuestRootNames.Deployment, deploymentId);

    /// <summary>The guest folder the guest registers the package from.</summary>
    public static GuestPathScope LayoutScope(string deploymentId) =>
        new(GuestRootNames.Deployment, deploymentId + LayoutScopeSuffix);

    /// <summary>
    /// Resolves <paramref name="scope"/> to an absolute guest path.
    /// </summary>
    /// <exception cref="ExecutionTargetException">
    /// The guest did not report a managed root, so no path can be named.
    /// </exception>
    public static string Resolve(ExecutionTargetCapabilities capabilities, GuestPathScope scope)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrWhiteSpace(capabilities.ManagedRoot))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.AgentIncompatible,
                "The guest agent did not report where it stores deployed applications.",
                userAction: "Update winapp on this machine, then retry so the guest agent is replaced.",
                nextCommand: new ExecutionTargetNextCommand { Command = "winapp update", Advisory = false });
        }

        var root = TargetPathSafety.CombineInsideRoot(capabilities.ManagedRoot, GuestRootNames.FolderFor(scope.Root));

        return scope.Scope is null
            ? root
            : TargetPathSafety.CombineInsideRoot(root, scope.Scope);
    }
}
