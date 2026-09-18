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
    /// <paramref name="create"/> is true. Existing state and its namespace must be private
    /// to this user; neither reads nor writes repair or discard untrusted state.
    /// </summary>
    DirectoryInfo GetTargetRoot(ExecutionTargetRef target, bool create = true);
}

/// <summary>
/// Default provider rooted at <c>%USERPROFILE%\.winapp\state\targets</c>.
/// </summary>
/// <remarks>
/// State is independent of the cache override and package identity so every process managing a
/// target shares its ownership record and locks. Each target has its own subdirectory.
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
            var targetsRoot = GetTargetsRoot();
            var root = TargetPathSafety.CombineInsideRoot(targetsRoot, target.StateKey);
            return TargetStateDirectorySecurity.EnsureTrusted(targetsRoot, root, create);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.StateUnavailable,
                $"The execution-target state directory is unavailable or untrusted: {ex.Message}",
                userAction: "Ensure %USERPROFILE%\\.winapp\\state is a writable local path without junctions or symbolic links, and secure its ownership and permissions against other users. " +
                    "If WINAPP_TARGET_STATE_ROOT is set, use the same private, fully qualified local directory in every winapp process. " +
                    "Do not reuse exposed connection keys: after safely stopping any affected Sandbox, replace its exposed state in a secure directory. Existing state has not been repaired or deleted.",
                innerException: ex);
        }
    }

    private string GetTargetsRoot()
    {
        if (rootOverride is not null)
        {
            return WinappDirectoryService.ValidateStateDirectory(rootOverride);
        }

        var environmentRoot = Environment.GetEnvironmentVariable(RootOverrideVariable);
        if (environmentRoot is not null)
        {
            return WinappDirectoryService.ValidateStateDirectory(environmentRoot);
        }

        return WinappDirectoryService.ValidateStateDirectory(
            TargetPathSafety.CombineInsideRoot(WinappDirectoryService.GetUserStateDirectory(UserProfileProvider()), "targets"));
    }
}
