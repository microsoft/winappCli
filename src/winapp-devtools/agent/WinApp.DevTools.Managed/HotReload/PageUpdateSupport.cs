// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// What a page holds, as text by member path ("_count", "Vm.Title"): its own fields (auto-properties by their
/// property name), and one level into objects of the app's own types it holds (a page-owned view model).
/// </summary>
internal sealed class PageData
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>The object that holds each member and the member, to say what changes it.</summary>
    public Dictionary<string, (Type Owner, string Name)> Members { get; } = new(StringComparer.Ordinal);

    /// <summary>Top-level members in declaration order, so a note lists them as the code does.</summary>
    public List<string> TopLevel { get; } = [];

    /// <param name="getters">Also read properties computed by a getter (to find what a bound value comes from).</param>
    public static PageData Take(object root, bool getters)
    {
        var data = new PageData();
        var assembly = root.GetType().Assembly;
        for (var type = root.GetType(); type is not null && type.Assembly == assembly; type = type.BaseType)
        {
            foreach (var field in type.GetFields(Instance))
            {
                if (MemberName(field) is not { } name || data.Values.ContainsKey(name))
                {
                    continue;
                }
                data.Add(root, type, name, Read(() => field.GetValue(root)), assembly, nested: true);
            }
            if (!getters)
            {
                continue;
            }
            foreach (var property in type.GetProperties(Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetMethod is null || data.Values.ContainsKey(property.Name) ||
                    typeof(DependencyObject).IsAssignableFrom(property.PropertyType) || property.Name == "Bindings")
                {
                    continue;
                }
                data.Add(root, type, property.Name, Read(() => property.GetValue(root)), assembly, nested: true);
            }
        }
        return data;
    }

    private void Add(object owner, Type ownerType, string name, (bool Ok, object? Value) read, Assembly app, bool nested)
    {
        if (!read.Ok || read.Value is DependencyObject or Delegate)
        {
            return;
        }
        var value = read.Value;
        Values[name] = Format(value);
        Members[name] = (ownerType, name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name);
        if (!name.Contains('.'))
        {
            TopLevel.Add(name);
        }
        if (!nested || value is null || value.GetType().Assembly != app || value.GetType().IsValueType)
        {
            return;
        }
        // A view model the page holds: its public state, by property.
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length == 0 && property.GetMethod is not null && !typeof(DependencyObject).IsAssignableFrom(property.PropertyType))
            {
                Add(value, property.DeclaringType ?? value.GetType(), name + "." + property.Name, Read(() => property.GetValue(value)), app, nested: false);
            }
        }
        foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            Add(value, field.DeclaringType ?? value.GetType(), name + "." + field.Name, Read(() => field.GetValue(value)), app, nested: false);
        }
    }

    /// <summary>The name the author gave the field (an auto-property's by its property), or null for the compiler's own.</summary>
    private static string? MemberName(FieldInfo field)
    {
        var name = field.Name;
        if (name.StartsWith('<'))
        {
            return name.EndsWith(">k__BackingField", StringComparison.Ordinal) ? name[1..name.IndexOf('>')] : null;
        }
        if (name is "_contentLoaded" or "Bindings" || typeof(Delegate).IsAssignableFrom(field.FieldType) ||
            typeof(DependencyObject).IsAssignableFrom(field.FieldType) || field.FieldType.Name.EndsWith("_Bindings", StringComparison.Ordinal))
        {
            return null;
        }
        return name;
    }

    private static (bool, object?) Read(Func<object?> read)
    {
        try
        {
            return (true, read());
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return (false, null);
        }
    }

    internal static string Format(object? value) => value switch
    {
        null => "null",
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        ICollection collection => $"[{collection.Count}]",
        _ when value.GetType().IsPrimitive || value.GetType().IsEnum => value.ToString() ?? "",
        _ => "{" + value.GetType().Name + "}",
    };

    /// <summary>Top-level members (in declaration order) whose value, or a value under it, differs in <paramref name="other"/>.</summary>
    public List<string> Changed(PageData other) =>
        TopLevel.Where(top => Values.Where(v => v.Key == top || v.Key.StartsWith(top + ".", StringComparison.Ordinal))
            .Any(v => !other.Values.TryGetValue(v.Key, out var o) || o != v.Value)).ToList();

    public static bool RaisesChanges(Type owner) => typeof(INotifyPropertyChanged).IsAssignableFrom(owner);
}

