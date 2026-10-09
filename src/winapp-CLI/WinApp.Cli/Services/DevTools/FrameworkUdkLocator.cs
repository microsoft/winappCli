// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// Locates <c>Microsoft.Internal.FrameworkUdk.dll</c> — the Windows App SDK framework module that
/// exports <c>InitializeXamlDiagnosticsEx</c>.
///
/// <para><c>%ProgramFiles%\WindowsApps</c> can't be enumerated by a normal user (its ACL hides the
/// listing), so discovery does <b>not</b> walk that folder. Instead it reads the target WinUI
/// process's own loaded modules — a running WinUI app has already loaded its framework UDK — and
/// supports an explicit override. Paths can be opened even when the parent cannot be listed.</para>
///
/// <para>The module read is retried briefly and derives the UDK directory from <em>any</em> loaded
/// <c>Microsoft.WindowsAppRuntime.*</c> module, so it stays correct across runtime versions and
/// architectures (rather than pinning a single hard-coded version) and tolerates being called before
/// the target has finished loading its modules.</para>
/// </summary>
internal static class FrameworkUdkLocator
{
    private const string UdkFileName = "Microsoft.Internal.FrameworkUdk.dll";
    private const string RuntimePackagePrefix = "Microsoft.WindowsAppRuntime.";

    public const string OverrideEnvironmentVariable = "WINAPP_DEVTOOLS_FRAMEWORKUDK";

    // The target may not have loaded its framework modules the instant we look, so the module probe is
    // retried for a short budget. Callers normally wait for the app's window first, in which case the
    // first attempt succeeds; this is defensive against a transiently unreadable module list.
    private static readonly TimeSpan ModuleProbeBudget = TimeSpan.FromSeconds(2);
    private const int ModuleProbeIntervalMs = 200;

    public static string? Resolve(Process target, CancellationToken cancellationToken = default)
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return FrameworkUdkSnapshot.ValidateSourcePath(overridePath);
        }

        return FindInProcessModulesWithRetry(target, cancellationToken);
    }

    private static string? FindInProcessModulesWithRetry(Process target, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)ModuleProbeBudget.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (target.HasExited)
            {
                return null;
            }

            var found = FindInProcessModules(target);
            if (found is not null)
            {
                return found;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return null;
            }

            if (cancellationToken.WaitHandle.WaitOne(ModuleProbeIntervalMs))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            target.Refresh();
        }
    }

    private static string? FindInProcessModules(Process target)
        => FindInModules(target.Modules.Cast<ProcessModule>().Select(module => (module.ModuleName, module.FileName)));

    internal static string? FindInModules(IEnumerable<(string Name, string Path)> modules)
    {
        var snapshot = modules.ToArray();
        // Primary: the UDK is itself loaded into the target — return its exact path.
        foreach (var module in snapshot)
        {
            if (string.Equals(module.Name, UdkFileName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(module.Path))
            {
                return FrameworkUdkSnapshot.ValidateSourcePath(module.Path);
            }
        }

        // Fallback: derive the UDK from any sibling Windows App Runtime module. The UDK sits in the
        // same framework-package folder as the runtime's other DLLs, so this resolves the correct
        // installed version/architecture even if the UDK itself isn't in the module list yet — and
        // without needing to enumerate the ACL-locked WindowsApps directory.
        foreach (var module in snapshot)
        {
            var fileName = module.Path;
            if (string.IsNullOrEmpty(fileName))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(fileName);
            if (directory is null)
            {
                continue;
            }

            var packageFolder = Path.GetFileName(directory);
            if (packageFolder.StartsWith(RuntimePackagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(directory, UdkFileName);
                var local = FrameworkUdkSnapshot.ValidateSourcePath(candidate);
                if (File.Exists(local))
                {
                    return local;
                }
            }
        }
        return null;
    }
}
