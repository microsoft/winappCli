// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// A selector that names Microsoft.UI.Reactor elements rather than an <c>x:Name</c>: a component
/// (<c>&lt;Counter&gt;</c>), a key (<c>key=t1</c>) or a C# call site (<c>App.cs:35</c>), optionally followed by
/// <c>/</c>-separated steps that narrow to elements below it (<c>&lt;Counter&gt;/Button</c>,
/// <c>&lt;Settings&gt;/&lt;Counter&gt;/key=inc</c>).
/// </summary>
internal sealed partial class ReactorSelector
{
    private enum StepKind { Component, Key, Site, Element }

    private sealed record Step(StepKind Kind, string Value, int Line = 0, int Column = 0);

    private readonly IReadOnlyList<Step> steps;

    private ReactorSelector(IReadOnlyList<Step> steps) => this.steps = steps;

    /// <summary>The parsed selector, or null when it is not a Reactor selector (an x:Name, a slug or a handle).</summary>
    public static ReactorSelector? Parse(string selector)
    {
        selector = selector.Trim();
        if (selector.Length == 0)
        {
            return null;
        }
        // A call site is one step on its own: File.cs:35 or File.cs:35:13. A #line directive can name a file of any
        // kind (Widget.rxt:3); XAML lines are not Reactor call sites.
        if (SiteStep().Match(selector) is { Success: true } site &&
            !site.Groups["file"].Value.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            return new([new Step(StepKind.Site, site.Groups["file"].Value, int.Parse(site.Groups["line"].Value,
                System.Globalization.CultureInfo.InvariantCulture),
                site.Groups["column"].Success ? int.Parse(site.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0)]);
        }
        if (selector[0] != '<' && !selector.StartsWith("key=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var steps = new List<Step>();
        foreach (var text in SplitSteps(selector).Select(part => part.Trim()))
        {
            if (text.Length > 2 && text[0] == '<' && text[^1] == '>' && text[1..^1].Trim() is { Length: > 0 } name)
            {
                steps.Add(new Step(StepKind.Component, name));
            }
            else if (text.StartsWith("key=", StringComparison.OrdinalIgnoreCase) && text.Length > 4)
            {
                steps.Add(new Step(StepKind.Key, text[4..]));
            }
            else if (steps.Count > 0 && ElementStep().IsMatch(text))
            {
                steps.Add(new Step(StepKind.Element, text));
            }
            else
            {
                return null;
            }
        }
        return steps.Count == 0 ? null : new ReactorSelector(steps);
    }

    /// <summary>The Reactor elements the selector names, each once, in description order.</summary>
    public IReadOnlyList<ReactorNode> Match(ReactorDescription reactor)
    {
        IEnumerable<ReactorNode> current = reactor.ByHandle.Values.Where(node => Matches(steps[0], node));
        for (var i = 1; i < steps.Count; i++)
        {
            var step = steps[i];
            current = current.SelectMany(Descendants).Where(node => Matches(step, node)).ToArray();
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matches = current.Where(node => seen.Add(node.Handle)).ToList();
        // A component's wrapper and the root control a host renders both answer to <Name>; prefer exact case.
        if (steps[^1].Kind == StepKind.Component)
        {
            var exact = matches.Where(node => ComponentNamed(node, steps[^1].Value, StringComparison.Ordinal)).ToList();
            if (exact.Count > 0)
            {
                return exact;
            }
        }
        return matches;
    }

    private static bool Matches(Step step, ReactorNode node) => step.Kind switch
    {
        StepKind.Component => ComponentNamed(node, step.Value, StringComparison.OrdinalIgnoreCase),
        StepKind.Key => string.Equals(node.Key, step.Value, StringComparison.Ordinal),
        StepKind.Element => string.Equals(node.Element, step.Value, StringComparison.OrdinalIgnoreCase),
        StepKind.Site => node.File is { } file && node.Line == step.Line && (step.Column == 0 || node.Column == step.Column) &&
            SameFile(file, step.Value),
        _ => false,
    };

    private static bool ComponentNamed(ReactorNode node, string name, StringComparison comparison)
    {
        bool Named(ReactorComponent? component) =>
            component is not null && (string.Equals(component.Name, name, comparison) || string.Equals(component.Type, name, comparison));
        return Named(node.Component) || Named(node.Host?.Root);
    }

    private static IEnumerable<ReactorNode> Descendants(ReactorNode node)
    {
        var stack = new Stack<ReactorNode>(node.Children);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            yield return next;
            foreach (var child in next.Children)
            {
                stack.Push(child);
            }
        }
    }

    // A file name (App.cs) or the end of a path, with either separator (Panels/Footer.cs, ..\Shared\Badge.cs): the
    // recorded file is relative to the project that compiled it, or a full path, so leading ..\ and .\ are dropped.
    private static bool SameFile(string recorded, string wanted)
    {
        var parts = wanted.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).SkipWhile(part => part is "." or "..");
        var tail = string.Join('\\', parts);
        var path = recorded.Replace('/', '\\');
        return tail.Length > 0 && (string.Equals(path, tail, StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith('\\' + tail, StringComparison.OrdinalIgnoreCase));
    }

    // Steps are separated by '/' outside a <...> component name (generic names such as <List<int>> stay whole).
    private static IEnumerable<string> SplitSteps(string selector)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < selector.Length; i++)
        {
            switch (selector[i])
            {
                case '<': depth++; break;
                case '>': depth = Math.Max(0, depth - 1); break;
                case '/' when depth == 0:
                    yield return selector[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return selector[start..];
    }

    [GeneratedRegex(@"^(?<file>[^:<>""|?*]+\.[A-Za-z][A-Za-z0-9_]{0,15}):(?<line>\d{1,9})(:(?<column>\d{1,9}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex SiteStep();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ElementStep();
}
