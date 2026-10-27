// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// Lets every <c>winapp ui</c> command take a DevTools selector for a Microsoft.UI.Reactor element
/// (<c>&lt;Counter&gt;/Button</c>, <c>key=save</c>, <c>App.cs:35</c>, <c>#incrementButton</c>, a DevTools slug, or a
/// rendered element's C# name). DevTools resolves the selector to one live element; the UI Automation element with its
/// AutomationId, or else at its rectangle on screen, becomes the target, under its own slug. Every other selector
/// passes through unchanged.
/// </summary>
internal sealed partial class DevToolsSelectorUiAutomation(IUiAutomation inner) : IUiAutomation
{
    /// <summary>
    /// Whether a <c>winapp ui</c> selector can only be a DevTools selector: a Reactor selector, a <c>#name</c> or a
    /// DevTools slug (its identity has 10 hex digits; a UI Automation slug's has 4).
    /// </summary>
    internal static bool IsDevToolsSelector(string? selector) =>
        selector?.Trim() is { Length: > 0 } text &&
        (ReactorSelector.Parse(text) is not null || IsHashName(text) || DevToolsSlug.Parse(text) is not null);

    /// <summary>
    /// Whether a selector reads like a C# variable name. <c>winapp ui</c> searches such text by name and AutomationId,
    /// so it means a Reactor element only when it is exactly one rendered Reactor element's C# name.
    /// </summary>
    internal static bool IsCSharpName(string? selector) =>
        selector?.Trim() is { Length: > 0 } text && (char.IsLetter(text[0]) || text[0] == '_') &&
        text.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static bool IsHashName(string text) => text.Length > 1 && text[0] == '#' && IsCSharpName(text[1..]);

    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByTitle(string titleQuery) => inner.FindWindowsByTitle(titleQuery);
    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByPid(int pid) => inner.FindWindowsByPid(pid);
    public bool TryGetWindowRect(long hwnd, out PointerRect rect) => inner.TryGetWindowRect(hwnd, out rect);

    public async Task<UiElement[]> InspectAsync(UiTarget uiTarget, string? elementId, int depth, CancellationToken ct)
    {
        var (target, id) = await TranslateIdAsync(uiTarget, elementId, ct);
        return await inner.InspectAsync(target, id, depth, ct);
    }

    public async Task<UiElement[]> InspectAncestorsAsync(UiTarget uiTarget, string elementId, CancellationToken ct)
    {
        var (target, id) = await TranslateIdAsync(uiTarget, elementId, ct);
        return await inner.InspectAncestorsAsync(target, id!, ct);
    }

    public async Task<UiElement[]> SearchAsync(UiTarget uiTarget, UiSelector selector, int maxResults, CancellationToken ct) =>
        await inner.SearchAsync(uiTarget, await TranslateAsync(uiTarget, selector, ct), maxResults, ct);

    public async Task<UiElement?> FindSingleElementAsync(UiTarget uiTarget, UiSelector selector, CancellationToken ct) =>
        await inner.FindSingleElementAsync(uiTarget, await TranslateAsync(uiTarget, selector, ct), ct);

    public async Task<UiElement?> FindSingleElementAsync(UiTarget uiTarget, UiSelector selector, bool requireUnique, CancellationToken ct) =>
        await inner.FindSingleElementAsync(uiTarget, await TranslateAsync(uiTarget, selector, ct), requireUnique, ct);

    public bool IsSameElement(UiElement selected, UiElement current, CancellationToken ct) => inner.IsSameElement(selected, current, ct);

    public Task<Dictionary<string, object?>> GetPropertiesAsync(UiTarget uiTarget, UiElement element, string? propertyName, CancellationToken ct) =>
        inner.GetPropertiesAsync(uiTarget, element, propertyName, ct);

    public async Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(UiTarget uiTarget, string? elementId, bool captureScreen, bool focus, CancellationToken ct)
    {
        var (target, id) = await TranslateIdAsync(uiTarget, elementId, ct);
        return await inner.ScreenshotAsync(target, id, captureScreen, focus, ct);
    }

