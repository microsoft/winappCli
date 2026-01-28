// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal class NugetService(ICurrentDirectoryProvider currentDirectoryProvider) : INugetService
{
    private static readonly HttpClient Http = new();
    private const string NugetExeUrl = "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe";
    private const string FlatIndex = "https://api.nuget.org/v3-flatcontainer";

    public static readonly string[] SDK_PACKAGES =
    [
        "Microsoft.Windows.CppWinRT",
        BuildToolsService.BUILD_TOOLS_PACKAGE,
        BuildToolsService.WINAPP_SDK_PACKAGE,
        "Microsoft.Windows.ImplementationLibrary",
        BuildToolsService.CPP_SDK_PACKAGE,
        $"{BuildToolsService.CPP_SDK_PACKAGE}.x64",
        $"{BuildToolsService.CPP_SDK_PACKAGE}.arm64"
    ];

    public async Task EnsureNugetExeAsync(DirectoryInfo winappDir, CancellationToken cancellationToken = default)
    {
        var toolsDir = Path.Combine(winappDir.FullName, "tools");
        var nugetExe = Path.Combine(toolsDir, "nuget.exe");
        if (File.Exists(nugetExe))
        {
            return;
        }

        Directory.CreateDirectory(toolsDir);
        using var resp = await Http.GetAsync(NugetExeUrl, cancellationToken);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(nugetExe);
        await resp.Content.CopyToAsync(fs, cancellationToken);
    }

    public async Task<string> GetLatestVersionAsync(string packageName, SdkInstallMode sdkInstallMode, CancellationToken cancellationToken = default)
    {
        if (sdkInstallMode == SdkInstallMode.None)
        {
            throw new ArgumentException("sdkInstallMode cannot be None", nameof(sdkInstallMode));
        }

        var url = $"{FlatIndex}/{packageName.ToLowerInvariant()}/index.json";
        using var resp = await Http.GetAsync(url, cancellationToken);
        resp.EnsureSuccessStatusCode();
        using var s = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("versions", out var versionsElem) || versionsElem.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"No versions found for {packageName}");
        }

        var list = new List<string>();
        foreach (var el in versionsElem.EnumerateArray())
        {
            var v = el.GetString();
            if (!string.IsNullOrWhiteSpace(v))
            {
                list.Add(v);
            }
        }

        // If not winapp SDK, preview and experimental versions are the same
        if (packageName.StartsWith(BuildToolsService.WINAPP_SDK_PACKAGE, StringComparison.OrdinalIgnoreCase))
        {
            if (sdkInstallMode == SdkInstallMode.Stable)
            {
                // Only stable versions (no prerelease suffix)
                list = [.. list.Where(v => !v.Contains('-', StringComparison.Ordinal))];
            }
            else if (sdkInstallMode == SdkInstallMode.Preview)
            {
                // Only with preview
                list = [.. list.Where(v => v.Contains("-preview", StringComparison.OrdinalIgnoreCase))];
            }
            else if (sdkInstallMode == SdkInstallMode.Experimental)
            {
                // Only with experimental
                list = [.. list.Where(v => v.Contains("-experimental", StringComparison.OrdinalIgnoreCase))];
            }
            // For Experimental mode: keep all versions (no filtering needed)
        }
        else
        {
            if (sdkInstallMode == SdkInstallMode.Stable)
            {
                // Only stable versions (no prerelease suffix)
                list = [.. list.Where(v => !v.Contains('-', StringComparison.Ordinal))];
            }
        }

        if (list.Count == 0)
        {
            throw new InvalidOperationException($"No versions found for {packageName}");
        }

        list.Sort(CompareVersions);
        return list[^1];
    }

    public async Task<Dictionary<string, string>> InstallPackageAsync(DirectoryInfo globalWinappDir, string package, string version, DirectoryInfo outputDir, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var nugetExe = Path.Combine(globalWinappDir.FullName, "tools", "nuget.exe");
        if (!File.Exists(nugetExe))
        {
            throw new FileNotFoundException("nuget.exe missing; call EnsureNugetExeAsync first", nugetExe);
        }

        outputDir.Create();

        // If already installed, skip
        var expectedFolder = Path.Combine(outputDir.FullName, $"{package}.{version}");
        if (Directory.Exists(expectedFolder))
        {
            taskContext.AddDebugMessage($"{UiSymbols.Skip} {package} {version} already present");
            packages[package] = version;
            return packages;
        }

        var psi = new ProcessStartInfo
        {
            FileName = nugetExe,
            Arguments = $"install {EscapeArg(package)} -Version {EscapeArg(version)} -OutputDirectory {Quote(outputDir.FullName)} -NonInteractive -ForceEnglishOutput",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = currentDirectoryProvider.GetCurrentDirectory(),
        };
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await p.StandardError.ReadToEndAsync(cancellationToken);
        await p.WaitForExitAsync(cancellationToken);
        if (p.ExitCode != 0)
        {
            taskContext.StatusError(stdout);
            taskContext.StatusError(stderr);
            throw new InvalidOperationException($"nuget install failed for {package} {version}");
        }

        var lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("Successfully installed '", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split('\'', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var installed = parts[1].Trim();
                    var spaceIdx = installed.LastIndexOf(' ');
                    if (spaceIdx > 0)
                    {
                        var installedName = installed[..spaceIdx];
                        var installedVersion = installed[(spaceIdx + 1)..];
                        packages[installedName] = installedVersion;
                        taskContext.AddStatusMessage($"{UiSymbols.Check} Installed {installedName} {installedVersion}");
                    }
                }
            }
        }

        if (!packages.ContainsKey(package))
        {
            throw new InvalidOperationException($"Could not determine installed version for {package} {version}");
        }

        return packages;
    }

    private static string Quote(string path) => $"\"{path}\"";

    private static string EscapeArg(string v)
    {
        if (v.Contains(' ') || v.Contains('"'))
        {
            return Quote(v.Replace("\"", "\\\""));
        }

        return v;
    }

    public static int CompareVersions(string a, string b)
    {
        var ap = a.Split('.', '-', StringSplitOptions.RemoveEmptyEntries);
        var bp = b.Split('.', '-', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Max(ap.Length, bp.Length); i++)
        {
            int ai = i < ap.Length && int.TryParse(ap[i], out var av) ? av : 0;
            int bi = i < bp.Length && int.TryParse(bp[i], out var bv) ? bv : 0;
            if (ai != bi)
            {
                return ai.CompareTo(bi);
            }
        }
        return 0;
    }

    private static ISettings ProcessConfigFile(string configFile, string projectOrSolution)
    {
        if (string.IsNullOrEmpty(configFile))
        {
            return Settings.LoadDefaultSettings(projectOrSolution);
        }

        var configFileFullPath = Path.GetFullPath(configFile);
        var directory = Path.GetDirectoryName(configFileFullPath);
        var configFileName = Path.GetFileName(configFileFullPath);
        return Settings.LoadDefaultSettings(
            directory,
            configFileName,
            machineWideSettings: new XPlatMachineWideSetting());
    }

    public static void ValidateSource(string source)
    {
        Uri? result;
        if (!Uri.TryCreate(source, UriKind.Absolute, out result))
        {
            throw new Exception($"InvalidSource: {source}");
        }
    }

    public static PackageSource ResolveSource(IEnumerable<PackageSource> availableSources, string source)
    {
        var resolvedSource = availableSources.FirstOrDefault(
                f => f.Source.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Equals(source, StringComparison.OrdinalIgnoreCase));

        if (resolvedSource == null)
        {
            ValidateSource(source);
            return new PackageSource(source);
        }
        else
        {
            return resolvedSource;
        }
    }

    private static List<PackageSource> GetPackageSources(ISettings settings, IEnumerable<string> sources, string? config)
    {
        var availableSources = PackageSourceProvider.LoadPackageSources(settings).Where(source => source.IsEnabled);
        var uniqueSources = new HashSet<string>();

        var packageSources = new List<PackageSource>();
        foreach (var source in sources)
        {
            if (uniqueSources.Add(source))
            {
                packageSources.Add(ResolveSource(availableSources, source));
            }
        }

        if (packageSources.Count == 0 || !string.IsNullOrEmpty(config))
        {
            packageSources.AddRange(availableSources);
        }

        return packageSources;
    }

    public async Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageId, string version, CancellationToken cancellationToken)
    {
        // TODO: Allow passing custom config
        string config = string.Empty;
        string project = currentDirectoryProvider.GetCurrentDirectory();
        var settings = ProcessConfigFile(config, project);
        List<string> sources = [];

        var packageSources = GetPackageSources(settings, sources, config);

        IEnumerable<Lazy<INuGetResourceProvider>> providers = Repository.Provider.GetCoreV3();
        using var sourceCacheContext = new SourceCacheContext();
        try
        {
            ConcurrentDictionary<string, string> result = new();
            await Parallel.ForEachAsync(packageSources, cancellationToken, async (source, ct) =>
            {
                SourceRepository sourceRepository = Repository.CreateSource(providers, source, FeedType.Undefined);

                PackageMetadataResource packageMetadataResource = await sourceRepository.GetResourceAsync<PackageMetadataResource>();
                var packageIdentity = new PackageIdentity(packageId, new NuGetVersion(version));
                IPackageSearchMetadata package =
                    await packageMetadataResource.GetMetadataAsync(
                        packageIdentity,
                        sourceCacheContext: sourceCacheContext,
                        log: NullLogger.Instance,
                        token: cancellationToken);

                if (package != null)
                {
                    result.AddRange(package.DependencySets.SelectMany(pdg => pdg.Packages).ToDictionary(pd => pd.Id, pd => pd.VersionRange.MinVersion?.ToNormalizedString() ?? pd.VersionRange.MaxVersion?.ToNormalizedString() ?? pd.VersionRange.ToString(), StringComparer.OrdinalIgnoreCase));
                }
            });

            return new Dictionary<string, string>(result, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to get metadata for package {packageId} {version}: {ex.Message}", ex);
        }
    }
}
