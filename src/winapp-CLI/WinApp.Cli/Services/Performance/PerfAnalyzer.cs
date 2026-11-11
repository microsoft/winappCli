// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

internal sealed class PerfElement
{
    public required string Id { get; init; }
    public required string ObjectId { get; init; }
    public string? Type { get; set; }
    public string? Name { get; set; }
    public string? Source { get; set; }
    public string? Line { get; set; }
    /// <summary>The Reactor component whose render span was innermost when WinUI created this element.</summary>
    public string? Component { get; set; }
    public string? ComponentId { get; set; }
    /// <summary>Where that component was created in C# (<c>File.cs:line</c>), when Reactor's source map recorded it.</summary>
    public string? ComponentCallSite { get; set; }
    public double FirstObservedMs { get; set; }
    /// <summary>Time of the WinUI Created event, when it was recorded.</summary>
    public double? CreatedMs { get; set; }
    public double? DestroyedMs { get; set; }
    public bool CreationObserved { get; set; }
    public List<PerfParentChange> Parents { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
}

internal sealed record PerfParentChange(double TimeMs, string? ParentId, string Evidence);
internal sealed record PerfCall(string Id, string Name, string Family, uint Thread, string? ElementId,
    string? ParentCallId, string? BeginEvent, string? EndEvent, double? StartMs, double? EndMs,
    double? DurationMs, double? SelfMs, string Status, double? ExclusiveMs = null,
    Dictionary<string, string>? Attributes = null);

internal sealed class PerfAnalyzer(Action<PerfCall> emitCall)
{
    private sealed class Scope(PerfEvent begin, string id, string? element, string? parent)
    {
        public PerfEvent Begin { get; } = begin;
        public string Id { get; } = id;
        public string? Element { get; } = element;
        public string? Parent { get; } = parent;
        public Scope? Root { get; init; }
        public List<(double Start, double End)> Children { get; } = [];
        public List<(double Start, double End)> DirectChildren { get; } = [];
        public List<PerfCall> CompletedCalls { get; } = [];
        public Dictionary<string, string?> OmittedParents { get; } = new(StringComparer.Ordinal);
        public bool IncompleteChildren { get; set; }
        public bool Corrupt { get; set; }
    }

    private readonly Dictionary<string, PerfElement> current = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, List<Scope>> stacks = [];
    public List<PerfElement> Elements { get; } = [];
    private int callSequence;

    public string? Accept(PerfEvent e)
    {
        if (e.DecodeError is not null)
        {
            // Diagnostic records do not establish operation boundaries.
            if (e.Family is not ("metadata" or "unknown" or "diagnostics") && stacks.TryGetValue(e.Thread, out var broken))
            {
                foreach (var scope in broken)
                {
                    scope.Corrupt = true;
                }
            }
            return null;
        }
        var element = ResolveElement(e);
        if (e.Phase == "info")
        {
            if (e.Name == "Created" && element is not null && stacks.TryGetValue(e.Thread, out var open))
            {
                AttributeToComponent(element, open);
            }
            return element?.Id;
        }
        if (!stacks.TryGetValue(e.Thread, out var stack))
        {
            stack = [];
            stacks.Add(e.Thread, stack);
        }
        string? matchedScopeElement = null;
        if (e.Phase == "begin")
        {
            if (stack.Count >= 512)
            {
                throw new PerfAnalysisLimitException("Scope nesting exceeded 512 levels.");
            }
            stack.Add(new(e, "c" + ++callSequence, element?.Id, stack.LastOrDefault()?.Id)
            {
                Root = stack.FirstOrDefault(),
            });
        }
        else if (e.Phase == "end")
        {
            var match = stack.FindLastIndex(s => s.Begin.Provider == e.Provider && Pairs(s.Begin, e) &&
                (e.ObjectId is null || s.Begin.ObjectId == e.ObjectId) &&
                (e.Activity == Guid.Empty || s.Begin.Activity == e.Activity));
            if (match < 0)
            {
                return element?.Id;
            }
            var scope = stack[match];
            matchedScopeElement = scope.Element;
            for (var i = stack.Count - 1; i > match; i--)
            {
                Publish(stack[i]);
                scope.IncompleteChildren = true;
            }
            stack.RemoveRange(match, stack.Count - match);
            if (scope.Corrupt || e.TimeMs < scope.Begin.TimeMs)
            {
                foreach (var ancestor in stack)
                {
                    ancestor.Corrupt = true;
                }
                Publish(scope);
            }
            else
            {
                var duration = e.TimeMs - scope.Begin.TimeMs;
                double? self = !scope.IncompleteChildren && IsElementOperation(scope.Begin.Name)
                    ? Math.Max(0, duration - Union(scope.Children)) : null;
                Publish(scope, new(scope.Id, scope.Begin.Name, e.Family, e.Thread, scope.Element, scope.Parent,
                    scope.Begin.Id, e.Id, scope.Begin.TimeMs, e.TimeMs, duration, self, "complete",
                    scope.IncompleteChildren ? null : Math.Max(0, duration - Union(scope.DirectChildren)),
                    ReactorEventDecoder.Attributes(scope.Begin, e)));
                if (stack.LastOrDefault() is { } parent)
                {
                    parent.IncompleteChildren |= scope.IncompleteChildren;
                    parent.DirectChildren.Add((scope.Begin.TimeMs, e.TimeMs));
                    if (IsElementOperation(scope.Begin.Name) &&
                        !(scope.Begin.Name.EndsWith("Override", StringComparison.Ordinal) &&
                          scope.Begin.ObjectId == parent.Begin.ObjectId))
                    {
                        parent.Children.Add((scope.Begin.TimeMs, e.TimeMs));
                    }
                    else
                    {
                        parent.Children.AddRange(scope.Children);
                    }
                }
            }
        }
        return element?.Id ?? matchedScopeElement;
    }

