// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
public class ExplicitUiInvokerTests
{
    private static readonly UiElement Element = new() { Id = "chosen", Selector = "aid:chosen", Type = "CheckBox" };
    private const ToggleState Off = ToggleState.ToggleState_Off;
    private const ToggleState On = ToggleState.ToggleState_On;
    private const ToggleState Indeterminate = ToggleState.ToggleState_Indeterminate;
    private static readonly string[] ToggleCalls = ["toggle"];
    private static readonly string[] ReadOnlyCalls = ["read"];
    private static readonly string[] OneToggleCalls = ["read", "toggle", "read"];
    private static readonly string[] TwoToggleCalls = ["read", "toggle", "read", "toggle", "read"];
    private static readonly string[] ActionNames = ["Invoke", "Select", "Toggle", "ToggleOn", "ToggleOff", "Expand", "Collapse"];

    [TestMethod]
    [DataRow(UiInvokeAction.Invoke, "InvokePattern", "invoke")]
    [DataRow(UiInvokeAction.Select, "SelectionItemPattern", "select")]
    [DataRow(UiInvokeAction.Toggle, "TogglePattern", "toggle")]
    [DataRow(UiInvokeAction.Expand, "ExpandCollapsePattern", "expand")]
    [DataRow(UiInvokeAction.Collapse, "ExpandCollapsePattern", "collapse")]
    public void Apply_MultipleAvailablePatterns_UsesOnlyRequestedOperation(UiInvokeAction action, string pattern, string operation)
    {
        var provider = new Patterns();
        Assert.AreEqual(new UiInvokeActionResult(pattern, operation), Apply(provider, action));
        CollectionAssert.AreEqual(new[] { operation }, provider.Calls);
    }

