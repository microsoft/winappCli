// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

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

        // If not WinApp SDK, preview and experimental versions are the same
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
}
