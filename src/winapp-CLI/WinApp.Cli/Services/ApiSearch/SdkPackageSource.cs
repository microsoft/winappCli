// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Supplies the machine-wide metadata packages that back <c>find-api</c>'s SDK
/// scope. Behind an interface so resolution can be faked in tests — the real
/// implementation probes the installed Windows SDK and WinAppSDK runtime, which
/// vary per machine.
/// </summary>
internal interface ISdkPackageSource
{
    List<PackageWithWinMd> GetSdkPackages();
}

/// <inheritdoc />
/// <remarks>
/// The SDK scope is not tied to a project, so the NuGet global packages folder (where the
/// SDK's XML docs live) is resolved from the nuget.config hierarchy of the working directory.
/// </remarks>
internal sealed class SdkPackageSource(INugetService nugetService, ICurrentDirectoryProvider currentDirectory) : ISdkPackageSource
{
    public List<PackageWithWinMd> GetSdkPackages() =>
        NuGetResolver.FindSdkPackages(
            nugetService.GetNuGetGlobalPackagesDir(currentDirectory.GetCurrentDirectory()).FullName,
            ApiCacheBuilder.DetectWinAppSdkRuntime());
}
