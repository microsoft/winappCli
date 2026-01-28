// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.ProjectInformationProviders;

internal interface IProjectInformationProvider
{
    Task<bool> IsSupportedAsync(DirectoryInfo projectDirectory, CancellationToken cancellationToken = default);
    Task<Version?> GetWinAppSDKVersionAsync(DirectoryInfo projectDirectory, CancellationToken cancellationToken = default);
    Task<DirectoryInfo?> BuildAsync(DirectoryInfo projectDirectory, CancellationToken cancellationToken = default);
}
