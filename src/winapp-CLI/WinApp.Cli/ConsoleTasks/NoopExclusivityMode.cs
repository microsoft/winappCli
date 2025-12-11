// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;

namespace WinApp.Cli.ConsoleTasks;

internal sealed class NoopExclusivityMode : IExclusivityMode
{
    public T Run<T>(Func<T> func)
    {
        return func();
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> func)
    {
        return await func().ConfigureAwait(false);
    }
}
