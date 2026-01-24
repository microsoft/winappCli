// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal interface IDotNetService
{
    Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(
                DirectoryInfo workingDirectory,
                string arguments,
                CancellationToken cancellationToken);

    Task<DotNetPackageListJson?> ParseDotnetPackageListJsonAsync(string output, CancellationToken cancellationToken);
}


public record DotNetPackageListJson(List<DotNetProject> Projects);

public record DotNetProject(List<DotNetFramework> Frameworks);

public record DotNetFramework(string Framework, List<DotNetPackage> TopLevelPackages, List<DotNetPackage> TransitivePackages);

public record DotNetPackage(string Id, string RequestedVersion, string ResolvedVersion);