    public void Complete()
    {
        foreach (var stack in stacks.Values)
        {
            for (var i = stack.Count - 1; i >= 0; i--)
            {
                Publish(stack[i]);
            }
        }
        stacks.Clear();
    }

    /// <summary>
    /// Begin and end normally share a name. A Reactor component render begins as <c>ReactorRender:&lt;name&gt;</c>
    /// but its Stop carries only the component ID, so it pairs by prefix and ID.
    /// </summary>
    private static bool Pairs(PerfEvent begin, PerfEvent end)
    {
        if (begin.Name == end.Name)
        {
            return true;
        }
        return end.Family == ReactorEventDecoder.Family && begin.Family == ReactorEventDecoder.Family &&
            end.Name == ReactorEventDecoder.Render && ReactorEventDecoder.ComponentName(begin.Name) is not null &&
            begin.Fields.GetValueOrDefault("componentId") == end.Fields.GetValueOrDefault("componentId");
    }

    private static void AttributeToComponent(PerfElement element, List<Scope> open)
    {
        for (var i = open.Count - 1; i >= 0; i--)
        {
            var begin = open[i].Begin;
            if (begin.Family == ReactorEventDecoder.Family && ReactorEventDecoder.ComponentName(begin.Name) is { } component)
            {
                element.Component = component;
                element.ComponentId = begin.Fields.GetValueOrDefault("componentId");
                element.ComponentCallSite = ReactorEventDecoder.CallSite(begin.Fields);
                return;
            }
        }
    }

    private void Publish(Scope scope, PerfCall? call = null)
    {
        // Buffer descendants until their parent closes so omitted scopes leave no dangling parent IDs.
        var root = scope.Root ?? scope;
        if (call is null)
        {
            root.OmittedParents.Add(scope.Id, scope.Parent);
        }
        else
        {
            root.CompletedCalls.Add(call);
        }
        if (scope.Root is not null)
        {
            return;
        }
        foreach (var child in root.CompletedCalls)
        {
            var parent = child.ParentCallId;
            while (parent is not null && root.OmittedParents.TryGetValue(parent, out var ancestor))
            {
                parent = ancestor;
            }
            if (child.ParentCallId is { } omitted && parent != omitted)
            {
                root.OmittedParents[omitted] = parent;
            }
            emitCall(child.ParentCallId == parent ? child : child with { ParentCallId = parent });
        }
    }

    private PerfElement? ResolveElement(PerfEvent e)
    {
        if (e.ObjectId is null)
        {
            return null;
        }
        var exists = current.TryGetValue(e.ObjectId, out var element);
        if (!exists || (e.Name == "Created" && element!.CreationObserved))
        {
            element = NewElement(e.ObjectId, e.TimeMs);
            current[e.ObjectId] = element;
        }
        element!.Type = e.Fields.GetValueOrDefault("ClassName") ?? element.Type;
        if (e.Name == "Created")
        {
            element.CreationObserved = true;
            element.CreatedMs = e.TimeMs;
        }
        if (e.Name == "Name")
        {
            element.Name = e.Fields.GetValueOrDefault("Name");
        }
        if (e.Name == "Source")
        {
            element.Source = e.Fields.GetValueOrDefault("FileURI");
            element.Line = e.Fields.GetValueOrDefault("LineNumber");
        }
        if (e.Name is "Added" or "Removed")
        {
            string? parentId = null;
            if (e.Name == "Added" && e.Fields.TryGetValue("ParentId", out var address) && address != "0000000000000000")
            {
                if (!current.TryGetValue(address, out var parent))
                {
                    parent = NewElement(address, e.TimeMs);
                    current.Add(address, parent);
                }
                parentId = parent.Id;
            }
            if (element.Parents.Count >= 256)
            {
                throw new PerfAnalysisLimitException("An element exceeded 256 observed parent transitions.");
            }
            element.Parents.Add(new(e.TimeMs, parentId, e.Id));
        }
        if (e.Name == "Destroyed")
        {
            element.DestroyedMs = e.TimeMs;
            current.Remove(e.ObjectId);
        }
        if (element.Evidence.Count < 4 && (e.Family == "metadata" || element.Evidence.Count == 0))
        {
            element.Evidence.Add(e.Id);
        }
        return element;
    }

    private PerfElement NewElement(string address, double time)
    {
        if (Elements.Count >= 100000)
        {
            throw new PerfAnalysisLimitException("The trace exceeded 100,000 element lifetimes.");
        }
        var element = new PerfElement { Id = "e" + (Elements.Count + 1), ObjectId = address, FirstObservedMs = time };
        Elements.Add(element);
        return element;
    }

    public static bool IsElementOperation(string name) =>
        name is "ApplyTemplate" or "MeasureElement" or "ArrangeElement" or "MeasureOverride" or "ArrangeOverride";

    public static double Union(IEnumerable<(double Start, double End)> intervals)
    {
        double total = 0;
        double? start = null;
        double end = 0;
        foreach (var interval in intervals.OrderBy(i => i.Start).ThenBy(i => i.End))
        {
            if (start is null)
            {
                (start, end) = interval;
            }
            else if (interval.Start <= end)
            {
                end = Math.Max(end, interval.End);
            }
            else
            {
                total += end - start.Value;
                (start, end) = interval;
            }
        }
        return total + (start is null ? 0 : end - start.Value);
    }
}

internal sealed class PerfAnalysisLimitException(string message) : Exception(message);
