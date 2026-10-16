// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools;

internal static class DevToolsStartupHooks
{
    public const string EnvVarName = "DOTNET_STARTUP_HOOKS";

    public static string Compose(string? existingHooks, string agentHook, string? ownedSourceHook = null)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(agentHook) };
        if (ownedSourceHook is not null)
        {
            owned.Add(Path.GetFullPath(ownedSourceHook));
        }
        var kept = (existingHooks ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(hook => !Path.IsPathFullyQualified(hook) || !owned.Contains(Path.GetFullPath(hook)));
        return string.Join(Path.PathSeparator, kept.Append(agentHook));
    }
}
