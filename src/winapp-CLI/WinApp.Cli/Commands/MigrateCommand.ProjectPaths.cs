// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal partial class MigrateCommand
{
    public partial class Handler
    {
        private static bool TryResolveSourceItemPath(
            string sourceRoot,
            MigrationReviewRequiredProjectItem sourceItem,
            out string fullPath,
            out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;
            if (!MigrationPathResolver.TryResolveContainedRelativePath(
                    sourceRoot,
                    sourceItem.SourceProject,
                    out var sourceProject,
                    out _,
                    out error))
            {
                error = $"Source project path is invalid: {error}";
                return false;
            }

            var include = sourceItem.Include.Trim();
            if (include.Contains(';')
                || include.Contains("$(", StringComparison.Ordinal)
                || include.Contains("@(", StringComparison.Ordinal)
                || include.Contains("%(", StringComparison.Ordinal)
                || include.IndexOfAny(['*', '?']) >= 0)
            {
                error =
                    $"Source item expression '{sourceItem.Include}' does not resolve to one literal file.";
                return false;
            }

            var projectDirectory = Path.GetDirectoryName(sourceProject)!;
            var candidate = Path.IsPathRooted(include)
                ? include
                : Path.Combine(projectDirectory, include);
            if (!MigrationPathResolver.TryCanonicalizeRoot(
                    candidate,
                    out fullPath,
                    out error))
            {
                error = $"Source content path is invalid: {error}";
                return false;
            }

            if (!File.Exists(fullPath))
            {
                error = $"Source content is unavailable: {sourceItem.Include}.";
                return false;
            }
            return true;
        }
    }
}
