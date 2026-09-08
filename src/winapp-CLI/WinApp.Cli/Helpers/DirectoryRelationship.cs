// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Directory-relationship checks used when deciding whether a selected configuration directory is part of the
/// <c>nuget.config</c> hierarchy that the .NET SDK will discover for a project. The SDK always walks up from
/// the project, so a config directory elsewhere on disk is invisible to it no matter what winapp was told.
/// </summary>
internal static class DirectoryRelationship
{
    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="directory"/> itself or one of its ancestors.
    /// </summary>
    internal static bool IsSameOrAncestor(DirectoryInfo candidate, DirectoryInfo directory)
    {
        var candidatePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.FullName));

        for (var current = directory; current is not null; current = current.Parent)
        {
            var currentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.FullName));
            if (string.Equals(candidatePath, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
