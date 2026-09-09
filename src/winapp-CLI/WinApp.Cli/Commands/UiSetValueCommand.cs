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
using WinApp.Cli.Services.InteractiveDesktop;

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
        IInteractiveDesktopLock desktopLock,
        ILogger<UiSetValueCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui set-value";

        /// <remarks>
        /// A background-safe mutation: it drives ValuePattern rather than the foreground, so it must
        /// stay usable on a locked or headless session and never takes <c>active.lock</c>. But it does
        /// change what the app shows, so it waits behind a foreign workflow's turn instead of editing a
        /// control underneath somebody else's click. Same-workflow commands still overlap under the
        /// ordinary <see cref="UiTurnMode.TurnShared"/> barrier rules.
        /// </remarks>
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.TurnShared;

        protected override int? Preflight(ParseResult parseResult)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var value = parseResult.GetValue(SharedUiOptions.ValueArgument);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

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

            return null;
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            // Preflight rejected a missing selector, so this is non-null by construction.
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument)!;
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var value = parseResult.GetValue(SharedUiOptions.ValueArgument)!;

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
            catch (Exception ex) when (!UiCoordinatedAction.IsCoordinationFault(ex))
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }
    }
}
