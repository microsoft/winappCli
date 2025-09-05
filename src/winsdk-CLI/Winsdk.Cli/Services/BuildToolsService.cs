using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Winsdk.Cli;

internal static class BuildToolsService
{
    private const string BUILD_TOOLS_PACKAGE = "Microsoft.Windows.SDK.BuildTools";

    private static string GetCurrentArchitecture()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        
        // Map .NET architecture names to BuildTools folder names
        return arch switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.Arm => "arm64", // Use arm64 as fallback for arm
            _ => "x64" // Default fallback
        };
    }

    private static string? FindWinsdkDirectory(string? startPath = null)
    {
        var currentDir = new DirectoryInfo(startPath ?? Directory.GetCurrentDirectory());
        
        while (currentDir != null)
        {
            var winsdkPath = Path.Combine(currentDir.FullName, ".winsdk");
            if (Directory.Exists(winsdkPath))
            {
                return winsdkPath;
            }
            currentDir = currentDir.Parent;
        }
        
        return null;
    }

    private static string? FindBuildToolsBinPath(string winsdkDir)
    {
        var packagesDir = Path.Combine(winsdkDir, "packages");
        if (!Directory.Exists(packagesDir))
            return null;

        // Find the BuildTools package directory
        var buildToolsPackageDirs = Directory.EnumerateDirectories(packagesDir)
            .Where(d => Path.GetFileName(d).StartsWith($"{BUILD_TOOLS_PACKAGE}.", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (buildToolsPackageDirs.Length == 0)
            return null;

        // Use the latest version if multiple exist
        var latestPackageDir = buildToolsPackageDirs
            .OrderByDescending(d => ExtractVersion(Path.GetFileName(d)))
            .First();

        var binPath = Path.Combine(latestPackageDir, "bin");
        if (!Directory.Exists(binPath))
            return null;

        // Find the version folder (should be something like 10.0.26100.0)
        var versionFolders = Directory.EnumerateDirectories(binPath)
            .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d+\.\d+\.\d+\.\d+$"))
            .ToArray();

        if (versionFolders.Length == 0)
            return null;

        // Use the latest version (sort by version number)
        var latestVersion = versionFolders
            .OrderByDescending(d => ParseVersion(Path.GetFileName(d)))
            .First();

        // Determine architecture based on current machine
        var currentArch = GetCurrentArchitecture();
        var archPath = Path.Combine(latestVersion, currentArch);
        
        if (Directory.Exists(archPath))
        {
            return archPath;
        }

        // If the detected architecture isn't available, fall back to common architectures
        var fallbackArchs = new[] { "x64", "x86", "arm64" };
        foreach (var arch in fallbackArchs)
        {
            if (arch != currentArch) // Skip the one we already tried
            {
                var fallbackArchPath = Path.Combine(latestVersion, arch);
                if (Directory.Exists(fallbackArchPath))
                {
                    return fallbackArchPath;
                }
            }
        }

        return null;
    }

    private static Version ExtractVersion(string packageFolderName)
    {
        // Extract version from package folder name like "Microsoft.Windows.SDK.BuildTools.10.0.26100.1742"
        var parts = packageFolderName.Split('.');
        if (parts.Length >= 4)
        {
            var versionPart = string.Join(".", parts.Skip(parts.Length - 4));
            if (Version.TryParse(versionPart, out var version))
                return version;
        }
        return new Version(0, 0, 0, 0);
    }

    private static Version ParseVersion(string versionString)
    {
        return Version.TryParse(versionString, out var version) ? version : new Version(0, 0, 0, 0);
    }

    /// <summary>
    /// Get the full path to a specific BuildTools executable
    /// </summary>
    /// <param name="toolName">Name of the tool (e.g., 'mt.exe', 'signtool.exe')</param>
    /// <param name="startPath">Starting directory to search for .winsdk (defaults to current directory)</param>
    /// <returns>Full path to the executable</returns>
    public static string? GetBuildToolPath(string toolName, string? startPath = null)
    {
        var winsdkDir = FindWinsdkDirectory(startPath);
        if (winsdkDir == null)
            return null;

        var binPath = FindBuildToolsBinPath(winsdkDir);
        if (binPath == null)
            return null;

        var toolPath = Path.Combine(binPath, toolName);
        return File.Exists(toolPath) ? toolPath : null;
    }

    /// <summary>
    /// Execute a build tool with the specified arguments
    /// </summary>
    /// <param name="toolName">Name of the tool (e.g., 'mt.exe', 'signtool.exe')</param>
    /// <param name="arguments">Arguments to pass to the tool</param>
    /// <param name="verbose">Whether to output verbose information</param>
    /// <param name="startPath">Starting directory to search for .winsdk (defaults to current directory)</param>
    /// <returns>Task representing the asynchronous operation</returns>
    public static async Task RunBuildToolAsync(string toolName, string arguments, bool verbose = false, string? startPath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var toolPath = GetBuildToolPath(toolName, startPath);
        if (toolPath == null)
        {
            throw new FileNotFoundException($"Could not find {toolName}. Make sure the Microsoft.Windows.SDK.BuildTools package is installed in a .winsdk directory.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        cancellationToken.ThrowIfCancellationRequested();

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {toolName} process");
        var stdout = await p.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await p.StandardError.ReadToEndAsync(cancellationToken);
        await p.WaitForExitAsync(cancellationToken);

        if (verbose)
        {
            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.WriteLine(stderr);
        }

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{toolName} execution failed with exit code {p.ExitCode}");
        }
    }
}
