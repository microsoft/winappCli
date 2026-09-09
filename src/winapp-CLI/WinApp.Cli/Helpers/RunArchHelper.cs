// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Pure mapping between a target architecture and a .NET runtime identifier (RID). Project mode passes
/// the RID on the build command line (never written to the csproj); project-specific build inputs such as
/// <c>Platform</c> and <c>PublishProfile</c> are resolved separately by <c>ProjectRunService</c>.
/// </summary>
internal static class RunArchHelper
{
    /// <summary>The architectures winapp accepts for <c>--arch</c>.</summary>
    public static readonly IReadOnlyList<string> SupportedArchitectures = ["x64", "arm64", "x86"];

    /// <summary>The current process architecture (canonicalized), falling back to <c>x64</c>.</summary>
    public static string DefaultArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    /// <summary>
    /// Normalizes user input (<c>--arch</c> value or the arch segment of a RID) to canonical
    /// <c>x64</c> / <c>arm64</c> / <c>x86</c>.
    /// </summary>
    /// <returns>The canonical arch, or <c>null</c> when <paramref name="value"/> is not recognized.</returns>
    public static string? NormalizeArchitecture(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "x64" or "amd64" or "x86-64" or "x86_64" => "x64",
            "arm64" or "aarch64" => "arm64",
            "x86" or "win32" or "ia32" => "x86",
            _ => null,
        };
    }

    /// <summary>Maps a canonical arch to its .NET RID (e.g. <c>x64</c> → <c>win-x64</c>).</summary>
    public static string ToRuntimeIdentifier(string architecture) => $"win-{architecture}";

    /// <summary>
    /// Extracts the canonical arch from a Windows RID (<c>win-x64</c>, <c>win10-arm64</c>) or a bare
    /// arch (<c>x64</c>). A non-Windows RID (<c>linux-x64</c>, <c>osx-arm64</c>) is rejected rather than
    /// silently reduced to a Windows target the user never asked for.
    /// </summary>
    /// <returns>The canonical arch, or <c>null</c> when unrecognized or carrying a non-Windows OS prefix.</returns>
    public static string? ArchitectureFromRid(string? rid)
    {
        if (string.IsNullOrWhiteSpace(rid))
        {
            return null;
        }

        var trimmed = rid.Trim();
        var dash = trimmed.LastIndexOf('-');
        var arch = NormalizeArchitecture(dash >= 0 ? trimmed[(dash + 1)..] : trimmed);
        if (arch == null)
        {
            return null;
        }

        if (dash >= 0 && !IsWindowsOsToken(trimmed[..dash]))
        {
            return null;
        }

        return arch;
    }

    // Accepts win, win10, win10.0.19041 (RID version qualifiers); rejects windows/winter/winrt so an
    // unrelated OS token that merely starts with "win" isn't silently treated as a Windows target.
    private static bool IsWindowsOsToken(string os)
    {
        if (!os.StartsWith("win", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int i = 3; i < os.Length; i++)
        {
            if (!char.IsDigit(os[i]) && os[i] != '.')
            {
                return false;
            }
        }

        return true;
    }
}
