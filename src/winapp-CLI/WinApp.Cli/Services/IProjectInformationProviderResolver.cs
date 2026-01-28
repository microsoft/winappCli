// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.ProjectInformationProviders;

namespace WinApp.Cli.Services;

internal interface IProjectInformationProviderResolver
{
    Task<IProjectInformationProvider?> Resolve(DirectoryInfo projectDirectory, CancellationToken cancellationToken);
}
