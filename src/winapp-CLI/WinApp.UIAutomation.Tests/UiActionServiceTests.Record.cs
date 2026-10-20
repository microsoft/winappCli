// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class UiActionServiceTests
{
    [TestMethod]
    public async Task Record_DefaultTextUsesOnePlaceholderWithoutReadingValuesOrRetainingKeys()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit with { Name = "private field contents" }, true);
        h.Source.Add(Key(0x41), Key(0x42), Key(0x20), Key(0xBA), Key(0x08),
            Key(0x56, UiActionModifiers.Control), Key(0x58, UiActionModifiers.Control));

        var result = await h.RecordAsync();

        Assert.AreEqual("recorded", result.Flow.Status);
        Assert.AreEqual("cancelled", result.StopReason);
        Assert.HasCount(1, result.Flow.Steps);
        var step = result.Flow.Steps[0];
        Assert.AreEqual("text", step.Kind);
        Assert.AreEqual("text1", step.Parameter);
        Assert.AreEqual(Edit, step.Target);
        Assert.IsNull(step.Text);
        Assert.IsNull(step.Keys);
        Assert.IsNull(result.Flow.InitialFocus!.Name);
        Assert.IsEmpty(h.Environment.ValueCalls);
        Assert.IsTrue(result.Flow.Warnings.Any(w => w.Contains("replacement text", StringComparison.Ordinal)));
        Assert.IsEmpty(h.Environment.SentKeys);
        Assert.IsEmpty(h.Environment.FocusCalls);
        Assert.IsTrue(h.Ready);
        Assert.AreEqual(1, h.Source.DisposeCalls);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }

    [TestMethod]
    [DataRow("final non-password value")]
    [DataRow("")]
    public async Task Record_CaptureTextStoresOnlyFinalValueIncludingClear(string finalValue)
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Source.Add(Key(0x41), Key(0x2E));
        h.Source.Events.BeforeEnd = () => h.Environment.Values["editor"] = finalValue;

        var result = await h.RecordAsync(new UiActionRecordOptions { CaptureText = true });

        Assert.AreEqual("recorded", result.Flow.Status);
        Assert.HasCount(1, result.Flow.Steps);
        Assert.AreEqual(finalValue, result.Flow.Steps[0].Text);
        Assert.IsNull(result.Flow.Steps[0].Parameter);
        Assert.IsNull(result.Flow.Steps[0].Keys);
        CollectionAssert.AreEqual(new[] { Edit }, h.Environment.ValueCalls);
    }

    [TestMethod]
    public async Task Record_TabUsesPreviousFocusAndFlushesOriginalFieldBeforeTypingInNextField()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Environment.Values["editor"] = "first final value";
        h.Environment.Values["nextEditor"] = "second final value";
        h.Source.Add(Key(0x41));
        h.Source.Add(() => h.Environment.CurrentFocus = Element(NextEdit, true), Key(0x09));
        h.Source.Add(Key(0x42));

        var result = await h.RecordAsync(new UiActionRecordOptions { CaptureText = true });

        Assert.AreEqual("recorded", result.Flow.Status);
        Assert.HasCount(3, result.Flow.Steps);
        Assert.AreEqual(Edit, result.Flow.InitialFocus);
        Assert.AreEqual(Edit, result.Flow.Steps[0].Target);
        Assert.AreEqual("first final value", result.Flow.Steps[0].Text);
        Assert.AreEqual(Edit, result.Flow.Steps[1].Target);
        Assert.AreEqual("tab", result.Flow.Steps[1].Keys);
        Assert.AreEqual(NextEdit, result.Flow.Steps[2].Target);
        Assert.AreEqual("second final value", result.Flow.Steps[2].Text);
        CollectionAssert.AreEqual(new[] { Edit, NextEdit }, h.Environment.ValueCalls);
        Assert.IsEmpty(h.Environment.FocusCalls);
    }

    [TestMethod]
    public async Task Record_StartsFromTerminalWithoutReadingOutsideFocusOrStealingFocus()
    {
        using var h = new Harness();
        h.Environment.IsForeground = false;
        h.Environment.CurrentFocus = Element(Edit, true) with { IsPassword = true };
        h.Environment.OnFocused = () => Assert.IsTrue(h.Environment.IsForeground);
        h.Source.Add(() =>
        {
            h.Environment.IsForeground = true;
            h.Environment.CurrentFocus = Element(Button);
        }, new UiActionInput(UiActionInputKind.ScopeEntered, 0));
        h.Source.Add(Key(0x20));

        var result = await h.RecordAsync();

        Assert.AreEqual("recorded", result.Flow.Status);
        Assert.AreEqual(Button, result.Flow.InitialFocus);
        Assert.HasCount(1, result.Flow.Steps);
        Assert.AreEqual("space", result.Flow.Steps[0].Keys);
        Assert.IsEmpty(h.Environment.ValueCalls);
        Assert.IsEmpty(h.Environment.FocusCalls);
    }

    [TestMethod]
    public async Task Record_ScopeLossFlushesOnlyOriginalSafeFieldAndIgnoresOutsideInput()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Environment.OnFocused = () => Assert.IsTrue(h.Environment.IsForeground);
        h.Source.Add(Key(0x41));
        h.Source.Add(() =>
        {
            h.Environment.IsForeground = false;
            h.Environment.CurrentFocus = Element(Edit, true) with { IsPassword = true };
        }, new UiActionInput(UiActionInputKind.ScopeLost, 0));
        h.Source.Add(new UiActionInput(UiActionInputKind.ScopeLost, 0));
        h.Source.Add(() =>
        {
            h.Environment.IsForeground = true;
            h.Environment.CurrentFocus = Element(NextEdit, true);
        }, new UiActionInput(UiActionInputKind.ScopeEntered, 0));
        h.Source.Add(Key(0x58));

        var result = await h.RecordAsync(new UiActionRecordOptions { CaptureText = true });

        Assert.AreEqual("recorded", result.Flow.Status);
        Assert.HasCount(2, result.Flow.Steps);
        Assert.AreEqual(Edit, result.Flow.Steps[0].Target);
        Assert.AreEqual("private text", result.Flow.Steps[0].Text);
        Assert.AreEqual(NextEdit, result.Flow.Steps[1].Target);
        CollectionAssert.AreEqual(new[] { Edit, NextEdit }, h.Environment.ValueCalls);
        Assert.AreEqual(1, result.Flow.Warnings.Count(w => w.Contains("paused", StringComparison.Ordinal)));
        Assert.IsTrue(result.Flow.Steps.All(s => s.Keys is null));
    }

    [TestMethod]
    public async Task Record_PasswordAtStartupStopsBeforeHooksAndPersistsNoTargetOrInput()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true) with { IsPassword = true };
        h.Source.Add(Key(0x50));

        var result = await h.RecordAsync(new UiActionRecordOptions { CaptureText = true });

        AssertStopped(result, "sensitive_input");
        Assert.IsEmpty(result.Flow.Steps);
        Assert.IsNull(result.Flow.InitialFocus);
        Assert.IsEmpty(h.Environment.ValueCalls);
        Assert.IsEmpty(h.Inputs.Calls);
        Assert.IsFalse(h.Ready);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }

    [TestMethod]
    public async Task Record_PasswordFlagChangingDuringTypingDiscardsPendingText()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Source.Add(Key(0x41));
        h.Source.Add(() => h.Environment.Replace(Element(Edit, true) with { IsPassword = true }), Key(0x50));

        var result = await h.RecordAsync(new UiActionRecordOptions { CaptureText = true });

        AssertStopped(result, "sensitive_input");
        Assert.IsEmpty(result.Flow.Steps);
        Assert.IsEmpty(h.Environment.ValueCalls);
        Assert.AreEqual(1, h.Source.DisposeCalls);
    }

    [TestMethod]
    public async Task Record_PasswordFlagChangingBeforeFinalValueReadDiscardsPendingText()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Source.Add(Key(0x41));
        h.Source.Events.BeforeEnd = () => h.Environment.Replace(Element(Edit, true) with { IsPassword = true });

        var result = await h.RecordAsync(new UiActionRecordOptions { CaptureText = true });

        AssertStopped(result, "sensitive_input");
        Assert.IsEmpty(result.Flow.Steps);
        Assert.IsEmpty(h.Environment.ValueCalls);
    }

    [TestMethod]
    public async Task Record_UnknownKeyboardFocusIsSensitiveAndDoesNotRetainKeyName()
    {
        using var h = new Harness();
        h.Source.Add(() => h.Environment.CurrentFocus = null, Key(0x41));

        var result = await h.RecordAsync();

        AssertStopped(result, "sensitive_input");
        Assert.IsEmpty(result.Flow.Steps);
        Assert.IsEmpty(h.Environment.ValueCalls);
    }

    [TestMethod]
    public async Task Record_NameOnlyEditIsRejectedWithoutRetainingItsValueAsALocator()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(new UiActionTarget { Name = "private text", ControlType = "Edit" }, true);

        var result = await h.RecordAsync();

        AssertStopped(result, "unstable_target");
        Assert.IsNull(result.Flow.InitialFocus);
        Assert.IsEmpty(result.Flow.Steps);
        Assert.IsEmpty(h.Environment.ValueCalls);
    }

    [TestMethod]
    public async Task Record_UnexplainedTextFocusChangeFailsInsteadOfAttributingToNewField()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Source.Add(() => h.Environment.CurrentFocus = Element(NextEdit, true), Key(0x41));

        var result = await h.RecordAsync();

        AssertStopped(result, "focus_changed");
        Assert.IsEmpty(result.Flow.Steps);
    }

    [TestMethod]
    [DataRow(0x43)]
    [DataRow(0x2D)]
    public async Task Record_CopyShortcutsAreUnsupportedAndNeverPersisted(int key)
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Source.Add(Key((ushort)key, UiActionModifiers.Control));

        var result = await h.RecordAsync();

        AssertStopped(result, "unsupported_clipboard");
        Assert.IsEmpty(result.Flow.Steps);
        Assert.IsEmpty(h.Environment.ValueCalls);
    }

    [TestMethod]
    [DataRow(0xE5)]
    [DataRow(0xE7)]
    public async Task Record_ImeAndUnicodePacketInputStopWithoutPersistingRawKeys(int key)
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Source.Add(Key((ushort)key));

        var result = await h.RecordAsync();

        AssertStopped(result, "unsupported_input");
        Assert.IsEmpty(result.Flow.Steps);
    }

    [TestMethod]
    public async Task Record_PrintableInputInNonEditableControlIsUnsupported()
    {
        using var h = new Harness();
        h.Source.Add(Key(0x41));

        var result = await h.RecordAsync();

        AssertStopped(result, "unsupported_input");
        Assert.IsEmpty(result.Flow.Steps);
    }

    [TestMethod]
    public async Task Record_ButtonActivationAndAppShortcutRemainKeyboardActions()
    {
        using var h = new Harness();
        h.Source.Add(Key(0x20));
        h.Source.Add(Key(0x53, UiActionModifiers.Control));
        h.Source.Add(Key(0x0D));

        var result = await h.RecordAsync();

        Assert.AreEqual("recorded", result.Flow.Status);
        Assert.AreEqual("space,ctrl+s,enter", string.Join(',', result.Flow.Steps.Select(s => s.Keys)));
        Assert.IsTrue(result.Flow.Steps.All(s => s.Kind == "key" && s.Target == Button));
        Assert.IsEmpty(h.Environment.Clicks);
        Assert.IsEmpty(h.Environment.FocusCalls);
    }

    [TestMethod]
    [DataRow(0x09, (int)UiActionModifiers.Alt)]
    [DataRow(0x4C, (int)UiActionModifiers.Windows)]
    [DataRow(0x73, (int)UiActionModifiers.Alt)]
    public async Task Record_ReservedKeysStopWithoutSavingTheChord(int key, int modifiers)
    {
        using var h = new Harness();
        h.Source.Add(Key((ushort)key, (UiActionModifiers)modifiers));

        var result = await h.RecordAsync();

        AssertStopped(result, "system_key");
        Assert.IsEmpty(result.Flow.Steps);
    }

    [TestMethod]
    public async Task Record_MouseClicksUseElementRelativePositionAndForwardInjectedOptIn()
    {
        using var h = new Harness();
        h.Source.Add(Down(60, 95), Up(60, 95));
        h.Source.Add(Down(right: true), Up(right: true));

        var result = await h.RecordAsync(new UiActionRecordOptions { IncludeInjected = true });

        Assert.AreEqual("recorded", result.Flow.Status);
        Assert.HasCount(2, result.Flow.Steps);
        Assert.AreEqual("left", result.Flow.Steps[0].Button);
        Assert.AreEqual(0.25, result.Flow.Steps[0].XFraction);
        Assert.AreEqual(0.75, result.Flow.Steps[0].YFraction);
        Assert.AreEqual("right", result.Flow.Steps[1].Button);
        Assert.IsTrue(h.Inputs.Calls[0].IncludeInjected);
        Assert.AreSame(App, h.Inputs.Calls[0].Target);
        Assert.IsTrue(h.Environment.PointCalls.All(p => !p.ForScroll));
        Assert.IsEmpty(h.Environment.Clicks);
    }

    [TestMethod]
    public async Task Record_TwoNearbyClicksCoalesceAsDoubleClickWithTimingWarning()
    {
        using var h = new Harness();
        h.Source.Add(Down(), Up());
        h.Source.Add(Down(), Up());

        var result = await h.RecordAsync();

        Assert.AreEqual("recorded", result.Flow.Status);
        Assert.HasCount(1, result.Flow.Steps);
        Assert.AreEqual(2, result.Flow.Steps[0].ClickCount);
        Assert.IsTrue(result.Flow.Warnings.Any(w => w.Contains("500 ms", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Record_ConsecutiveWheelInputCoalescesWithoutCancellingOppositeDirections()
    {
        using var h = new Harness();
        h.Source.Add(Wheel(), Wheel(240), Wheel(-120));

        var result = await h.RecordAsync();

        Assert.AreEqual("recorded", result.Flow.Status);
        Assert.HasCount(2, result.Flow.Steps);
        Assert.AreEqual(360, result.Flow.Steps[0].WheelDelta);
        Assert.AreEqual(-120, result.Flow.Steps[1].WheelDelta);
        Assert.IsTrue(h.Environment.PointCalls.All(p => p.ForScroll));
        Assert.IsEmpty(h.Environment.Wheels);
    }

    [TestMethod]
    public async Task Record_DragStopsAsIncomplete()
    {
        using var h = new Harness();
        h.Source.Add(Down(), Up(120, 80));

        var result = await h.RecordAsync();

        AssertStopped(result, "unsupported_drag");
        Assert.IsEmpty(result.Flow.Steps);
    }

    [TestMethod]
    public async Task Record_MouseReleaseOnAnotherControlStopsAsIncomplete()
    {
        using var h = new Harness();
        h.Source.Add(Down());
        h.Source.Add(() => h.Environment.PointerElement = Element(Edit, true), Up());

        var result = await h.RecordAsync();

        AssertStopped(result, "target_changed");
        Assert.IsEmpty(result.Flow.Steps);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Record_IncompleteMousePressOrReleaseNeverProducesCompletedFlow(bool downOnly)
    {
        using var h = new Harness();
        h.Source.Add(downOnly ? Down() : Up());

        var result = await h.RecordAsync();

        AssertStopped(result, "incomplete_click");
        Assert.IsEmpty(result.Flow.Steps);
        Assert.AreEqual(1, h.Source.DisposeCalls);
    }

    [TestMethod]
    public async Task Record_SourceOverflowRetainsPriorStepsButMarksWholeFlowIncomplete()
    {
        using var h = new Harness();
        h.Source.Add(Key(0x20));
        h.Source.Add(new UiActionInput(UiActionInputKind.Error, 0, ErrorCode: "input_overflow"));

        var result = await h.RecordAsync();

        AssertStopped(result, "input_overflow");
        Assert.HasCount(1, result.Flow.Steps);
        Assert.AreEqual(1, h.Source.DisposeCalls);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }

    [TestMethod]
    public async Task Record_QueuedInputAcrossTabCheckpointStopsInsteadOfGuessingFocus()
    {
        using var h = new Harness();
        h.Source.Add(() => h.Environment.CurrentFocus = Element(Edit, true), Key(0x09), Key(0x41));

        var result = await h.RecordAsync();

        AssertStopped(result, "input_backlog");
        Assert.IsEmpty(result.Flow.Steps);
    }

    [TestMethod]
    public async Task Record_EmptyRecordingIsIncompleteAndDisposesHook()
    {
        using var h = new Harness();

        var result = await h.RecordAsync();

        AssertStopped(result, "empty_recording");
        Assert.IsTrue(h.Ready);
        Assert.IsFalse(h.Inputs.Calls[0].IncludeInjected);
        Assert.AreEqual(1, h.Source.DisposeCalls);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }

    [TestMethod]
    public async Task Record_SourceStartFailureDisposesHookWithoutInvokingReady()
    {
        using var h = new Harness();
        h.Source.OnStart = () => throw new InvalidOperationException("private provider details");

        var result = await h.RecordAsync();

        AssertStopped(result, "input_error");
        Assert.IsFalse(h.Ready);
        Assert.AreEqual(1, h.Source.DisposeCalls);
    }

    [TestMethod]
    public async Task Record_UnexpectedSourceClosureIsNotACompletedRecording()
    {
        using var h = new Harness();
        h.Source.Add(Key(0x20));
        h.Source.Events.CloseInsteadOfCancel = true;

        var result = await h.RecordAsync();

        AssertStopped(result, "input_source_closed");
        Assert.HasCount(1, result.Flow.Steps);
    }

    [TestMethod]
    public async Task Record_StepLimitRetainsBoundedPartialFlow()
    {
        using var h = new Harness();
        h.Source.Add(Key(0x20));

        var result = await h.RecordAsync(new UiActionRecordOptions { MaxSteps = 1 });

        AssertStopped(result, "step_limit");
        Assert.HasCount(1, result.Flow.Steps);
    }

    [TestMethod]
    [DataRow(-1, 1000)]
    [DataRow(3601, 1000)]
    [DataRow(0, 0)]
    [DataRow(0, 10001)]
    public async Task Record_InvalidOptionsFailBeforeEnvironmentOrHookCreation(int duration, int maximum)
    {
        using var h = new Harness();

        var error = await Assert.ThrowsExactlyAsync<UiActionException>(
            () => h.RecordAsync(new UiActionRecordOptions { DurationSec = duration, MaxSteps = maximum }));

        Assert.AreEqual("invalid_options", error.Code);
        Assert.IsEmpty(h.Environments.Targets);
        Assert.IsEmpty(h.Inputs.Calls);
    }

    [TestMethod]
    public async Task Record_CancellationBeforeStartupCreatesNoEnvironmentOrHook()
    {
        using var h = new Harness();
        h.Cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => h.RecordAsync());

        Assert.IsEmpty(h.Environments.Targets);
        Assert.IsEmpty(h.Inputs.Calls);
    }

    [TestMethod]
    public async Task Record_DurationStartsAfterHookStartupAndDisposesHookWhenFinished()
    {
        using var h = new Harness();
        h.Source.OnStart = () => Thread.Sleep(1100);
        h.Source.Add(Key(0x20));
        h.Source.Events.WaitInsteadOfCancel = true;

        var result = await h.RecordAsync(new UiActionRecordOptions { DurationSec = 1 });

        Assert.AreEqual("recorded", result.Flow.Status);
        Assert.AreEqual("duration_elapsed", result.StopReason);
        Assert.HasCount(1, result.Flow.Steps);
        Assert.AreEqual(1, h.Source.DisposeCalls);
        Assert.IsFalse(h.Cancellation.IsCancellationRequested);
    }

    [TestMethod]
    public async Task Record_CancellationWithUnprocessedNativeInputIsIncomplete()
    {
        using var h = new Harness();
        h.Source.OnStart = () =>
        {
            Assert.IsTrue(h.Source.Events.Channel.Writer.TryWrite(Down()));
            h.Cancellation.Cancel();
        };

        var result = await h.RecordAsync();

        AssertStopped(result, "input_backlog");
        Assert.IsFalse(h.Ready);
        Assert.IsEmpty(result.Flow.Steps);
    }

    [TestMethod]
    public async Task Record_AmbiguousTargetStopsBeforeKeepingItsLocator()
    {
        using var h = new Harness();
        h.Environment.Elements.Add(Element(Button));

        var result = await h.RecordAsync();

        AssertStopped(result, "ambiguous_target");
        Assert.IsNull(result.Flow.InitialFocus);
        Assert.IsEmpty(result.Flow.Steps);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Record_DuplicateEditTargetFailsAtFirstObservationOrCommit(bool atCommit, bool captureText)
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        if (atCommit)
        {
            h.Source.Add(Key(0x41));
            h.Source.Events.BeforeEnd = () => h.Environment.Elements.Add(Element(Edit, true));
        }
        else
        {
            h.Source.Add(() => h.Environment.Elements.Add(Element(Edit, true)), Key(0x41));
        }

        var result = await h.RecordAsync(new UiActionRecordOptions { CaptureText = captureText });

        AssertStopped(result, "ambiguous_target");
        Assert.IsEmpty(result.Flow.Steps);
        Assert.IsEmpty(h.Environment.ValueCalls);
        Assert.AreEqual(1, h.Source.DisposeCalls);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }

    [TestMethod]
    public async Task Record_AltGrTypingIsNotStoredAsAnAppShortcut()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Source.Add(Key(0x51, UiActionModifiers.Control | UiActionModifiers.Alt));

        var result = await h.RecordAsync();

        AssertStopped(result, "unsupported_input");
        Assert.IsEmpty(result.Flow.Steps);
        Assert.IsEmpty(h.Environment.ValueCalls);
    }

    [TestMethod]
    public async Task Record_MinimizingWindowStillFlushesOriginalSafeFieldWithoutReadingOutsideFocus()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Environment.OnFocused = () => Assert.IsTrue(h.Environment.IsForeground);
        h.Source.Add(Key(0x41));
        h.Source.Add(() =>
        {
            h.Environment.IsForeground = false;
            h.Environment.Replace(Element(Edit, true) with { IsOffscreen = true });
            h.Environment.CurrentFocus = null;
        }, new UiActionInput(UiActionInputKind.ScopeLost, 0));

        var result = await h.RecordAsync(new UiActionRecordOptions { CaptureText = true });

        Assert.AreEqual("recorded", result.Flow.Status);
        Assert.AreEqual("private text", result.Flow.Steps.Single().Text);
        CollectionAssert.AreEqual(new[] { Edit }, h.Environment.ValueCalls);
    }

    [TestMethod]
    public async Task Record_DefaultParametersAreSequentialAndIndependentOfFieldValues()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Source.Add(Key(0x41));
        h.Source.Add(() => h.Environment.CurrentFocus = Element(NextEdit, true), Key(0x09));
        h.Source.Add(Key(0x42));

        var result = await h.RecordAsync();

        Assert.AreEqual("recorded", result.Flow.Status);
        Assert.AreEqual("text1", result.Flow.Steps[0].Parameter);
        Assert.AreEqual("text2", result.Flow.Steps[2].Parameter);
        Assert.IsEmpty(h.Environment.ValueCalls);
    }

    [TestMethod]
    public async Task Record_ReadonlyChangeBeforeFlushDiscardsPendingText()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Source.Add(Key(0x41));
        h.Source.Events.BeforeEnd = () => h.Environment.Replace(Element(Edit, false));

        var result = await h.RecordAsync(new UiActionRecordOptions { CaptureText = true });

        AssertStopped(result, "readonly_target");
        Assert.IsEmpty(result.Flow.Steps);
        Assert.IsEmpty(h.Environment.ValueCalls);
    }

    [TestMethod]
    public async Task Record_ScopeLossDuringMousePressCannotBeSilentlyCompleted()
    {
        using var h = new Harness();
        h.Source.Add(Down());
        h.Source.Add(() => h.Environment.IsForeground = false, new UiActionInput(UiActionInputKind.ScopeLost, 0));

        var result = await h.RecordAsync();

        AssertStopped(result, "incomplete_click");
        Assert.IsEmpty(result.Flow.Steps);
    }

    [TestMethod]
    public async Task Record_UnorderedInputStopsWithoutAppendingBackdatedStep()
    {
        using var h = new Harness();
        h.Source.Add(Key(0x20) with { ElapsedMs = 1000 });
        h.Source.Add(Key(0x0D) with { ElapsedMs = 500 });

        var result = await h.RecordAsync();

        AssertStopped(result, "input_order");
        Assert.HasCount(1, result.Flow.Steps);
        Assert.AreEqual(1000, result.Flow.Steps[0].ElapsedMs);
    }

    [TestMethod]
    public async Task Record_QueuedNavigationOnScopeEntryCannotBecomeAnIncorrectInitialAnchor()
    {
        using var h = new Harness();
        h.Environment.IsForeground = false;
        h.Source.Add(() =>
        {
            h.Environment.IsForeground = true;
            h.Environment.CurrentFocus = Element(Edit, true);
        }, new UiActionInput(UiActionInputKind.ScopeEntered, 0), Key(0x09));

        var result = await h.RecordAsync();

        AssertStopped(result, "input_backlog");
        Assert.IsNull(result.Flow.InitialFocus);
        Assert.IsEmpty(result.Flow.Steps);
    }

    [TestMethod]
    public async Task Record_EnvironmentLifetimeStaysOnOneDedicatedMtaThread()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Source.Add(Key(0x41));
        h.Source.Add(() => h.Environment.CurrentFocus = Element(Button), Key(0x09));
        h.Source.Add(Down(), Up());
        h.Source.Add(Wheel());

        var result = await h.RecordAsync(new UiActionRecordOptions { CaptureText = true });

        Assert.AreEqual("recorded", result.Flow.Status);
        Assert.HasCount(4, result.Flow.Steps);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
        AssertDedicatedMta(h.Environment);
    }

    [TestMethod]
    [DataRow("password_target")]
    [DataRow("privacy_unavailable")]
    public async Task Record_NativePrivacyFailuresUseSensitiveStopReason(string code)
    {
        using var h = new Harness();
        h.Environment.OnFocused = () => throw new UiActionException(code, "private provider details");

        var result = await h.RecordAsync(new UiActionRecordOptions { CaptureText = true });

        AssertStopped(result, "sensitive_input");
        Assert.IsNull(result.Flow.InitialFocus);
        Assert.IsEmpty(result.Flow.Steps);
        Assert.IsEmpty(h.Environment.ValueCalls);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }

    [TestMethod]
    public async Task Record_NonInteractiveDesktopUsesCanonicalFailureCode()
    {
        using var h = new Harness();
        h.Environment.HasInteractiveDesktop = false;

        var result = await h.RecordAsync();

        AssertStopped(result, "no_interactive_desktop");
        Assert.IsFalse(h.Ready);
        Assert.IsEmpty(h.Inputs.Calls);
        Assert.AreEqual(1, h.Environment.DisposeCalls);
    }

    [TestMethod]
    [DataRow(3_600_000L, true)]
    [DataRow(3_600_001L, false)]
    public async Task Record_UntilCancelledEnforcesExactActionTimestampBound(long elapsedMs, bool complete)
    {
        using var h = new Harness();
        h.Source.Add(Key(0x20) with { ElapsedMs = 1000 });
        h.Source.Add(Key(0x0D) with { ElapsedMs = elapsedMs });

        var result = await h.RecordAsync(new UiActionRecordOptions { DurationSec = 0 });

        if (complete)
        {
            Assert.AreEqual("recorded", result.Flow.Status);
            Assert.AreEqual("cancelled", result.StopReason);
            Assert.HasCount(2, result.Flow.Steps);
            Assert.AreEqual(elapsedMs, result.Flow.Steps[1].ElapsedMs);
        }
        else
        {
            AssertStopped(result, "duration_limit");
            Assert.HasCount(1, result.Flow.Steps);
            Assert.AreEqual(1000, result.Flow.Steps[0].ElapsedMs);
        }

        UiActionFlowDocument.Validate(result.Flow);
        Assert.AreEqual(1, h.Source.DisposeCalls);
    }

    [TestMethod]
    [DataRow((int)UiActionInputKind.MouseDown, (int)UiActionModifiers.Control)]
    [DataRow((int)UiActionInputKind.MouseDown, (int)UiActionModifiers.Shift)]
    [DataRow((int)UiActionInputKind.MouseDown, (int)UiActionModifiers.Alt)]
    [DataRow((int)UiActionInputKind.MouseDown, (int)UiActionModifiers.Windows)]
    [DataRow((int)UiActionInputKind.MouseUp, (int)UiActionModifiers.Control)]
    [DataRow((int)UiActionInputKind.MouseUp, (int)UiActionModifiers.Shift)]
    [DataRow((int)UiActionInputKind.MouseUp, (int)UiActionModifiers.Alt)]
    [DataRow((int)UiActionInputKind.MouseUp, (int)UiActionModifiers.Windows)]
    [DataRow((int)UiActionInputKind.Wheel, (int)UiActionModifiers.Control)]
    [DataRow((int)UiActionInputKind.Wheel, (int)UiActionModifiers.Shift)]
    [DataRow((int)UiActionInputKind.Wheel, (int)UiActionModifiers.Alt)]
    [DataRow((int)UiActionInputKind.Wheel, (int)UiActionModifiers.Windows)]
    public async Task Record_ModifiedMouseInputStopsWithoutDroppingModifiers(int kind, int modifiers)
    {
        using var h = new Harness();
        h.Source.Add(Key(0x20));
        var input = (UiActionInputKind)kind switch
        {
            UiActionInputKind.MouseDown => Down(),
            UiActionInputKind.MouseUp => Up(),
            UiActionInputKind.Wheel => Wheel(),
            _ => throw new InvalidOperationException("Unexpected test input."),
        };
        if (input.Kind == UiActionInputKind.MouseUp)
        {
            h.Source.Add(Down());
        }

        h.Source.Add(input with { Modifiers = (UiActionModifiers)modifiers });

        var result = await h.RecordAsync();

        AssertStopped(result, "unsupported_input");
        Assert.HasCount(1, result.Flow.Steps);
        Assert.AreEqual("key", result.Flow.Steps[0].Kind);
        Assert.IsEmpty(h.Environment.ValueCalls);
        Assert.AreEqual(1, h.Source.DisposeCalls);
    }

    [TestMethod]
    [DataRow(0x41)]
    [DataRow(0x09)]
    [DataRow(0x10)]
    [DataRow(0x11)]
    [DataRow(0x12)]
    [DataRow(0xA0)]
    public async Task Record_MousePressWithKeyboardInputIsNotTruncatedToClick(int key)
    {
        using var h = new Harness();
        h.Source.Add(Down(), Key((ushort)key), Up());

        var result = await h.RecordAsync();

        AssertStopped(result, "unsupported_input");
        Assert.IsEmpty(result.Flow.Steps);
        Assert.IsEmpty(h.Environment.ValueCalls);
        Assert.AreEqual(1, h.Source.DisposeCalls);
    }

    [TestMethod]
    public async Task Record_SourceCannotForwardMouseInputWithoutAnEnteredScope()
    {
        using var h = new Harness();
        h.Environment.IsForeground = false;
        h.Source.Add(Down() with { Modifiers = UiActionModifiers.Control },
            Up() with { Modifiers = UiActionModifiers.Control },
            Wheel() with { Modifiers = UiActionModifiers.Shift });

        var result = await h.RecordAsync();

        AssertStopped(result, "input_scope_changed");
        Assert.IsEmpty(result.Flow.Steps);
        Assert.IsEmpty(h.Environment.PointCalls);
    }

    [TestMethod]
    public async Task Record_TextBurstBatchesFocusReadsAndPreservesTabAndEnter()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Environment.OnFocused = () => Thread.Sleep(50);
        h.Source.Add("RecordedDemo".Select(c => Key(char.ToUpperInvariant(c),
            char.IsUpper(c) ? UiActionModifiers.Shift : UiActionModifiers.None)).ToArray());
        h.Source.Add(() => h.Environment.CurrentFocus = Element(Button), Key(0x09));
        h.Source.Add(Key(0x0D));

        var result = await h.RecordAsync();

        Assert.AreEqual("recorded", result.Flow.Status);
        Assert.HasCount(3, result.Flow.Steps);
        Assert.AreEqual("text1", result.Flow.Steps[0].Parameter);
        Assert.AreEqual(Edit, result.Flow.Steps[0].Target);
        Assert.AreEqual("tab", result.Flow.Steps[1].Keys);
        Assert.AreEqual(Edit, result.Flow.Steps[1].Target);
        Assert.AreEqual("enter", result.Flow.Steps[2].Keys);
        Assert.AreEqual(Button, result.Flow.Steps[2].Target);
        Assert.IsTrue(h.Environment.FocusedCalls <= 9);
        Assert.IsEmpty(h.Environment.ValueCalls);
    }

    [TestMethod]
    public async Task Record_TextBurstRechecksPrivacyAfterBatching()
    {
        using var h = new Harness();
        h.Environment.CurrentFocus = Element(Edit, true);
        h.Environment.OnFocused = () =>
        {
            if (h.Environment.FocusedCalls == 3)
            {
                h.Environment.Replace(Element(Edit, true) with { IsPassword = true });
            }
        };
        h.Source.Add(Key(0x41), Key(0x42), Key(0x43));

        var result = await h.RecordAsync();

        AssertStopped(result, "sensitive_input");
        Assert.IsEmpty(result.Flow.Steps);
        Assert.IsEmpty(h.Environment.ValueCalls);
    }
}
