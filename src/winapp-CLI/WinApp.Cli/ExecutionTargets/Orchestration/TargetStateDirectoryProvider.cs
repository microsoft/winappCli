// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Resolves the on-disk state root for one execution target.</summary>
internal interface ITargetStateDirectoryProvider
{
    /// <summary>
    /// Returns the state root for <paramref name="target"/>, creating it when
    /// <paramref name="create"/> is true.
    /// </summary>
    DirectoryInfo GetTargetRoot(ExecutionTargetRef target, bool create = true);
}

/// <summary>
/// Default provider rooted at the physical equivalent of
/// <c>%LOCALAPPDATA%\Microsoft\WinApp\Targets</c> (spec §"Host coordination and state").
/// </summary>
/// <remarks>
/// This deliberately differs from the repository's usual <c>%USERPROFILE%\.winapp</c> cache root:
/// the spec pins this location, and giving each target its own state root is what allows future
/// targets to mutate concurrently without sharing a lock or a state file.
/// <para>
/// The root can be redirected two ways. Tests pass <paramref name="rootOverride"/> directly, which
/// keeps them isolated under the assembly's method-level parallelism; CI and end-to-end runs set
/// <c>WINAPP_TARGET_STATE_ROOT</c>, which redirects the whole process. Neither path ever touches
/// real user state by accident.
/// </para>
/// </remarks>
/// <param name="rootOverride">
/// Explicit targets root. When null the environment variable, then <c>%LOCALAPPDATA%</c>, is used.
/// </param>
internal sealed class TargetStateDirectoryProvider(string? rootOverride = null) : ITargetStateDirectoryProvider
{
    /// <summary>Environment override for the state root.</summary>
    internal const string RootOverrideVariable = "WINAPP_TARGET_STATE_ROOT";

    /// <summary>
    /// Resolves the physical packaged-app equivalent of <c>%LOCALAPPDATA%</c>, or null when the
    /// process has no package identity.
    /// </summary>
    /// <remarks>
    /// A full-trust packaged process sees the ordinary LocalAppData path, but writes beneath it are
    /// redirected to <c>LocalCache\Local</c>. Passing the logical path to an out-of-package broker
    /// such as <c>wsb.exe</c> therefore points it at a directory that does not exist. Using the
    /// physical path keeps state shared with earlier packaged builds while making mapped folders
    /// visible across the process boundary.
    /// </remarks>
    internal Func<string?> PackagedLocalAppDataProvider { get; set; } = ResolvePackagedLocalAppData;

    /// <summary>Unpackaged LocalAppData lookup, exposed as a test seam.</summary>
    internal Func<string> LocalAppDataProvider { get; set; } =
        () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <inheritdoc/>
    public DirectoryInfo GetTargetRoot(ExecutionTargetRef target, bool create = true)
    {
        ArgumentNullException.ThrowIfNull(target);

        var root = TargetPathSafety.CombineInsideRoot(GetTargetsRoot(), target.StateKey);
        var directory = new DirectoryInfo(root);
        if (create && !directory.Exists)
        {
            directory.Create();
            directory.Refresh();
        }

        return directory;
    }

    private string GetTargetsRoot()
    {
        if (!string.IsNullOrWhiteSpace(rootOverride))
        {
            return rootOverride;
        }

        var environmentRoot = Environment.GetEnvironmentVariable(RootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            return environmentRoot;
        }

        var localAppData = PackagedLocalAppDataProvider() ?? LocalAppDataProvider();
        return TargetPathSafety.CombineInsideRoot(localAppData, "Microsoft", "WinApp", "Targets");
    }

    private static string? ResolvePackagedLocalAppData()
    {
        try
        {
            var localCache = Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path;
            return string.IsNullOrWhiteSpace(localCache)
                ? null
                : TargetPathSafety.CombineInsideRoot(localCache, "Local");
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.Runtime.InteropServices.COMException ex)
            when (ex.HResult == unchecked((int)0x80073D54)) // APPMODEL_ERROR_NO_PACKAGE
        {
            return null;
        }
    }
}
