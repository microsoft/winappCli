// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// Carries a run's DevTools mode, its source and what DevTools did out to command-completion telemetry. Same
/// pattern as <c>UiCoordinationTelemetryScope</c>: a box published before the command runs, filled in during it.
/// </summary>
internal static class DevToolsRunTelemetryScope
{
    internal sealed record Summary(DevToolsMode Mode, DevToolsModeSource Source, DevToolsOutcome? Outcome);

    private static readonly AsyncLocal<StrongBox<Summary?>?> s_current = new();

    public static void Begin() => s_current.Value = new StrongBox<Summary?>(null);

    public static Summary? Current => s_current.Value?.Value;

    public static void Set(DevToolsResolution resolution)
    {
        if (s_current.Value is { } box)
        {
            box.Value = new(resolution.Mode, resolution.Source, null);
        }
    }

    public static void SetOutcome(DevToolsOutcome outcome)
    {
        if (s_current.Value is { Value: { } summary } box)
        {
            box.Value = summary with { Outcome = outcome };
        }
    }

    public static void Clear() => s_current.Value = null;
}
