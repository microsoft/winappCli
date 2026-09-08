// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiSetValueCommand : Command, IShortDescription
{
    public string ShortDescription => "Set a value on an element via UIA ValuePattern (with LegacyIAccessible fallback)";

    public UiSetValueCommand()
        : base("set-value", "Set a value on an element programmatically. " +
               "Works for TextBox, ComboBox, Slider, and other editable controls via UIA ValuePattern/RangeValuePattern, " +
               "with a LegacyIAccessible (put_accValue) fallback for TextPattern-only edit controls — no app foreground required. " +
               "Some rich text controls (e.g. WinUI 3 RichEditBox and WPF RichTextBox) don't support setting their value programmatically — " +
               "use the 'send-keys' command with '--via send-input' to type into them instead. Usage: winapp ui set-value <selector> <value> -a <app>")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Arguments.Add(SharedUiOptions.ValueArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IUiSelectorParser selectorParser,
        IAnsiConsole ansiConsole,
        ILogger<UiSetValueCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }
            var value = parseResult.GetValue(SharedUiOptions.ValueArgument);

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "set-value", json);
                return 1;
            }
            if (value is null)
            {
                logger.LogError("{Symbol} A value is required. Usage: winapp ui set-value <selector> <value> -a <app>", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    "A value is required. Usage: winapp ui set-value <selector> <value> -a <app>");
                return 1;
            }

            try
            {
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);
                var selector = selectorParser.Parse(selectorStr);
                var element = await uiAutomation.FindSingleElementAsync(uiTarget, selector, cancellationToken);

                if (element is null)
                {
                    UiErrors.ElementNotFound(logger, selectorStr, json);
                    return 1;
                }

                await uiAutomation.SetValueAsync(uiTarget, element, value, cancellationToken);
                if (json)
                {
                    var result = new UiSetValueResult { ElementId = (element.Selector ?? element.Id ?? ""), Hwnd = uiTarget.WindowHandle };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiSetValueResult));
                }
                else
                {
                    logger.LogInformation("Set value on {ElementId}", (element.Selector ?? element.Id ?? ""));
                }
                return 0;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json);
                return 1;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }
    }
}
