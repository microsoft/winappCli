// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// Lets every <c>winapp ui</c> command take a DevTools selector for a Microsoft.UI.Reactor element
/// (<c>&lt;Counter&gt;/Button</c>, <c>key=save</c>, <c>App.cs:35</c>). DevTools resolves the selector to one live
/// element and its bounds; the UI Automation element with those bounds becomes the target, under its own slug.
/// Every other selector passes through unchanged.
/// </summary>
internal sealed partial class DevToolsSelectorUiAutomation(IUiAutomation inner) : IUiAutomation
{
    /// <summary>Whether a <c>winapp ui</c> selector is a DevTools selector for a Reactor element.</summary>
    internal static bool IsDevToolsSelector(string? selector) =>
        !string.IsNullOrWhiteSpace(selector) && ReactorSelector.Parse(selector.Trim()) is not null;

    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByTitle(string titleQuery) => inner.FindWindowsByTitle(titleQuery);
    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByPid(int pid) => inner.FindWindowsByPid(pid);
    public bool TryGetWindowRect(long hwnd, out PointerRect rect) => inner.TryGetWindowRect(hwnd, out rect);

    public async Task<UiElement[]> InspectAsync(UiTarget uiTarget, string? elementId, int depth, CancellationToken ct) =>
        await inner.InspectAsync(uiTarget, await TranslateIdAsync(uiTarget, elementId, ct), depth, ct);

    public async Task<UiElement[]> InspectAncestorsAsync(UiTarget uiTarget, string elementId, CancellationToken ct) =>
        await inner.InspectAncestorsAsync(uiTarget, (await TranslateIdAsync(uiTarget, elementId, ct))!, ct);

    public async Task<UiElement[]> SearchAsync(UiTarget uiTarget, UiSelector selector, int maxResults, CancellationToken ct) =>
        await inner.SearchAsync(uiTarget, await TranslateAsync(uiTarget, selector, ct), maxResults, ct);

    public async Task<UiElement?> FindSingleElementAsync(UiTarget uiTarget, UiSelector selector, CancellationToken ct) =>
        await inner.FindSingleElementAsync(uiTarget, await TranslateAsync(uiTarget, selector, ct), ct);

    public async Task<UiElement?> FindSingleElementAsync(UiTarget uiTarget, UiSelector selector, bool requireUnique, CancellationToken ct) =>
        await inner.FindSingleElementAsync(uiTarget, await TranslateAsync(uiTarget, selector, ct), requireUnique, ct);

    public bool IsSameElement(UiElement selected, UiElement current, CancellationToken ct) => inner.IsSameElement(selected, current, ct);

    public Task<Dictionary<string, object?>> GetPropertiesAsync(UiTarget uiTarget, UiElement element, string? propertyName, CancellationToken ct) =>
        inner.GetPropertiesAsync(uiTarget, element, propertyName, ct);

