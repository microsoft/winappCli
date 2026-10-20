// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class UiActionServiceTests
{
    [TestMethod]
    public async Task Replay_ApprovalIsRequiredBeforeEnvironmentCreation()
    {
        using var h = new Harness();

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(Flow(KeyStep()), Accepted with { AcceptRecording = false }));

        Assert.AreEqual("review_required", error.Code);
        Assert.IsEmpty(h.Environments.Targets);
        Assert.IsEmpty(h.Environment.SentKeys);
        Assert.IsEmpty(h.Environment.FocusCalls);
    }

    [TestMethod]
    public async Task Replay_MissingParameterInLaterStepPreventsEvenTheFirstClick()
    {
        using var h = new Harness();

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(Flow(ClickStep(), TextStep(parameter: "entry"))));

        Assert.AreEqual("missing_parameter", error.Code);
        Assert.IsEmpty(h.Environments.Targets);
        Assert.IsEmpty(h.Environment.Clicks);
        Assert.IsEmpty(h.Environment.FocusCalls);
    }

    [TestMethod]
    public async Task Replay_UnusedParameterFailsBeforeEnvironmentCreation()
    {
        using var h = new Harness();
        var options = Accepted with { Parameters = new Dictionary<string, string> { ["unused"] = "private value" } };

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(() => h.ReplayAsync(Flow(KeyStep()), options));

        Assert.AreEqual("invalid_flow", error.Code);
        Assert.IsFalse(error.Message.Contains("private value", StringComparison.Ordinal));
        Assert.IsEmpty(h.Environments.Targets);
    }

    [TestMethod]
    public async Task Replay_IncompleteRecordingCannotBeAccepted()
    {
        using var h = new Harness();

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(Flow(KeyStep()) with { Status = "incomplete" }));

        Assert.AreEqual("incomplete_recording", error.Code);
        Assert.IsEmpty(h.Environments.Targets);
    }

    [TestMethod]
    [DataRow("literal-private-text")]
    [DataRow("vk=0x41")]
    [DataRow("tab enter")]
    [DataRow("ctrl+c")]
    [DataRow("alt+f4")]
    [DataRow("win+l")]
    public async Task Replay_UnsafeOrNonChordKeyFailsBeforeEnvironmentCreation(string keys)
    {
        using var h = new Harness();

        await Assert.ThrowsExactlyAsync<UiActionException>(() => h.ReplayAsync(Flow(KeyStep(keys))));

        Assert.IsEmpty(h.Environments.Targets);
        Assert.IsEmpty(h.Environment.SentKeys);
    }

    [TestMethod]
    public async Task Replay_EmptyFlowFailsBeforeEnvironmentCreation()
    {
        using var h = new Harness();

        await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(new UiActionFlow { ProcessName = "SampleApp" }));

        Assert.IsEmpty(h.Environments.Targets);
    }

    [TestMethod]
    public async Task Replay_ExplicitFreshProcessTargetWorksWithoutRecordedPid()
    {
        using var h = new Harness();
        var fresh = new UiTarget { ProcessId = 200, WindowHandle = 300, ProcessName = "sampleapp" };
        h.Environment.ProcessName = "sampleapp";

        var result = await h.ReplayAsync(Flow(KeyStep()), target: fresh);

        Assert.AreSame(fresh, h.Environments.Targets.Single());
        Assert.AreEqual(1, result.CompletedSteps);
        Assert.AreEqual(0, result.AssertionsChecked);
        AssertChord(h.Environment.SentKeys.Single(), 0x0D);
        Assert.IsEmpty(h.Inputs.Calls);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }

    [TestMethod]
    public async Task Replay_DifferentProcessNameFailsBeforeFocusOrInput()
    {
        using var h = new Harness();
        h.Environment.ProcessName = "AnotherApp";

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(() => h.ReplayAsync(Flow(KeyStep())));

        Assert.AreEqual("target_mismatch", error.Code);
        Assert.IsEmpty(h.Environment.FocusCalls);
        Assert.IsEmpty(h.Environment.SentKeys);
    }

    [TestMethod]
    public async Task Replay_ActivatesInitialAnchorOnceThenUsesRealTabAndReplacementTextActions()
    {
        using var h = new Harness();
        h.Environment.IsForeground = false;
        h.Environment.OnSend = keys =>
        {
            if (keys[0] is KeyChord { Vk: 0x09 })
            {
                h.Environment.CurrentFocus = Element(Edit, true);
            }
        };
        var flow = Flow(KeyStep("tab"), TextStep(parameter: "entry"), KeyStep("enter", Edit));
        var options = Accepted with { Parameters = new Dictionary<string, string> { ["entry"] = "new value" } };

        var result = await h.ReplayAsync(flow, options);

        Assert.AreEqual(3, result.CompletedSteps);
        Assert.AreEqual(0, result.AssertionsChecked);
        CollectionAssert.AreEqual(new[] { Button }, h.Environment.FocusCalls);
        Assert.HasCount(4, h.Environment.SentKeys);
        AssertChord(h.Environment.SentKeys[0], 0x09);
        AssertChord(h.Environment.SentKeys[1], 0x41, 0x11);
        Assert.AreEqual(new TextInput("new value"), h.Environment.SentKeys[2][0]);
        AssertChord(h.Environment.SentKeys[3], 0x0D);
        Assert.IsEmpty(h.Environment.Clicks);
        CollectionAssert.AreEqual(new[] { Edit }, h.Environment.ValueCalls);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("hello  ctrl+a")]
    [DataRow("line one\r\nline two\t🙂")]
    public async Task Replay_TextIsVerbatimReplacementAndEmptyTextSendsDelete(string text)
    {
        using var h = new Harness();

        var result = await h.ReplayAsync(Flow(TextStep(text)));

        Assert.AreEqual(1, result.CompletedSteps);
        Assert.AreEqual(0, result.AssertionsChecked);
        Assert.HasCount(2, h.Environment.SentKeys);
        AssertChord(h.Environment.SentKeys[0], 0x41, 0x11);
        if (text.Length == 0)
        {
            AssertChord(h.Environment.SentKeys[1], 0x2E);
        }
        else
        {
            Assert.AreEqual(new TextInput(text), h.Environment.SentKeys[1].Single());
        }

        CollectionAssert.AreEqual(new[] { Edit }, h.Environment.ValueCalls);
    }

    [TestMethod]
    public async Task Replay_LaterFocusMismatchDoesNotForceFocusOrConvertKeyToClick()
    {
        using var h = new Harness();

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(Flow(ClickStep(), KeyStep("enter", Edit))));

        Assert.AreEqual("focus_mismatch", error.Code);
        Assert.IsTrue(error.Message.Contains("Step 2", StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[] { Button }, h.Environment.FocusCalls);
        Assert.HasCount(1, h.Environment.Clicks);
        Assert.IsEmpty(h.Environment.SentKeys);
    }

    [TestMethod]
    public async Task Replay_PasswordTargetIsRejectedBeforeFocusOrInput()
    {
        using var h = new Harness();
        h.Environment.Replace(Element(Edit, true) with { IsPassword = true });

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(() => h.ReplayAsync(Flow(TextStep("private value"))));

        Assert.AreEqual("sensitive_input", error.Code);
        Assert.IsEmpty(h.Environment.FocusCalls);
        Assert.IsEmpty(h.Environment.SentKeys);
        Assert.IsEmpty(h.Environment.ValueCalls);
        Assert.IsFalse(error.Message.Contains("private value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Replay_ReadonlyTargetRejectsReplacementBeforeAnyKeys()
    {
        using var h = new Harness();
        h.Environment.Replace(Element(Edit, false));

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(() => h.ReplayAsync(Flow(TextStep("private value"))));

        Assert.AreEqual("readonly_target", error.Code);
        Assert.IsEmpty(h.Environment.SentKeys);
        Assert.IsEmpty(h.Environment.ValueCalls);
    }

    [TestMethod]
    public async Task Replay_FocusChangeAfterSelectAllPreventsTextInjection()
    {
        using var h = new Harness();
        h.Environment.OnSend = _ => h.Environment.CurrentFocus = Element(Button);
        var options = Accepted with { Parameters = new Dictionary<string, string> { ["entry"] = "private replacement" } };

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(Flow(TextStep(parameter: "entry")), options));

        Assert.AreEqual("focus_mismatch", error.Code);
        AssertChord(h.Environment.SentKeys.Single(), 0x41, 0x11);
        CollectionAssert.AreEqual(new[] { Edit }, h.Environment.FocusCalls);
        Assert.IsFalse(error.Message.Contains("private replacement", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Replay_PasswordFlagChangingAfterSelectAllPreventsTextInjection()
    {
        using var h = new Harness();
        h.Environment.OnSend = _ => h.Environment.Replace(Element(Edit, true) with { IsPassword = true });

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(Flow(TextStep("private replacement"))));

        Assert.AreEqual("sensitive_input", error.Code);
        AssertChord(h.Environment.SentKeys.Single(), 0x41, 0x11);
        Assert.IsEmpty(h.Environment.ValueCalls);
    }

    [TestMethod]
    public async Task Replay_ForegroundLossAfterSelectAllStopsInsteadOfBringingAppBack()
    {
        using var h = new Harness();
        h.Environment.OnSend = _ => h.Environment.IsForeground = false;

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(Flow(TextStep("private replacement"))));

        Assert.AreEqual("foreground_not_target", error.Code);
        AssertChord(h.Environment.SentKeys.Single(), 0x41, 0x11);
        Assert.HasCount(1, h.Environment.FocusCalls);
    }

    [TestMethod]
    public async Task Replay_ClosedTargetAfterInputStopsBeforeNextStep()
    {
        using var h = new Harness();
        h.Environment.OnSend = _ => h.Environment.TargetExists = false;

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(Flow(KeyStep(), KeyStep())));

        Assert.AreEqual("target_closed", error.Code);
        Assert.HasCount(1, h.Environment.SentKeys);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }

    [TestMethod]
    public async Task Replay_NonInteractiveDesktopRejectsStartup()
    {
        using var h = new Harness();
        h.Environment.HasInteractiveDesktop = false;

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(() => h.ReplayAsync(Flow(KeyStep())));

        Assert.AreEqual("no_interactive_desktop", error.Code);
        Assert.IsEmpty(h.Environment.FocusCalls);
        Assert.IsEmpty(h.Environment.SentKeys);
    }

    [TestMethod]
    public async Task Replay_WithoutInitialAnchorDoesNotActivateBackgroundWindow()
    {
        using var h = new Harness();
        h.Environment.IsForeground = false;

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(Flow(ClickStep()) with { InitialFocus = null }));

        Assert.AreEqual("foreground_not_target", error.Code);
        Assert.IsEmpty(h.Environment.FocusCalls);
        Assert.IsEmpty(h.Environment.Clicks);
    }

    [TestMethod]
    public async Task Replay_WaitsForObservedFocusAfterTabWithoutForcingIt()
    {
        using var h = new Harness();
        bool tabSent = false;
        int focusReads = 0;
        h.Environment.OnSend = keys => tabSent |= keys[0] is KeyChord { Vk: 0x09 };
        h.Environment.OnFocused = () =>
        {
            if (tabSent && ++focusReads == 3)
            {
                h.Environment.CurrentFocus = Element(Edit, true);
            }
        };
        var flow = Flow(KeyStep("tab"), TextStep("replacement"));

        var result = await h.ReplayAsync(flow, Accepted with { TimeoutMs = 500 });

        Assert.AreEqual(2, result.CompletedSteps);
        Assert.HasCount(1, h.Environment.FocusCalls);
        Assert.IsTrue(focusReads >= 3);
        Assert.AreEqual(new TextInput("replacement"), h.Environment.SentKeys[^1].Single());
    }

    [TestMethod]
    public async Task Replay_DiagnosticElapsedTimeDoesNotCauseRecordedDelays()
    {
        using var h = new Harness();
        var flow = Flow(KeyStep() with { ElapsedMs = 3_500_000 });

        var result = await h.ReplayAsync(flow).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, result.CompletedSteps);
    }

    [TestMethod]
    public async Task Replay_AssertionsCountSeparatelyAndCanCheckDisabledControls()
    {
        using var h = new Harness();
        h.Environment.Replace(Element(Edit, true) with { IsEnabled = false });
        var flow = Flow(
            new UiActionStep { Kind = "assert", Target = Button, Property = "Name", Expected = "Submit" },
            new UiActionStep { Kind = "assert", Target = Edit, Property = "IsEnabled", Expected = "False" });

        var result = await h.ReplayAsync(flow);

        Assert.AreEqual(2, result.CompletedSteps);
        Assert.AreEqual(2, result.AssertionsChecked);
        Assert.IsEmpty(h.Environment.SentKeys);
        Assert.IsEmpty(h.Environment.Clicks);
    }

    [TestMethod]
    public async Task Replay_AssertionFailurePollsButNeverEchoesExpectedOrObservedValues()
    {
        using var h = new Harness();
        h.Environment.PropertyOverride = (_, _) => "private actual value";
        var flow = Flow(new UiActionStep
        {
            Kind = "assert", Target = Edit, Property = "Value", Expected = "private expected value",
        });

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(() => h.ReplayAsync(flow));

        Assert.AreEqual("assertion_failed", error.Code);
        Assert.IsTrue(error.Message.Contains("Step 1", StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains("private", StringComparison.Ordinal));
        Assert.IsTrue(h.Environment.PropertyCalls.Count > 1);
        Assert.IsEmpty(h.Environment.SentKeys);
    }

    [TestMethod]
    public async Task Replay_AssertionWaitsForExactExpectedProperty()
    {
        using var h = new Harness();
        int reads = 0;
        h.Environment.PropertyOverride = (_, _) => ++reads < 3 ? "loading" : "ready";
        var flow = Flow(new UiActionStep { Kind = "assert", Target = Button, Property = "Name", Expected = "ready" });

        var result = await h.ReplayAsync(flow, Accepted with { TimeoutMs = 500 });

        Assert.AreEqual(1, result.AssertionsChecked);
        Assert.AreEqual(3, reads);
    }

    [TestMethod]
    public async Task Replay_MissingTargetTimesOutWithoutInput()
    {
        using var h = new Harness();
        var missing = new UiActionTarget { AutomationId = "missing", ControlType = "Button" };
        var flow = Flow(ClickStep(missing)) with { InitialFocus = Button };

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(() => h.ReplayAsync(flow));

        Assert.AreEqual("target_not_found", error.Code);
        Assert.IsTrue(error.Message.Contains("Step 1", StringComparison.Ordinal));
        Assert.IsTrue(h.Environment.ResolveCalls.Count(t => t == missing) > 1);
        Assert.IsEmpty(h.Environment.Clicks);
    }

    [TestMethod]
    public async Task Replay_AmbiguityFailsImmediatelyWithoutRetryingOrChoosingFirstMatch()
    {
        using var h = new Harness();
        h.Environment.Elements.Add(Element(Button));
        var flow = Flow(ClickStep()) with { InitialFocus = Edit };

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(flow, Accepted with { TimeoutMs = 5000 }));

        Assert.AreEqual("ambiguous_target", error.Code);
        Assert.AreEqual(1, h.Environment.ResolveCalls.Count(t => t == Button));
        Assert.IsEmpty(h.Environment.Clicks);
    }

    [TestMethod]
    public async Task Replay_ClickAndWheelUseFreshResolvedGeometryAndExplicitButtonCount()
    {
        using var h = new Harness();
        var moved = Element(Button) with { Bounds = new PointerRect(100, 200, 500, 400) };
        h.Environment.OnClick = () => h.Environment.Replace(moved);
        var flow = Flow(
            ClickStep() with { Button = "right", ClickCount = 2, XFraction = 0.25, YFraction = 0.75 },
            new UiActionStep { Kind = "wheel", Target = Button, WheelDelta = -240 });

        var result = await h.ReplayAsync(flow);

        Assert.AreEqual(2, result.CompletedSteps);
        Assert.AreEqual(0, result.AssertionsChecked);
        Assert.IsTrue(h.Environment.Clicks.Single().Right);
        Assert.IsTrue(h.Environment.Clicks.Single().Twice);
        Assert.AreEqual(0.25, h.Environment.Clicks.Single().X);
        Assert.AreEqual(0.75, h.Environment.Clicks.Single().Y);
        Assert.AreEqual(moved.Bounds, h.Environment.Wheels.Single().Element.Bounds);
        Assert.AreEqual(-240, h.Environment.Wheels.Single().Delta);
    }

    [TestMethod]
    public async Task Replay_NameLocatorComparesCanonicalResolvedFocusIdentity()
    {
        using var h = new Harness();
        var named = Button with { Name = "Submit" };
        h.Environment.Elements.Clear();
        h.Environment.Elements.Add(Element(named));
        h.Environment.CurrentFocus = Element(named);
        var locator = new UiActionTarget { Name = "Submit", ControlType = "Button" };

        var result = await h.ReplayAsync(Flow(KeyStep("enter", locator)));

        Assert.AreEqual(1, result.CompletedSteps);
        AssertChord(h.Environment.SentKeys.Single(), 0x0D);
    }

    [TestMethod]
    public async Task Replay_CancellationBeforeStartupCreatesNoEnvironment()
    {
        using var h = new Harness();
        h.Cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => h.ReplayAsync(Flow(KeyStep())));

        Assert.IsEmpty(h.Environments.Targets);
        Assert.IsEmpty(h.Environment.SentKeys);
    }

    [TestMethod]
    public async Task Replay_CancellationAfterSelectAllDoesNotDeliverTextOrReturnSuccess()
    {
        using var h = new Harness();
        h.Environment.OnSend = _ => h.Cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => h.ReplayAsync(Flow(TextStep("private replacement"))));

        AssertChord(h.Environment.SentKeys.Single(), 0x41, 0x11);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }

    [TestMethod]
    public async Task Replay_BackendFailureStopsAndDoesNotEchoProviderDetails()
    {
        using var h = new Harness();
        h.Environment.OnSend = _ => throw new InvalidOperationException("private provider details");

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(Flow(KeyStep(), KeyStep())));

        Assert.AreEqual("action_failed", error.Code);
        Assert.IsFalse(error.Message.Contains("private", StringComparison.Ordinal));
        Assert.IsNull(error.InnerException);
        Assert.HasCount(1, h.Environment.SentKeys);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }

    [TestMethod]
    public async Task Replay_SeparateClickStepsRemainDistinctInsteadOfBecomingDoubleClick()
    {
        using var h = new Harness();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var delivered = new List<long>();
        h.Environment.OnClick = () => delivered.Add(clock.ElapsedMilliseconds);

        var result = await h.ReplayAsync(Flow(ClickStep(), ClickStep()));

        Assert.AreEqual(2, result.CompletedSteps);
        Assert.IsTrue(delivered[1] - delivered[0] >= 500);
        Assert.IsTrue(h.Environment.Clicks.All(c => !c.Twice));
    }

    [TestMethod]
    public async Task Replay_CopiesReviewedStepsAndParametersBeforeStartingEnvironment()
    {
        using var h = new Harness();
        var steps = new List<UiActionStep> { ClickStep(), TextStep(parameter: "entry") };
        var parameters = new Dictionary<string, string> { ["entry"] = "reviewed value" };
        var flow = Flow(ClickStep()) with { Steps = steps };
        h.Environment.OnClick = () =>
        {
            steps[1] = KeyStep("ctrl+c");
            parameters["entry"] = "changed after approval";
            h.Environment.CurrentFocus = Element(Edit, true);
        };

        var result = await h.ReplayAsync(flow, Accepted with { Parameters = parameters });

        Assert.AreEqual(2, result.CompletedSteps);
        Assert.AreEqual(new TextInput("reviewed value"), h.Environment.SentKeys[^1].Single());
    }

    [TestMethod]
    public async Task Replay_CancellationDuringLastFocusGuardPreventsTheKeyHandoff()
    {
        using var h = new Harness();
        h.Environment.OnFocused = () =>
        {
            if (h.Environment.FocusedCalls == 3)
            {
                h.Cancellation.Cancel();
            }
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => h.ReplayAsync(Flow(KeyStep())));

        Assert.IsEmpty(h.Environment.SentKeys);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }

    [TestMethod]
    public async Task Replay_EnvironmentLifetimeStaysOnOneDedicatedMtaThread()
    {
        using var h = new Harness();
        h.Environment.OnSend = keys =>
        {
            if (keys[0] is KeyChord { Vk: 0x09 })
            {
                h.Environment.CurrentFocus = Element(Edit, true);
            }
            else if (keys[0] is TextInput text)
            {
                h.Environment.Values["editor"] = text.Text;
            }
        };
        var flow = Flow(KeyStep("tab"), TextStep("replacement"),
            new UiActionStep { Kind = "assert", Target = Edit, Property = "Value", Expected = "replacement" });

        var result = await h.ReplayAsync(flow);

        Assert.AreEqual(3, result.CompletedSteps);
        Assert.AreEqual(1, result.AssertionsChecked);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
        AssertDedicatedMta(h.Environment);
    }

    [TestMethod]
    [DataRow("on")]
    [DataRow("off")]
    [DataRow("indeterminate")]
    public async Task Replay_ToggleAssertionsUseNativePropertyStrings(string state)
    {
        using var h = new Harness();
        if (state != "on")
        {
            h.Environment.PropertyOverride = (_, property) =>
            {
                Assert.AreEqual("ToggleState", property);
                return state;
            };
        }

        var flow = Flow(new UiActionStep { Kind = "assert", Target = Button, Property = "ToggleState", Expected = state });

        var result = await h.ReplayAsync(flow);

        Assert.AreEqual(1, result.CompletedSteps);
        Assert.AreEqual(1, result.AssertionsChecked);
        Assert.HasCount(1, h.Environment.PropertyCalls);
        Assert.IsEmpty(h.Environment.SentKeys);
    }

    [TestMethod]
    public async Task Replay_ToggleAssertionDoesNotNormalizeExpectedCase()
    {
        using var h = new Harness();
        var flow = Flow(new UiActionStep { Kind = "assert", Target = Button, Property = "ToggleState", Expected = "On" });

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(() => h.ReplayAsync(flow));

        Assert.AreEqual("assertion_failed", error.Code);
        Assert.IsEmpty(h.Environment.SentKeys);
    }

    [TestMethod]
    public async Task Replay_TextWaitsForValuePatternDeliveryWithoutCountingAnAuthoredAssertion()
    {
        using var h = new Harness();
        h.Environment.DeliverTextInput = false;
        h.Environment.ValueOverride = target =>
        {
            Assert.AreEqual(Edit, target);
            return h.Environment.ValueCalls.Count < 3 ? "previous value" : "replacement";
        };

        var result = await h.ReplayAsync(Flow(TextStep("replacement")));

        Assert.AreEqual(1, result.CompletedSteps);
        Assert.AreEqual(0, result.AssertionsChecked);
        Assert.HasCount(3, h.Environment.ValueCalls);
        Assert.HasCount(2, h.Environment.SentKeys);
        Assert.IsEmpty(h.Environment.PropertyCalls);
        AssertDedicatedMta(h.Environment);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("private replacement")]
    public async Task Replay_AcceptedInputWithoutValueDeliveryTimesOutBeforeNextAction(string replacement)
    {
        using var h = new Harness();
        h.Environment.DeliverTextInput = false;
        h.Environment.Values["editor"] = "private observed value";
        var options = Accepted with { Parameters = new Dictionary<string, string> { ["entry"] = replacement } };
        var flow = Flow(TextStep(parameter: "entry"), KeyStep("enter", Edit));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(flow, options).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.AreEqual("text_not_applied", error.Code);
        Assert.IsTrue(error.Message.Contains("Step 1", StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains("private", StringComparison.Ordinal));
        Assert.IsNull(error.InnerException);
        Assert.IsTrue(clock.ElapsedMilliseconds >= options.TimeoutMs);
        Assert.IsTrue(h.Environment.ValueCalls.Count > 1);
        Assert.HasCount(2, h.Environment.SentKeys);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }

    [TestMethod]
    [DataRow("focus", "focus_mismatch")]
    [DataRow("password", "sensitive_input")]
    [DataRow("foreground", "foreground_not_target")]
    [DataRow("readonly", "readonly_target")]
    public async Task Replay_TextReadbackRechecksPrivacyAndFocusBeforeAcceptingMatchingValue(string change, string code)
    {
        using var h = new Harness();
        h.Environment.ValueOverride = _ =>
        {
            switch (change)
            {
                case "focus":
                    h.Environment.CurrentFocus = Element(Button);
                    break;
                case "password":
                    h.Environment.Replace(Element(Edit, true) with { IsPassword = true });
                    break;
                case "foreground":
                    h.Environment.IsForeground = false;
                    break;
                case "readonly":
                    h.Environment.Replace(Element(Edit, false));
                    break;
            }

            return "replacement";
        };

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(Flow(TextStep("replacement"))));

        Assert.AreEqual(code, error.Code);
        Assert.HasCount(1, h.Environment.ValueCalls);
        Assert.HasCount(2, h.Environment.SentKeys);
        Assert.HasCount(1, h.Environment.FocusCalls);
    }

    [TestMethod]
    public async Task Replay_CancellationDuringMatchingTextReadbackDoesNotReturnSuccess()
    {
        using var h = new Harness();
        h.Environment.ValueOverride = _ =>
        {
            h.Cancellation.Cancel();
            return "replacement";
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => h.ReplayAsync(Flow(TextStep("replacement"))));

        Assert.HasCount(1, h.Environment.ValueCalls);
        Assert.HasCount(2, h.Environment.SentKeys);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }

    [TestMethod]
    public async Task Replay_ValueReadbackFailureIsNotIgnoredOrEchoed()
    {
        using var h = new Harness();
        h.Environment.ValueOverride = _ => throw new UiActionException("privacy_unavailable", "private provider value");

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.ReplayAsync(Flow(TextStep("replacement"))));

        Assert.AreEqual("privacy_unavailable", error.Code);
        Assert.IsFalse(error.Message.Contains("private", StringComparison.Ordinal));
        Assert.HasCount(1, h.Environment.ValueCalls);
        Assert.HasCount(2, h.Environment.SentKeys);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }
}
