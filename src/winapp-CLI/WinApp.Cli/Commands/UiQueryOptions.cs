// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

/// <summary>Composable element filters shared by every command that takes a selector.</summary>
internal static class UiQueryOptions
{
    internal static readonly Option<string?> Root = new("--root")
    {
        HelpName = "selector",
        Description = "Only search inside this element (must match exactly one element)",
    };
    internal static readonly Option<string?> Type = new("--type")
    {
        Description = "Only match this control type (Button, Edit, MenuItem, ...; TextBox = Edit, TextBlock = Text). Works without a selector",
    };
    internal static readonly Option<string?> ClassName = new("--class-name")
    {
        HelpName = "name",
        Description = "Only match this exact UIA ClassName (case-insensitive). Works without a selector",
    };

    internal static void AddTo(Command command)
    {
        command.Options.Add(Root);
        command.Options.Add(Type);
        command.Options.Add(ClassName);
    }

    internal static int? Validate(ParseResult result, ILogger logger, bool json)
    {
        var type = result.GetValue(Type);
        var root = result.GetValue(Root);
        string? error = type is not null && UiControlTypes.GetId(type) == 0
            ? $"Unknown UIA control type '{type}'. Use an official UIA type (for example Button or Edit), TextBox, or TextBlock."
            : root is not null && string.IsNullOrWhiteSpace(root) ? "--root requires a non-empty selector." : null;
        if (error is null) { return null; }
        logger.LogError("{Message}", error);
        UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, error, errorOut: result.InvocationConfiguration.Error);
        return 1;
    }

    /// <summary>Builds the query. With no selector, <c>--type</c> and <c>--class-name</c> are the whole query.</summary>
    internal static UiSelector Parse(ParseResult result, IUiSelectorParser parser, string? selector) =>
        (string.IsNullOrWhiteSpace(selector) ? new UiSelector() : parser.Parse(selector)) with
        {
            Root = result.GetValue(Root) is { } root ? parser.Parse(root) : null,
            ControlType = result.GetValue(Type),
            ClassName = result.GetValue(ClassName),
        };

    internal static bool HasFilters(ParseResult result) =>
        result.GetValue(Root) is not null || HasElementFilters(result);

    /// <summary>Whether <c>--type</c> or <c>--class-name</c> was given. Either can stand in for a selector.</summary>
    internal static bool HasElementFilters(ParseResult result) =>
        result.GetValue(Type) is not null || result.GetValue(ClassName) is not null;

    /// <summary>
    /// Whether there is something to look for: a selector, or <c>--type</c>/<c>--class-name</c> on their own.
    /// <c>--root</c> alone is not a query, because it only says where to look.
    /// </summary>
    internal static bool HasQuery(ParseResult result, string? selector) =>
        !string.IsNullOrWhiteSpace(selector) || HasElementFilters(result);

    /// <summary>The selector for messages, or the filters that replace it when there is no selector.</summary>
    internal static string Describe(ParseResult result, string? selector)
    {
        if (!string.IsNullOrWhiteSpace(selector)) { return selector; }
        var parts = new List<string>(2);
        if (result.GetValue(Type) is { } type) { parts.Add($"--type {type}"); }
        if (result.GetValue(ClassName) is { } className) { parts.Add($"--class-name {className}"); }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Finds the one element a command acts on. With filters, the match must be unique across every
    /// window searched, so a same-named control in an owned dialog fails as ambiguous instead of the
    /// main window's match being used. Without filters, the existing main-window-first lookup applies.
    /// </summary>
    internal static Task<UiElement?> FindTargetAsync(
        ParseResult result, IUiAutomation uiAutomation, UiTarget target, UiSelector selector, CancellationToken ct) =>
        HasFilters(result)
            ? uiAutomation.FindSingleElementAsync(target, selector, requireUnique: true, ct)
            : uiAutomation.FindSingleElementAsync(target, selector, ct);

    /// <summary><see cref="Validate"/> for commands whose selector is optional. <c>--type</c> or
    /// <c>--class-name</c> without a selector targets the one element they match; <c>--root</c> alone is
    /// rejected, because it only scopes a query.</summary>
    internal static int? ValidateWithOptionalSelector(ParseResult result, string? selector, ILogger logger, bool json)
    {
        if (string.IsNullOrWhiteSpace(selector) && HasFilters(result) && !HasElementFilters(result))
        {
            const string error = "--root only scopes a query; pass a selector, --type, or --class-name too.";
            logger.LogError("{Message}", error);
            UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, error, errorOut: result.InvocationConfiguration.Error);
            return 1;
        }

        return Validate(result, logger, json);
    }

    /// <summary>
    /// For commands whose engine call takes a selector string (inspect, screenshot, record): when
    /// filters are present, resolves the filtered selector to one element, whose
    /// <see cref="UiElement.Selector"/> is a slug that identifies that exact element. Without filters
    /// the selector is returned unchanged in an element with no window handle.
    /// </summary>
    /// <param name="searchOtherWindows">True when the caller can find the slug in the app's other
    /// top-level windows too (record). Inspect and screenshot look only in the target window, so a match
    /// elsewhere would be lost and they would silently fall back to the whole window.</param>
    /// <returns>The element whose selector to pass on, or <see langword="null"/> when no element matched.</returns>
    /// <exception cref="UiAmbiguousSelectorException">The filtered selector matched several elements.</exception>
    internal static async Task<UiElement?> ResolveExactSelectorAsync(
        ParseResult result, IUiSelectorParser parser, IUiAutomation uiAutomation, UiTarget target, string? selector,
        bool searchOtherWindows, CancellationToken ct)
    {
        if (!HasFilters(result))
        {
            return new UiElement { Selector = selector };
        }

        var scope = searchOtherWindows || target.WindowHandle == 0 || target.IsExplicitWindow
            ? target
            : new UiTarget
            {
                ProcessId = target.ProcessId,
                ProcessName = target.ProcessName,
                WindowTitle = target.WindowTitle,
                WindowHandle = target.WindowHandle,
                IsExplicitWindow = true,
            };
        var element = await uiAutomation.FindSingleElementAsync(
            scope, Parse(result, parser, selector), requireUnique: true, ct).ConfigureAwait(false);
        if (element is null)
        {
            return null;
        }

        return element.Selector is not null
            ? element
            : throw new InvalidOperationException(
                $"The element matched by '{Describe(result, selector)}' has no stable selector. Run 'winapp ui inspect' and pass its slug instead.");
    }
}
