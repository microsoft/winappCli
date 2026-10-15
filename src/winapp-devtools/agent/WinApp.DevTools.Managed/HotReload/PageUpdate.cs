// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.UI.Xaml.Controls;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>Names a page update strategy (WINAPP_HOT_RELOAD_PAGE_UPDATE), on a class implementing <see cref="IPageUpdateStrategy"/>.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PageUpdateStrategyAttribute(string name) : Attribute
{
    public string Name => name;
}

/// <summary>
/// How a live page shows a save of its own XAML or code, on either pipeline (WINAPP_HOT_RELOAD_PIPELINE). Windows, user
/// controls, App.xaml and resource dictionaries don't go through it: they're reloaded in place.
/// </summary>
public interface IPageUpdateStrategy
{
    /// <summary>
    /// Once per save, for each live page whose class the save recompiled. Runs on the UI thread, after the compiler's
    /// code delta and .xbf are in place. The old instance's compiled x:Bind was stopped before the delta.
    /// </summary>
    void Update(PageUpdate update);
}

public sealed class PageUpdate
{
    private readonly Action _reloadInPlace;
    private readonly Func<Page> _recreate;
    private readonly List<string> _notes;
    private readonly List<string> _restarts;
    private readonly List<Func<Task>> _settled;

    internal PageUpdate(Page page, Frame? frame, string markupUri, IReadOnlyList<string> savedFiles, Action reloadInPlace, Func<Page> recreate,
        List<string> notes, List<string> restarts, List<Func<Task>> settled)
    {
        Page = page;
        Frame = frame;
        MarkupUri = markupUri;
        SavedFiles = savedFiles;
        _reloadInPlace = reloadInPlace;
        _recreate = recreate;
        _notes = notes;
        _restarts = restarts;
        _settled = settled;
    }

    public Page Page { get; }

    /// <summary>The frame showing the page; null when the page isn't the content of a Frame.</summary>
    public Frame? Frame { get; }

    /// <summary>ms-appx:///Folder/Page.xaml</summary>
    public string MarkupUri { get; }

    /// <summary>The files of this save, relative to the project.</summary>
    public IReadOnlyList<string> SavedFiles { get; }

    /// <summary>
    /// The page's UI state (selection, text, toggles) as it was before the update: taken before the update detached its
    /// lists, so a recreated page can be given it back.
    /// </summary>
    internal UiState.Snapshot? StateBefore { get; init; }

    /// <summary>InitializeComponent runs again on the same instance, its compiled x:Bind starts again, and the UI state is kept.</summary>
    public void ReloadInPlace() => _reloadInPlace();

    /// <summary>
    /// A new instance navigated in place of the old one (the back and forward stacks are kept); returns the new page.
    /// User state isn't put back. Without a frame, or when the frame hands back the same instance, returns <see cref="Page"/>.
    /// </summary>
    public Page Recreate() => _recreate();

    /// <summary>Printed under the save as "note: ...".</summary>
    public void Note(string text) => _notes.Add(text);

    /// <summary>The save prints "restart &lt;reason&gt;" instead of "applied", and the app restarts.</summary>
    public void Restart(string reason) => _restarts.Add(reason);

    /// <summary>
    /// Work that must wait until the page has settled: runs on the UI thread after the update's own deferred x:Bind pass,
    /// and the save is reported when it finishes (at most 10 s). Notes and restarts from it belong to this save.
    /// </summary>
    public void WhenSettled(Func<Task> work) => _settled.Add(work);
}

/// <summary>The default: the page keeps its instance, fields and view model, and InitializeComponent runs again.</summary>
[PageUpdateStrategy("inplace")]
internal sealed class InPlacePageUpdate : IPageUpdateStrategy
{
    public void Update(PageUpdate update) => update.ReloadInPlace();
}

internal static class PageUpdateStrategies
{
    public const string Default = "inplace";

    private static readonly Dictionary<string, Type> Known = typeof(PageUpdateStrategies).Assembly.GetTypes()
        .Where(t => typeof(IPageUpdateStrategy).IsAssignableFrom(t) && !t.IsAbstract && t.GetCustomAttribute<PageUpdateStrategyAttribute>() is not null)
        .ToDictionary(t => t.GetCustomAttribute<PageUpdateStrategyAttribute>()!.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, IPageUpdateStrategy> Created = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> Names => Known.Keys;

    /// <summary>The strategy for a name (null or empty: the default), or null when there is none by that name.</summary>
    public static IPageUpdateStrategy? Get(string? name)
    {
        name = string.IsNullOrEmpty(name) ? Default : name;
        if (!Created.TryGetValue(name, out var strategy) && Known.TryGetValue(name, out var type))
        {
            strategy = (IPageUpdateStrategy)Activator.CreateInstance(type, nonPublic: true)!;
            Created[name] = strategy;
        }
        return strategy;
    }
}
