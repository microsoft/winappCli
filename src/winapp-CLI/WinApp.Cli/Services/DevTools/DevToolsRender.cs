// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// The one-line renderings the agent-facing <c>winapp devtools</c> commands share. Every element line starts
/// with its selector in brackets, because that bracketed token is what the next command accepts verbatim —
/// output and input are the same vocabulary. The selector is the element's <c>x:Name</c> when that names
/// it uniquely, else a semantic slug, else the raw handle; see <see cref="DevToolsSelector.Display"/>.
/// </summary>
internal static class DevToolsRender
{
    internal static void WriteTextLine(IAnsiConsole console, string text)
    {
        if (console.Profile.Out.IsTerminal) { console.WriteLine(text); }
        else { console.Profile.Out.Writer.WriteLine(TerminalText.Sanitize(text)); }
    }

    internal static void WriteMarkupLine(IAnsiConsole console, string markup)
    {
        if (console.Profile.Out.IsTerminal) { console.MarkupLine(markup); }
        else { WriteTextLine(console, Markup.Remove(markup)); }
    }

    public static string Node(VisualTreeNode node) =>
        Line(node.Selector, node.ShortType, node.Name, node.ShortFile);

    public static string Match(DevToolsSelector.VisualTreeMatch match) =>
        Line(match.Selector, match.ShortType, match.Name, match.ShortFile);

    public static string Handle(string selector) => $"[bold cyan][[{Markup.Escape(selector)}]][/]";

    public static string Header(string handle, VisualTreeNode? element, string? suffix = null)
    {
        if (element is null)
        {
            return Handle(handle) + (suffix is null ? string.Empty : $".{Markup.Escape(suffix)}");
        }

        return suffix is null
            ? Line(element.Selector, element.ShortType, element.Name, element.ShortFile)
            : Line(element.Selector, element.ShortType, element.Name, null) + $".{Markup.Escape(suffix)}";
    }

    private static string Line(string selector, string shortType, string name, string? shortFile)
    {
        // The x:Name is dropped from the label when the selector already IS the name: printing
        // `[SubmitButton] Button x:Name="SubmitButton"` says the same thing twice.
        var named = string.IsNullOrEmpty(name) || string.Equals(name, selector, StringComparison.Ordinal)
            ? string.Empty
            : $" [green]x:Name=\"{Markup.Escape(name)}\"[/]";
        var file = shortFile is null ? string.Empty : $" [grey]{Markup.Escape(shortFile)}[/]";
        return $"{Handle(selector)} {Markup.Escape(shortType)}{named}{file}";
    }
}
