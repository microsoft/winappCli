// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

internal static class SafeExecutableResolver
{
    public static string? Resolve(
        string executableName,
        string? pathValue,
        IEnumerable<string?> additionalDirectories)
    {
        if (Path.GetFileName(executableName) != executableName
            || !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Executable names must be bare .exe file names.", nameof(executableName));
        }

        foreach (var directory in additionalDirectories
            .Concat((pathValue ?? string.Empty).Split(Path.PathSeparator)))
        {
            var normalizedDirectory = directory?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(normalizedDirectory)
                || !Path.IsPathFullyQualified(normalizedDirectory))
            {
                continue;
            }

            try
            {
                var candidate = Path.GetFullPath(Path.Join(normalizedDirectory, executableName));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (
                ex is ArgumentException or IOException or NotSupportedException)
            {
            }
        }

        return null;
    }
}
