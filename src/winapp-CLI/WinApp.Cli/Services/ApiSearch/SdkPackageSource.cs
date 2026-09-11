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
internal sealed class SdkPackageSource : ISdkPackageSource
{
    public List<PackageWithWinMd> GetSdkPackages() =>
        NuGetResolver.FindSdkPackages(ApiCacheBuilder.DetectWinAppSdkRuntime());
}