    public Task<string> InvokeAsync(UiTarget uiTarget, UiElement element, CancellationToken ct) => inner.InvokeAsync(uiTarget, element, ct);
    public Task<UiInvokeActionResult> InvokeAsync(UiTarget uiTarget, UiElement element, UiInvokeAction action, CancellationToken ct) =>
        inner.InvokeAsync(uiTarget, element, action, ct);
    public Task SetValueAsync(UiTarget uiTarget, UiElement element, string text, CancellationToken ct) => inner.SetValueAsync(uiTarget, element, text, ct);
    public Task FocusAsync(UiTarget uiTarget, UiElement element, CancellationToken ct) => inner.FocusAsync(uiTarget, element, ct);
    public Task ScrollIntoViewAsync(UiTarget uiTarget, UiElement element, CancellationToken ct) => inner.ScrollIntoViewAsync(uiTarget, element, ct);
    public Task ScrollContainerAsync(UiTarget uiTarget, UiElement element, string? direction, string? destination, CancellationToken ct) =>
        inner.ScrollContainerAsync(uiTarget, element, direction, destination, ct);
    public Task<UiElement?> GetFocusedElementAsync(UiTarget uiTarget, CancellationToken ct) => inner.GetFocusedElementAsync(uiTarget, ct);
    public Task<string?> GetTextAsync(UiTarget uiTarget, UiElement element, CancellationToken ct) => inner.GetTextAsync(uiTarget, element, ct);
    public bool TryResolveRootWindow(UiTarget target, out nint hwnd, out string? title) => inner.TryResolveRootWindow(target, out hwnd, out title);
    public nint ResolveElementTopLevelWindow(UiTarget target, UiElement element) => inner.ResolveElementTopLevelWindow(target, element);
    public PointerRect GetVisibleWindowBounds(nint hwnd, PointerRect fallback) => inner.GetVisibleWindowBounds(hwnd, fallback);

    private async Task<UiSelector> TranslateAsync(UiTarget target, UiSelector selector, CancellationToken ct)
    {
        var root = selector.Root is { } r ? await TranslateAsync(target, r, ct) : null;
        if (selector.Query is { } query && await TranslateTextAsync(target, query, ct) is { } resolved)
        {
            return selector with { Query = null, Slug = resolved.Slug, Root = root };
        }
        return root == selector.Root ? selector : selector with { Root = root };
    }

    /// <summary>
    /// The element id <c>winapp ui</c> should use, and the window to look in: the one DevTools found the element in,
    /// unless the command named a window.
    /// </summary>
    private async Task<(UiTarget Target, string? ElementId)> TranslateIdAsync(UiTarget target, string? elementId, CancellationToken ct)
    {
        if (elementId is null || await TranslateTextAsync(target, elementId, ct) is not { } resolved)
        {
            return (target, elementId);
        }
        return target.IsExplicitWindow || resolved.Window == 0 || resolved.Window == target.WindowHandle
            ? (target, resolved.Slug)
            : (new UiTarget
            {
                ProcessId = target.ProcessId,
                ProcessName = target.ProcessName,
                WindowHandle = resolved.Window,
                IsExplicitWindow = true,
            }, resolved.Slug);
    }

    /// <summary>The UI Automation slug of a DevTools element, and the window UI Automation found it in.</summary>
    private readonly record struct Resolved(string Slug, nint Window);

    private async Task<Resolved?> TranslateTextAsync(UiTarget target, string text, CancellationToken ct) =>
        IsDevToolsSelector(text) ? await ResolveSlugAsync(target, text.Trim(), csharpNameOnly: false, ct)
        : IsCSharpName(text) ? await ResolveSlugAsync(target, text.Trim(), csharpNameOnly: true, ct)
        : null;

