// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Services;

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
/// Default provider rooted at <c>%USERPROFILE%\.winapp\state\targets</c>.
/// </summary>
/// <remarks>
/// State is independent of the cache override and package identity. Each target has its own
/// directory so separate targets do not share a lock or state file.
/// <para>
/// The root can be redirected two ways. Tests pass <paramref name="rootOverride"/> directly, which
/// keeps them isolated under the assembly's method-level parallelism; CI and end-to-end runs set
/// <c>WINAPP_TARGET_STATE_ROOT</c>, which redirects the whole process. Neither path ever touches
/// real user state by accident.
/// </para>
/// </remarks>
/// <param name="rootOverride">
/// Explicit targets root. When null the environment variable, then the user state root, is used.
/// </param>
internal sealed class TargetStateDirectoryProvider(string? rootOverride = null) : ITargetStateDirectoryProvider
{
    /// <summary>Environment override for the state root.</summary>
    internal const string RootOverrideVariable = "WINAPP_TARGET_STATE_ROOT";

    /// <summary>Profile lookup shared by packaged and unpackaged processes; test seam.</summary>
    internal Func<string> UserProfileProvider { get; set; } =
        () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <inheritdoc/>
    public DirectoryInfo GetTargetRoot(ExecutionTargetRef target, bool create = true)
    {
        ArgumentNullException.ThrowIfNull(target);

        try
        {
            var root = TargetPathSafety.CombineInsideRoot(GetTargetsRoot(), target.StateKey);
            var directory = new DirectoryInfo(root);
            if (create && !directory.Exists)
            {
                directory.Create();
                directory.Refresh();
            }

            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.StateUnavailable,
                $"The execution-target state directory could not be accessed: {ex.Message}",
                userAction: "Ensure %USERPROFILE%\\.winapp\\state is on a writable local drive, or set WINAPP_TARGET_STATE_ROOT to a writable directory.",
                innerException: ex);
        }
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

        return Path.Combine(WinappDirectoryService.GetUserStateDirectory(UserProfileProvider()), "targets");
    }
}
