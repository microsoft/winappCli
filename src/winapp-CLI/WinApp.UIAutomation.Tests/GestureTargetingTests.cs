// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
public class GestureTargetingTests
{
    private static readonly UiTarget Session = new() { ProcessId = 1, ProcessName = "app", WindowHandle = 2 };
    private static readonly UiSelector Selector = new() { Slug = "btn-ok" };

    [TestMethod]
    public async Task ResolveStableAsync_ReturnsOkWhenConfirmingReadSettlesWithinTolerance()
    {
        var ui = new QueueUiAutomation([Element(11, 22, 101, 52)]);
        var delays = new List<int>();

        var result = await GestureTargeting.ResolveStableAsync(
            ui, Session, Selector, Element(10, 20, 100, 50), maxReads: 3, readDelayMs: 17,
            (ms, _) => { delays.Add(ms); return Task.CompletedTask; }, CancellationToken.None);

        Assert.AreEqual(TargetStatus.Ok, result.Status);
        Assert.AreEqual(61, result.CenterX);
        Assert.AreEqual(48, result.CenterY);
        Assert.AreEqual(1, delays.Count);
        Assert.AreEqual(17, delays[0]);
        Assert.AreEqual(1, ui.FindCalls);
    }

    [TestMethod]
    public async Task ResolveStableAsync_ReturnsNotFoundWhenElementVanishes()
    {
        var initial = Element(1, 2, 3, 4);
        var result = await GestureTargeting.ResolveStableAsync(
            new QueueUiAutomation([null]), Session, Selector, initial, 2, 0,
            (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.AreEqual(TargetStatus.NotFound, result.Status);
        Assert.AreSame(initial, result.Element);
        Assert.AreEqual(0, result.CenterX);
        Assert.AreEqual(0, result.CenterY);
    }

    [TestMethod]
    [DataRow(0, 10)]
    [DataRow(10, 0)]
    public async Task ResolveStableAsync_ReturnsZeroSizeWhenCurrentElementCollapses(double width, double height)
    {
        var collapsed = Element(1, 2, width, height);
        var result = await GestureTargeting.ResolveStableAsync(
            new QueueUiAutomation([collapsed]), Session, Selector, Element(1, 2, 10, 10), 1, 0,
            (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.AreEqual(TargetStatus.ZeroSize, result.Status);
        Assert.AreSame(collapsed, result.Element);
    }

    [TestMethod]
    public async Task ResolveStableAsync_ReturnsMovingWithLastKnownCenterWhenBudgetExhausted()
    {
        var latest = Element(30, 40, 20, 10);
        var result = await GestureTargeting.ResolveStableAsync(
            new QueueUiAutomation([Element(10, 10, 20, 10), latest]), Session, Selector, Element(0, 0, 20, 10), 2, 0,
            (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.AreEqual(TargetStatus.Moving, result.Status);
        Assert.AreSame(latest, result.Element);
        Assert.AreEqual(40, result.CenterX);
        Assert.AreEqual(45, result.CenterY);
    }

    [TestMethod]
    public async Task ResolveStableAsync_ZeroReadBudgetReportsInitialAsMoving()
    {
        var initial = Element(2, 4, 10, 20);
        var result = await GestureTargeting.ResolveStableAsync(
            new QueueUiAutomation([]), Session, Selector, initial, 0, 0, null, CancellationToken.None);

        Assert.AreEqual(TargetStatus.Moving, result.Status);
        Assert.AreSame(initial, result.Element);
        Assert.AreEqual(7, result.CenterX);
        Assert.AreEqual(14, result.CenterY);
    }

    [TestMethod]
    public async Task ConfirmStillAsync_CoversNotFoundZeroSizeMovingAndOk()
    {
        var expected = Element(10, 20, 100, 40);

        var notFound = await GestureTargeting.ConfirmStillAsync(new QueueUiAutomation([null]), Session, Selector, expected, CancellationToken.None);
        Assert.AreEqual(TargetStatus.NotFound, notFound.Status);
        Assert.AreSame(expected, notFound.Element);

        var zero = Element(10, 20, 0, 40);
        var zeroResult = await GestureTargeting.ConfirmStillAsync(new QueueUiAutomation([zero]), Session, Selector, expected, CancellationToken.None);
        Assert.AreEqual(TargetStatus.ZeroSize, zeroResult.Status);
        Assert.AreSame(zero, zeroResult.Element);

        var movedElement = Element(20, 20, 100, 40);
        var moved = await GestureTargeting.ConfirmStillAsync(new QueueUiAutomation([movedElement]), Session, Selector, expected, CancellationToken.None);
        Assert.AreEqual(TargetStatus.Moving, moved.Status);
        Assert.AreEqual(70, moved.CenterX);
        Assert.AreEqual(40, moved.CenterY);

        var okElement = Element(12, 19, 101, 39);
        var ok = await GestureTargeting.ConfirmStillAsync(new QueueUiAutomation([okElement]), Session, Selector, expected, CancellationToken.None);
        Assert.AreEqual(TargetStatus.Ok, ok.Status);
        Assert.AreEqual(62, ok.CenterX);
        Assert.AreEqual(38, ok.CenterY);
    }

    private static UiElement Element(double x, double y, double width, double height)
        => new() { X = x, Y = y, Width = width, Height = height, Type = "Button", Name = "OK" };

    private sealed class QueueUiAutomation : IUiAutomation
    {
        private readonly Queue<UiElement?> _elements;
        public int FindCalls { get; private set; }

        public QueueUiAutomation(IEnumerable<UiElement?> elements) => _elements = new Queue<UiElement?>(elements);

        public Task<UiElement?> FindSingleElementAsync(UiTarget uiTarget, UiSelector selector, CancellationToken ct)
        {
            Assert.AreSame(Session, uiTarget);
            Assert.AreSame(Selector, selector);
            FindCalls++;
            return Task.FromResult(_elements.Dequeue());
        }

        public List<(nint Hwnd, int Pid, string Title)> FindWindowsByTitle(string titleQuery) => throw new NotImplementedException();
        public List<(nint Hwnd, int Pid, string Title)> FindWindowsByPid(int pid) => throw new NotImplementedException();
        public Task<UiElement[]> InspectAsync(UiTarget uiTarget, string? elementId, int depth, CancellationToken ct) => throw new NotImplementedException();
        public Task<UiElement[]> InspectAncestorsAsync(UiTarget uiTarget, string elementId, CancellationToken ct) => throw new NotImplementedException();
        public Task<UiElement[]> SearchAsync(UiTarget uiTarget, UiSelector selector, int maxResults, CancellationToken ct) => throw new NotImplementedException();
        public Task<Dictionary<string, object?>> GetPropertiesAsync(UiTarget uiTarget, UiElement element, string? propertyName, CancellationToken ct) => throw new NotImplementedException();
        public Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(UiTarget uiTarget, string? elementId, bool captureScreen, bool focus, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> InvokeAsync(UiTarget uiTarget, UiElement element, CancellationToken ct) => throw new NotImplementedException();
        public Task SetValueAsync(UiTarget uiTarget, UiElement element, string text, CancellationToken ct) => throw new NotImplementedException();
        public Task FocusAsync(UiTarget uiTarget, UiElement element, CancellationToken ct) => throw new NotImplementedException();
        public Task ScrollIntoViewAsync(UiTarget uiTarget, UiElement element, CancellationToken ct) => throw new NotImplementedException();
        public Task ScrollContainerAsync(UiTarget uiTarget, UiElement element, string? direction, string? destination, CancellationToken ct) => throw new NotImplementedException();
        public Task<UiElement?> GetFocusedElementAsync(UiTarget uiTarget, CancellationToken ct) => throw new NotImplementedException();
        public Task<string?> GetTextAsync(UiTarget uiTarget, UiElement element, CancellationToken ct) => throw new NotImplementedException();
        public bool TryResolveRootWindow(UiTarget target, out nint hwnd, out string? title) => throw new NotImplementedException();
        public nint ResolveElementTopLevelWindow(UiTarget target, UiElement element) => throw new NotImplementedException();
        public PointerRect GetVisibleWindowBounds(nint hwnd, PointerRect fallback) => fallback;

        public bool TryGetWindowRect(long hwnd, out PointerRect rect)
        {
            rect = default;
            return false;
        }
    }
}