    /// <summary>
    /// The UI Automation slug of the element a DevTools selector names; null when the app has no DevTools attached, so
    /// the text means what it always meant to <c>winapp ui</c> (a name or AutomationId search). With
    /// <paramref name="csharpNameOnly"/>, the text is a Reactor element's C# name only when one rendered element has
    /// it, and otherwise also keeps its <c>winapp ui</c> meaning.
    /// </summary>
    private async Task<Resolved?> ResolveSlugAsync(UiTarget target, string selector, bool csharpNameOnly, CancellationToken ct)
    {
        var tap = new VisualTreeTap(unchecked((uint)target.ProcessId), window: target.IsExplicitWindow && target.WindowHandle != 0 ? target.WindowHandle : null);
        if (!File.Exists(@"\\.\pipe\" + DevToolsPipeDiscovery.PipeNameFor(target.ProcessId)) || !tap.Hello(cancellationToken: ct).Ok)
        {
            return null;
        }
        string handle;
        if (csharpNameOnly)
        {
            if (ResolveCSharpName(tap, selector, ct) is not { } named)
            {
                return null;
            }
            handle = named;
        }
        else
        {
            var resolved = DevToolsSelector.Resolve(tap, selector, ct);
            if (!resolved.Ok)
            {
                var message = resolved.RemoteError?.Message ?? resolved.Error ?? $"'{selector}' matched no element.";
                throw resolved.Candidates.Length > 0
                    ? Ambiguous(message, resolved.Candidates)
                    : new UiElementNotFoundException(selector);
            }
            handle = resolved.Handle!;
        }

        var bounds = ReadBounds(tap, handle, ct)
            ?? throw new InvalidOperationException($"DevTools found '{selector}' but could not read where it is on screen.");
        var props = DevToolsPropertyRow.Parse(tap.RequestProperties(handle, ct).ResultJson);
        var automationId = NonEmpty(DevToolsPropertyRow.Find(props, "AutomationProperties.AutomationId")?.Value);
        var name = NonEmpty(DevToolsPropertyRow.Find(props, "AutomationProperties.Name")?.Value);

        var windows = new List<nint>();
        if (target.WindowHandle != 0)
        {
            windows.Add((nint)target.WindowHandle);
        }
        windows.AddRange(inner.FindWindowsByPid(target.ProcessId).Select(w => w.Hwnd).Where(h => !windows.Contains(h)));

        // The AutomationId is the element's identity in both trees, so try it first; geometry decides otherwise.
        foreach (var query in new[] { automationId, name, null }.Distinct())
        {
            var perWindow = new List<(nint Hwnd, UiElement[] Elements, (double X, double Y, double W, double H)? Expected)>();
            foreach (var hwnd in windows)
            {
                var windowTarget = new UiTarget
                {
                    ProcessId = target.ProcessId,
                    ProcessName = target.ProcessName,
                    WindowHandle = hwnd,
                    IsExplicitWindow = true,
                };
                var found = query is not null
                    ? await inner.SearchAsync(windowTarget, new UiSelector { Query = query }, 64, ct)
                    : Flatten(await inner.InspectAsync(windowTarget, null, 64, ct)).ToArray();
                // The engine reports the element's rectangle in physical screen pixels, which is what UI Automation
                // uses whatever the app's DPI awareness, and wherever its XAML island sits in the window.
                perWindow.Add((hwnd, found, bounds.Screen ?? ScreenRect(hwnd, bounds.Local)));
            }
            var all = perWindow.SelectMany(w => w.Elements).Where(e => e.Selector is not null)
                .DistinctBy(e => e.Selector, StringComparer.Ordinal).ToList();
            if (Match(all, automationId, null, null) is { Selector: { } byId } identified)
            {
                return new Resolved(byId, perWindow.First(w => w.Elements.Contains(identified)).Hwnd);
            }
            foreach (var (hwnd, elements, expected) in perWindow)
            {
                if (Match(elements, automationId, name, expected) is { Selector: { } slug })
                {
                    return new Resolved(slug, hwnd);
                }
            }
        }
        throw new UiElementNotFoundException(selector);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// The one rendered Reactor element whose C# variable is <paramref name="name"/>; null when none is, so the text
    /// keeps its <c>winapp ui</c> meaning.
    /// </summary>
    private static string? ResolveCSharpName(VisualTreeTap tap, string name, CancellationToken ct)
    {
        var (result, error) = DevToolsSelector.Find(tap, name, appAuthoredOnly: false, ct);
        if (error is not null || result is null)
        {
            return null;
        }
        var named = result.Matches.Where(m => m.IsReactor && string.Equals(m.ReactorName, name, StringComparison.Ordinal)).ToArray();
        return named.Length switch
        {
            0 => null,
            1 => named[0].Handle,
            _ => throw Ambiguous($"{named.Length} elements are named '{name}'. Use one of their selectors instead:", named),
        };
    }

    private static UiAmbiguousSelectorException Ambiguous(string message, IEnumerable<DevToolsSelector.VisualTreeMatch> candidates)
    {
        var lines = string.Join(Environment.NewLine, candidates.Select(c =>
            $"  [{c.Selector}] {c.Label ?? c.ShortType}{(c.Detail is null ? "" : " " + c.Detail)}"));
        return new UiAmbiguousSelectorException(lines.Length > 0 ? message + Environment.NewLine + lines : message);
    }

    /// <summary>
    /// The UI Automation element DevTools' element is: the only one with its AutomationId; else the one whose bounds
    /// match its rectangle on screen; else, when it is scrolled out of view (UI Automation then reports no rectangle,
    /// or only the part still shown), the only such element with its name.
    /// </summary>
    internal static UiElement? Match(IReadOnlyList<UiElement> candidates, string? automationId, string? name,
        (double X, double Y, double W, double H)? expected)
    {
        if (automationId is not null)
        {
            var byId = candidates.Where(c => string.Equals(c.AutomationId, automationId, StringComparison.Ordinal)).ToList();
            if (byId.Count == 1)
            {
                return byId[0];
            }
            if (byId.Count > 1)
            {
                return expected is { } among ? Closest(byId, among) : null;
            }
        }
        if (expected is not { } rect)
        {
            return null;
        }
        if (Closest(candidates, rect) is { } at)
        {
            return at;
        }
        if (name is null)
        {
            return null;
        }
        var hidden = candidates.Where(c => string.Equals(c.Name, name, StringComparison.Ordinal) &&
            (c.Width <= 0 || c.Height <= 0 || Inside(c, rect, Tolerance(rect)))).Take(2).ToList();
        return hidden.Count == 1 ? hidden[0] : null;
    }

    private static double Tolerance((double X, double Y, double W, double H) rect) => Math.Max(3, Math.Min(rect.W, rect.H) * 0.1);

    private static bool Inside(UiElement element, (double X, double Y, double W, double H) rect, double tolerance) =>
        element.X >= rect.X - tolerance && element.Y >= rect.Y - tolerance &&
        element.X + element.Width <= rect.X + rect.W + tolerance && element.Y + element.Height <= rect.Y + rect.H + tolerance;

    /// <summary>The candidate whose bounds match the expected screen rectangle within a few pixels.</summary>
    internal static UiElement? Closest(IEnumerable<UiElement> candidates, (double X, double Y, double W, double H) expected)
    {
        var tolerance = Tolerance(expected);
        return candidates
            .Select(c => (Element: c, Distance: Math.Abs(c.X - expected.X) + Math.Abs(c.Y - expected.Y) +
                Math.Abs(c.Width - expected.W) + Math.Abs(c.Height - expected.H)))
            .Where(c => Math.Abs(c.Element.X - expected.X) <= tolerance && Math.Abs(c.Element.Y - expected.Y) <= tolerance &&
                Math.Abs(c.Element.Width - expected.W) <= tolerance && Math.Abs(c.Element.Height - expected.H) <= tolerance)
            .OrderBy(c => c.Distance)
            .Select(c => c.Element)
            .FirstOrDefault();
    }

    private static IEnumerable<UiElement> Flatten(IEnumerable<UiElement> elements)
    {
        foreach (var element in elements)
        {
            yield return element;
            foreach (var child in Flatten(element.Children ?? []))
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// Where DevTools says the element is: its offset and size in device-independent pixels relative to its XAML root,
    /// and, when the engine reports it, its rectangle in physical screen pixels.
    /// </summary>
    internal readonly record struct ElementBounds((double X, double Y, double W, double H) Local, (double X, double Y, double W, double H)? Screen);

    internal static ElementBounds? ReadBounds(VisualTreeTap tap, string handle, CancellationToken ct)
    {
        using var doc = tap.RequestLayout(handle, ct).TryParseResult();
        // UI Automation reports the arranged (render) box; a TextBlock's actual text box can be smaller.
        if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("offset", out var offset) ||
            !(root.TryGetProperty("render", out var size) || root.TryGetProperty("actual", out size)))
        {
            return null;
        }
        (double, double, double, double)? screen = root.TryGetProperty("screen", out var s) && s.ValueKind == JsonValueKind.Object
            ? (s.GetProperty("x").GetDouble(), s.GetProperty("y").GetDouble(), s.GetProperty("w").GetDouble(), s.GetProperty("h").GetDouble())
            : null;
        return new ElementBounds((offset.GetProperty("x").GetDouble(), offset.GetProperty("y").GetDouble(),
            size.GetProperty("w").GetDouble(), size.GetProperty("h").GetDouble()), screen);
    }

    private static (double X, double Y, double W, double H)? ScreenRect(nint hwnd, (double X, double Y, double W, double H) bounds)
    {
        // This process is per-monitor aware, so the origin is in physical pixels like UI Automation's bounds.
        var origin = new POINT();
        if (!ClientToScreen(hwnd, ref origin))
        {
            return null;
        }
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        return monitor != 0 && GetDpiForMonitor(monitor, MonitorDpiEffective, out var dpi, out _) == 0
            ? ScreenRect((origin.X, origin.Y), dpi, bounds)
            : null;
    }

    /// <summary>
    /// A rectangle in a window's XAML DIPs, in physical screen pixels. A physical pixel per DIP is the monitor's scale
    /// whatever the app's DPI awareness: a DPI-unaware window reports 96 DPI and renders at scale 1, and Windows
    /// stretches it to the monitor's scale. So the window's own DPI must not be used here.
    /// </summary>
    internal static (double X, double Y, double W, double H) ScreenRect((int X, int Y) clientOrigin, uint monitorDpi,
        (double X, double Y, double W, double H) bounds)
    {
        var scale = monitorDpi == 0 ? 1.0 : monitorDpi / 96.0;
        return (clientOrigin.X + bounds.X * scale, clientOrigin.Y + bounds.Y * scale, bounds.W * scale, bounds.H * scale);
    }

    private const uint MonitorDefaultToNearest = 2;
    private const int MonitorDpiEffective = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hwnd, ref POINT point);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