/// <summary>
/// The page's authored tree (what its markup creates, plus the item templates its lists show): elements keyed by
/// x:Name or by position under the nearest named element, with the values a user sees.
/// </summary>
internal static class AuthoredTree
{
    /// <summary>Properties a user sets by interacting; a difference there is what they entered, not the markup.</summary>
    public static bool IsInput(DependencyObject element, string property) => (element, property) switch
    {
        (TextBox, "Text") or (PasswordBox, _) or (ToggleButton, "IsChecked") or (ToggleSwitch, "IsOn") or (Selector, "SelectedIndex") => true,
        (RangeBase range, "Value") => range is not ProgressBar,
        _ => false,
    };

    public static Dictionary<string, (DependencyObject Element, Dictionary<string, string> Values)> Read(Page page)
    {
        var result = new Dictionary<string, (DependencyObject, Dictionary<string, string>)>(StringComparer.Ordinal);
        void Visit(DependencyObject? node, string key)
        {
            if (node is null || result.ContainsKey(key))
            {
                return;
            }
            result[key] = (node, Values(node));
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in Children(node))
            {
                var typeName = child.GetType().Name;
                seen[typeName] = seen.TryGetValue(typeName, out var n) ? n + 1 : 0;
                var name = (child as FrameworkElement)?.Name;
                var named = !string.IsNullOrEmpty(name) && !name.StartsWith("__hr_", StringComparison.Ordinal) && ReferenceEquals(page.FindName(name), child);
                Visit(child, named ? "#" + name : $"{key}/{typeName}[{seen[typeName]}]");
            }
        }
        Visit(page.Content, "");
        return result;
    }

    /// <summary>"CaseLate" for a named element, "TextBlock[1] under Root" otherwise.</summary>
    public static string Describe(string key)
    {
        if (key.StartsWith('#') && !key.Contains('/'))
        {
            return key[1..];
        }
        var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var anchor = parts.Length > 1 && parts[0].StartsWith('#') ? " under " + parts[0][1..] : "";
        return (parts.Length > 0 ? parts[^1] : "(page content)") + anchor;
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject node)
    {
        switch (node)
        {
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    yield return child;
                }
                break;
            case Border border when border.Child is { } child:
                yield return child;
                break;
            case Viewbox box when box.Child is { } child:
                yield return child;
                break;
            case UserControl control when control.Content is { } controlContent:
                yield return controlContent;
                break;
            case SplitView split:
                if (split.Pane is { } pane)
                {
                    yield return pane;
                }
                if (split.Content is { } splitContent)
                {
                    yield return splitContent;
                }
                break;
            case ItemsRepeater repeater:
                for (var i = 0; i < Math.Min(repeater.ItemsSourceView?.Count ?? 0, 100); i++)
                {
                    if (repeater.TryGetElement(i) is { } element)
                    {
                        yield return element;
                    }
                }
                break;
            case ItemsControl list:
                for (var i = 0; i < Math.Min(list.Items.Count, 100); i++)
                {
                    var container = list.ContainerFromIndex(i);
                    var root = container switch
                    {
                        ContentControl item when item.ContentTemplateRoot is { } templateRoot => templateRoot,
                        ContentPresenter presenter when presenter.Content is UIElement element => element,
                        _ => container,
                    };
                    if (root is not null)
                    {
                        yield return root;
                    }
                }
                break;
            case ContentControl control:
                if (control is Expander { Header: UIElement header })
                {
                    yield return header;
                }
                if (control.Content is UIElement content)
                {
                    yield return content;
                }
                break;
            case ContentPresenter presenter when presenter.Content is UIElement presented:
                yield return presented;
                break;
        }
    }

    private static Dictionary<string, string> Values(DependencyObject node)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (node)
        {
            case TextBlock text:
                values["Text"] = text.Text;
                values["FontSize"] = PageData.Format(text.FontSize);
                break;
            case TextBox box:
                values["Text"] = box.Text;
                break;
            case ContentControl { Content: { } content } when content is not DependencyObject:
                values["Content"] = PageData.Format(content);
                break;
        }
        if (node is UIElement element)
        {
            values["Visibility"] = element.Visibility.ToString();
            values["Opacity"] = PageData.Format(element.Opacity);
        }
        if (node is FrameworkElement framework)
        {
            values["Width"] = PageData.Format(framework.Width);
            values["Height"] = PageData.Format(framework.Height);
        }
        if (node is Control control)
        {
            values["IsEnabled"] = control.IsEnabled.ToString();
        }
        switch (node)
        {
            case ToggleButton toggle:
                values["IsChecked"] = PageData.Format(toggle.IsChecked);
                break;
            case ToggleSwitch toggle:
                values["IsOn"] = toggle.IsOn.ToString();
                break;
            case RangeBase range:
                values["Value"] = PageData.Format(range.Value);
                break;
        }
        if (node is Selector selector)
        {
            values["SelectedIndex"] = PageData.Format(selector.SelectedIndex);
        }
        if (node is ItemsControl list)
        {
            values["Items"] = PageData.Format(list.Items.Count);
        }
        return values;
    }
}