    public async Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(UiTarget uiTarget, string? elementId, bool captureScreen, bool focus, CancellationToken ct) =>
        await inner.ScreenshotAsync(uiTarget, await TranslateIdAsync(uiTarget, elementId, ct), captureScreen, focus, ct);

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
        if (selector.Query is { } query && IsDevToolsSelector(query) && await ResolveSlugAsync(target, query.Trim(), ct) is { } slug)
        {
            return selector with { Query = null, Slug = slug, Root = root };
        }
        return root == selector.Root ? selector : selector with { Root = root };
    }

    private async Task<string?> TranslateIdAsync(UiTarget target, string? elementId, CancellationToken ct) =>
        IsDevToolsSelector(elementId) ? await ResolveSlugAsync(target, elementId!.Trim(), ct) ?? elementId : elementId;

    /// <summary>
    /// The UI Automation slug of the element a DevTools selector names; null when the app has no DevTools attached, so
    /// the text means what it always meant to <c>winapp ui</c> (a name or AutomationId search).
    /// </summary>
    private async Task<string?> ResolveSlugAsync(UiTarget target, string selector, CancellationToken ct)
    {
        var tap = new VisualTreeTap(unchecked((uint)target.ProcessId), window: target.IsExplicitWindow && target.WindowHandle != 0 ? target.WindowHandle : null);
        if (!File.Exists(@"\\.\pipe\" + DevToolsPipeDiscovery.PipeNameFor(target.ProcessId)) || !tap.Hello(cancellationToken: ct).Ok)
        {
            return null;
        }
        var resolved = DevToolsSelector.Resolve(tap, selector, ct);
        if (!resolved.Ok)
        {
            var candidates = string.Join(Environment.NewLine, resolved.Candidates.Select(c =>
                $"  [{c.Selector}] {c.Label ?? c.Type}{(c.Detail is null ? "" : " " + c.Detail)}"));
            var message = resolved.RemoteError?.Message ?? resolved.Error ?? $"'{selector}' matched no element.";
            throw resolved.Candidates.Length > 0
                ? new UiAmbiguousSelectorException(message + (candidates.Length > 0 ? Environment.NewLine + candidates : ""))
                : new UiElementNotFoundException(selector);
        }

        var bounds = ReadBounds(tap, resolved.Handle!, ct)
            ?? throw new InvalidOperationException($"DevTools found '{selector}' but could not read where it is on screen.");
        var name = DevToolsPropertyRow.Find(DevToolsPropertyRow.Parse(tap.RequestProperties(resolved.Handle!, ct).ResultJson),
            "AutomationProperties.Name")?.Value;

        var windows = new List<nint>();
        if (target.WindowHandle != 0)
        {
            windows.Add((nint)target.WindowHandle);
        }
        windows.AddRange(inner.FindWindowsByPid(target.ProcessId).Select(w => w.Hwnd).Where(h => !windows.Contains(h)));
        foreach (var hwnd in windows)
        {
            var expected = ScreenRect(hwnd, bounds);
            if (expected is null)
            {
                continue;
            }
            var windowTarget = new UiTarget
            {
                ProcessId = target.ProcessId,
                ProcessName = target.ProcessName,
                WindowHandle = hwnd,
                IsExplicitWindow = true,
            };
            var candidates = name is { Length: > 0 }
                ? await inner.SearchAsync(windowTarget, new UiSelector { Query = name }, 64, ct)
                : Flatten(await inner.InspectAsync(windowTarget, null, 64, ct)).ToArray();
            if (Closest(candidates, expected.Value) is { Selector: { } slug })
            {
                return slug;
            }
        }
        throw new UiElementNotFoundException(selector);
    }

    /// <summary>The candidate whose bounds match the expected screen rectangle within a few pixels.</summary>
    internal static UiElement? Closest(IEnumerable<UiElement> candidates, (double X, double Y, double W, double H) expected)
    {
        var tolerance = Math.Max(3, Math.Min(expected.W, expected.H) * 0.1);
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

    /// <summary>The element's offset and size in device-independent pixels, relative to its window's content.</summary>
    internal static (double X, double Y, double W, double H)? ReadBounds(VisualTreeTap tap, string handle, CancellationToken ct)
    {
        using var doc = tap.RequestLayout(handle, ct).TryParseResult();
        // UI Automation reports the arranged (render) box; a TextBlock's actual text box can be smaller.
        if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("offset", out var offset) ||
            !(root.TryGetProperty("render", out var size) || root.TryGetProperty("actual", out size)))
        {
            return null;
        }
        return (offset.GetProperty("x").GetDouble(), offset.GetProperty("y").GetDouble(),
            size.GetProperty("w").GetDouble(), size.GetProperty("h").GetDouble());
    }

    private static (double X, double Y, double W, double H)? ScreenRect(nint hwnd, (double X, double Y, double W, double H) bounds)
    {
        var origin = new POINT();
        if (!ClientToScreen(hwnd, ref origin))
        {
            return null;
        }
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        return (origin.X + bounds.X * scale, origin.Y + bounds.Y * scale, bounds.W * scale, bounds.H * scale);
    }

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
    private static partial uint GetDpiForWindow(nint hwnd);
}
