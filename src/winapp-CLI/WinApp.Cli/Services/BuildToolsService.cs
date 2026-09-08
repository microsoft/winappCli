// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.RegularExpressions;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

internal partial class BuildToolsService(
    IConfigService configService,
    IWinappDirectoryService winappDirectoryService,
    INugetService nugetService,
    IPackageInstallationService packageInstallationService,
    IDotNetService dotNetService,
    ICurrentDirectoryProvider currentDirectoryProvider,
    ILogger<BuildToolsService> logger) : IBuildToolsService
{
    internal const string BUILD_TOOLS_PACKAGE = "Microsoft.Windows.SDK.BuildTools";
    internal const string CPP_SDK_PACKAGE = "Microsoft.Windows.SDK.CPP";
    internal const string WINAPP_SDK_PACKAGE = "Microsoft.WindowsAppSDK";
    internal const string WINAPP_SDK_RUNTIME_PACKAGE = "Microsoft.WindowsAppSDK.Runtime";

    /// <summary>
    /// Authenticode gate applied to every build tool before it is executed. Defaults to the real
    /// verifier; tests override it because their fixtures stand in dummy unsigned files for the
    /// real SDK binaries.
    /// </summary>
    internal static Func<string, ILogger, bool> SignatureVerifier { get; set; } = AuthenticodeVerifier.IsTrustedMicrosoftSigned;

    /// <summary>
    /// Throws unless <paramref name="toolPath"/> carries a valid Authenticode signature from
    /// Microsoft. These binaries are downloaded over the network and then executed, so they are
    /// verified at the point of use — which also covers <c>winapp tool</c>, where the executable
    /// name comes from the user.
    /// </summary>
    private void VerifyToolIsMicrosoftSigned(FileInfo toolPath)
    {
        // Deliberately not memoized. Any cache key cheap enough to compute here — path, length,
        // last-write time — is metadata that whoever can replace the file can also reproduce, so a
        // cached "signed" verdict could be inherited by different bytes.
        if (SignatureVerifier(toolPath.FullName, logger))
        {
            return;
        }

        throw new BuildToolSignatureException(
            $"'{toolPath.Name}' is not validly signed by Microsoft, so it was not run ({toolPath.FullName}). " +
            "The file on disk is not what Microsoft published, which usually means a corrupt or partial " +
            "download. Delete the package from the NuGet cache and run the command again to re-download it.");
    }

    /// <summary>
    /// Find the architecture-specific bin path within a package in the NuGet global packages
    /// cache (layout: {cache}/{lowercase-id}/{version}/{subPath}/{sdk-version}/{arch}).
    /// Resolves the pinned version (from winapp.yaml or the project's .csproj) when available,
    /// otherwise the latest installed version, then the current architecture with a fallback
    /// across x64/x86/arm64.
    /// </summary>
    /// <param name="packageName">The package name (e.g., BUILD_TOOLS_PACKAGE).</param>
    /// <param name="subPath">The subdirectory within the package (e.g., "bin").</param>
    /// <returns>Full path to the architecture-specific directory, or null if not found.</returns>
    private DirectoryInfo? FindPackagePath(string packageName, string subPath)
    {
        var nugetCacheDir = nugetService.GetNuGetGlobalPackagesDir();
        var packageBaseDir = new DirectoryInfo(Path.Join(nugetCacheDir.FullName, packageName.ToLowerInvariant()));
        if (!packageBaseDir.Exists)
        {
            return null;
        }

        // Enumerate version directories (NuGet cache layout: lowercase-id/version/)
        var versionDirs = packageBaseDir.EnumerateDirectories().ToArray();

        if (versionDirs.Length == 0)
        {
            return null;
        }

        // Resolve pinned version from winapp.yaml or .csproj
        string? pinnedVersion = null;

        // Path 1: Try winapp.yaml
        if (configService.Exists())
        {
            var pinnedConfig = configService.Load();
            pinnedVersion = pinnedConfig.GetVersion(packageName);
        }

        // Path 2: Try .csproj via `dotnet list package --format json`
        if (string.IsNullOrWhiteSpace(pinnedVersion))
        {
            try
            {
                var cwd = new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());
                var csprojFiles = dotNetService.FindCsproj(cwd);
                var csproj = csprojFiles.Count > 0 ? csprojFiles[0] : null;
                if (csproj != null)
                {
                    var packageList = dotNetService.GetPackageListAsync(csproj).GetAwaiter().GetResult();

                    var allPackages = packageList?.Projects?
                        .SelectMany(p => p.Frameworks ?? [])
                        .SelectMany(f => (f.TopLevelPackages ?? []).Concat(f.TransitivePackages ?? []));

                    var matchedPkg = allPackages?
                        .FirstOrDefault(p => string.Equals(p.Id, packageName, StringComparison.OrdinalIgnoreCase));

                    if (matchedPkg != null && !string.IsNullOrEmpty(matchedPkg.ResolvedVersion))
                    {
                        pinnedVersion = matchedPkg.ResolvedVersion;
                    }
                }
            }
            catch
            {
                // Silently fall through to latest-version fallback
            }
        }

        DirectoryInfo? selectedVersionDir = null;

        // Check if we have a pinned version
        if (!string.IsNullOrWhiteSpace(pinnedVersion))
        {
            // A pin may be shorthand (e.g. "1.0"); the cache uses NuGet's canonical folder name ("1.0.0"),
            // so normalize before matching directory names — otherwise a valid shorthand pin would never
            // match and would fall through to the strict null return below.
            var normalizedPin = NugetService.NormalizeVersion(pinnedVersion);

            // Look for the specific pinned version directory
            selectedVersionDir = versionDirs
                .FirstOrDefault(d => string.Equals(d.Name, normalizedPin, StringComparison.OrdinalIgnoreCase));

            // If pinned version is specified but not found, return null (strict requirement).
            if (selectedVersionDir == null)
            {
                return null;
            }
        }

        // No pinned version specified or not found, use latest
        selectedVersionDir ??= versionDirs
            .OrderByDescending(d => ParseVersion(d.Name))
            .First();

        var basePath = new DirectoryInfo(Path.Join(selectedVersionDir.FullName, subPath));
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

        // Resolve the architecture-specific bin directory.
        var currentArch = WorkspaceSetupService.GetSystemArchitecture();
        var archPath = Path.Join(latestVersion.FullName, currentArch);

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
                var fallbackArchPath = Path.Join(latestVersion.FullName, arch);
                if (Directory.Exists(fallbackArchPath))
                {
                    return new DirectoryInfo(fallbackArchPath);
                }
            }
        }
        return null;
    }

    private DirectoryInfo? FindBuildToolsBinPath()
    {
        return FindPackagePath(BUILD_TOOLS_PACKAGE, "bin");
    }

    private static Version ParseVersion(string versionString)
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
        var binPath = FindBuildToolsBinPath();
        if (binPath == null)
        {
            return null;
        }

        var toolPath = new FileInfo(Path.Join(binPath.FullName, toolName));
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
    public async Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default)
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
            var binPath = await EnsureBuildToolsAsync(taskContext, cancellationToken: cancellationToken);
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

        VerifyToolIsMicrosoftSigned(toolPath);

        return toolPath;
    }

    /// <summary>
    /// Ensure BuildTools package is installed, downloading it if necessary
    /// </summary>
    /// <param name="forceLatest">Force installation of the latest version, even if a version is already installed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to BuildTools bin directory if successful, null otherwise</returns>
    public async Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default)
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
        DirectoryInfo? binPath = null;
        await taskContext.AddSubTaskAsync($"{actionMessage} {BUILD_TOOLS_PACKAGE}{versionInfo}...", async (taskContext, cancellationToken) =>
        {
            var globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();

            var success = await packageInstallationService.EnsurePackageAsync(
                globalWinappDir,
                BUILD_TOOLS_PACKAGE,
                taskContext,
                version: pinnedVersion,
                sdkInstallMode: SdkInstallMode.Stable,
                cancellationToken: cancellationToken);

            if (!success)
            {
                return (1, $"Failed to install {BUILD_TOOLS_PACKAGE}.");
            }

            // Verify installation and return bin path
            binPath = FindBuildToolsBinPath();
            if (binPath != null)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Check} BuildTools installed successfully → {binPath}");
                return (0, "Windows SDK Build Tools installed successfully.");
            }

            return (1, $"Could not find BuildTools bin path after installation.");
        }, cancellationToken);

        return binPath;
    }

    /// <summary>
    /// Execute a build tool with the specified arguments
    /// </summary>
    /// <param name="tool">The tool to execute</param>
    /// <param name="arguments">Arguments to pass to the tool</param>
    /// <param name="printErrors">Whether to print errors using the tool's PrintErrorText method</param>
    /// <param name="taskContext">Task context for logging</param>
    /// <param name="toolPathOverride">Explicit executable path to run instead of resolving the tool by name</param>
    /// <param name="environment">Additional environment variables to set on the child process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing (stdout, stderr)</returns>
    public async Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, FileInfo? toolPathOverride = null, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Use the caller-supplied executable when provided (e.g. an architecture-matched
        // signtool), otherwise ensure the build tool is available, installing BuildTools if necessary.
        var toolPath = toolPathOverride
            ?? await EnsureBuildToolAvailableAsync(tool.ExecutableName, taskContext, cancellationToken: cancellationToken);

        // Re-checked here because callers may supply an override that never went through
        // resolution (the architecture-matched signtool). Memoized, so this is not a second scan.
        VerifyToolIsMicrosoftSigned(toolPath);

        var psi = new ProcessStartInfo
        {
            FileName = toolPath.FullName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // A caller-supplied working directory is used verbatim; otherwise the child inherits the
        // caller's current directory. Signing supplies a trusted directory so that when the Trusted
        // Signing dlib shells out to resolve 'az', a repo-local 'az.cmd' in the caller's working
        // directory cannot be picked up ahead of a legitimate one on PATH.
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        if (environment != null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {tool.ExecutableName} process");

        // Drain both pipes concurrently before awaiting exit. Reading stdout to completion
        // before touching stderr can deadlock if the tool fills the stderr buffer (signtool
        // /v /debug can) while we're still blocked on stdout.
        var stdoutTask = p.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = p.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await p.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(p);

            // We killed the process while its pipes were still being read. Observe both read tasks
            // before rethrowing so their reads don't keep running and can't surface later as
            // unobserved task exceptions; the failures they raise here are the expected result of
            // cancelling/killing mid-read.
            await DrainReadsQuietlyAsync(stdoutTask, stderrTask);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            taskContext.AddDebugMessage(stdout);
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            taskContext.AddDebugMessage(stderr);
        }

        if (p.ExitCode != 0)
        {
            // Print tool-specific error output when not in verbose mode
            // In verbose mode, all output is already visible via LogDebug above
            if (!logger.IsEnabled(LogLevel.Debug) && printErrors)
            {
                tool.PrintErrorText(stdout, stderr, logger);
            }

            throw new InvalidBuildToolException(p.Id, stdout, stderr, $"{tool.ExecutableName} execution failed with exit code {p.ExitCode}");
        }

        return (stdout, stderr);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Best-effort cleanup on cancellation
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best-effort cleanup on cancellation
        }
        catch (NotSupportedException)
        {
            // Best-effort cleanup on cancellation
        }
    }

    /// <summary>
    /// Awaits the stdout/stderr read tasks after the process was killed on cancellation, swallowing
    /// the expected failures (cancellation or a broken pipe from the kill) so the tasks are observed
    /// and never surface as unobserved exceptions. Any unexpected exception is left to propagate.
    /// </summary>
    private static async Task DrainReadsQuietlyAsync(Task stdoutTask, Task stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Expected: the reads were cancelled or their pipe closed when we killed the process.
        }
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