/// <summary>Where the app's code sets a member: "MainPage.xaml.cs:30", from the app's portable PDB.</summary>
internal static class SourceSites
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];
    private static readonly Dictionary<Assembly, MetadataReaderProvider?> Pdbs = [];

    static SourceSites()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode code)
            {
                var value = (ushort)code.Value;
                if (value < 0x100)
                {
                    OneByte[value] = code;
                }
                else if ((value & 0xff00) == 0xfe00)
                {
                    TwoByte[value & 0xff] = code;
                }
            }
        }
    }

    /// <summary>
    /// The first place outside a constructor where <paramref name="owner"/>.<paramref name="member"/> is assigned, in
    /// the page's code or the owner's (including their lambdas), or null when it can't be told.
    /// </summary>
    public static string? WhereSet(Type owner, string member, Type page)
    {
        try
        {
            var targets = new List<MemberInfo>();
            if (owner.GetProperty(member, All & ~BindingFlags.DeclaredOnly)?.SetMethod is { } setter)
            {
                targets.Add(setter);
            }
            if ((owner.GetField(member, All) ?? owner.GetField($"<{member}>k__BackingField", All)) is { } field)
            {
                targets.Add(field);
            }
            if (targets.Count == 0)
            {
                return null;
            }
            foreach (var type in new[] { page, owner }.Distinct().SelectMany(WithNested))
            {
                // Constructors are left out: what they assign is in place before the page loads.
                foreach (var method in type.GetMethods(All))
                {
                    if (targets.Contains(method) || method.Name.StartsWith("set_", StringComparison.Ordinal) && method.DeclaringType == owner)
                    {
                        continue;
                    }
                    if (FindCall(method, targets) is { } offset && Line(method, offset) is { } line)
                    {
                        return line;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            HotReloadAgent.Log($"page update: where {owner.Name}.{member} is set: {ex.Message}");
        }
        return null;
    }

    private static IEnumerable<Type> WithNested(Type type) => new[] { type }.Concat(type.GetNestedTypes(All)
        // The XAML compiler's x:Bind classes write two-way values back: that's the user typing, not the app's code.
        .Where(t => !t.Name.Contains("_Bindings", StringComparison.Ordinal)).SelectMany(WithNested));

    private static int? FindCall(MethodBase method, List<MemberInfo> targets)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            return null;
        }
        var i = 0;
        while (i < il.Length)
        {
            var start = i;
            var code = il[i] == 0xfe && i + 1 < il.Length ? TwoByte[il[++i]] : OneByte[il[i]];
            i++;
            switch (code.OperandType)
            {
                case OperandType.InlineMethod or OperandType.InlineField:
                    var token = BitConverter.ToInt32(il, i);
                    MemberInfo? resolved = null;
                    try
                    {
                        resolved = method.Module.ResolveMember(token, method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                    }
                    catch (ArgumentException)
                    {
                    }
                    if (resolved is not null && targets.Any(t => t.MetadataToken == resolved.MetadataToken && t.Module == resolved.Module) &&
                        (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Stfld))
                    {
                        return start;
                    }
                    i += 4;
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                    i += 4 + 4 * BitConverter.ToInt32(il, i);
                    break;
                default:
                    i += 4;
                    break;
            }
        }
        return null;
    }

    private static string? Line(MethodBase method, int offset)
    {
        var assembly = method.Module.Assembly;
        if (!Pdbs.TryGetValue(assembly, out var provider))
        {
            Pdbs[assembly] = provider = OpenPdb(assembly);
        }
        if (provider is null)
        {
            return null;
        }
        var reader = provider.GetMetadataReader();
        var row = method.MetadataToken & 0x00ffffff;
        if (row < 1 || row > reader.MethodDebugInformation.Count)
        {
            return null;
        }
        var info = reader.GetMethodDebugInformation(MetadataTokens.MethodDefinitionHandle(row));
        SequencePoint? at = null;
        foreach (var point in info.GetSequencePoints())
        {
            if (point.Offset > offset)
            {
                break;
            }
            if (!point.IsHidden)
            {
                at = point;
            }
        }
        return at is { } found ? $"{Path.GetFileName(reader.GetString(reader.GetDocument(found.Document).Name))}:{found.StartLine}" : null;
    }

    private static MetadataReaderProvider? OpenPdb(Assembly assembly)
    {
        if (string.IsNullOrEmpty(assembly.Location))
        {
            return null;
        }
        var pdb = Path.ChangeExtension(assembly.Location, ".pdb");
        if (File.Exists(pdb))
        {
            // Read into memory: a rebuild must be able to replace the file.
            return MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create(File.ReadAllBytes(pdb)));
        }
        using var pe = new PEReader(File.OpenRead(assembly.Location));
        var embedded = pe.ReadDebugDirectory().FirstOrDefault(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        return embedded.DataSize > 0 ? pe.ReadEmbeddedPortablePdbDebugDirectoryData(embedded) : null;
    }
}

/// <summary>The parameter each page was navigated to with, recorded from its Frame's Navigated event.</summary>
internal static class PageNavigation
{
    private static readonly ConditionalWeakTable<Page, StrongBox<object?>> Parameters = new();
    private static readonly ConditionalWeakTable<Frame, object> Watched = new();

    /// <summary>Starts recording navigations of the frames under <paramref name="root"/>.</summary>
    public static void Watch(DependencyObject? root)
    {
        if (root is null)
        {
            return;
        }
        if (root is Frame frame && Watched.TryAdd(frame, new object()))
        {
            frame.Navigated += OnNavigated;
        }
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            Watch(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
        }
    }

    private static void OnNavigated(object sender, NavigationEventArgs e)
    {
        if (e.Content is Page page)
        {
            Parameters.AddOrUpdate(page, new StrongBox<object?>(e.Parameter));
        }
    }

    /// <summary>
    /// The page's navigation parameter. One shown before hot reload watched its frame is known only when the page
    /// can't read it (it doesn't override OnNavigatedTo), in which case it doesn't matter.
    /// </summary>
    public static bool TryGetParameter(Page page, out object? parameter)
    {
        if (Parameters.TryGetValue(page, out var box))
        {
            parameter = box.Value;
            return true;
        }
        parameter = null;
        return !Overrides(page, "OnNavigatedTo");
    }

    /// <summary>
    /// The frame's navigation history as Frame.GetNavigationState serializes it (basic parameter types only), when
    /// taking it has no effect the app sees: it calls the current page's OnNavigatedFrom, so only for a page that
    /// doesn't override it.
    /// </summary>
    public static bool TryGetState(Frame frame, Page page, out string? state)
    {
        state = null;
        if (!ReferenceEquals(frame.Content, page) || Overrides(page, "OnNavigatedFrom"))
        {
            return false;
        }
        try
        {
            state = frame.GetNavigationState();
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            // A parameter the format can't hold (an object).
            HotReloadAgent.Log($"page update: {page.GetType().Name}'s navigation state can't be serialized: {ex.Message}");
            return false;
        }
    }

    private static bool Overrides(Page page, string method) =>
        page.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, [typeof(NavigationEventArgs)])?.DeclaringType != typeof(Page);
}

internal static class UiTicks
{
    /// <summary>Completes after the dispatcher ran what was queued at Low priority before it (layout, deferred x:Bind).</summary>
    public static Task Idle(DispatcherQueue dispatcher)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => done.TrySetResult()))
        {
            done.TrySetResult();
        }
        return done.Task;
    }

    public static async Task<bool> Loaded(FrameworkElement element, TimeSpan timeout)
    {
        if (element.IsLoaded)
        {
            return true;
        }
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs e) => loaded.TrySetResult();
        element.Loaded += OnLoaded;
        try
        {
            return await Task.WhenAny(loaded.Task, Task.Delay(timeout)) == loaded.Task;
        }
        finally
        {
            element.Loaded -= OnLoaded;
        }
    }
}
