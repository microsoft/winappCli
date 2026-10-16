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

    /// <summary>
    /// Whether the app's runtimeconfig turns startup hooks off. Trimmed builds (PublishTrimmed, which WinUI and Reactor
    /// templates set for Release) do, so the managed agent never loads even though the launch requested it.
    /// </summary>
    public static bool DisabledByRuntimeConfig(string? runtimeConfigPath)
    {
        if (runtimeConfigPath is null || !File.Exists(runtimeConfigPath))
        {
            return false;
        }
        try
        {
            using var stream = File.OpenRead(runtimeConfigPath);
            using var doc = System.Text.Json.JsonDocument.Parse(stream, new System.Text.Json.JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            });
            return doc.RootElement.TryGetProperty("runtimeOptions", out var options) &&
                options.TryGetProperty("configProperties", out var properties) &&
                properties.TryGetProperty("System.StartupHookProvider.IsSupported", out var supported) &&
                supported.ValueKind == System.Text.Json.JsonValueKind.False;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public const string DisabledWarning =
        "This build turns off .NET startup hooks (System.StartupHookProvider.IsSupported=false, usually from PublishTrimmed), " +
        "so binding diagnosis and the Reactor component tree are unavailable. Run a Debug build, or set " +
        "<StartupHookSupport>true</StartupHookSupport> for this configuration.";
}
