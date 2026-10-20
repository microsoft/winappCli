// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class UiAutomationStateTests
{
    private static readonly UiTarget Target = new() { ProcessId = 1, WindowHandle = 1234, IsExplicitWindow = true };
    private static readonly UiElement Element = new() { AutomationId = "ExactTarget", Selector = "ExactTarget" };
    private readonly ImmediatePollDelay _delay = new();
    private UiAutomationService _service = null!;
    private readonly List<UIA_PATTERN_ID> _patternRequests = [];

    [TestInitialize]
    public void Initialize()
    {
        _service = new UiAutomationService(NullLogger<UiAutomationService>.Instance, new UiSelectorParser(), _delay);
        UiAutomationService.s_getRootElement = (_, _) => throw new AssertFailedException("No target access expected.");
    }

    [TestCleanup]
    public void Cleanup() => UiAutomationService.ResetNativeSeams();

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ExpandCollapse_ConfirmsDelayedStateAndIsIdempotent(bool expand)
    {
        var desired = expand ? ExpandCollapseState.ExpandCollapseState_Expanded : ExpandCollapseState.ExpandCollapseState_Collapsed;
        var current = expand ? ExpandCollapseState.ExpandCollapseState_Collapsed : ExpandCollapseState.ExpandCollapseState_Expanded;
        int changes = 0;
        var pattern = Proxy<IUIAutomationExpandCollapsePattern>((method, _) => method.Name switch
        {
            "get_CurrentExpandCollapseState" => current,
            "Expand" when expand => Change(),
            "Collapse" when !expand => Change(),
            _ => throw new AssertFailedException($"Unexpected pattern method {method.Name}."),
        });
        object? Change() { changes++; return null; }
        _delay.OnDelay = () => { if (_delay.Calls >= 2) { current = desired; } };
        InstallPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId, pattern);
        var state = expand ? UiExpandCollapseState.Expanded : UiExpandCollapseState.Collapsed;

        Assert.AreEqual(state, await _service.SetExpandCollapseStateAsync(Target, Element, state, CancellationToken.None));
        Assert.IsGreaterThanOrEqualTo(3, _delay.Calls);
        Assert.AreEqual(state, await _service.SetExpandCollapseStateAsync(Target, Element, state, CancellationToken.None));
        Assert.AreEqual(1, changes, "The second, already-matching request must not act again.");
        Assert.IsTrue(_patternRequests.All(p => p == UIA_PATTERN_ID.UIA_ExpandCollapsePatternId));
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(999)]
    public async Task ExpandCollapse_RejectsLeafAndUnknownStateWithoutActing(int state)
    {
        var pattern = Proxy<IUIAutomationExpandCollapsePattern>((method, _) => method.Name switch
        {
            "get_CurrentExpandCollapseState" => (ExpandCollapseState)state,
            _ => throw new AssertFailedException("Invalid-state target must not be changed."),
        });
        InstallPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId, pattern);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _service.SetExpandCollapseStateAsync(Target, Element, UiExpandCollapseState.Expanded, CancellationToken.None));

        StringAssert.Contains(error.Message, "unsupported ExpandCollapseState");
        Assert.AreEqual(0, _delay.Calls);
    }

    [TestMethod]
    public async Task StateSetters_RejectDisabledTargetBeforeRequestingPatterns()
    {
        InstallTarget(_ => throw new AssertFailedException("Disabled target must not be acted on."), enabled: false);

        var expand = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _service.SetExpandCollapseStateAsync(Target, Element, UiExpandCollapseState.Expanded, CancellationToken.None));
        var scroll = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _service.SetVerticalScrollPercentAsync(Target, Element, 50, CancellationToken.None));

        StringAssert.Contains(expand.Message, "disabled");
        StringAssert.Contains(scroll.Message, "disabled");
        Assert.IsEmpty(_patternRequests);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StateSetters_RequireExactTargetPatternWithoutAncestorFallback(bool comNotSupported)
    {
        InstallTarget(_ => comNotSupported
            ? throw new COMException("not supported", unchecked((int)0x80040204))
            : null);

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            _service.SetExpandCollapseStateAsync(Target, Element, UiExpandCollapseState.Expanded, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            _service.SetVerticalScrollPercentAsync(Target, Element, 50, CancellationToken.None));

        CollectionAssert.AreEqual(new[]
        {
            UIA_PATTERN_ID.UIA_ExpandCollapsePatternId,
            UIA_PATTERN_ID.UIA_ScrollPatternId,
        }, _patternRequests);
    }

    [TestMethod]
    public async Task StateSetters_RejectDuplicateTargetsBeforeInput()
    {
        InstallTarget(_ => throw new AssertFailedException("Ambiguous target must not be acted on."), matches: 2);

        await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(() =>
            _service.SetExpandCollapseStateAsync(Target, Element, UiExpandCollapseState.Expanded, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(() =>
            _service.SetVerticalScrollPercentAsync(Target, Element, 50, CancellationToken.None));
        Assert.IsEmpty(_patternRequests);
    }

    [TestMethod]
    public async Task StateSetters_MissingAutomationIdDoesNotFallBackToName()
    {
        InstallTarget(_ => throw new AssertFailedException("Missing target must not be acted on."), matches: 0);
        var element = new UiElement { AutomationId = "Missing", Name = "Unrelated target with same name" };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _service.SetExpandCollapseStateAsync(Target, element, UiExpandCollapseState.Expanded, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _service.SetVerticalScrollPercentAsync(Target, element, 50, CancellationToken.None));
        Assert.IsEmpty(_patternRequests);
    }

    [TestMethod]
    public async Task ExpandCollapse_ProviderRefusalTimesOutInsteadOfReportingSuccess()
    {
        int changes = 0;
        var pattern = Proxy<IUIAutomationExpandCollapsePattern>((method, _) =>
        {
            if (method.Name == "Expand") { changes++; return null; }
            return method.Name == "get_CurrentExpandCollapseState"
                ? ExpandCollapseState.ExpandCollapseState_Collapsed
                : throw new AssertFailedException("No fallback allowed.");
        });
        InstallPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId, pattern);

        var error = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            _service.SetExpandCollapseStateAsync(Target, Element, UiExpandCollapseState.Expanded, CancellationToken.None));

        Assert.AreEqual(1, changes);
        Assert.AreEqual(40, _delay.Calls);
        StringAssert.Contains(error.Message, "did not confirm Expanded");
        StringAssert.Contains(error.Message, "Collapsed");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExpandCollapse_ProviderMutationAndReadbackFailuresAreNotSwallowed(bool readbackFailure)
    {
        bool acted = false;
        var failure = new COMException("provider unavailable");
        var pattern = Proxy<IUIAutomationExpandCollapsePattern>((method, _) =>
        {
            if (method.Name == "Expand")
            {
                acted = true;
                return readbackFailure ? null : throw failure;
            }
            if (method.Name == "get_CurrentExpandCollapseState")
            {
                return acted ? throw failure : ExpandCollapseState.ExpandCollapseState_Collapsed;
            }
            throw new AssertFailedException("No fallback allowed.");
        });
        InstallPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId, pattern);

        var error = await Assert.ThrowsExactlyAsync<COMException>(() =>
            _service.SetExpandCollapseStateAsync(Target, Element, UiExpandCollapseState.Expanded, CancellationToken.None));

        Assert.AreSame(failure, error);
        Assert.IsTrue(acted);
        Assert.HasCount(1, _patternRequests);
    }

    [TestMethod]
    public async Task ExpandCollapse_CancellationStopsReadback()
    {
        using var cancellation = new CancellationTokenSource();
        var pattern = Proxy<IUIAutomationExpandCollapsePattern>((method, _) => method.Name switch
        {
            "get_CurrentExpandCollapseState" => ExpandCollapseState.ExpandCollapseState_Collapsed,
            "Expand" => null,
            _ => throw new AssertFailedException("No fallback allowed."),
        });
        InstallPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId, pattern);
        _delay.OnDelay = cancellation.Cancel;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            _service.SetExpandCollapseStateAsync(Target, Element, UiExpandCollapseState.Expanded, cancellation.Token));
        Assert.AreEqual(1, _delay.Calls);
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(100.0)]
    [DataRow(37.125)]
    public async Task ScrollPercent_SetsOnlyVerticalAndConfirmsDelayedReadback(double requested)
    {
        var calls = new List<(double Horizontal, double Vertical)>();
        var actual = requested == 0 ? 50 : 0.0;
        var pattern = ScrollPattern(() => actual, (_, args) =>
        {
            calls.Add(((double)args![0]!, (double)args[1]!));
            return null;
        });
        _delay.OnDelay = () => { if (_delay.Calls >= 2) { actual = requested; } };
        InstallPattern(UIA_PATTERN_ID.UIA_ScrollPatternId, pattern);

        var result = await _service.SetVerticalScrollPercentAsync(Target, Element, requested, CancellationToken.None);

        Assert.AreEqual(requested, result);
        Assert.AreEqual((-1.0, requested), calls.Single());
        Assert.IsGreaterThanOrEqualTo(3, _delay.Calls);
        Assert.AreEqual(requested, await _service.SetVerticalScrollPercentAsync(Target, Element, requested, CancellationToken.None));
        Assert.HasCount(1, calls, "An already-matching request must not change the provider again.");
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(-0.01)]
    [DataRow(100.01)]
    public async Task ScrollPercent_RejectsInvalidInputWithoutResolving(double percent)
    {
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            _service.SetVerticalScrollPercentAsync(Target, Element, percent, CancellationToken.None));
    }

    [TestMethod]
    public async Task StateSetters_CancelledAndInvalidStateRequestsDoNotResolve()
    {
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            _service.SetExpandCollapseStateAsync(Target, Element, (UiExpandCollapseState)999, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            _service.SetExpandCollapseStateAsync(Target, Element, UiExpandCollapseState.Expanded, cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            _service.SetVerticalScrollPercentAsync(Target, Element, 50, cancellation.Token));
    }

    [TestMethod]
    public async Task ScrollPercent_RequiresVerticalScrolling()
    {
        var pattern = Proxy<IUIAutomationScrollPattern>((method, _) => method.Name switch
        {
            "get_CurrentVerticallyScrollable" => new BOOL(false),
            _ => throw new AssertFailedException("Non-scrollable target must not be changed."),
        });
        InstallPattern(UIA_PATTERN_ID.UIA_ScrollPatternId, pattern);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _service.SetVerticalScrollPercentAsync(Target, Element, 50, CancellationToken.None));
        StringAssert.Contains(error.Message, "itself cannot scroll vertically");
    }

    [TestMethod]
    public async Task ScrollPercent_ProviderRefusalTimesOut()
    {
        int changes = 0;
        InstallPattern(UIA_PATTERN_ID.UIA_ScrollPatternId, ScrollPattern(() => 0, (_, _) => { changes++; return null; }));

        var error = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            _service.SetVerticalScrollPercentAsync(Target, Element, 50, CancellationToken.None));

        Assert.AreEqual(1, changes);
        Assert.AreEqual(40, _delay.Calls);
        StringAssert.Contains(error.Message, "Last observed: 0");
    }

    [TestMethod]
    public async Task ScrollPercent_ReturnsActualRatherThanEchoingRequestedPercentage()
    {
        double actual = 0;
        InstallPattern(UIA_PATTERN_ID.UIA_ScrollPatternId, ScrollPattern(() => actual, (_, _) => { actual = 37.124; return null; }));

        var result = await _service.SetVerticalScrollPercentAsync(Target, Element, 37.125, CancellationToken.None);

        Assert.AreEqual(37.124, result);
        Assert.AreEqual(1, _delay.Calls);
    }

    [TestMethod]
    public async Task ScrollPercent_RejectsUnexpectedHorizontalChange()
    {
        double horizontal = 25;
        double vertical = 0;
        InstallPattern(UIA_PATTERN_ID.UIA_ScrollPatternId, ScrollPattern(() => vertical, (_, _) =>
        {
            horizontal = 50;
            vertical = 50;
            return null;
        }, () => horizontal));

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _service.SetVerticalScrollPercentAsync(Target, Element, 50, CancellationToken.None));
        StringAssert.Contains(error.Message, "horizontal position");
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(-1.0)]
    [DataRow(101.0)]
    public async Task ScrollPercent_RejectsInvalidProviderReadback(double actual)
    {
        InstallPattern(UIA_PATTERN_ID.UIA_ScrollPatternId,
            ScrollPattern(() => actual, (_, _) => throw new AssertFailedException("Invalid provider must not be changed.")));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _service.SetVerticalScrollPercentAsync(Target, Element, 50, CancellationToken.None));
    }

    [TestMethod]
    public async Task ScrollPercent_CancellationStopsReadback()
    {
        using var cancellation = new CancellationTokenSource();
        InstallPattern(UIA_PATTERN_ID.UIA_ScrollPatternId, ScrollPattern(() => 0, (_, _) => null));
        _delay.OnDelay = cancellation.Cancel;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            _service.SetVerticalScrollPercentAsync(Target, Element, 50, cancellation.Token));
        Assert.AreEqual(1, _delay.Calls);
    }

    [TestMethod]
    public async Task Properties_IncludeTypedViewSizesAndOnlySelectedAutomationIds()
    {
        string[] ids = ["ModeBalanced", "", "ModeBalanced"];
        var selection = SelectionPattern(ids);
        var scroll = Proxy<IUIAutomationScrollPattern>((method, _) => method.Name switch
        {
            "get_CurrentHorizontalScrollPercent" => -1.0,
            "get_CurrentVerticalScrollPercent" => 35.5,
            "get_CurrentHorizontallyScrollable" => (object)new BOOL(false),
            "get_CurrentVerticallyScrollable" => new BOOL(true),
            "get_CurrentHorizontalViewSize" => 100.0,
            "get_CurrentVerticalViewSize" => 27.25,
            _ => throw new AssertFailedException("Properties must not change the provider."),
        });
        InstallTarget(id => id switch
        {
            UIA_PATTERN_ID.UIA_SelectionPatternId => selection,
            UIA_PATTERN_ID.UIA_ScrollPatternId => scroll,
            _ => null,
        });

        var properties = await _service.GetPropertiesAsync(Target, Element, null, CancellationToken.None);

        Assert.AreEqual(100.0, properties["ScrollHorizontalViewSize"]);
        Assert.AreEqual(27.25, properties["ScrollVerticalViewSize"]);
        Assert.AreEqual(false, properties["HorizontallyScrollable"]);
        Assert.AreEqual(true, properties["VerticallyScrollable"]);
        CollectionAssert.AreEqual(ids, (string[])properties["SelectionAutomationIds"]!);
    }

    [TestMethod]
    public async Task Properties_EmptySelectionIsAnActualEmptyArray()
    {
        InstallPattern(UIA_PATTERN_ID.UIA_SelectionPatternId, SelectionPattern([]));

        var properties = await _service.GetPropertiesAsync(Target, Element, "SelectionAutomationIds", CancellationToken.None);

        Assert.IsEmpty((string[])properties["SelectionAutomationIds"]!);
    }

    [TestMethod]
    [DataRow("unavailable")]
    [DataRow("selectionFailure")]
    [DataRow("identifierFailure")]
    [DataRow("tooMany")]
    [DataRow("tooLong")]
    public async Task Properties_UnavailableFailedOrUnboundedSelectionIsOmitted(string failure)
    {
        var pattern = failure == "unavailable" ? null : failure == "selectionFailure"
            ? Proxy<IUIAutomationSelectionPattern>((_, _) => throw new COMException("Selection unavailable."))
            : SelectionPattern(failure switch
            {
                "tooMany" => new string[257],
                "tooLong" => [new string('x', 1025)],
                _ => ["FirstId", "SecondId"],
            }, failIdentifier: failure == "identifierFailure");
        InstallPattern(UIA_PATTERN_ID.UIA_SelectionPatternId, pattern);

        var properties = await _service.GetPropertiesAsync(Target, Element, null, CancellationToken.None);

        Assert.IsFalse(properties.ContainsKey("SelectionAutomationIds"),
            "Never turn a provider failure or partial selection into an empty/partial success array.");
    }

    [TestMethod]
    public async Task InvokeAsync_StillOnlyExpands()
    {
        int expands = 0;
        var pattern = Proxy<IUIAutomationExpandCollapsePattern>((method, _) =>
        {
            if (method.Name == "Expand") { expands++; return null; }
            throw new AssertFailedException("Existing invoke must not collapse or inspect state.");
        });
        InstallPattern(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId, pattern);

        Assert.AreEqual("ExpandCollapsePattern", await _service.InvokeAsync(Target, Element, CancellationToken.None));
        Assert.AreEqual(1, expands);
    }

    private void InstallPattern(UIA_PATTERN_ID id, object? pattern)
        => InstallTarget(requested => requested == id ? pattern : null);

    private void InstallTarget(Func<UIA_PATTERN_ID, object?> pattern, bool enabled = true, int matches = 1)
    {
        var target = Proxy<IUIAutomationElement>((method, args) =>
        {
            if (method.Name == "get_CurrentIsEnabled") { return new BOOL(enabled); }
            if (method.Name == "GetCurrentPattern")
            {
                var id = (UIA_PATTERN_ID)args![0]!;
                _patternRequests.Add(id);
                return pattern(id);
            }
            throw new COMException("Optional property unavailable.");
        });
        var array = Proxy<IUIAutomationElementArray>((method, _) => method.Name switch
        {
            "get_Length" => matches,
            "GetElement" => target,
            _ => throw new AssertFailedException($"Unexpected array call: {method.Name}."),
        });
        var root = Proxy<IUIAutomationElement>((method, args) => method.Name switch
        {
            "FindAll" when (TreeScope)args![0]! == TreeScope.TreeScope_Subtree => array,
            "FindFirst" when matches == 1 => target,
            _ => throw new AssertFailedException($"Unexpected root call: {method.Name}."),
        });
        UiAutomationService.s_getRootElement = (_, _) => root;
    }

    private static IUIAutomationScrollPattern ScrollPattern(
        Func<double> vertical, Func<MethodInfo, object?[]?, object?> set, Func<double>? horizontal = null)
        => Proxy<IUIAutomationScrollPattern>((method, args) => method.Name switch
        {
            "get_CurrentVerticallyScrollable" => new BOOL(true),
            "get_CurrentHorizontalScrollPercent" => horizontal?.Invoke() ?? 25.0,
            "get_CurrentVerticalScrollPercent" => vertical(),
            "SetScrollPercent" => set(method, args),
            _ => throw new AssertFailedException($"Unexpected scroll call: {method.Name}."),
        });

    private static IUIAutomationSelectionPattern SelectionPattern(string[] ids, bool failIdentifier = false)
    {
        var selection = Proxy<IUIAutomationElementArray>((method, args) => method.Name switch
        {
            "get_Length" => ids.Length,
            "GetElement" => Proxy<IUIAutomationElement>((property, _) =>
            {
                if (property.Name != "get_CurrentAutomationId")
                {
                    throw new AssertFailedException("Selection metadata must read only AutomationId, never names, values, or text.");
                }
                if (failIdentifier && (int)args![0]! == 1)
                {
                    throw new COMException("Selected item unavailable.");
                }
                return StringBstr(ids[(int)args![0]!]);
            }),
            _ => throw new AssertFailedException($"Unexpected selection call: {method.Name}."),
        });
        return Proxy<IUIAutomationSelectionPattern>((method, _) => method.Name == "GetCurrentSelection"
            ? selection : throw new AssertFailedException("Selection metadata must be read-only."));
    }

    private static unsafe BSTR StringBstr(string value) => new((char*)Marshal.StringToBSTR(value));

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, PatternProxy>();
        ((PatternProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private class PatternProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

    private sealed class ImmediatePollDelay : IPollDelay
    {
        public int Calls { get; private set; }
        public Action? OnDelay { get; set; }

        public Task DelayAsync(int milliseconds, CancellationToken ct)
        {
            Assert.AreEqual(50, milliseconds);
            Calls++;
            OnDelay?.Invoke();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
