// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using System.CommandLine;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.Performance;

internal interface IPerformanceUiCommandInvoker
{
    Task<int> InvokeAsync(
        IReadOnlyList<string> uiArguments,
        long windowHandle,
        CancellationToken cancellationToken);

    Task<int> YieldAsync(CancellationToken cancellationToken);
}

internal sealed class PerformanceUiCommandInvoker : IPerformanceUiCommandInvoker
{
    private readonly Command _uiCommand;

    public PerformanceUiCommandInvoker(UiCommand uiCommand)
        : this((Command)uiCommand)
    {
    }

    internal PerformanceUiCommandInvoker(Command uiCommand)
    {
        _uiCommand = uiCommand;
    }

    public Task<int> InvokeAsync(
        IReadOnlyList<string> uiArguments,
        long windowHandle,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>(uiArguments.Count + 2);
        arguments.AddRange(uiArguments);
        arguments.Add("--window");
        arguments.Add(windowHandle.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return InvokeCoreAsync(arguments, cancellationToken);
    }

    public Task<int> YieldAsync(CancellationToken cancellationToken) =>
        InvokeCoreAsync(["yield"], cancellationToken);

    private async Task<int> InvokeCoreAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var parseResult = _uiCommand.Parse(
            [.. arguments],
            WinAppParserConfiguration.Default);
        if (parseResult.Errors.Count > 0)
        {
            return 1;
        }

        parseResult.InvocationConfiguration.Output = TextWriter.Null;
        parseResult.InvocationConfiguration.Error = TextWriter.Null;
        return await parseResult.InvokeAsync(
            parseResult.InvocationConfiguration,
            cancellationToken);
    }
}
