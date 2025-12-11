// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface IDevModeService
{
    public int EnsureWin11DevMode(TaskContext taskContext);
    public bool IsEnabled();
}
