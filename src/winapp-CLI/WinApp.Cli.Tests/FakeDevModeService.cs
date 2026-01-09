// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

internal class FakeDevModeService : IDevModeService
{
    public Task<int> EnsureWin11DevModeAsync(TaskContext taskContext, CancellationToken cancellationToken)
    {
        return Task.FromResult(0);
    }

    public bool IsEnabled()
    {
        return true;
    }
}
