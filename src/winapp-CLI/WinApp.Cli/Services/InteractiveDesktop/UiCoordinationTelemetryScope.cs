// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// Carries the coordination summary for the current command out to command-completion telemetry,
/// without threading a parameter through every handler.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the existing <c>TelemetryCorrelation</c> pattern, but stores a mutable box rather than the
/// value itself. That indirection is required: an <see cref="AsyncLocal{T}"/> assignment made
/// <em>inside</em> an async method is not visible to its caller once the method returns, and the
/// coordinator sets the summary deep inside the invocation while <c>Program</c> reads it afterwards.
/// Publishing the box up front and mutating its contents keeps the value visible to the reader.
/// </para>
/// <para>
/// <see cref="AsyncLocal{T}"/> rather than a static field because the test host runs many commands in
/// one process, and a leaked summary would mislabel an unrelated command.
/// </para>
/// </remarks>
internal static class UiCoordinationTelemetryScope
{
    private static readonly AsyncLocal<StrongBox<UiCoordinationSummary?>?> s_current = new();

    /// <summary>
    /// Opens a scope for one command invocation. Must be called before the command runs, from the same
    /// async flow that will later read <see cref="Current"/>.
    /// </summary>
    public static void Begin() => s_current.Value = new StrongBox<UiCoordinationSummary?>(null);

    /// <summary>The summary for the command in the current scope, or <see langword="null"/>.</summary>
    public static UiCoordinationSummary? Current => s_current.Value?.Value;

    /// <summary>
    /// Records the summary for the current scope. A no-op when no scope was opened — for example a unit
    /// test invoking a handler directly — so coordination never depends on telemetry being wired up.
    /// </summary>
    public static void Set(UiCoordinationSummary summary)
    {
        if (s_current.Value is { } box)
        {
            box.Value = summary;
        }
    }

    /// <summary>Closes the scope. Called by tests and between invocations.</summary>
    public static void Clear() => s_current.Value = null;
}