    [TestMethod]
    [DataRow(UiInvokeAction.Invoke, "invoke")]
    [DataRow(UiInvokeAction.Select, "select")]
    [DataRow(UiInvokeAction.Toggle, "toggle")]
    [DataRow(UiInvokeAction.ToggleOn, "read")]
    [DataRow(UiInvokeAction.ToggleOff, "read")]
    [DataRow(UiInvokeAction.Expand, "expand")]
    [DataRow(UiInvokeAction.Collapse, "collapse")]
    public void Apply_UnsupportedMatchingPattern_DoesNotFallBack(UiInvokeAction action, string operation)
    {
        var failure = new InvalidOperationException("Matching pattern unavailable.");
        var provider = new Patterns { FailAt = 1, Failure = failure };
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => Apply(provider, action)));
        CollectionAssert.AreEqual(new[] { operation }, provider.Calls);
    }

    [TestMethod]
    [DataRow(UiInvokeAction.Invoke)]
    [DataRow(UiInvokeAction.Select)]
    [DataRow(UiInvokeAction.Toggle)]
    [DataRow(UiInvokeAction.ToggleOn)]
    [DataRow(UiInvokeAction.ToggleOff)]
    [DataRow(UiInvokeAction.Expand)]
    [DataRow(UiInvokeAction.Collapse)]
    public void Apply_ProviderFailure_IsExplicitAndNeverFallsBack(UiInvokeAction action)
    {
        var provider = new Patterns { FailAt = 1 };
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => Apply(provider, action));
        Assert.AreSame(provider.Failure, error.InnerException);
        StringAssert.Contains(error.Message, "aid:chosen");
        StringAssert.Contains(error.Message, ExplicitUiInvoker.Describe(action).Pattern);
        Assert.HasCount(1, provider.Calls);
    }

    [TestMethod]
    public void Toggle_AlwaysWritesExactlyOnce_WithoutReadingAnyState()
    {
        foreach (var state in new[] { Off, On, Indeterminate })
        {
            var provider = new Patterns(state);
            Assert.AreEqual(new UiInvokeActionResult("TogglePattern", "toggle"), Apply(provider, UiInvokeAction.Toggle));
            CollectionAssert.AreEqual(ToggleCalls, provider.Calls);
            Assert.HasCount(1, provider.States); // State was never read, including indeterminate.
        }
    }

    [TestMethod]
    public void ToggleToState_AllDeterminateTransitions_AreBoundedAndVerified()
    {
        foreach (var desired in new[] { Off, On })
        {
            var action = desired == On ? UiInvokeAction.ToggleOn : UiInvokeAction.ToggleOff;
            foreach (var initial in new[] { Off, On })
            {
                foreach (var after in new[] { Off, On, Indeterminate })
                {
                    var provider = new Patterns(initial, after);
                    if (initial == desired)
                    {
                        Assert.AreEqual(new UiInvokeActionResult("TogglePattern", "none"), Apply(provider, action));
                        CollectionAssert.AreEqual(ReadOnlyCalls, provider.Calls);
                    }
                    else
                    {
                        if (after == desired)
                        {
                            Assert.AreEqual(new UiInvokeActionResult("TogglePattern", "toggle"), Apply(provider, action));
                        }
                        else
                        {
                            var error = Assert.ThrowsExactly<InvalidOperationException>(() => Apply(provider, action));
                            StringAssert.Contains(error.Message, "after 1 toggle(s)");
                        }
                        CollectionAssert.AreEqual(OneToggleCalls, provider.Calls);
                    }
                }
            }
        }
    }

    [TestMethod]
    public void ToggleToState_AllIndeterminateTransitionsAndCycles_CheckEachWriteAndStopWithinTwo()
    {
        foreach (var desired in new[] { Off, On })
        {
            var action = desired == On ? UiInvokeAction.ToggleOn : UiInvokeAction.ToggleOff;
            foreach (var first in new[] { Off, On, Indeterminate })
            {
                foreach (var second in new[] { Off, On, Indeterminate })
                {
                    var provider = new Patterns(Indeterminate, first, second);
                    if (first == desired || second == desired)
                    {
                        Assert.AreEqual(new UiInvokeActionResult("TogglePattern", "toggle"), Apply(provider, action));
                    }
                    else
                    {
                        var error = Assert.ThrowsExactly<InvalidOperationException>(() => Apply(provider, action));
                        StringAssert.Contains(error.Message, "after 2 toggle(s)");
                    }
                    CollectionAssert.AreEqual(first == desired
                        ? OneToggleCalls
                        : TwoToggleCalls, provider.Calls);
                }
            }
        }
    }

    [TestMethod]
    [DataRow(UiInvokeAction.ToggleOn)]
    [DataRow(UiInvokeAction.ToggleOff)]
    public void ToggleToState_ReadAndWriteFailuresAtEveryStep_StopImmediately(UiInvokeAction action)
    {
        for (var failureAt = 1; failureAt <= 5; failureAt++)
        {
            var provider = new Patterns(Indeterminate, Indeterminate, Indeterminate) { FailAt = failureAt };
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => Apply(provider, action));
            Assert.AreSame(provider.Failure, error.InnerException);
            StringAssert.Contains(error.Message, "TogglePattern");
            Assert.HasCount(failureAt, provider.Calls);
        }
    }

    [TestMethod]
    [DataRow(UiInvokeAction.Invoke)]
    [DataRow(UiInvokeAction.Select)]
    [DataRow(UiInvokeAction.Toggle)]
    [DataRow(UiInvokeAction.ToggleOn)]
    [DataRow(UiInvokeAction.ToggleOff)]
    [DataRow(UiInvokeAction.Expand)]
    [DataRow(UiInvokeAction.Collapse)]
    public void Apply_ElementNotAvailable_PreservesStaleComErrorWithoutFallback(UiInvokeAction action)
    {
        var steps = action is UiInvokeAction.ToggleOn or UiInvokeAction.ToggleOff ? 5 : 1;
        for (var failureAt = 1; failureAt <= steps; failureAt++)
        {
            var stale = new COMException("Element no longer available.", unchecked((int)0x80040201));
            var provider = new Patterns(Indeterminate, Indeterminate, Indeterminate)
            {
                FailAt = failureAt,
                Failure = stale,
            };
            Assert.AreSame(stale, Assert.ThrowsExactly<COMException>(() => Apply(provider, action)));
            Assert.HasCount(failureAt, provider.Calls);
        }
    }

    [TestMethod]
    [DataRow(unchecked((int)0x80004002))] // E_NOINTERFACE
    [DataRow(unchecked((int)0x80040204))] // UIA_E_NOTSUPPORTED
    public void Apply_UnsupportedPatternComError_IsAnOperationErrorNotStale(int hresult)
    {
        var provider = new Patterns
        {
            FailAt = 1,
            Failure = new COMException("Matching pattern unavailable.", hresult),
        };
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => Apply(provider, UiInvokeAction.Invoke));
        Assert.AreSame(provider.Failure, error.InnerException);
        StringAssert.Contains(error.Message, "InvokePattern");
        Assert.HasCount(1, provider.Calls);
    }

    [TestMethod]
    public void ToggleToState_InvalidProviderState_FailsBeforeAnyFurtherWrite()
    {
        foreach (var states in new[]
        {
            new[] { (ToggleState)99 },
            new[] { Off, (ToggleState)99 },
            new[] { Indeterminate, Indeterminate, (ToggleState)99 },
        })
        {
            var provider = new Patterns(states);
            StringAssert.Contains(Assert.ThrowsExactly<InvalidOperationException>(
                () => Apply(provider, UiInvokeAction.ToggleOn)).Message, "invalid toggle state");
            Assert.AreEqual(states.Length - 1, provider.Calls.Count(c => c == "toggle"));
        }
    }

    [TestMethod]
    public void InvalidAction_IsRejectedWithoutTouchingProvider()
    {
        foreach (var value in new[] { -1, 7, int.MaxValue })
        {
            var provider = new Patterns();
            var error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Apply(provider, (UiInvokeAction)value));
            Assert.AreEqual("action", error.ParamName);
            Assert.IsEmpty(provider.Calls);
        }
    }

    [TestMethod]
    public void Cancellation_PreventsInitialAndSubsequentWrites()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new Patterns(Indeterminate, Indeterminate, On)
        {
            OnCall = call => { if (call == 3) { cancellation.Cancel(); } },
        };
        Assert.ThrowsExactly<OperationCanceledException>(
            () => ExplicitUiInvoker.Apply(provider, Element, UiInvokeAction.ToggleOn, cancellation.Token));
        CollectionAssert.AreEqual(OneToggleCalls, provider.Calls);
        provider.Calls.Clear();
        Assert.ThrowsExactly<OperationCanceledException>(
            () => ExplicitUiInvoker.Apply(provider, Element, UiInvokeAction.Toggle, cancellation.Token));
        Assert.IsEmpty(provider.Calls);
    }

    [TestMethod]
    public async Task PublicApi_OriginalAndExplicitOverloads_HaveIndependentFakeContracts()
    {
        Assert.IsTrue(typeof(UiInvokeAction).IsPublic);
        Assert.IsTrue(typeof(UiInvokeActionResult).IsPublic);
        CollectionAssert.AreEqual(ActionNames, Enum.GetNames<UiInvokeAction>());
        var fake = new FakeUiAutomationService();
        IUiAutomation api = fake;
        Func<UiTarget, UiElement, CancellationToken, Task<string>> original = api.InvokeAsync;
        Func<UiTarget, UiElement, UiInvokeAction, CancellationToken, Task<UiInvokeActionResult>> explicitAction = api.InvokeAsync;
        var target = new UiTarget { ProcessId = 1, ProcessName = "test" };

        Assert.AreEqual("InvokePattern", await original(target, Element, CancellationToken.None));
        Assert.IsNull(fake.LastInvokeAction);
        Assert.AreEqual(1, fake.AutomaticInvokeCalls);
        Assert.AreEqual(new UiInvokeActionResult("TogglePattern", "toggle"),
            await explicitAction(target, Element, UiInvokeAction.ToggleOn, CancellationToken.None));
        Assert.AreEqual(UiInvokeAction.ToggleOn, fake.LastInvokeAction);
        Assert.AreSame(Element, fake.LastInvokedElement);
        fake.ExplicitInvokeResult = new("TogglePattern", "none");
        Assert.AreSame(fake.ExplicitInvokeResult, await explicitAction(target, Element, UiInvokeAction.ToggleOff, CancellationToken.None));
        var failure = new InvalidOperationException("explicit only");
        fake.ExplicitInvokeThrow = failure;
        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => explicitAction(target, Element, UiInvokeAction.Select, CancellationToken.None)));
        Assert.AreEqual(3, fake.ExplicitInvokeCalls);
        Assert.AreEqual(1, fake.AutomaticInvokeCalls);
        Assert.AreEqual(UiInvokeAction.Select, fake.LastInvokeAction);
        Assert.AreEqual("InvokePattern", await original(target, Element, CancellationToken.None));
        Assert.IsNull(fake.LastInvokeAction);
        Assert.AreEqual(2, fake.AutomaticInvokeCalls);
        fake.InvokeThrow = failure;
        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => original(target, Element, CancellationToken.None)));
        Assert.AreEqual(3, fake.AutomaticInvokeCalls);
    }

    [TestMethod]
    public async Task SharedFake_AllExplicitActionsAndInvalidValues()
    {
        var fake = new FakeUiAutomationService { InvokeThrow = new InvalidOperationException("legacy only") };
        var target = new UiTarget { ProcessId = 1, ProcessName = "test" };
        foreach (var action in Enum.GetValues<UiInvokeAction>())
        {
            var result = await fake.InvokeAsync(target, Element, action, CancellationToken.None);
            var (pattern, name) = ExplicitUiInvoker.Describe(action);
            Assert.AreEqual(pattern, result.Pattern);
            Assert.AreEqual(action is UiInvokeAction.ToggleOn or UiInvokeAction.ToggleOff ? "toggle" : name, result.PerformedAction);
            Assert.AreEqual(action, fake.LastInvokeAction);
        }
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => fake.InvokeAsync(target, Element, (UiInvokeAction)99, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => fake.InvokeAsync(target, Element, UiInvokeAction.Invoke, new CancellationToken(true)));
        Assert.AreEqual(7, fake.ExplicitInvokeCalls);
    }

    private static UiInvokeActionResult Apply(Patterns patterns, UiInvokeAction action) =>
        ExplicitUiInvoker.Apply(patterns, Element, action, CancellationToken.None);

    private sealed class Patterns(params ToggleState[] states) : IExplicitUiInvokePatterns
    {
        public Queue<ToggleState> States { get; } = new(states);
        public List<string> Calls { get; } = [];
        public int FailAt { get; init; }
        public Exception Failure { get; init; } = new COMException("Provider failed.");
        public Action<int>? OnCall { get; init; }

        private void Record(string name)
        {
            Calls.Add(name);
            if (Calls.Count == FailAt) { throw Failure; }
            OnCall?.Invoke(Calls.Count);
        }

        public void Invoke() => Record("invoke");
        public void Select() => Record("select");
        public void Toggle() => Record("toggle");
        public void Expand() => Record("expand");
        public void Collapse() => Record("collapse");
        public ToggleState ReadToggleState()
        {
            Record("read");
            return States.Dequeue();
        }
    }
}
