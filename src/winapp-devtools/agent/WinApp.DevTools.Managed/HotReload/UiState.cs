// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// Carries what the user typed, checked, selected, expanded and scrolled, and focus, from a page to the page built
/// again in its place. Each control is matched by the first of: its x:Name, its binding path, its AutomationId, or its
/// position, but only when every container on the way to it has the same children as before. A value that can't be
/// matched is dropped, never put into another control. A value is carried only when the user changed it since the
/// control loaded and the markup didn't change where it starts.
/// </summary>
internal static class UiState
{
    private static readonly ConditionalWeakTable<DependencyObject, State> Baselines = new();

    internal sealed record State(string? Text, int SelectionStart, int SelectionLength, bool? Checked, int? SelectedIndex, double? Value,
        double? HorizontalOffset, double? VerticalOffset, string? SelectedKey = null, int ItemCount = 0);

    internal sealed class Snapshot
    {
        public Dictionary<string, (State Current, State? Baseline)> Elements { get; } = new(StringComparer.Ordinal);
        public string? FocusedKey { get; set; }
    }

    /// <summary>Records each control's starting values, to tell later what the user changed.</summary>
    public static void CaptureBaseline(DependencyObject? root)
    {
        foreach (var (element, _) in Keys(root))
        {
            if (Read(element) is { } state)
            {
                Baselines.AddOrUpdate(element, state);
            }
        }
    }

    public static Snapshot Take(DependencyObject root)
    {
        var snapshot = new Snapshot();
        var focused = (root as UIElement)?.XamlRoot is { } xamlRoot ? FocusManager.GetFocusedElement(xamlRoot) as DependencyObject : null;
        foreach (var (element, key) in Keys(root))
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
        return snapshot;
    }

    /// <summary>Puts <paramref name="snapshot"/> back into the new tree under <paramref name="root"/> (loaded).</summary>
    public static void Restore(FrameworkElement root, Snapshot snapshot)
    {
        var elements = Keys(root).ToList();
        CaptureBaseline(root);
        foreach (var (element, key) in elements)
        {
            if (snapshot.Elements.TryGetValue(key, out var old) && Baselines.TryGetValue(element, out var fresh))
            {
                Apply(element, old.Current, old.Baseline, fresh);
            }
            if (key == snapshot.FocusedKey && element is Control control)
            {
                control.Focus(FocusState.Programmatic);
            }
        }
    }

    private static State? Read(DependencyObject element) => element switch
    {
        TextBox box => new(box.Text, box.SelectionStart, box.SelectionLength, null, null, null, null, null),
        PasswordBox box => new(box.Password, 0, 0, null, null, null, null, null),
        AutoSuggestBox box => new(box.Text, 0, 0, null, null, null, null, null),
        ToggleButton toggle => new(null, 0, 0, toggle.IsChecked, null, null, null, null),
        ToggleSwitch toggle => new(null, 0, 0, toggle.IsOn, null, null, null, null),
        Selector selector => new(null, 0, 0, null, selector.SelectedIndex, null, null, null, ItemKey(selector.SelectedItem), selector.Items.Count),
        RangeBase range when range is not ScrollBar => new(null, 0, 0, null, null, range.Value, null, null),
        ScrollViewer viewer => new(null, 0, 0, null, null, null, viewer.HorizontalOffset, viewer.VerticalOffset),
        // Checked holds whether it is expanded or its pane open.
        Expander expander => new(null, 0, 0, expander.IsExpanded, null, null, null, null),
        SplitView split => new(null, 0, 0, split.IsPaneOpen, null, null, null, null),
        NavigationView navigation => new(null, 0, 0, navigation.IsPaneOpen, null, null, null, null),
        _ => null,
    };

