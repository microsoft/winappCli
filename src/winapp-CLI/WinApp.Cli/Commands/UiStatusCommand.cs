// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiStatusCommand : Command, IShortDescription
{
    public string ShortDescription => "Connect to a running app and show connection info";

    public UiStatusCommand()
        : base("status", "Connect to a target app and display connection info.")
    {
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IAnsiConsole ansiConsole,
        ILogger<UiStatusCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            try
            {
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);

                if (json)
                {
                    var result = new UiStatusResult
                    {
                        ProcessId = uiTarget.ProcessId,
                        ProcessName = uiTarget.ProcessName,
                        WindowTitle = uiTarget.WindowTitle,
                        Hwnd = uiTarget.WindowHandle,
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiStatusResult));
                }
                else
                {
                    ansiConsole.WriteLine($"Process: {uiTarget.ProcessName}");
                    ansiConsole.WriteLine($"PID: {uiTarget.ProcessId}");
                    ansiConsole.WriteLine($"Window: {uiTarget.WindowTitle ?? "(none)"}");
                    if (uiTarget.WindowHandle != 0)
                    {
                        ansiConsole.WriteLine($"HWND: {uiTarget.WindowHandle}");
                    }
                }

                if (!json)
                {
                    logger.LogInformation("Connected to {ProcessName} (PID {ProcessId})", uiTarget.ProcessName, uiTarget.ProcessId);
                }
                return 0;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }
    }
}
