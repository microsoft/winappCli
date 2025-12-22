// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface IDevModeService
{
    public Task<int> EnsureWin11DevModeAsync(TaskContext taskContext, CancellationToken cancellationToken);
    public bool IsEnabled();
}