    /// <summary>Applies a value the user changed (it differs from where it started) unless the markup moved where it starts.</summary>
    private static void Apply(DependencyObject element, State current, State? oldBaseline, State fresh)
    {
        bool Carry<T>(Func<State, T> value) =>
            !EqualityComparer<T>.Default.Equals(value(current), value(fresh)) &&
            (oldBaseline is null || (!EqualityComparer<T>.Default.Equals(value(current), value(oldBaseline)) &&
                EqualityComparer<T>.Default.Equals(value(oldBaseline), value(fresh))));
        switch (element)
        {
            case TextBox box when Carry(s => s.Text):
                box.Text = current.Text ?? "";
                var start = Math.Min(current.SelectionStart, box.Text.Length);
                box.Select(start, Math.Min(current.SelectionLength, box.Text.Length - start));
                break;
            case PasswordBox box when Carry(s => s.Text):
                box.Password = current.Text;
                break;
            case AutoSuggestBox box when Carry(s => s.Text):
                box.Text = current.Text;
                break;
            case ToggleButton toggle when Carry(s => s.Checked):
                toggle.IsChecked = current.Checked;
                break;
            case ToggleSwitch toggle when Carry(s => s.Checked):
                toggle.IsOn = current.Checked == true;
                break;
            case Selector selector when Carry(s => s.SelectedIndex):
                // Items the page fills in later (in OnNavigatedTo, or from a load) aren't there yet. The same item, found by
                // what it says it is; by position only when items don't say and there are as many as before; else none.
                WhenReady(selector, () => current.SelectedIndex < selector.Items.Count, () =>
                {
                    var index = current.SelectedKey is { } key ? selector.Items.Select(ItemKey).ToList().IndexOf(key)
                        : selector.Items.Count == current.ItemCount ? current.SelectedIndex ?? -1 : -1;
                    if (index >= 0)
                    {
                        selector.SelectedIndex = index;
                    }
                });
                break;
            case RangeBase range when Carry(s => s.Value):
                range.Value = current.Value ?? range.Value;
                break;
            case ScrollViewer viewer when Carry(s => s.HorizontalOffset) || Carry(s => s.VerticalOffset):
                bool Fits() => viewer.ScrollableHeight + 0.5 >= (current.VerticalOffset ?? 0) && viewer.ScrollableWidth + 0.5 >= (current.HorizontalOffset ?? 0);
                WhenReady(viewer, Fits, () => viewer.ChangeView(current.HorizontalOffset, current.VerticalOffset, null, disableAnimation: true));
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

    /// <summary>What an item says it is (its text), or null when it only names its type.</summary>
    private static string? ItemKey(object? item)
    {
        var value = item is ContentControl { Content: { } content } ? content : item;
        return value?.ToString() is { } text && text != value.GetType().FullName ? text : null;
    }

    /// <summary>Runs <paramref name="apply"/> now when <paramref name="ready"/>, else after the layout pass that makes it so (up to 3 s).</summary>
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

    /// <summary>
    /// Every element under <paramref name="root"/> with the key it's matched by, in the matching chain's order. A key
    /// two elements share is dropped for both. List items are skipped (they come from data); a page or UserControl
    /// inside starts a scope of its own names, under its own key.
    /// </summary>
    internal static List<(DependencyObject Element, string Key)> Keys(DependencyObject? root)
    {
        var found = new List<(DependencyObject, string)>();
        if (root is null)
        {
            return found;
        }
        void Visit(DependencyObject node, string key, FrameworkElement? scope, string scopeKey)
        {
            found.Add((node, key));
            if (node is SelectorItem or ScrollBar)
            {
                return;
            }
            if (node is Page or UserControl && !ReferenceEquals(node, root))
            {
                scope = (FrameworkElement)node;
                scopeKey = key + ">";
            }
            var count = VisualTreeHelper.GetChildrenCount(node);
            var children = Enumerable.Range(0, count).Select(i => VisualTreeHelper.GetChild(node, i)).ToList();
            // A position only means the same control while the container holds the same kinds of children.
            var shape = string.Join(",", children.Select(c => c.GetType().Name)).GetHashCode(StringComparison.Ordinal).ToString("x8");
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in children)
            {
                var typeName = child.GetType().Name;
                seen[typeName] = seen.TryGetValue(typeName, out var n) ? n + 1 : 0;
                Visit(child, Identity(child, scope) is { } identity ? scopeKey + identity : $"{key}/{shape}:{typeName}[{seen[typeName]}]", scope, scopeKey);
            }
        }
        Visit(root, "", root as FrameworkElement, "");
        // A key two elements share (two boxes bound to the same path) identifies neither.
        var shared = found.GroupBy(f => f.Item2).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return [.. found.Where(f => !shared.Contains(f.Item2))];
    }

    /// <summary>What names an element, whatever its position: x:Name in its scope, then binding path, then AutomationId.</summary>
    private static string? Identity(DependencyObject element, FrameworkElement? scope)
    {
        if (element is FrameworkElement { Name.Length: > 0 } named && ReferenceEquals(scope?.FindName(named.Name), named))
        {
            return "#" + named.Name;
        }
        var property = element switch
        {
            TextBox => TextBox.TextProperty,
            ToggleButton => ToggleButton.IsCheckedProperty,
            ToggleSwitch => ToggleSwitch.IsOnProperty,
            Selector => Selector.SelectedItemProperty,
            RangeBase => RangeBase.ValueProperty,
            _ => null,
        };
        if (property is not null && element is FrameworkElement bound && bound.GetBindingExpression(property)?.ParentBinding?.Path?.Path is { Length: > 0 } path)
        {
            return $"@{element.GetType().Name}:{path}";
        }
        return AutomationProperties.GetAutomationId(element) is { Length: > 0 } id ? "$" + id : null;
    }
}
