// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

/// <summary>Composable read-query options shared by search, property, value and wait commands.</summary>
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
}
