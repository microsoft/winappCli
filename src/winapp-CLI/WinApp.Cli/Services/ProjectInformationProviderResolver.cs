// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.ProjectInformationProviders;

namespace WinApp.Cli.Services;

internal class ProjectInformationProviderResolver(IEnumerable<IProjectInformationProvider> projectInformationProviders) : IProjectInformationProviderResolver
{
    public async Task<IProjectInformationProvider?> Resolve(DirectoryInfo projectDirectory, CancellationToken cancellationToken)
    {
        foreach (var provider in projectInformationProviders)
        {
            if (await provider.IsSupportedAsync(projectDirectory, cancellationToken))
            {
                return provider;
            }
        }

        return null;
    }
}
