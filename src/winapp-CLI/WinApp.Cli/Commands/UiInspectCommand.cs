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

internal partial class UiInspectCommand : Command, IShortDescription
{
    public string ShortDescription => "View the element tree of a running app";

    public static Option<bool> AncestorsOption { get; }

    static UiInspectCommand()
    {
        AncestorsOption = new Option<bool>("--ancestors")
        {
            Description = "Walk up the tree from the specified element to the root"
        };
    }

    public UiInspectCommand()
        : base("inspect", "View the UI element tree with semantic slugs, element types, names, and bounds.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.DepthOption);
        Options.Add(AncestorsOption);
        Options.Add(SharedUiOptions.InteractiveOption);
        Options.Add(SharedUiOptions.HideDisabledOption);
        Options.Add(SharedUiOptions.HideOffscreenOption);
    }

    public partial class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IAnsiConsole ansiConsole,
        ILogger<UiInspectCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            var selector = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }
            var depth = parseResult.GetRequiredValue(SharedUiOptions.DepthOption);
            var depthExplicit = parseResult.GetResult(SharedUiOptions.DepthOption)?.Implicit == false;
            var ancestors = parseResult.GetValue(AncestorsOption);
            var interactive = parseResult.GetValue(SharedUiOptions.InteractiveOption);
            var hideDisabled = parseResult.GetValue(SharedUiOptions.HideDisabledOption);
            var hideOffscreen = parseResult.GetValue(SharedUiOptions.HideOffscreenOption);

            // --interactive bumps the default depth to 8 (sparse tree after filtering).
            // Only override when the user did NOT explicitly pass --depth, so an explicit
            // `--depth 4` is honored.
            if (interactive && !depthExplicit)
            {
                depth = 8;
            }

            try
            {
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);
                UiElement[] elements;

                if (ancestors && selector is not null)
                {
                    elements = await uiAutomation.InspectAncestorsAsync(uiTarget, selector, cancellationToken);
                    // The ancestor walk returns root-first..target-last but does not assign Depth.
                    // BuildWindows/NestElements use a depth-stack to nest children, so without an
                    // ascending depth assignment all ancestors collapse into sibling roots in JSON.
                    for (int i = 0; i < elements.Length; i++) { elements[i].Depth = i; }
                }
                else
                {
                    elements = await uiAutomation.InspectAsync(uiTarget, selector, depth, cancellationToken);
                }

                // Apply filters (preserve window separator elements)
                if (hideDisabled)
                {
                    elements = elements.Where(e => e.Type == "---" || e.IsEnabled).ToArray();
                }
                if (hideOffscreen)
                {
                    elements = elements.Where(e => e.Type == "---" || !e.IsOffscreen).ToArray();
                }

                // For --interactive, filter to interactive elements but keep the full
                // list for breadcrumb context rendering (non-JSON path).
                var allElements = elements;
                if (interactive)
                {
                    elements = elements.Where(e => e.Type == "---" || IsInteractive(e)).ToArray();
                }

                if (json)
                {
                    // For --interactive, attach an InvokableAncestor hint for elements that don't
                    // themselves support an actionable pattern (e.g. some MenuItems / TabItems).
                    if (interactive)
                    {
                        AttachInvokableAncestors(elements, allElements);
                    }

                    // Build a nested tree grouped by window. The flat element list is in DFS walk
                    // order with separators marking window boundaries. For --interactive we pass
                    // the unfiltered tree so BuildWindows can collapse non-interactive ancestors
                    // and surface them as ancestorPath breadcrumbs on the surviving descendants.
                    var jsonElements = interactive ? allElements : elements;
                    var windows = BuildWindows(jsonElements, uiTarget, interactive);

                    var result = new UiInspectResult
                    {
                        Depth = depth,
                        Interactive = interactive,
                        HideDisabled = hideDisabled,
                        HideOffscreen = hideOffscreen,
                        Windows = windows,
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiInspectResult));
                }
                else
                {
                    // Track ancestor types per depth for breadcrumb rendering in --interactive mode
                    var ancestorTypes = new string?[depth + 10]; // Type at each depth level
                    var lastBreadcrumb = "";

                    foreach (var el in interactive ? allElements : elements)
                    {
                        // Window separator element
                        if (el.Type == "---")
                        {
                            ansiConsole.WriteLine();
                            ansiConsole.MarkupLine($"[grey]--- {EscapeMarkup(el.Name ?? "")} ---[/]");
                            lastBreadcrumb = "";
                            Array.Clear(ancestorTypes, 0, ancestorTypes.Length);
                            continue;
                        }

                        if (interactive && !IsInteractive(el))
                        {
                            // Track as ancestor context for upcoming interactive elements
                            ancestorTypes[el.Depth ?? 0] = el.Type;

                            // Even though this non-interactive ancestor is itself hidden, if its
                            // subtree was truncated (HasMoreChildren), there may be hidden
                            // interactive descendants — emit a breadcrumb + +more hint.
                            if (el.HasMoreChildren == true)
                            {
                                var parts = new List<string>();
                                for (int d = 0; d <= (el.Depth ?? 0); d++)
                                {
                                    if (ancestorTypes[d] is not null) { parts.Add(ancestorTypes[d]!); }
                                }
                                var breadcrumb = string.Join(" > ", parts);
                                if (breadcrumb.Length > 0 && breadcrumb != lastBreadcrumb)
                                {
                                    ansiConsole.MarkupLine($"[grey]{EscapeMarkup(breadcrumb)}[/]");
                                    lastBreadcrumb = breadcrumb;
                                }
                                var moreIndent = new string(' ', ((el.Depth ?? 0) + 1) * 2);
                                ansiConsole.MarkupLine($"{moreIndent}[grey]+more[/]");
                            }
                            continue;
                        }

                        // In --interactive mode, emit a breadcrumb when ancestor path changed
                        if (interactive && (el.Depth ?? 0) > 0)
                        {
                            var parts = new List<string>();
                            for (int d = 0; d < (el.Depth ?? 0); d++)
                            {
                                if (ancestorTypes[d] is not null) { parts.Add(ancestorTypes[d]!); }
                            }
                            var breadcrumb = string.Join(" > ", parts);
                            if (breadcrumb.Length > 0 && breadcrumb != lastBreadcrumb)
                            {
                                ansiConsole.MarkupLine($"[grey]{EscapeMarkup(breadcrumb)}[/]");
                                lastBreadcrumb = breadcrumb;
                            }
                        }

                        var indent = new string(' ', (el.Depth ?? 0) * 2);
                        var elSelector = el.Selector ?? el.Id ?? "";
                        var displayName = el.Name ?? el.AutomationId;
                        var name = displayName is not null && displayName != elSelector
                            ? $" [green]\"{EscapeMarkup(Truncate(displayName, 80))}\"[/]" : "";
                        var value = el.Value is not null && el.Value != el.Name
                            ? $" [yellow]value=\"{EscapeMarkup(Truncate(el.Value, 60))}\"[/]" : "";
                        var toggle = el.ToggleState is not null ? $" [grey][[{el.ToggleState}]][/]" : "";
                        var expand = el.ExpandState is not null ? $" [grey][[{el.ExpandState}]][/]" : "";
                        var scroll = el.ScrollDir is not null ? $" [grey][[scroll:{el.ScrollDir}]][/]" : "";
                        var bounds = el.Width > 0 ? $" [grey]({el.X},{el.Y} {el.Width}x{el.Height})[/]" : "";
                        var disabled = el.IsEnabled ? "" : " [grey][[disabled]][/]";
                        var offscreen = el.IsOffscreen ? " [grey][[offscreen]][/]" : "";
                        ansiConsole.MarkupLine($"{indent}[bold cyan]{EscapeMarkup(elSelector)}[/] {el.Type}{name}{value}{toggle}{expand}{scroll}{bounds}{disabled}{offscreen}");

                        // Render truncated descendants as a single short child line for readability.
                        if (el.HasMoreChildren == true)
                        {
                            var childIndent = new string(' ', ((el.Depth ?? 0) + 1) * 2);
                            ansiConsole.MarkupLine($"{childIndent}[grey]+more[/]");
                        }
                    }

                    // Footer with example using first interactive element or first element
                    var realElements = (interactive ? allElements : elements).Where(e => e.Type != "---").ToArray();
                    var displayedElements = interactive
                        ? realElements.Where(IsInteractive).ToArray()
                        : realElements;
                    var separators = (interactive ? allElements : elements).Where(e => e.Type == "---").ToArray();
                    var truncated = realElements.Count(e => e.HasMoreChildren == true);
                    var example = realElements.FirstOrDefault(IsInteractive) ?? realElements.FirstOrDefault();
                    var exampleSelector = example?.Selector ?? example?.Id;
                    var exampleHint = exampleSelector is not null
                        ? $" Use the [bold cyan]first token[/] as selector, e.g.: [grey]winapp ui invoke {EscapeMarkup(exampleSelector)} -a <app>[/]"
                        : "";
                    ansiConsole.WriteLine();
                    ansiConsole.MarkupLine($"[grey]Found {displayedElements.Length} elements (--depth {depth}).{exampleHint}[/]");
                    if (truncated > 0)
                    {
                        ansiConsole.MarkupLine($"[grey]{truncated} element(s) have hidden children ([yellow]+more[/]) - re-run with [bold]--depth {depth + 4}[/] to expand.[/]");
                    }
                    if (separators.Length > 1)
                    {
                        ansiConsole.MarkupLine("[grey]Use -w <HWND> to target a specific window.[/]");
                    }
                    if (!interactive)
                    {
                        ansiConsole.MarkupLine("[grey]Use -i to only show interactive elements.[/]");
                    }
                }

                logger.LogDebug("Inspect returned {Count} elements at depth {Depth}", elements.Length, depth);
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

        private static string EscapeMarkup(string text) => Markup.Escape(SanitizeForDisplay(text));

        private static string Truncate(string text, int maxLength)
        {
            if (text.Length <= maxLength) { return text; }
            return string.Concat(text.AsSpan(0, maxLength), "…");
        }

        /// <summary>Replace control characters (newlines, tabs, carriage returns) with visual representations for single-line display.</summary>
        private static string SanitizeForDisplay(string text)
        {
            if (text.AsSpan().IndexOfAny('\r', '\n', '\t') < 0)
            {
                return text;
            }
            return text.Replace("\r\n", "↵").Replace("\r", "↵").Replace("\n", "↵").Replace("\t", "→");
        }

        // ControlType allowlist for back-compat: even if pattern detection misses something,
        // these types are conventionally interactive.
        private static readonly HashSet<string> InteractiveTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Button", "CheckBox", "ComboBox", "Edit", "TextBox", "Hyperlink",
            "ListItem", "MenuItem", "RadioButton", "Tab", "TabItem", "SplitButton",
            "TreeItem", "DataItem", "Slider"
        };

        /// <summary>An element is interactive if it supports an actionable UIA pattern OR matches a conventional control type.</summary>
        private static bool IsInteractive(UiElement el)
            => el.IsInvokable || InteractiveTypes.Contains(el.Type);

        /// <summary>For each interactive element without its own actionable pattern, find the nearest
        /// invokable ancestor in the unfiltered element list and attach it as a fallback hint.</summary>
        private static void AttachInvokableAncestors(UiElement[] filtered, UiElement[] all)
        {
            // Index 'all' by selector for fast lookup.
            var bySelector = new Dictionary<string, UiElement>(StringComparer.Ordinal);
            foreach (var el in all)
            {
                var key = el.Selector ?? el.Id;
                if (!string.IsNullOrEmpty(key)) { bySelector[key] = el; }
            }

            foreach (var el in filtered)
            {
                if (el.Type == "---" || el.IsInvokable) { continue; }

                var parentKey = el.ParentSelector;
                while (!string.IsNullOrEmpty(parentKey) && bySelector.TryGetValue(parentKey, out var parent))
                {
                    if (parent.IsInvokable)
                    {
                        el.InvokableAncestor = parent;
                        break;
                    }
                    parentKey = parent.ParentSelector;
                }
            }
        }

        // Pattern: HWND <hwnd>: "<title>" (<label>, <className>[, owner: HWND <ownerHwnd>])
        [System.Text.RegularExpressions.GeneratedRegex(
            @"^HWND\s+\d+:\s+""(?<title>.*)""\s+\((?<label>[^,]+),\s*(?<class>[^,)]+)(,\s*owner:.*)?\)$")]
        private static partial System.Text.RegularExpressions.Regex SeparatorRegex();

        /// <summary>Group the flat element walk into per-window nested trees. The flat list is in
        /// DFS order; separators (Type=="---") delimit independent windows. For --interactive,
        /// keeps the full structural tree but prunes subtrees with no interactive descendants.
        /// Clears redundant per-element fields (depth/parentSelector/ancestorPath/windowHandle)
        /// since they're implied by the tree structure.</summary>
        private static UiInspectWindowInfo[] BuildWindows(UiElement[] elements, UiTarget uiTarget, bool interactive)
        {
            var windows = new List<UiInspectWindowInfo>();
            UiInspectWindowInfo? current = null;
            var bucket = new List<UiElement>();

            void Flush()
            {
                if (current is null) { return; }
                var roots = NestElements(bucket, interactive, out var totalCount);
                current.Elements = roots;
                current.ElementCount = totalCount;
                if (roots.Length > 0 || current.Hwnd != 0) { windows.Add(current); }
                bucket.Clear();
            }

            foreach (var el in elements)
            {
                if (el.Type == "---")
                {
                    Flush();
                    var match = el.Name is not null ? SeparatorRegex().Match(el.Name) : null;
                    current = new UiInspectWindowInfo
                    {
                        Hwnd = el.WindowHandle ?? 0,
                        Title = match?.Success == true ? match.Groups["title"].Value : el.Name,
                        ClassName = match?.Success == true ? match.Groups["class"].Value.Trim() : null,
                    };
                }
                else
                {
                    // No separator yet — implicit single window. Initialize from session info.
                    if (current is null)
                    {
                        current = new UiInspectWindowInfo
                        {
                            Hwnd = el.WindowHandle ?? uiTarget.WindowHandle,
                            Title = uiTarget.WindowTitle,
                        };
                    }
                    bucket.Add(el);
                }
            }
            Flush();

            return windows.ToArray();
        }

        /// <summary>Build a nested tree from a DFS-ordered flat list using a depth-stack.
        /// Clears redundant fields and (for --interactive) prunes branches with no interactive descendants.</summary>
        private static UiElement[] NestElements(List<UiElement> flat, bool interactive, out int totalCount)
        {
            var roots = new List<UiElement>();
            var stack = new Stack<(UiElement el, List<UiElement> kids)>();

            foreach (var el in flat)
            {
                var d = el.Depth ?? 0;
                while (stack.Count > 0 && (stack.Peek().el.Depth ?? 0) >= d) { Finalize(stack.Pop()); }

                var kids = new List<UiElement>();
                if (stack.Count == 0) { roots.Add(el); }
                else { stack.Peek().kids.Add(el); }
                stack.Push((el, kids));
            }
            while (stack.Count > 0) { Finalize(stack.Pop()); }

            // Prune for --interactive: drop subtrees with no interactive descendants,
            // then collapse non-interactive intermediate nodes — surviving nodes are
            // interactive elements (or +more sentinels), each with an ancestorPath
            // showing the collapsed chain of non-interactive types above it.
            if (interactive)
            {
                roots = roots.Where(r => PruneInteractive(r)).ToList();
                roots = CollapseNonInteractive(roots, droppedAbove: []);
            }

            // Strip redundant tree-rebuild metadata (implied by nesting)
            totalCount = 0;
            foreach (var r in roots) { StripRedundant(r, ref totalCount, interactive); }
            return roots.ToArray();

            static void Finalize((UiElement el, List<UiElement> kids) frame)
            {
                frame.el.Children = frame.kids.Count > 0 ? frame.kids.ToArray() : null;
            }

            static bool PruneInteractive(UiElement el)
            {
                if (el.Children is { Length: > 0 } kids)
                {
                    var keptKids = kids.Where(PruneInteractive).ToArray();
                    el.Children = keptKids.Length > 0 ? keptKids : null;
                }
                return IsInteractive(el) || el.HasMoreChildren == true || (el.Children?.Length ?? 0) > 0;
            }

            // Collapse non-interactive intermediate nodes. A node "survives" if it's interactive
            // or carries the +more truncation hint. Non-survivors are stripped from the tree;
            // their type is appended to droppedAbove and propagated to their (recursively collapsed)
            // children, which are spliced up to take their place. Each surviving node gets the
            // accumulated droppedAbove chain attached as ancestorPath.
            static List<UiElement> CollapseNonInteractive(List<UiElement> nodes, List<string> droppedAbove)
            {
                var output = new List<UiElement>();
                foreach (var el in nodes)
                {
                    var selfSurvives = IsInteractive(el) || el.HasMoreChildren == true;
                    var childrenInput = el.Children?.ToList() ?? [];

                    if (selfSurvives)
                    {
                        var collapsedKids = CollapseNonInteractive(childrenInput, droppedAbove: []);
                        el.Children = collapsedKids.Count > 0 ? collapsedKids.ToArray() : null;
                        el.AncestorPath = droppedAbove.Count > 0 ? droppedAbove.ToArray() : null;
                        output.Add(el);
                    }
                    else
                    {
                        var nextDropped = new List<string>(droppedAbove) { el.Type };
                        output.AddRange(CollapseNonInteractive(childrenInput, nextDropped));
                    }
                }
                return output;
            }

            static void StripRedundant(UiElement el, ref int count, bool interactive)
            {
                count++;
                el.Id = null;
                el.Depth = null;
                el.ParentSelector = null;
                el.WindowHandle = null;
                // Flatten InvokableAncestor to a hint (no nested Children / no nested ancestor)
                // — without this System.Text.Json can hit a reference cycle when the surviving
                // descendant points back to one of its own ancestors that's also in the tree.
                if (el.InvokableAncestor is { } anc)
                {
                    el.InvokableAncestor = new UiElement
                    {
                        Type = anc.Type,
                        Name = anc.Name,
                        AutomationId = anc.AutomationId,
                        Selector = anc.Selector,
                        IsInvokable = anc.IsInvokable,
                    };
                }
                // In non-interactive mode the tree is complete, so ancestorPath is redundant.
                // In interactive mode it's populated only with the collapsed non-interactive chain.
                if (!interactive) { el.AncestorPath = null; }
                if (el.Children is { } kids)
                {
                    foreach (var c in kids) { StripRedundant(c, ref count, interactive); }
                }
            }
        }
    }
}
