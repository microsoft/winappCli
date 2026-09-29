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
        Description = "Search only descendants of this uniquely matching selector (excludes the root).",
    };
    internal static readonly Option<string?> Type = new("--type")
    {
        Description = "UIA control type, case-insensitive. Supports all 41 official types; aliases: TextBox -> Edit, TextBlock -> Text.",
    };
    internal static readonly Option<string?> ClassName = new("--class-name")
    {
        Description = "Exact, case-insensitive UIA ClassName (literal, not a substring or wildcard).",
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

    internal static UiSelector Parse(ParseResult result, IUiSelectorParser parser, string selector) =>
        parser.Parse(selector) with
        {
            Root = result.GetValue(Root) is { } root ? parser.Parse(root) : null,
            ControlType = result.GetValue(Type),
            ClassName = result.GetValue(ClassName),
        };

    internal static bool HasFilters(ParseResult result) =>
        result.GetValue(Root) is not null || result.GetValue(Type) is not null || result.GetValue(ClassName) is not null;

    /// <summary><see cref="Validate"/> for commands whose selector is optional: filters narrow a
    /// selector, so they are rejected when no selector was given.</summary>
    internal static int? ValidateWithOptionalSelector(ParseResult result, string? selector, ILogger logger, bool json)
    {
        if (string.IsNullOrWhiteSpace(selector) && HasFilters(result))
        {
            const string error = "--type, --root, and --class-name narrow a selector; pass a selector too.";
            logger.LogError("{Message}", error);
            UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, error, errorOut: result.InvocationConfiguration.Error);
            return 1;
        }

        return Validate(result, logger, json);
    }

    /// <summary>
    /// For commands whose engine call takes a selector string (inspect, screenshot, record): when
    /// filters are present, resolves the filtered selector to one element and returns its slug, which
    /// identifies that exact element. Without filters the selector is returned unchanged.
    /// </summary>
    /// <returns>The selector to pass on, or <see langword="null"/> when no element matched.</returns>
    /// <exception cref="UiAmbiguousSelectorException">The filtered selector matched several elements.</exception>
    internal static async Task<string?> ResolveExactSelectorAsync(
        ParseResult result, IUiSelectorParser parser, IUiAutomation uiAutomation, UiTarget target, string selector, CancellationToken ct)
    {
        if (!HasFilters(result))
        {
            return selector;
        }

        var element = await uiAutomation.FindSingleElementAsync(target, Parse(result, parser, selector), ct).ConfigureAwait(false);
        if (element is null)
        {
            return null;
        }

        return element.Selector
            ?? throw new InvalidOperationException(
                $"The element matched by '{selector}' has no stable selector. Run 'winapp ui inspect' and pass its slug instead.");
    }
}
