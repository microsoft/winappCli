// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed class UiAuditSnapshotBudget(UiAuditSnapshotOptions options, CancellationToken cancellationToken, Func<TimeSpan>? elapsed = null)
{
    private readonly long _started = Stopwatch.GetTimestamp();

    internal void Check()
    {
        if ((elapsed?.Invoke() ?? Stopwatch.GetElapsedTime(_started)).TotalMilliseconds >= options.TimeoutMs)
        {
            throw new UiAuditSnapshotStoppedException("deadline_reached", "The cooperative snapshot deadline was reached.");
        }
        if (cancellationToken.IsCancellationRequested)
        {
            throw new UiAuditSnapshotStoppedException("cancelled", "Snapshot capture was cancelled.");
        }
    }
}

internal sealed class UiAuditSnapshotStoppedException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

internal sealed class UiAuditSnapshotState(UiAuditSnapshotOptions options, UiAuditSnapshotBudget budget)
{
    internal UiAuditSnapshotOptions Options { get; } = options;
    internal UiAuditSnapshotBudget Budget { get; } = budget;
    internal List<UiAuditNode> Nodes { get; } = [];
    internal List<UiAuditSnapshotIssue> Issues { get; } = [];
    internal bool Partial { get; set; }
    internal bool DepthLimitReached { get; set; }
    internal bool ElementLimitReached { get; set; }

    internal void Issue(List<UiAuditSnapshotIssue> destination, string code, string message, bool partial = true, long? excludedWindowHandle = null)
    {
        Partial |= partial;
        var issue = new UiAuditSnapshotIssue(code, message) { ExcludedWindowHandle = excludedWindowHandle };
        if (!destination.Contains(issue))
        {
            destination.Add(issue);
        }
    }
}

// A small tree-reader boundary exercises traversal policy without implementing UIA's COM interfaces.
internal abstract class UiAuditSnapshotTree<T> where T : class
{
    internal abstract T? FirstChild(T parent);
    internal abstract T? NextSibling(T node);
    internal abstract UiAuditNode? ReadNode(T node, T? parent, UiAuditNode header, bool inheritedWithholding);
    internal abstract void Release(T node);
}

internal static class UiAuditSnapshotTraversal
{
    internal static void Walk<T>(T root, UiAuditSnapshotTree<T> tree, UiAuditSnapshotState state) where T : class
    {
        var examined = 0;
        var runtimeIds = new HashSet<string>(StringComparer.Ordinal);

        void AddLimit(int nodeIndex, string code, string message)
        {
            var issue = new UiAuditSnapshotIssue(code, message);
            state.Nodes[nodeIndex] = state.Nodes[nodeIndex] with { Issues = [.. state.Nodes[nodeIndex].Issues, issue] };
            state.Partial = true;
        }

        void Visit(T current, T? parent, string? parentId, int depth, bool inheritedWithholding)
        {
            state.Budget.Check();
            examined++;
            var header = new UiAuditNode
            {
                Id = "n" + (state.Nodes.Count + 1).ToString(CultureInfo.InvariantCulture),
                ParentId = parentId,
                Depth = depth,
            };
            var node = tree.ReadNode(current, parent, header, inheritedWithholding);
            if (node is null)
            {
                return;
            }
            if (node.RuntimeId is not null && !runtimeIds.Add(node.RuntimeId))
            {
                state.Issue(state.Issues, "repeated_runtime_id", "A repeated UIA runtime ID was omitted; the raw tree may have changed or contain a cycle.");
                return;
            }

            var nodeIndex = state.Nodes.Count;
            state.Nodes.Add(node);
            state.Budget.Check();
            var child = tree.FirstChild(current);
            if (child is null)
            {
                return;
            }
            try
            {
                if (depth >= state.Options.MaxDepth)
                {
                    state.DepthLimitReached = true;
                    AddLimit(nodeIndex, "depth_limit_reached", "Children were omitted because the maximum raw-tree depth was reached.");
                    return;
                }
                while (child is not null)
                {
                    state.Budget.Check();
                    if (examined >= state.Options.MaxElements)
                    {
                        state.ElementLimitReached = true;
                        AddLimit(nodeIndex, "element_limit_reached", "Additional elements were omitted because the examined-element limit was reached.");
                        return;
                    }
                    Visit(child, current, node.Id, depth + 1, ShouldWithhold(inheritedWithholding, node.IsPassword));
                    if (state.ElementLimitReached)
                    {
                        return;
                    }
                    state.Budget.Check();
                    var next = tree.NextSibling(child);
                    tree.Release(child);
                    child = next;
                }
            }
            finally
            {
                if (child is not null)
                {
                    tree.Release(child);
                }
            }
        }

        Visit(root, null, null, 0, false);
    }

    internal static bool ShouldWithhold(bool inheritedWithholding, bool? isPassword)
        => inheritedWithholding || isPassword != false;

    internal static long? NearestNativeWindow<T>(T element, int maximumDepth,
        Func<T, long> windowHandle, Func<T, T?> parent, Action checkBudget) where T : class
    {
        T? current = element;
        for (var depth = 0; current is not null && depth <= maximumDepth; depth++)
        {
            checkBudget();
            var window = windowHandle(current);
            if (window != 0)
            {
                return window;
            }
            if (depth == maximumDepth)
            {
                break;
            }
            checkBudget();
            current = parent(current);
        }
        return null;
    }

    internal static UiAuditNodeBounds? Bounds(double left, double top, double width, double height)
        => double.IsFinite(left) && double.IsFinite(top) && double.IsFinite(width) && double.IsFinite(height)
            && width >= 0 && height >= 0 && double.IsFinite(left + width) && double.IsFinite(top + height)
            ? new UiAuditNodeBounds(left, top, width, height) : null;

    internal static string? RuntimeId(ReadOnlySpan<int> values)
    {
        if (values.Length is 0 or > 128)
        {
            return null;
        }
        var parts = new string[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            parts[index] = values[index].ToString("X", CultureInfo.InvariantCulture);
        }
        return "[" + string.Join(",", parts) + "]";
    }
}
