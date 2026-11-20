// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools;

internal static class DevToolsArtifacts
{
    public static string TapPath => Path.Combine(AppContext.BaseDirectory, "WinApp.DevTools.Native.dll");
    public static string BindingHostPath => Path.Combine(AppContext.BaseDirectory, "WinApp.DevTools.Managed.dll");

    public static string? CliExecutablePath
    {
        get
        {
            var process = Environment.ProcessPath;
            if (string.Equals(Path.GetFileName(process), "winapp.exe", StringComparison.OrdinalIgnoreCase))
            {
                return process;
            }

            var appHost = Path.Combine(AppContext.BaseDirectory, "winapp.exe");
            return File.Exists(appHost) ? appHost : null;
        }
    }

    public static string StageTap() => EngineStaging.StageForForeignLoad(TapPath);

    public static IReadOnlyDictionary<string, string?> CreateLaunchEnvironment(string? sourceRoot, bool managed)
    {
        _ = StageTap();
        var hook = managed ? EngineStaging.StageForForeignLoad(BindingHostPath) : null;
        return ComposeLaunchEnvironment(
            sourceRoot, hook, BindingHostPath,
            Environment.GetEnvironmentVariable(DevToolsStartupHooks.EnvVarName), Environment.CurrentDirectory,
            Environment.GetEnvironmentVariable(ReactorSourceMapVariable),
            Environment.GetEnvironmentVariable(ReactorDiagnosticsVariable));
    }

    // Microsoft.UI.Reactor records each element's C# call site only when this is exactly "1"; apps that do not
    // use Reactor ignore it.
    internal const string ReactorSourceMapVariable = "REACTOR_SOURCEMAP";

    // Microsoft.UI.Reactor builds with diagnostics (Debug, or -p:ReactorDiagnostics=true) publish each control's
    // component, call site and C# name for DevTools to read without loading managed code, only when this is "1".
    internal const string ReactorDiagnosticsVariable = "REACTOR_DIAGNOSTICS";

    internal static IReadOnlyDictionary<string, string?> ComposeLaunchEnvironment(
        string? sourceRoot, string? hook, string ownedSourceHook, string? inheritedHooks, string? launchDirectory = null, string? inheritedReactorSourceMap = null,
        string? inheritedReactorDiagnostics = null)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO"] = "1",
            ["WINAPP_WATCH_PID"] = null,
            ["WINAPP_DEVTOOLS_WATCH_RUN"] = null,
            ["WINAPP_DEVTOOLS_SOURCE_INVENTORY"] = null,
            ["WINAPP_DEVTOOLS_SOURCE_INVENTORY_HASH"] = null,
            ["WINAPP_DEVTOOLS_SOURCE_PAYLOAD_HELD"] = null,
            ["WINAPP_DEVTOOLS_COMMENT_ROOT"] = null,
        };
        if (string.IsNullOrEmpty(inheritedReactorSourceMap))
        {
            environment[ReactorSourceMapVariable] = "1";
        }
        if (string.IsNullOrEmpty(inheritedReactorDiagnostics))
        {
            environment[ReactorDiagnosticsVariable] = "1";
        }
        if (!string.IsNullOrEmpty(sourceRoot))
        {
            environment["WINAPP_DEVTOOLS_SOURCE_ROOT"] = Path.GetFullPath(sourceRoot);
        }
        else if (!string.IsNullOrEmpty(launchDirectory))
        {
            // No project to link comments to: keep them where the developer ran winapp, not in the build output.
            environment["WINAPP_DEVTOOLS_COMMENT_ROOT"] = Path.GetFullPath(launchDirectory);
        }
        if (hook is not null)
        {
            environment[DevToolsStartupHooks.EnvVarName] =
                DevToolsStartupHooks.Compose(inheritedHooks, hook, ownedSourceHook);
        }
        return environment;
    }
}
