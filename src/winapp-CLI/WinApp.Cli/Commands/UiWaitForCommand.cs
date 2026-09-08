// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiWaitForCommand : Command, IShortDescription
{
    public string ShortDescription => "Wait for an element to appear, disappear, or change";

    public static Option<bool> GoneOption { get; }
    public static Option<string?> ValueOption { get; }
    public static Option<bool> ContainsOption { get; }

    static UiWaitForCommand()
    {
        GoneOption = new Option<bool>("--gone")
        {
            Description = "Wait for element to disappear instead of appear"
        };

        ValueOption = new Option<string?>("--value")
        {
            Description = "Wait for element value to equal this string. Uses smart fallback (TextPattern -> ValuePattern -> Name). Combine with --property to check a specific property instead."
        };

        ContainsOption = new Option<bool>("--contains")
        {
            Description = "Use substring matching for --value instead of exact match"
        };
    }

    public UiWaitForCommand()
        : base("wait-for", "Wait for an element to appear, disappear, or have a property reach a target value. " +
               "Polls at 100ms intervals until condition met or timeout.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.TimeoutOption);
        Options.Add(SharedUiOptions.PropertyOption);
        Options.Add(GoneOption);
        Options.Add(ValueOption);
        Options.Add(ContainsOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IUiSelectorParser selectorParser,
        IPollDelay pollDelay,
        IAnsiConsole ansiConsole,
        ILogger<UiWaitForCommand> logger) : AsynchronousCommandLineAction
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
            var timeout = parseResult.GetRequiredValue(SharedUiOptions.TimeoutOption);
            var gone = parseResult.GetValue(GoneOption);
            var property = parseResult.GetValue(SharedUiOptions.PropertyOption);
            var value = parseResult.GetValue(ValueOption);
            var contains = parseResult.GetValue(ContainsOption);

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "wait-for", json);
                return 1;
            }

            if (value != null && string.IsNullOrWhiteSpace(property))
            {
                // --value without --property: use smart get-value fallback (TextPattern → ValuePattern → Name)
                // No error — this is the recommended usage
            }

            try
            {
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);
                var selector = selectorParser.Parse(selectorStr);
                var sw = Stopwatch.StartNew();

                while (sw.ElapsedMilliseconds < timeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    UiElement? element;
                    try
                    {
                        element = await uiAutomation.FindSingleElementAsync(uiTarget, selector, cancellationToken);
                    }
                    catch
                    {
                        element = null;
                    }

                    if (gone)
                    {
                        if (element is null)
                        {
                            if (json)
                            {
                                var result = new UiWaitForResult { Found = false, WaitedMs = (int)sw.ElapsedMilliseconds };
                                ansiConsole.Profile.Out.Writer.WriteLine(
                                    JsonSerializer.Serialize(result, UiJsonContext.Default.UiWaitForResult));
                            }
                            else
                            {
                                logger.LogInformation("Element disappeared after {Elapsed}ms", sw.ElapsedMilliseconds);
                            }
                            return 0;
                        }
                    }
                    else if (element is not null)
                    {
                        // Check value condition
                        if (value is not null)
                        {
                            string? currentValue = null;

                            if (property is not null)
                            {
                                // --property specified: check raw UIA property
                                var props = await uiAutomation.GetPropertiesAsync(uiTarget, element, property, cancellationToken);
                                if (props.TryGetValue(property, out var propValue))
                                {
                                    currentValue = propValue?.ToString();
                                }
                            }
                            else
                            {
                                // No --property: use smart fallback (TextPattern → ValuePattern → Name)
                                currentValue = await uiAutomation.GetTextAsync(uiTarget, element, cancellationToken);
                            }

                            var valueMatches = contains
                                ? currentValue?.Contains(value!, StringComparison.OrdinalIgnoreCase) == true
                                : string.Equals(currentValue, value, StringComparison.OrdinalIgnoreCase);

                            if (valueMatches)
                            {
                                if (json)
                                {
                                    UiElementScrubber.Scrub(element);
                                    var result = new UiWaitForResult
                                    {
                                        Found = true,
                                        WaitedMs = (int)sw.ElapsedMilliseconds,
                                        Element = element
                                    };
                                    ansiConsole.Profile.Out.Writer.WriteLine(
                                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiWaitForResult));
                                }
                                else
                                {
                                    var source = property is not null ? $"{property}=" : "value=";
                                    logger.LogInformation("Element found with {Source}\"{Value}\" after {Elapsed}ms", source, value, sw.ElapsedMilliseconds);
                                }
                                return 0;
                            }
                            // Value doesn't match yet — keep polling
                        }
                        else
                        {
                            if (json)
                            {
                                UiElementScrubber.Scrub(element);
                                var result = new UiWaitForResult
                                {
                                    Found = true,
                                    WaitedMs = (int)sw.ElapsedMilliseconds,
                                    Element = element
                                };
                                ansiConsole.Profile.Out.Writer.WriteLine(
                                    JsonSerializer.Serialize(result, UiJsonContext.Default.UiWaitForResult));
                            }
                            else
                            {
                                logger.LogInformation("Element found after {Elapsed}ms", sw.ElapsedMilliseconds);
                            }
                            return 0;
                        }
                    }

                    await pollDelay.DelayAsync(100, cancellationToken);
                }

                // Timeout exhausted without satisfying the condition.
                if (json)
                {
                    var result = new UiWaitForResult
                    {
                        Found = false,
                        TimedOut = true,
                        WaitedMs = (int)sw.ElapsedMilliseconds,
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiWaitForResult));
                }
                else
                {
                    logger.LogError("'{Selector}' not found after {Timeout}ms", selectorStr, timeout);
                }
                return 1;
            }
            catch (OperationCanceledException)
            {
                logger.LogError("Wait cancelled");
                UiJsonError.Emit(json, UiJsonError.CodeInternalError, "Wait cancelled");
                return 1;
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
