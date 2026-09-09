// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>The target reference the Windows Sandbox provider serves.</summary>
/// <remarks>
/// Windows permits exactly one Sandbox at a time, so this provider has exactly one target and
/// <c>--on sandbox</c> resolves to it. The constant lives here, inside the provider, rather than on
/// <see cref="ExecutionTargetRef"/>: shared orchestration must use the reference carried by the
/// target it prepared, because a hard-coded one would silently keep pointing at Sandbox after a
/// second provider existed.
/// </remarks>
internal static class WindowsSandboxTarget
{
    /// <summary>The single managed Windows Sandbox.</summary>
    public static ExecutionTargetRef Default { get; } =
        new(ExecutionTargetRef.SandboxKind, ExecutionTargetRef.DefaultId);
}
