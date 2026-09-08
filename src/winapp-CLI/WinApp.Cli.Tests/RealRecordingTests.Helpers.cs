// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinApp.Cli.Tests;

/// <summary>
/// Shared setup for the real-recording suite: a live UI Automation stack from the shipped library
/// plus the CLI's recording service on top of an injectable capture seam.
/// </summary>
public partial class RealRecordingTests
{
    private const int ReadyTimeoutMs = 10_000;

    /// <summary>Real UI Automation engine, resolved the same way the CLI resolves it.</summary>
    private static TestAutomation NewAutomation()
        => new(new ServiceCollection()
            .AddLogging()
            .AddWinAppUiAutomation()
            .BuildServiceProvider()
            .GetRequiredService<IUiAutomation>());

    /// <summary>
    /// Wraps the real engine so element resolution still runs against the real UIA tree. Raw capture
    /// substitution now lives on <see cref="FakeWindowCapture"/>, which owns those primitives.
    /// </summary>
    internal sealed class TestAutomation(IUiAutomation inner) : IUiAutomation
    {
        public List<(nint Hwnd, int Pid, string Title)> FindWindowsByTitle(string titleQuery) => inner.FindWindowsByTitle(titleQuery);
        public List<(nint Hwnd, int Pid, string Title)> FindWindowsByPid(int pid) => inner.FindWindowsByPid(pid);
        public bool TryGetWindowRect(long hwnd, out PointerRect rect) => inner.TryGetWindowRect(hwnd, out rect);
        public Task<UiElement[]> InspectAsync(UiTarget s, string? id, int depth, CancellationToken ct) => inner.InspectAsync(s, id, depth, ct);
        public Task<UiElement[]> InspectAncestorsAsync(UiTarget s, string id, CancellationToken ct) => inner.InspectAncestorsAsync(s, id, ct);
        public Task<UiElement[]> SearchAsync(UiTarget s, UiSelector sel, int max, CancellationToken ct) => inner.SearchAsync(s, sel, max, ct);
        public Task<UiElement?> FindSingleElementAsync(UiTarget s, UiSelector sel, CancellationToken ct) => inner.FindSingleElementAsync(s, sel, ct);
        public Task<Dictionary<string, object?>> GetPropertiesAsync(UiTarget s, UiElement e, string? p, CancellationToken ct) => inner.GetPropertiesAsync(s, e, p, ct);
        public Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(UiTarget s, string? id, bool screen, bool focus, CancellationToken ct) => inner.ScreenshotAsync(s, id, screen, focus, ct);
        public Task<string> InvokeAsync(UiTarget s, UiElement e, CancellationToken ct) => inner.InvokeAsync(s, e, ct);
        public Task SetValueAsync(UiTarget s, UiElement e, string text, CancellationToken ct) => inner.SetValueAsync(s, e, text, ct);
        public Task FocusAsync(UiTarget s, UiElement e, CancellationToken ct) => inner.FocusAsync(s, e, ct);
        public Task ScrollIntoViewAsync(UiTarget s, UiElement e, CancellationToken ct) => inner.ScrollIntoViewAsync(s, e, ct);
        public Task ScrollContainerAsync(UiTarget s, UiElement e, string? dir, string? dest, CancellationToken ct) => inner.ScrollContainerAsync(s, e, dir, dest, ct);
        public Task<UiElement?> GetFocusedElementAsync(UiTarget s, CancellationToken ct) => inner.GetFocusedElementAsync(s, ct);
        public Task<string?> GetTextAsync(UiTarget s, UiElement e, CancellationToken ct) => inner.GetTextAsync(s, e, ct);
        public bool TryResolveRootWindow(UiTarget t, out nint hwnd, out string? title) => inner.TryResolveRootWindow(t, out hwnd, out title);
        public nint ResolveElementTopLevelWindow(UiTarget t, UiElement e) => inner.ResolveElementTopLevelWindow(t, e);
        public PointerRect GetVisibleWindowBounds(nint hwnd, PointerRect fallback) => inner.GetVisibleWindowBounds(hwnd, fallback);
    }

    private static UiRecordingService NewRecordingService(IUiAutomation automation, IWindowCapture capture)
        => new(automation, capture, new UiSelectorParserAdapter(), NullLogger<UiRecordingService>.Instance);

    /// <summary>
    /// Minimal selector parser for the recording tests: <c>ui record --selector</c> only ever
    /// receives plain-text queries here.
    /// </summary>
    private sealed class UiSelectorParserAdapter : IUiSelectorParser
    {
        public UiSelector Parse(string selector) => new() { Query = selector };
    }

    private static UiTarget SessionFor(UiaTestFixture fx, bool explicitWindow = true) => new()
    {
        ProcessId = fx.ProcessId,
        ProcessName = "WinApp.Cli.Tests",
        WindowHandle = fx.Hwnd,
        WindowTitle = fx.Title,
        IsExplicitWindow = explicitWindow,
    };

    private static async Task<UiElement> ResolveAsync(TestAutomation svc, UiTarget uiTarget, string automationId)
    {
        var deadline = Environment.TickCount64 + ReadyTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var el = await svc.FindSingleElementAsync(uiTarget, new UiSelector { Query = automationId }, CancellationToken.None);
            if (el is not null)
            {
                return el;
            }
            await Task.Delay(50);
        }

        Assert.Inconclusive($"Element '{automationId}' never became available on the test fixture window.");
        throw new InvalidOperationException("unreachable");
    }
}
