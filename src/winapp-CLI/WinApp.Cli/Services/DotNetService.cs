// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Service for detecting and working with .NET projects, using the dotnet CLI
/// </summary>
internal partial class DotNetService : IDotNetService
{
    /// <summary>
    /// Minimum Windows SDK version that supports WinAppSDK
    /// </summary>
    private const string MinWindowsSdkVersion = "10.0.17763.0";

    /// <summary>
    /// Recommended TargetFramework for new WinAppSDK projects
    /// </summary>
    private const string RecommendedTfm = "net10.0-windows10.0.26100.0";

    private const string MSIXInfoComment = "<!-- Enables targets that generate package layout, required for running with winapp run or msix packaging -->";

    // NuGet package names for .NET WinAppSDK projects
    internal const string WINAPP_SDK_NUGET_PACKAGE = "Microsoft.WindowsAppSDK";

    internal const string WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE = "Microsoft.Windows.SDK.BuildTools.WinApp";

    [GeneratedRegex(@"^net(\d+\.\d+)-windows([\d.]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsTfmRegex();

    [GeneratedRegex(@"^net(\d+\.\d+)-windows$", RegexOptions.IgnoreCase)]
    private static partial Regex PlainWindowsTfmRegex();

    [GeneratedRegex(@"^net(\d+\.\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PlainNetTfmRegex();

    [GeneratedRegex(@"<TargetFramework>(.*?)</TargetFramework>", RegexOptions.Singleline)]
    private static partial Regex TargetFrameworkElementRegex();

    [GeneratedRegex(@"<TargetFrameworks>(.*?)</TargetFrameworks>", RegexOptions.Singleline)]
    private static partial Regex TargetFrameworksElementRegex();

    [GeneratedRegex(@"<RuntimeIdentifier\b[^>]*>(.*?)</RuntimeIdentifier>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RuntimeIdentifierElementRegex();

    [GeneratedRegex(@"<RuntimeIdentifiers[\s>].*?</RuntimeIdentifiers>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RuntimeIdentifiersElementRegex();

    [GeneratedRegex(@"<EnableMsixTooling>(.*?)</EnableMsixTooling>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex EnableMsixToolingElementRegex();

    [GeneratedRegex(@"[ \t]*<WindowsPackageType>None</WindowsPackageType>\r?\n?", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsPackageTypeNoneElementRegex();

    public IReadOnlyList<FileInfo> FindCsproj(DirectoryInfo directory)
    {
        if (!directory.Exists)
        {
            return [];
        }

        return directory.GetFiles("*.csproj", SearchOption.TopDirectoryOnly);
    }

    public string? GetTargetFramework(FileInfo csprojPath)
    {
        if (!csprojPath.Exists)
        {
            return null;
        }

        var content = File.ReadAllText(csprojPath.FullName);

        // Check singular <TargetFramework> first
        var match = TargetFrameworkElementRegex().Match(content);
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        // Fall back to <TargetFrameworks> — return the first TFM from the semicolon-separated list
        var pluralMatch = TargetFrameworksElementRegex().Match(content);
        if (pluralMatch.Success)
        {
            var first = pluralMatch.Groups[1].Value
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return first;
        }

        return null;
    }

    public bool IsMultiTargeted(FileInfo csprojPath)
    {
        if (!csprojPath.Exists)
        {
            return false;
        }

        var content = File.ReadAllText(csprojPath.FullName);
        return TargetFrameworksElementRegex().IsMatch(content);
    }

    public bool IsTargetFrameworkSupported(string targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return false;
        }

        var match = WindowsTfmRegex().Match(targetFramework);
        if (!match.Success)
        {
            // Not a windows TFM (e.g. "net8.0" without -windows)
            return false;
        }

        var netVersion = match.Groups[1].Value;
        var windowsVersion = match.Groups[2].Value;

        // We need at least .NET 6.0
        if (!Version.TryParse(netVersion, out var parsedNetVersion) || parsedNetVersion < new Version(6, 0))
        {
            return false;
        }

        // We need at least Windows SDK 10.0.17763.0
        if (!Version.TryParse(windowsVersion, out var parsedWinVersion) ||
            !Version.TryParse(MinWindowsSdkVersion, out var minWinVersion))
        {
            return false;
        }

        return parsedWinVersion >= minWinVersion;
    }

    public string GetRecommendedTargetFramework(string? currentTargetFramework = null)
    {
        // Default Windows SDK version to use
        const string defaultWindowsSdkVersion = "10.0.26100.0";

        if (string.IsNullOrWhiteSpace(currentTargetFramework))
        {
            return RecommendedTfm;
        }

        // Try to parse the current TFM to extract .NET version and optional Windows version
        var windowsTfmMatch = WindowsTfmRegex().Match(currentTargetFramework);
        if (windowsTfmMatch.Success)
        {
            // Already a Windows TFM (e.g., net10.0-windows10.0.26100.0)
            var netVersion = windowsTfmMatch.Groups[1].Value;
            var windowsVersion = windowsTfmMatch.Groups[2].Value;

            // Check if the .NET version is supported (>= 6.0)
            if (Version.TryParse(netVersion, out var parsedNetVersion) && parsedNetVersion >= new Version(6, 0))
            {
                // Check if Windows SDK version is supported
                if (Version.TryParse(windowsVersion, out var parsedWinVersion) &&
                    Version.TryParse(MinWindowsSdkVersion, out var minWinVersion) &&
                    parsedWinVersion >= minWinVersion)
                {
                    // Current TFM is already fully supported
                    return currentTargetFramework;
                }

                // Keep .NET version, update Windows SDK version
                return $"net{netVersion}-windows{defaultWindowsSdkVersion}";
            }
        }

        // Try to match a plain Windows TFM without SDK version (e.g., net10.0-windows)
        var plainWindowsMatch = PlainWindowsTfmRegex().Match(currentTargetFramework);
        if (plainWindowsMatch.Success)
        {
            var netVersion = plainWindowsMatch.Groups[1].Value;

            // Check if the .NET version is supported (>= 6.0)
            if (Version.TryParse(netVersion, out var parsedNetVersion) && parsedNetVersion >= new Version(6, 0))
            {
                // Keep .NET version, add Windows SDK version
                return $"net{netVersion}-windows{defaultWindowsSdkVersion}";
            }
        }

        // Try to match a plain .NET TFM (e.g., net8.0)
        var plainNetMatch = PlainNetTfmRegex().Match(currentTargetFramework);
        if (plainNetMatch.Success)
        {
            var netVersion = plainNetMatch.Groups[1].Value;

            // Check if the .NET version is supported (>= 6.0)
            if (Version.TryParse(netVersion, out var parsedNetVersion) && parsedNetVersion >= new Version(6, 0))
            {
                // Keep .NET version, add Windows TFM
                return $"net{netVersion}-windows{defaultWindowsSdkVersion}";
            }
        }

        // Fallback to default recommended TFM
        return RecommendedTfm;
    }

    public void SetTargetFramework(FileInfo csprojPath, string newTargetFramework)
    {
        var content = File.ReadAllText(csprojPath.FullName);
        var match = TargetFrameworkElementRegex().Match(content);

        if (match.Success)
        {
            content = content[..match.Index]
                + $"<TargetFramework>{newTargetFramework}</TargetFramework>"
                + content[(match.Index + match.Length)..];
        }
        else
        {
            // No TargetFramework element exists; insert one into the first PropertyGroup
            var propGroupIdx = content.IndexOf("<PropertyGroup", StringComparison.OrdinalIgnoreCase);
            if (propGroupIdx >= 0)
            {
                // Find the closing > of the <PropertyGroup> tag
                var closeTag = content.IndexOf('>', propGroupIdx);
                if (closeTag >= 0)
                {
                    var insertPos = closeTag + 1;
                    content = content[..insertPos]
                        + Environment.NewLine + $"    <TargetFramework>{newTargetFramework}</TargetFramework>"
                        + content[insertPos..];
                }
            }
        }

        File.WriteAllText(csprojPath.FullName, content);
    }

    public async Task<bool> EnsureRuntimeIdentifierAsync(FileInfo csprojPath, CancellationToken cancellationToken = default)
    {
        if (!csprojPath.Exists)
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(csprojPath.FullName, cancellationToken);

        // Don't modify if the project already defines RuntimeIdentifier (singular)
        if (RuntimeIdentifierElementRegex().IsMatch(content))
        {
            return false;
        }

        // Insert a RuntimeIdentifier with a Condition so it only applies when not already set
        // (e.g. via command-line -r or Directory.Build.props)
        const string runtimeIdentifierComment =
            "<!-- Added by winapp: default RuntimeIdentifier to current architecture when not specified. Only applies when not set via -r or Directory.Build.props. -->";
        const string runtimeIdentifierProperty =
            "<RuntimeIdentifier Condition=\"'$(RuntimeIdentifier)' == ''\">win-$([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant())</RuntimeIdentifier>";
        var runtimeIdentifierElement = runtimeIdentifierComment + Environment.NewLine + "    " + runtimeIdentifierProperty;

        // Insert into the first PropertyGroup:
        // 1. After <RuntimeIdentifiers> if present (keep RID properties together)
        // 2. After <TargetFramework> if present
        // 3. At start of first PropertyGroup as last resort
        var ridsMatch = RuntimeIdentifiersElementRegex().Match(content);
        if (ridsMatch.Success)
        {
            // Insert right after the </RuntimeIdentifiers> element
            var insertPos = ridsMatch.Index + ridsMatch.Length;
            content = content[..insertPos]
                + Environment.NewLine + "    " + runtimeIdentifierElement
                + content[insertPos..];
        }
        else
        {
            var tfmMatch = TargetFrameworkElementRegex().Match(content);
            if (tfmMatch.Success)
            {
                // Insert after the TargetFramework line
                var insertPos = tfmMatch.Index + tfmMatch.Length;
                content = content[..insertPos]
                    + Environment.NewLine + "    " + runtimeIdentifierElement
                    + content[insertPos..];
            }
            else
            {
                // No TargetFramework found; insert at start of first PropertyGroup
                var propGroupIdx = content.IndexOf("<PropertyGroup", StringComparison.OrdinalIgnoreCase);
                if (propGroupIdx >= 0)
                {
                    var closeTag = content.IndexOf('>', propGroupIdx);
                    if (closeTag >= 0)
                    {
                        var insertPos = closeTag + 1;
                        content = content[..insertPos]
                            + Environment.NewLine + "    " + runtimeIdentifierElement
                            + content[insertPos..];
                    }
                }
            }
        }

        await File.WriteAllTextAsync(csprojPath.FullName, content, cancellationToken);
        return true;
    }

    [GeneratedRegex(@"<PublishProfile>([^<]*\$\(Platform\)[^<]*\.pubxml)</PublishProfile>", RegexOptions.Singleline)]
    private static partial Regex PublishProfileElementRegex();

    public async Task<bool> UpdatePublishProfileAsync(FileInfo csprojPath, CancellationToken cancellationToken = default)
    {
        if (!csprojPath.Exists)
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(csprojPath.FullName, cancellationToken);
        var match = PublishProfileElementRegex().Match(content);

        if (!match.Success)
        {
            return false;
        }

        var profileValue = match.Groups[1].Value;
        var replacement = $"<PublishProfile Condition=\"Exists('Properties\\PublishProfiles\\{profileValue}')\">{profileValue}</PublishProfile>";
        content = content[..match.Index] + replacement + content[(match.Index + match.Length)..];

        await File.WriteAllTextAsync(csprojPath.FullName, content, cancellationToken);
        return true;
    }

    public async Task<string> AddOrUpdatePackageReferenceAsync(FileInfo csprojPath, string packageName, string? version, CancellationToken cancellationToken = default)
    {
        var args = $"add \"{csprojPath.FullName}\" package \"{packageName}\"";
        if (version != null)
        {
            args += $" --version \"{version}\"";
        }
        else
        {
            args += " --prerelease";
        }
        var (exitCode, output, error) = await RunDotnetCommandAsync(csprojPath.Directory!, args, cancellationToken);

        if (exitCode != 0)
        {
            var message = !string.IsNullOrWhiteSpace(error) ? error.Trim() : output.Trim();
            throw new InvalidOperationException(
                $"Failed to add package {packageName} {version} (exit code {exitCode}): {message}");
        }

        // NOTE: This regex is tightly coupled to the current "dotnet add package" CLI output format.
        // If the dotnet team changes that message, this match may fail and we will fall back to
        // returning the requested version (if provided) or "latest" below.
        var pattern = $@"PackageReference for package '{Regex.Escape(packageName)}' version '([\d\.\-a-zA-Z]+)' (?:added to|updated in) file";
        var match = Regex.Match(output, pattern);
        if (match.Success && match.Groups.Count > 1)
        {
            return match.Groups[1].Value;
        }

        return version ?? "latest";
    }

    /// <inheritdoc />
    public async Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(
        DirectoryInfo workingDirectory,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        var exitCode = await RunDotnetCoreAsync(
            workingDirectory,
            arguments,
            line => outputBuilder.AppendLine(line),
            line => errorBuilder.AppendLine(line),
            cancellationToken);

        return (exitCode, outputBuilder.ToString(), errorBuilder.ToString());
    }

    public Task<int> RunDotnetStreamingAsync(
        DirectoryInfo workingDirectory,
        string arguments,
        Action<string>? onOutputLine,
        Action<string>? onErrorLine,
        CancellationToken cancellationToken = default)
        => RunDotnetCoreAsync(workingDirectory, arguments, onOutputLine, onErrorLine, cancellationToken);

    public Task<int> RunDotnetInheritedAsync(
        DirectoryInfo workingDirectory,
        string arguments,
        CancellationToken cancellationToken = default)
        => RunDotnetCoreAsync(workingDirectory, arguments, onOutputLine: null, onErrorLine: null, cancellationToken, inheritStdio: true);

    /// <summary>
    /// Shared launch core for the buffered (<see cref="RunDotnetCommandAsync"/>), streaming
    /// (<see cref="RunDotnetStreamingAsync"/>), and inherited-stdio (<see cref="RunDotnetInheritedAsync"/>)
    /// dotnet invocations. All three share one <see cref="ProcessStartInfo"/> shape and — critically — a
    /// single kill-on-cancel policy: on cancellation every caller kills the whole
    /// <c>dotnet</c>/MSBuild/restore process tree (<c>Process.Kill(entireProcessTree: true)</c>), awaits
    /// termination, then rethrows, so none of the classification/evaluate/discovery, streaming build, or
    /// native-terminal build paths can orphan child processes.
    ///
    /// When <paramref name="inheritStdio"/> is <see langword="false"/> (buffered/streaming) stdout and
    /// stderr are redirected and each received line is forwarded to <paramref name="onOutputLine"/>/
    /// <paramref name="onErrorLine"/>. When <paramref name="inheritStdio"/> is <see langword="true"/> the
    /// child inherits winapp's console handles (no redirection, no read pumps) so dotnet sees a real TTY
    /// and its native terminal logger renders live; the callbacks are ignored in that mode.
    /// </summary>
    private static async Task<int> RunDotnetCoreAsync(
        DirectoryInfo workingDirectory,
        string arguments,
        Action<string>? onOutputLine,
        Action<string>? onErrorLine,
        CancellationToken cancellationToken,
        bool inheritStdio = false)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDirectory.FullName,
            // inheritStdio: hand winapp's own console handles to dotnet so it sees a real terminal and its
            // native terminal logger activates (single warnings, live progress). Otherwise redirect+pump.
            RedirectStandardOutput = !inheritStdio,
            RedirectStandardError = !inheritStdio,
            UseShellExecute = false,
            CreateNoWindow = !inheritStdio
        };

        using var process = new Process { StartInfo = processStartInfo };

        if (!inheritStdio)
        {
            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    onOutputLine?.Invoke(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    onErrorLine?.Invoke(e.Data);
                }
            };
        }

        process.Start();

        if (!inheritStdio)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C — kill the dotnet/MSBuild/restore process tree so it isn't orphaned. Shared by the
            // buffered path (classification/evaluate/discovery) and the streaming build path so neither
            // leaves child builds running.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);

                    // Process.Kill only *requests* termination; without awaiting exit the dotnet/MSBuild/
                    // restore children can still hold file locks after winapp returns. Wait (best-effort,
                    // uncancellable — we're already cancelling) so the tree is truly gone before we rethrow.
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch
            {
                // Best-effort cleanup; the process may have already exited.
            }

            throw;
        }

        return process.ExitCode;
    }

    /// <inheritdoc />
    public Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(
        DirectoryInfo workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // ArgumentList passes each token to the child process as a distinct argv entry, so no shell or
        // string splitting can inject extra tokens. It does NOT, however, stop an option-shaped value
        // (e.g. "--force") from being interpreted as a switch by dotnet's own parser — callers must
        // validate user-derived values against the child command's option grammar, as NewCommand does.
        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        if (environmentOverrides is not null)
        {
            foreach (var (key, value) in environmentOverrides)
            {
                processStartInfo.Environment[key] = value;
            }
        }

        return RunDotnetProcessAsync(processStartInfo, cancellationToken, onOutputLine: onOutputLine, onErrorLine: onErrorLine);
    }

    internal static async Task<(int ExitCode, string Output, string Error)> RunDotnetProcessAsync(
        ProcessStartInfo processStartInfo,
        CancellationToken cancellationToken,
        Action<Process>? onProcessStarted = null,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null)
    {
        using var process = new Process { StartInfo = processStartInfo };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
                onOutputLine?.Invoke(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
                onErrorLine?.Invoke(e.Data);
            }
        };

        process.Start();
        onProcessStarted?.Invoke(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // WaitForExitAsync only stops awaiting on cancellation; the spawned dotnet process keeps
            // running and can continue mutating the global template store or the output directory
            // after winapp exits. Kill the whole process tree and wait for it to actually stop before
            // propagating the cancellation.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch (InvalidOperationException)
            {
                // The process already exited between the HasExited check and Kill — nothing to do.
            }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited)
            {
                // Kill raced with the process exiting on its own; it is already gone, nothing to do.
                // A Win32Exception while the process is still running means termination genuinely
                // failed, so it is intentionally left to propagate and surface the kill failure.
            }

            throw;
        }

        // WaitForExitAsync returns once the process exits, but the async stdout/stderr readers may
        // still have buffered data in flight. The parameterless overload blocks until those readers
        // have flushed, so the StringBuilders are complete before we read them.
        process.WaitForExit();

        return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
    }

    public async Task<bool> HasPackageReferenceAsync(FileInfo csprojPath, string packageName, CancellationToken cancellationToken = default)
    {
        // Fast path: many .csproj files declare PackageReference inline. A direct XML scan avoids
        // an implicit `dotnet restore` (which can take 30s+ on a fresh machine — see #463).
        // We only short-circuit on a positive match; absence still requires the slow path because
        // the package may come from Directory.Packages.props (CPM), Directory.Build.props, an SDK,
        // or another import that only MSBuild evaluation can resolve.
        if (TryFindPackageReferenceInCsproj(csprojPath, packageName))
        {
            return true;
        }

        var packageList = await GetPackageListAsync(csprojPath, includeTransitive: false, cancellationToken: cancellationToken);
        if (packageList?.Projects is null)
        {
            return false;
        }

        return packageList.Projects
            .SelectMany(p => p.Frameworks ?? [])
            .SelectMany(f => f.TopLevelPackages ?? [])
            .Any(pkg => string.Equals(pkg.Id, packageName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryFindPackageReferenceInCsproj(FileInfo csprojPath, string packageName)
    {
        if (!csprojPath.Exists)
        {
            return false;
        }

        try
        {
            var doc = System.Xml.Linq.XDocument.Load(csprojPath.FullName);
            // PackageReference items live in the default (no-prefix) MSBuild namespace; new-style
            // SDK csproj files have no xmlns, so XName.LocalName is what we want either way.
            return doc.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "PackageReference", StringComparison.Ordinal))
                .Any(e => string.Equals((string?)e.Attribute("Include"), packageName, StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// When the caller knows the <c>project.assets.json</c> the build consumed, the graph is read from it:
    /// that is restore's output for the build's actual inputs, so it describes the binary that exists.
    /// Otherwise this falls back to <c>dotnet list package</c>, which re-evaluates with the default
    /// Configuration and no RID/Platform and accepts none of <c>-c</c>/<c>-r</c>/<c>-p:</c> — so a
    /// conditional <c>PackageReference</c> (rare for the Windows App SDK) isn't captured. The built TFM is
    /// scoped downstream by <c>FilterPackageListToFramework</c>, and the runtime presence-gate in
    /// <c>EnsureWindowsAppRuntimeInstalledAsync</c> is the backstop: a genuinely missing runtime fails the
    /// launch with an actionable error rather than silently under-provisioning.
    /// </remarks>
    public async Task<DotNetPackageListJson?> GetPackageListAsync(FileInfo projectOrFile, bool includeTransitive = true, bool noRestore = false, PackageGraphSource? packageGraph = null, CancellationToken cancellationToken = default)
    {
        if (!projectOrFile.Exists)
        {
            return null;
        }

        if (packageGraph is not null && ProjectAssetsFileReader.TryRead(packageGraph.AssetsFile, packageGraph.RuntimeIdentifier) is { } resolvedFromAssets)
        {
            return resolvedFromAssets;
        }

        // `--no-restore` on `dotnet list package` is a .NET 10 SDK addition: that SDK made an implicit
        // restore the default and `--no-restore` opts out. On .NET 9 and earlier the switch is unknown
        // and the command fails — and those SDKs don't restore here anyway (they read existing assets),
        // so there's nothing to opt out of. Only forward it on SDK 10+; otherwise omit it so package
        // discovery keeps working on the SDK 8.0.100+ range project mode supports.
        var applyNoRestore = noRestore
            && await GetSdkMajorVersionAsync(projectOrFile.Directory!, cancellationToken) is int major
            && major >= 10;

        // A .NET file-based app (a single .cs) has no project file for `dotnet list <project> package` to
        // read, so it uses the SDK 10 `dotnet package list --file <app>.cs` form instead. Without this a
        // caller would have to pass null and fall back to globbing the current directory for ANY .csproj,
        // which for a file-based app can only ever find an unrelated project.
        var isSingleFileApp = string.Equals(projectOrFile.Extension, ".cs", StringComparison.OrdinalIgnoreCase);
        var argTokens = new List<string>();
        if (isSingleFileApp)
        {
            argTokens.AddRange(["package", "list", "--file", projectOrFile.FullName]);
        }
        else
        {
            argTokens.AddRange(["list", projectOrFile.FullName, "package"]);
        }

        if (includeTransitive)
        {
            argTokens.Add("--include-transitive");
        }

        if (applyNoRestore)
        {
            argTokens.Add("--no-restore");
        }

        argTokens.AddRange(["--format", "json"]);

        var (exitCode, output, _) = await RunDotnetCommandAsync(
            projectOrFile.Directory!, argTokens, cancellationToken: cancellationToken);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(output, DotNetServiceJsonContext.Default.DotNetPackageListJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Caches the resolved dotnet SDK major version per working directory (global.json can pin different
    // SDKs per project), so the `--no-restore` capability probe runs `dotnet --version` at most once.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int?> _sdkMajorByDir =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the major version of the dotnet SDK active in <paramref name="workingDirectory"/> by
    /// running <c>dotnet --version</c> (respecting any <c>global.json</c>). Returns <c>null</c> when the
    /// version can't be determined; callers treat unknown conservatively (they skip SDK 10+-only switches).
    /// </summary>
    private async Task<int?> GetSdkMajorVersionAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        var key = workingDirectory.FullName;
        if (_sdkMajorByDir.TryGetValue(key, out var cached))
        {
            return cached;
        }

        int? major = null;
        try
        {
            var (exitCode, output, _) = await RunDotnetCommandAsync(workingDirectory, "--version", cancellationToken);
            if (exitCode == 0)
            {
                major = ParseSdkMajorVersion(output);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Leave unknown; the caller falls back to the safe (switch-omitted) behavior.
        }

        _sdkMajorByDir[key] = major;
        return major;
    }

    /// <summary>
    /// Parses the SDK major version from <c>dotnet --version</c> output such as <c>10.0.302</c> or
    /// <c>10.0.100-preview.1.24101.2</c>. Returns <c>null</c> for unrecognized output.
    /// </summary>
    internal static int? ParseSdkMajorVersion(string? versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput))
        {
            return null;
        }

        var firstLine = versionOutput
            .Split('\n', '\r')
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?
            .Trim();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return null;
        }

        // Drop any prerelease/build suffix (after '-') before reading the leading major component.
        var core = firstLine.Split('-', ' ')[0];
        var dot = core.IndexOf('.');
        var majorPart = dot >= 0 ? core[..dot] : core;
        return int.TryParse(majorPart, out var parsed) && parsed > 0 ? parsed : null;
    }

    public async Task<bool> EnsureEnableMsixToolingAsync(FileInfo csprojPath, CancellationToken cancellationToken = default)
    {
        if (!csprojPath.Exists)
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(csprojPath.FullName, cancellationToken);
        var match = EnableMsixToolingElementRegex().Match(content);

        if (match.Success)
        {
            var existingValue = match.Groups[1].Value.Trim();

            if (string.Equals(existingValue, "true", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(existingValue, "false", StringComparison.OrdinalIgnoreCase))
            {
                // Update existing element from false to true, adding a comment if one doesn't already exist
                var replacement = "<EnableMsixTooling>true</EnableMsixTooling>";

                // Check if there's already a comment above the element
                var beforeMatch = content[..match.Index];
                if (!beforeMatch.TrimEnd().EndsWith("-->", StringComparison.Ordinal))
                {
                    // Detect indentation from the EnableMsixTooling line
                    var lastNewline = beforeMatch.LastIndexOf('\n');
                    var indent = lastNewline >= 0 ? beforeMatch[(lastNewline + 1)..] : "";
                    replacement = $"{indent}{MSIXInfoComment}"
                        + Environment.NewLine + replacement;
                    // Replace including the leading whitespace on this line
                    content = content[..(lastNewline + 1)]
                        + replacement
                        + content[(match.Index + match.Length)..];
                }
                else
                {
                    content = content[..match.Index]
                        + replacement
                        + content[(match.Index + match.Length)..];
                }

                await File.WriteAllTextAsync(csprojPath.FullName, content, cancellationToken);
                return true;
            }

            return false;
        }

        // Insert EnableMsixTooling after RuntimeIdentifier, TargetFramework, or at start of first PropertyGroup
        var element =
            MSIXInfoComment
            + Environment.NewLine + "    <EnableMsixTooling>true</EnableMsixTooling>";

        var modified = false;
        var ridMatch = RuntimeIdentifierElementRegex().Match(content);
        if (ridMatch.Success)
        {
            // Insert after the full closing </RuntimeIdentifier> tag
            var insertPos = ridMatch.Index + ridMatch.Length;
            content = content[..insertPos]
                + Environment.NewLine + "    " + element
                + content[insertPos..];
            modified = true;
        }
        else
        {
            var tfmMatch = TargetFrameworkElementRegex().Match(content);
            if (tfmMatch.Success)
            {
                var insertPos = tfmMatch.Index + tfmMatch.Length;
                content = content[..insertPos]
                    + Environment.NewLine + "    " + element
                    + content[insertPos..];
                modified = true;
            }
            else
            {
                var propGroupIdx = content.IndexOf("<PropertyGroup", StringComparison.OrdinalIgnoreCase);
                if (propGroupIdx >= 0)
                {
                    var closeTag = content.IndexOf('>', propGroupIdx);
                    if (closeTag >= 0)
                    {
                        var insertPos = closeTag + 1;
                        content = content[..insertPos]
                            + Environment.NewLine + "    " + element
                            + content[insertPos..];
                        modified = true;
                    }
                }
            }
        }

        if (modified)
        {
            await File.WriteAllTextAsync(csprojPath.FullName, content, cancellationToken);
        }

        return modified;
    }

    public async Task<bool> RemoveWindowsPackageTypeNoneAsync(FileInfo csprojPath, CancellationToken cancellationToken = default)
    {
        if (!csprojPath.Exists)
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(csprojPath.FullName, cancellationToken);
        var match = WindowsPackageTypeNoneElementRegex().Match(content);

        if (!match.Success)
        {
            return false;
        }

        content = content[..match.Index] + content[(match.Index + match.Length)..];
        await File.WriteAllTextAsync(csprojPath.FullName, content, cancellationToken);
        return true;
    }

    public async Task<bool> AnnotatePackageReferencesAsync(FileInfo csprojPath, IReadOnlyDictionary<string, string> packageComments, CancellationToken cancellationToken = default)
    {
        if (!csprojPath.Exists || packageComments.Count == 0)
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(csprojPath.FullName, cancellationToken);
        var modified = false;

        foreach (var (packageName, comment) in packageComments)
        {
            // Find <PackageReference Include="packageName" and check if there's already a comment above it
            var pattern = $@"<PackageReference\s+Include=""{Regex.Escape(packageName)}""";
            var pkgMatch = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
            if (!pkgMatch.Success)
            {
                continue;
            }

            // Check if there's already an XML comment on the line(s) immediately before
            var beforePkg = content[..pkgMatch.Index];
            var lastNewline = beforePkg.LastIndexOf('\n');
            var linePrefix = lastNewline >= 0 ? beforePkg[(lastNewline + 1)..] : beforePkg;

            // If the content before on this line is just whitespace, check the previous line for a comment
            if (string.IsNullOrWhiteSpace(linePrefix))
            {
                var prevContent = lastNewline >= 0 ? beforePkg[..lastNewline].TrimEnd('\r') : "";
                if (prevContent.TrimEnd().EndsWith("-->", StringComparison.Ordinal))
                {
                    continue; // Already has a comment
                }
            }

            // Detect indentation from the PackageReference line
            var indent = linePrefix;
            var commentLine = $"{indent}<!-- {comment} -->" + Environment.NewLine;
            var insertPos = lastNewline >= 0 ? lastNewline + 1 : pkgMatch.Index;
            content = content[..insertPos] + commentLine + content[insertPos..];
            modified = true;
        }

        if (modified)
        {
            await File.WriteAllTextAsync(csprojPath.FullName, content, cancellationToken);
        }

        return modified;
    }

    public async Task<bool> EnsureAssetContentItemsAsync(FileInfo csprojPath, CancellationToken cancellationToken = default)
    {
        if (!csprojPath.Exists)
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(csprojPath.FullName, cancellationToken);

        // Skip if the csproj already includes Assets content (glob or individual entries)
        if (AssetsContentItemRegex().IsMatch(content))
        {
            return false;
        }

        // Insert a new ItemGroup with the Assets glob before </Project>
        var closeProjectIdx = content.LastIndexOf("</Project>", StringComparison.OrdinalIgnoreCase);
        if (closeProjectIdx < 0)
        {
            return false;
        }

        var itemGroup =
            "  <ItemGroup>" + Environment.NewLine
            + "    <Content Include=\"Assets\\**\\*\" />" + Environment.NewLine
            + "  </ItemGroup>" + Environment.NewLine + Environment.NewLine;

        content = content[..closeProjectIdx] + itemGroup + content[closeProjectIdx..];
        await File.WriteAllTextAsync(csprojPath.FullName, content, cancellationToken);
        return true;
    }

    [GeneratedRegex(@"<Content\s[^>]*Include\s*=\s*""Assets\\", RegexOptions.IgnoreCase)]
    private static partial Regex AssetsContentItemRegex();
}

[JsonSerializable(typeof(DotNetPackageListJson))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
internal partial class DotNetServiceJsonContext : JsonSerializerContext
{
}
