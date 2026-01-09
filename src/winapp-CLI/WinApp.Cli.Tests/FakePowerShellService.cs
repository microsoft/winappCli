// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

internal class FakePowerShellService : IPowerShellService
{
    public Task<(int exitCode, string output)> RunCommandAsync(string command, TaskContext taskContext, bool elevated = false, Dictionary<string, string>? environmentVariables = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult((0, "Fake PowerShell command executed successfully."));
    }
}
