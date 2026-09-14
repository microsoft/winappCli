// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

/// <summary>
/// Internal launch boundary shared with commands that need to observe <c>run</c> without duplicating
/// its target resolution, build, registration, or activation behavior.
/// </summary>
internal interface IRunLaunchObserver
{
    Task BeforeLaunchAsync(string? packageFamilyName, CancellationToken cancellationToken);

    void AfterLaunch(uint processId);
}
