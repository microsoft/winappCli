// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Provides banner display functionality for the CLI.
/// ASCII art is pre-computed for optimal startup performance.
/// </summary>
internal static class BannerHelper
{
    // Stylized "winapp" text in block letters
    private static readonly string[] TitleBlockArt =
    {
        @"▄▄              ▀▀                          ",
        @" ▀█▄    ██   ██ ██  ████▄  ▀▀█▄ ████▄ ████▄ ",
        @"  ▄█▀   ██ █ ██ ██  ██ ██ ▄█▀██ ██ ██ ██ ██ ",
        @"▄█▀      ██▀██  ██▄ ██ ██ ▀█▄██ ████▀ ████▀ ",
        @"                                ██    ██    ",
        @"                                ▀▀    ▀▀    ",
    };

    // Simple ASCII fallback for the title
    private static readonly string[] TitleAsciiArt =
    {
        @"           _                          ",
        @" __      _(_)_ __   __ _ _ __  _ __   ",
        @" \ \ /\ / / | '_ \ / _` | '_ \| '_ \  ",
        @"  \ V  V /| | | | | (_| | |_) | |_) | ",
        @"   \_/\_/ |_|_| |_|\__,_| .__/| .__/  ",
        @"                        |_|   |_|     ",
    };

    // ANSI color codes for gradient effect (Blue -> Purple, Windows-themed)
    private static readonly string[] GradientColors =
    {
        "\x1b[38;5;39m",   // Bright Blue
        "\x1b[38;5;33m",   // Blue
        "\x1b[38;5;63m",   // Blue-Purple
        "\x1b[38;5;99m",   // Purple
        "\x1b[38;5;135m",  // Light Purple
        "\x1b[38;5;141m",  // Lavender
    };

    private const string ResetColor = "\x1b[0m";

    private static bool? _useEmoji;
    public static bool UseEmoji => _useEmoji ??= Compute();

    private static bool Compute()
    {
        try
        {
            bool isUtf8 = Console.OutputEncoding?.CodePage == 65001;
            bool isVsCode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VSCODE_PID")) ||
                            string.Equals(Environment.GetEnvironmentVariable("TERM_PROGRAM"), "vscode", StringComparison.OrdinalIgnoreCase);
            bool isWindowsTerminal = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"));
            bool notRedirected = !Console.IsOutputRedirected;
            return isUtf8 && notRedirected && (isVsCode || isWindowsTerminal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Displays the CLI banner with version information.
    /// </summary>
    public static void DisplayBanner()
    {
        var useColor = UseEmoji; // Same check - modern terminals support both
        var version = GetVersionString();
        
        Console.WriteLine();
        
        if (useColor)
        {
            DisplayColorBanner(version);
        }
        else
        {
            DisplayPlainBanner(version);
        }
        
        Console.WriteLine();
    }

    private static void DisplayColorBanner(string version)
    {
        var titleLines = TitleBlockArt;
        
        // Display each line with a gradient color
        for (int i = 0; i < titleLines.Length; i++)
        {
            var color = GradientColors[i % GradientColors.Length];
            Console.WriteLine($"  {color}{titleLines[i]}{ResetColor}");
        }
        
        Console.WriteLine();
        Console.WriteLine($"  \x1b[90mWindows App Development CLI · Version {version}{ResetColor}");
    }

    private static void DisplayPlainBanner(string version)
    {
        foreach (var line in TitleAsciiArt)
        {
            Console.WriteLine($"  {line}");
        }
        
        Console.WriteLine();
        Console.WriteLine($"  Windows App Development CLI - Version {version}");
    }

    /// <summary>
    /// Gets the version string from the assembly.
    /// </summary>
    private static string GetVersionString()
    {
        var assembly = Assembly.GetExecutingAssembly();
        
        // Try to get informational version first (includes git info if available)
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(infoVersion))
        {
            // Remove git hash suffix if present (e.g., "0.1.8+abc123" -> "0.1.8")
            var plusIndex = infoVersion.IndexOf('+');
            return plusIndex >= 0 ? infoVersion[..plusIndex] : infoVersion;
        }
        
        // Fall back to assembly version
        var version = assembly.GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";
    }
}
