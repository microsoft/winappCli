// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// Carries what the user typed, checked, selected and scrolled across a reload that rebuilds a component's tree.
/// Elements are matched by x:Name, else by their position under the nearest named ancestor. A value is carried
/// over only when the user changed it since the element loaded and the markup didn't change its starting value.
/// </summary>
internal static class UiState
{
    private static readonly ConditionalWeakTable<DependencyObject, State> Baselines = new();

    internal sealed record State(string? Text, int SelectionStart, int SelectionLength, bool? Checked, int? SelectedIndex, double? Value, double? HorizontalOffset, double? VerticalOffset);

    internal sealed class Snapshot
    {
        public Dictionary<string, (State Current, State? Baseline)> Elements { get; } = [];
        public string? FocusedKey { get; set; }
    }

    /// <summary>
    /// Records each element's starting values, to tell later what the user changed. An element keeps the values it
    /// loaded with: one moved into a rebuilt window, or put back after a reload failed, isn't starting over.
    /// </summary>
    public static void CaptureBaseline(DependencyObject? root)
    {
        if (root is null)
        {
            return;
        }
        foreach (var (element, _) in Walk(root))
        {
            if (Read(element) is { } state)
            {
                Baselines.TryAdd(element, state);
            }
        }
    }

    public static Snapshot? Take(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }
        var snapshot = new Snapshot();
        var focused = (root as UIElement)?.XamlRoot is { } xamlRoot ? FocusManager.GetFocusedElement(xamlRoot) as DependencyObject : null;
        foreach (var (element, key) in Walk(root))
        {
            if (Read(element) is { } state)
            {
                snapshot.Elements[key] = (state, Baselines.TryGetValue(element, out var baseline) ? baseline : null);
            }
            if (ReferenceEquals(element, focused))
            {
                snapshot.FocusedKey = key;
            }
        }
        return snapshot.Elements.Count > 0 || snapshot.FocusedKey is not null ? snapshot : null;
    }

    /// <summary>Restores <paramref name="snapshot"/> into <paramref name="root"/> once its new tree has loaded.</summary>
    public static void RestoreWhenLoaded(FrameworkElement? root, Snapshot? snapshot, DispatcherQueue? dispatcher)
    {
        if (root is null || snapshot is null)
        {
            return;
        }
        void Restore()
        {
            var elements = Walk(root).ToList();
            CaptureBaseline(root);
            foreach (var (element, key) in elements)
            {
                if (snapshot.Elements.TryGetValue(key, out var old) && Baselines.TryGetValue(element, out var fresh))
                {
                    Apply(element, old.Current, old.Baseline, fresh, dispatcher);
                }
                if (key == snapshot.FocusedKey && element is Control control)
                {
                    control.Focus(FocusState.Programmatic);
                }
            }
        }
        if (root.IsLoaded)
        {
            dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, Restore);
            return;
        }
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            root.Loaded -= OnLoaded;
            Restore();
        }
        root.Loaded += OnLoaded;
    }

    private static State? Read(DependencyObject element) => element switch
    {
        TextBox box => new(box.Text, box.SelectionStart, box.SelectionLength, null, null, null, null, null),
        PasswordBox box => new(box.Password, 0, 0, null, null, null, null, null),
        ToggleButton toggle => new(null, 0, 0, toggle.IsChecked, null, null, null, null),
        ToggleSwitch toggle => new(null, 0, 0, toggle.IsOn, null, null, null, null),
        Selector selector => new(null, 0, 0, null, selector.SelectedIndex, null, null, null),
        RangeBase range when range is not ScrollBar => new(null, 0, 0, null, null, range.Value, null, null),
        ScrollViewer viewer => new(null, 0, 0, null, null, null, viewer.HorizontalOffset, viewer.VerticalOffset),
        // Expanded and open panes: Checked holds whether it is expanded or open.
        Expander expander => new(null, 0, 0, expander.IsExpanded, null, null, null, null),
        SplitView split => new(null, 0, 0, split.IsPaneOpen, null, null, null, null),
        NavigationView navigation => new(null, 0, 0, navigation.IsPaneOpen, null, null, null, null),
        _ => null,
    };

    /// <summary>Applies each value the user changed (current differs from its old baseline) unless the markup moved its default.</summary>
    private static void Apply(DependencyObject element, State current, State? oldBaseline, State fresh, DispatcherQueue? dispatcher)
    {
        bool Carry<T>(Func<State, T> value) =>
            !EqualityComparer<T>.Default.Equals(value(current), value(fresh)) &&
            (oldBaseline is null || (!EqualityComparer<T>.Default.Equals(value(current), value(oldBaseline)) &&
                EqualityComparer<T>.Default.Equals(value(oldBaseline), value(fresh))));
        switch (element)
        {
            case TextBox box when Carry(s => s.Text):
                box.Text = current.Text;
                box.Select(Math.Min(current.SelectionStart, box.Text.Length), Math.Min(current.SelectionLength, box.Text.Length - Math.Min(current.SelectionStart, box.Text.Length)));
                break;
            case PasswordBox box when Carry(s => s.Text):
                box.Password = current.Text;
                break;
            case ToggleButton toggle when Carry(s => s.Checked):
                toggle.IsChecked = current.Checked;
                break;
            case ToggleSwitch toggle when Carry(s => s.Checked):
                toggle.IsOn = current.Checked == true;
                break;
            case Selector selector when Carry(s => s.SelectedIndex):
                // Items the page fills in later (in OnNavigatedTo, or from a load) aren't there yet: selected once they are.
                WhenReady(selector, () => current.SelectedIndex < selector.Items.Count, () => selector.SelectedIndex = current.SelectedIndex ?? -1);
                break;
            case RangeBase range when Carry(s => s.Value):
                range.Value = current.Value ?? range.Value;
                break;
            case ScrollViewer viewer when Carry(s => s.HorizontalOffset) || Carry(s => s.VerticalOffset):
                // Offsets need the new content measured; scroll after layout. Content built later (a menu the page fills in
                // OnNavigatedTo, then expands) isn't that tall yet: tried again as it grows, for a few seconds.
                dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () => ScrollWhenTallEnough(viewer, current.HorizontalOffset ?? 0, current.VerticalOffset ?? 0));
                break;
            case Expander expander when Carry(s => s.Checked):
                expander.IsExpanded = current.Checked == true;
                break;
            case SplitView split when Carry(s => s.Checked):
                split.IsPaneOpen = current.Checked == true;
                break;
            case NavigationView navigation when Carry(s => s.Checked):
                navigation.IsPaneOpen = current.Checked == true;
                break;
        }
    }

    private static void ScrollWhenTallEnough(ScrollViewer viewer, double horizontal, double vertical)
    {
        bool Fits() => viewer.ScrollableHeight + 0.5 >= vertical && viewer.ScrollableWidth + 0.5 >= horizontal;
        viewer.ChangeView(horizontal, vertical, null, disableAnimation: true);
        if (!Fits())
        {
            WhenReady(viewer, Fits, () => viewer.ChangeView(horizontal, vertical, null, disableAnimation: true));
        }
    }

    /// <summary>Runs <paramref name="apply"/> now when <paramref name="ready"/>, else after a layout pass that makes it so (up to 3 s).</summary>
    private static void WhenReady(FrameworkElement element, Func<bool> ready, Action apply)
    {
        if (ready())
        {
            apply();
            return;
        }
        var until = DateTime.UtcNow.AddSeconds(3);
        void OnLayout(object? sender, object e)
        {
            if (DateTime.UtcNow > until)
            {
                element.LayoutUpdated -= OnLayout;
            }
            else if (ready())
            {
                element.LayoutUpdated -= OnLayout;
                apply();
            }
        }
        element.LayoutUpdated += OnLayout;
    }

    /// <summary>Elements under <paramref name="root"/> with stable keys; list items are skipped (they come from data).</summary>
    internal static IEnumerable<(DependencyObject Element, string Key)> Walk(DependencyObject root)
    {
        var scope = root as FrameworkElement;
        var stack = new Stack<(DependencyObject, string)>();
        stack.Push((root, ""));
        while (stack.Count > 0)
        {
            var (node, key) = stack.Pop();
            yield return (node, key);
            // Pages hosted inside (a window's Frame) are kept as they are and have their own state.
            if (node is SelectorItem or ScrollBar || (node is Page && !ReferenceEquals(node, root)))
            {
                continue;
            }
            var count = VisualTreeHelper.GetChildrenCount(node);
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var children = new List<(DependencyObject, string)>(count);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                var typeName = child.GetType().Name;
                seen[typeName] = seen.TryGetValue(typeName, out var n) ? n + 1 : 0;
                var name = (child as FrameworkElement)?.Name;
                var named = !string.IsNullOrEmpty(name) && !name.StartsWith("__hr_", StringComparison.Ordinal) &&
                    ReferenceEquals(scope?.FindName(name), child);
                children.Add((child, named ? "#" + name : $"{key}/{typeName}[{seen[typeName]}]"));
            }
            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }
    }
}
