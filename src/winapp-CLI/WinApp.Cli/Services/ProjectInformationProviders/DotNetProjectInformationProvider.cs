// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace WinApp.Cli.Services.ProjectInformationProviders;

internal partial class DotNetProjectInformationProvider(IDotNetService dotNetService) : IProjectInformationProvider
{
    [GeneratedRegex(@"^\s*.+?\s->\s(?<path>.+\.(dll|exe))\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled, "en-US")]
    private static partial Regex BuildOutputPathRegex();

    public Task<bool> IsSupportedAsync(DirectoryInfo projectDirectory, CancellationToken cancellationToken = default)
    {
        var csprojFiles = projectDirectory.GetFiles("*.csproj").FirstOrDefault();
        return Task.FromResult(csprojFiles != null && csprojFiles.Length > 0);
    }

    public async Task<Version?> GetWinAppSDKVersionAsync(DirectoryInfo projectDirectory, CancellationToken cancellationToken = default)
    {
        var packageList = await dotNetService.GetPackageListAsync(projectDirectory, cancellationToken);

        if (packageList == null)
        {
            return null;
        }

        var version = packageList
            .Projects
            .FirstOrDefault()?.Frameworks
            .SelectMany(f =>
            {
                var packages = f.TopLevelPackages.ToList();
                packages.AddRange(f.TransitivePackages);
                return packages;
            })
            .FirstOrDefault(p => p.Id == BuildToolsService.WINAPP_SDK_PACKAGE)
            ?.ResolvedVersion;

        return version != null ? new Version(version) : null;
    }

    public async Task<DirectoryInfo?> BuildAsync(DirectoryInfo projectDirectory, CancellationToken cancellationToken = default)
    {
        var csprojFiles = projectDirectory.GetFiles("*.csproj").FirstOrDefault();
        if (csprojFiles == null)
        {
            throw new Exception("No .csproj file found in the current directory.");
        }

        // get current architecture (arm64, x64)
        var currentArch = WorkspaceSetupService.GetSystemArchitecture();

        var buildResult = await dotNetService.RunDotnetCommandAsync(projectDirectory, $"build {csprojFiles.Name} -c Debug -r win-{currentArch}", cancellationToken);

        if (buildResult.ExitCode != 0)
        {
            throw new Exception($"Build failed: {buildResult.Output}");
        }

        var match = BuildOutputPathRegex().Match(buildResult.Output);
        if (!match.Success)
        {
            throw new Exception("Failed to determine build output path.");
        }
        var outputDirectory = match.Groups["path"].Value;

        return new DirectoryInfo(Path.GetDirectoryName(outputDirectory)!);
    }
}
