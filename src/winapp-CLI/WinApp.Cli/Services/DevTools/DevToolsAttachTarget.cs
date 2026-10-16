// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// Whether a running process is a WinUI 3 app DevTools can attach to. A WinUI 2 app and the Windows shell load a
/// <c>Microsoft.UI.Xaml.dll</c> too, so the file name alone is not enough.
/// </summary>
internal static class DevToolsAttachTarget
{
    private static readonly string[] WindowsAppSdkModules = ["Microsoft.WindowsAppRuntime.dll", "Microsoft.Internal.FrameworkUdk.dll"];

    /// <summary>Whether `devtools list --include-available` offers the process. Unreadable processes are not offered.</summary>
    public static bool IsCandidate(Process process) =>
        TryRead(process, out var path, out var modules) &&
        WhyNot(process.Id, process.ProcessName, path, modules, WindowsDirectory) is null;

    /// <summary>Why the process can't be attached to, or null when it can (or when its modules can't be read).</summary>
    public static string? WhyNot(Process process) =>
        TryRead(process, out var path, out var modules)
            ? WhyNot(process.Id, process.ProcessName, path, modules, WindowsDirectory)
            : null;   // protected or cross-architecture: the attach itself reports what failed

    private static string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static bool TryRead(Process process, out string? path, out string[] modules)
    {
        try
        {
            path = process.MainModule?.FileName;
            modules = process.Modules.Cast<ProcessModule>().Select(module => module.ModuleName).ToArray();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            path = null;
            modules = [];
            return false;
        }
    }

    internal static string? WhyNot(int pid, string name, string? executablePath, IReadOnlyCollection<string> modules, string windowsDirectory)
    {
        var who = $"{name} (PID {pid})";
        if (executablePath is not null && windowsDirectory.Length > 0 &&
            executablePath.StartsWith(Path.TrimEndingDirectorySeparator(windowsDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return $"{who} is part of Windows. DevTools attaches only to WinUI 3 apps.";
        }
        if (!modules.Contains("Microsoft.UI.Xaml.dll", StringComparer.OrdinalIgnoreCase))
        {
            return $"{who} has not loaded WinUI 3 (Microsoft.UI.Xaml.dll). DevTools attaches only to WinUI 3 apps.";
        }
        if (!modules.Any(module => WindowsAppSdkModules.Contains(module, StringComparer.OrdinalIgnoreCase)))
        {
            return $"{who} uses WinUI 2, not WinUI 3 from the Windows App SDK. DevTools attaches only to WinUI 3 apps.";
        }
        return null;
    }
}
