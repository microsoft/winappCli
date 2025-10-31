// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.RegularExpressions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal partial class BuildToolsService(
    IConfigService configService,
    IWinappDirectoryService winappDirectoryService,
    IPackageInstallationService packageInstallationService,
    ILogger<BuildToolsService> logger) : IBuildToolsService
{
    internal const string BUILD_TOOLS_PACKAGE = "Microsoft.Windows.SDK.BuildTools";
    internal const string CPP_SDK_PACKAGE = "Microsoft.Windows.SDK.CPP";

    /// <summary>
    /// Find a path within any package structure (generic version)
    /// </summary>
    /// <param name="packageName">The package name (e.g., BUILD_TOOLS_PACKAGE or CPP_SDK_PACKAGE)</param>
    /// <param name="subPath">The subdirectory within the package (e.g., "bin", "schemas", "c")</param>
    /// <param name="finalSubPath">Optional final subdirectory (e.g., "winrt" for schemas, "Include" for SDK)</param>
    /// <param name="requireArchitecture">Whether to append architecture directory for bin paths</param>
    /// <returns>Full path to the requested location, or null if not found</returns>
    private DirectoryInfo? FindPackagePath(string packageName, string subPath, string? finalSubPath = null, bool requireArchitecture = false)
    {
        var globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();
        var packagesDir = new DirectoryInfo(Path.Combine(globalWinappDir.FullName, "packages"));
        if (!packagesDir.Exists)
        {
            return null;
        }

        // Find the package directory
        var packageDirs = packagesDir.EnumerateDirectories()
            .Where(d => d.Name.StartsWith($"{packageName}.", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (packageDirs.Length == 0)
        {
            return null;
        }

        WinappConfig? pinnedConfig = null;
        if (configService.Exists())
        {
            pinnedConfig = configService.Load();
        }

        DirectoryInfo? selectedPackageDir = null;

        // Check if we have a pinned version in config
        if (pinnedConfig != null)
        {
            var pinnedVersion = pinnedConfig.GetVersion(packageName);
            if (!string.IsNullOrWhiteSpace(pinnedVersion))
            {
                // Look for the specific pinned version
                selectedPackageDir = packageDirs
                    .FirstOrDefault(d => d.Name.EndsWith($".{pinnedVersion}", StringComparison.OrdinalIgnoreCase));

                // If pinned version is specified but not found for bin path, return null (strict requirement)
                // For other paths, continue to try latest
                if (selectedPackageDir == null && requireArchitecture)
                {
                    return null;
                }
            }
        }

        // No pinned version specified, use latest
        selectedPackageDir ??= packageDirs
            .OrderByDescending(d => ExtractVersion(d.Name))
            .First();

        var basePath = new DirectoryInfo(Path.Combine(selectedPackageDir.FullName, subPath));
        if (!basePath.Exists)
        {
            return null;
        }

        // Find the version folder (should be something like 10.0.26100.0)
        var versionFolders = basePath.EnumerateDirectories()
            .Where(d => VersionFolderRegex().IsMatch(d.Name))
            .ToArray();

        if (versionFolders.Length == 0)
        {
            return null;
        }

        // Use the latest version (sort by version number)
        var latestVersion = versionFolders
            .OrderByDescending(d => ParseVersion(d.Name))
            .First();

        if (requireArchitecture)
        {
            // For bin paths, need to find architecture directory
            var currentArch = WorkspaceSetupService.GetSystemArchitecture();
            var archPath = Path.Combine(latestVersion.FullName, currentArch);

            if (Directory.Exists(archPath))
            {
                return new DirectoryInfo(archPath);
            }

            // If the detected architecture isn't available, fall back to common architectures
            var fallbackArchs = new[] { "x64", "x86", "arm64" };
            foreach (var arch in fallbackArchs)
            {
                if (arch != currentArch) // Skip the one we already tried
                {
                    var fallbackArchPath = Path.Combine(latestVersion.FullName, arch);
                    if (Directory.Exists(fallbackArchPath))
                    {
                        return new DirectoryInfo(fallbackArchPath);
                    }
                }
            }
            return null;
        }
        else if (!string.IsNullOrEmpty(finalSubPath))
        {
            // For schemas path or SDK Include path with final subdirectory
            var finalPath = new DirectoryInfo(Path.Combine(latestVersion.FullName, finalSubPath));
            return finalPath.Exists ? finalPath : null;
        }
        else
        {
            // Return the version folder directly
            return latestVersion;
        }
    }

    private DirectoryInfo? FindBuildToolsBinPath()
    {
        return FindPackagePath(BUILD_TOOLS_PACKAGE, "bin", requireArchitecture: true);
    }

    private Version ExtractVersion(string packageFolderName)
    {
        // Extract version from package folder name like "Microsoft.Windows.SDK.BuildTools.10.0.26100.1742"
        var parts = packageFolderName.Split('.');
        if (parts.Length >= 4)
        {
            var versionPart = string.Join(".", parts.Skip(parts.Length - 4));
            if (Version.TryParse(versionPart, out var version))
            {
                return version;
            }
        }
        return new Version(0, 0, 0, 0);
    }

    private Version ParseVersion(string versionString)
    {
        return Version.TryParse(versionString, out var version) ? version : new Version(0, 0, 0, 0);
    }

    /// <summary>
    /// Get the full path to a specific BuildTools executable if it exists in the current installation.
    /// This method does NOT install BuildTools if they are missing.
    /// Use EnsureBuildToolAvailableAsync if you want automatic installation.
    /// </summary>
    /// <param name="toolName">Name of the tool (e.g., 'mt.exe', 'signtool.exe')</param>
    /// <returns>Full path to the executable if found, null otherwise</returns>
    public FileInfo? GetBuildToolPath(string toolName)
    {
        var globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();
        if (globalWinappDir == null)
        {
            return null;
        }

        var binPath = FindBuildToolsBinPath();
        if (binPath == null)
        {
            return null;
        }

        var toolPath = new FileInfo(Path.Combine(binPath.FullName, toolName));
        return toolPath.Exists ? toolPath : null;
    }

    /// <summary>
    /// Ensures a build tool is available by finding it in existing installation or installing BuildTools if necessary
    /// </summary>
    /// <param name="toolName">Name of the tool (e.g., 'mt.exe', 'signtool.exe'). The .exe extension will be automatically added if not present.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Full path to the executable. Throws an exception if the tool cannot be found or installed.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the tool cannot be found even after installing BuildTools</exception>
    /// <exception cref="InvalidOperationException">Thrown when BuildTools installation fails</exception>
    public async Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, CancellationToken cancellationToken = default)
    {
        // First, try to find the tool in existing installation
        var toolPath = GetBuildToolPath(toolName);
        if (toolPath == null && !toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            toolPath = GetBuildToolPath(toolName + ".exe");
        }

        // If tool not found, ensure BuildTools are installed
        if (toolPath == null)
        {
            var binPath = await EnsureBuildToolsAsync(cancellationToken: cancellationToken);
            if (binPath == null)
            {
                throw new InvalidOperationException("Could not install or find Windows SDK Build Tools.");
            }

            // Try again after installation
            toolPath = GetBuildToolPath(toolName);
            if (toolPath == null && !toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                toolPath = GetBuildToolPath(toolName + ".exe");
            }
        }

        if (toolPath == null)
        {
            var actualToolName = toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? toolName : toolName + ".exe";
            throw new FileNotFoundException($"Could not find '{actualToolName}' in the Windows SDK Build Tools.");
        }

        return toolPath;
    }

    /// <summary>
    /// Ensure BuildTools package is installed, downloading it if necessary
    /// </summary>
    /// <param name="forceLatest">Force installation of the latest version, even if a version is already installed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to BuildTools bin directory if successful, null otherwise</returns>
    public async Task<DirectoryInfo?> EnsureBuildToolsAsync(bool forceLatest = false, CancellationToken cancellationToken = default)
    {
        // Check if BuildTools are already installed (unless forcing latest)
        var existingBinPath = FindBuildToolsBinPath();
        if (existingBinPath != null && !forceLatest)
        {
            return existingBinPath;
        }

        // Get pinned version if available (ignore if forcing latest)
        string? pinnedVersion = null;
        if (configService.Exists() && !forceLatest)
        {
            var pinnedConfig = configService.Load();
            pinnedVersion = pinnedConfig.GetVersion(BUILD_TOOLS_PACKAGE);
        }

        // BuildTools not found or forcing latest, install them
        var actionMessage = existingBinPath != null ? "Updating" : "Installing";
        var versionInfo = !string.IsNullOrWhiteSpace(pinnedVersion) ? $" (pinned version {pinnedVersion})" : forceLatest ? " (latest version)" : "";
        logger.LogInformation("{UISymbol} {ActionMessage} {BUILD_TOOLS_PACKAGE}{VersionInfo}...", UiSymbols.Wrench, actionMessage, BUILD_TOOLS_PACKAGE, versionInfo);

        var globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();

        var success = await packageInstallationService.EnsurePackageAsync(
            globalWinappDir,
            BUILD_TOOLS_PACKAGE,
            version: pinnedVersion,
            includeExperimental: false,
            cancellationToken: cancellationToken);

        if (!success)
        {
            return null;
        }

        // Verify installation and return bin path
        var binPath = FindBuildToolsBinPath();
        if (binPath != null)
        {
            logger.LogInformation("{UISymbol} BuildTools installed successfully → {BinPath}", UiSymbols.Check, binPath);
        }

        return binPath;
    }

    /// <summary>
    /// Execute a build tool with the specified arguments
    /// </summary>
    /// <param name="toolName">Name of the tool (e.g., 'mt.exe', 'signtool.exe')</param>
    /// <param name="arguments">Arguments to pass to the tool</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing (stdout, stderr)</returns>
    public async Task<(string stdout, string stderr)> RunBuildToolAsync(string toolName, string arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Ensure the build tool is available, installing BuildTools if necessary
        var toolPath = await EnsureBuildToolAvailableAsync(toolName, cancellationToken: cancellationToken);

        var psi = new ProcessStartInfo
        {
            FileName = toolPath.FullName,
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

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            logger.LogDebug("{Stdout}", stdout);
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            logger.LogDebug("{StdErr}", stderr);
        }

        if (p.ExitCode != 0)
        {
            throw new InvalidBuildToolException(p.Id, stdout, stderr, $"{toolName} execution failed with exit code {p.ExitCode}");
        }

        return (stdout, stderr);
    }

    internal class InvalidBuildToolException : InvalidOperationException
    {
        public InvalidBuildToolException(int processId, string stdout, string stderr, string message) : base(message)
        {
            ProcessId = processId;
            Stdout = stdout;
            Stderr = stderr;
        }

        public int ProcessId { get; }
        public string Stdout { get; }
        public string Stderr { get; }
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+\.\d+$")]
    private static partial Regex VersionFolderRegex();
}
