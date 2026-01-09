// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface IStatusService
{
    public Task<int> ExecuteWithStatusAsync<T>(string inProgressMessage, Func<TaskContext, CancellationToken, Task<(int ReturnCode, T CompletedMessage)>> taskFunc, CancellationToken cancellationToken);
}
