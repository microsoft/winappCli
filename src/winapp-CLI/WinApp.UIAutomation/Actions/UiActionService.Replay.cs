// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed partial class UiActionService
{
    private UiActionReplayResult ReplayCore(UiTarget target, UiActionFlow flow,
        UiActionReplayOptions options, CancellationToken cancellationToken)
    {
        int completed = 0;
        int assertions = 0;
        int? stepIndex = null;
        var previousClick = new Stopwatch();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var environment = environments.Create(target, cancellationToken);
            CheckEnvironment(environment, requireForeground: false);
            if (!string.Equals(environment.ProcessName, flow.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                throw new UiActionException("target_mismatch", "Select a window belonging to the recorded application.");
            }

            if (flow.InitialFocus is not null)
            {
                WaitForReplayTarget(environment, flow.InitialFocus, "initial", options.TimeoutMs, cancellationToken);
                CheckEnvironment(environment, requireForeground: false);
                cancellationToken.ThrowIfCancellationRequested();
                environment.Focus(flow.InitialFocus);
                WaitForReplayTarget(environment, flow.InitialFocus, "key", options.TimeoutMs, cancellationToken);
            }

            CheckEnvironment(environment);
            foreach (var step in flow.Steps)
            {
                stepIndex = completed;
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Kind == "assert")
                {
                    CheckAssertion(environment, step, options.TimeoutMs, cancellationToken);
                    assertions++;
                }
                else
                {
                    if (step.Kind == "click")
                    {
                        SeparateClick(environment, previousClick, cancellationToken);
                    }

                    var element = WaitForReplayTarget(environment, step.Target, step.Kind, options.TimeoutMs, cancellationToken);
                    CheckEnvironment(environment);
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (step.Kind)
                    {
                        case "click":
                            environment.Click(element, step.XFraction, step.YFraction, step.Button == "right",
                                step.ClickCount == 2);
                            previousClick.Restart();
                            break;
                        case "wheel":
                            environment.Wheel(element, step.XFraction, step.YFraction, step.WheelDelta);
                            break;
                        case "key":
                            var keys = ParseSingleChord(step.Keys!);
                            CheckReplayFocus(environment, step.Target, requireEditable: false);
                            cancellationToken.ThrowIfCancellationRequested();
                            environment.SendKeys(keys);
                            break;
                        case "text":
                            ReplayText(environment, step, options, cancellationToken);
                            break;
                        default:
                            throw new UiActionException("invalid_flow", "The flow contains an unsupported action.");
                    }

                    SettleReplayInput(environment, cancellationToken);
                }

                completed++;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new UiActionReplayResult(completed, assertions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string code = ex is UiActionException action ? SafeCode(action.Code) : "action_failed";
            string prefix = stepIndex.HasValue ? $"Step {stepIndex.Value + 1} failed" : "Replay could not start";
            // Providers may put control names or field values in their exceptions. Do not echo them.
            throw new UiActionException(code, $"{prefix} ({code}). No further input was sent.");
        }
    }

    private static UiActionElement WaitForReplayTarget(IUiActionEnvironment environment,
        UiActionTarget target, string kind, int timeoutMs, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        string failure = "target_not_found";
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckEnvironment(environment, requireForeground: kind != "initial");
            var element = environment.Resolve(target);
            if (element is not null)
            {
                CheckPrivacy(element);
                if (kind == "text")
                {
                    RequireWritable(element);
                }

                if (element.IsEnabled && !element.IsOffscreen)
                {
                    if (kind is not ("key" or "text"))
                    {
                        return element;
                    }

                    var focused = environment.Focused();
                    if (focused is not null)
                    {
                        CheckPrivacy(focused);
                        if (SameTarget(element.Target, focused.Target))
                        {
                            if (kind == "text")
                            {
                                RequireWritable(focused);
                            }

                            return element;
                        }
                    }

                    failure = "focus_mismatch";
                }
                else
                {
                    failure = "target_unavailable";
                }
            }

            DelayCheckpoint(clock, timeoutMs, cancellationToken);
        }
        while (clock.ElapsedMilliseconds < timeoutMs);

        throw new UiActionException(failure, "The target or its expected focus did not become ready before the timeout.");
    }

    private static void ReplayText(IUiActionEnvironment environment, UiActionStep step,
        UiActionReplayOptions options, CancellationToken cancellationToken)
    {
        string text = step.Parameter is null ? step.Text! : options.Parameters[step.Parameter];
        CheckReplayFocus(environment, step.Target, requireEditable: true);
        cancellationToken.ThrowIfCancellationRequested();
        environment.SendKeys(ParseSingleChord("ctrl+a"));

        for (int elapsed = 0; elapsed < InputSettleMs; elapsed += PollIntervalMs)
        {
            Delay(PollIntervalMs, cancellationToken);
            CheckReplayFocus(environment, step.Target, requireEditable: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        CheckReplayFocus(environment, step.Target, requireEditable: true);
        cancellationToken.ThrowIfCancellationRequested();
        environment.SendKeys(text.Length == 0 ? ParseSingleChord("delete") : [new TextInput(text)]);

        var clock = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckReplayFocus(environment, step.Target, requireEditable: true);
            string actual = environment.ReadValue(step.Target);
            cancellationToken.ThrowIfCancellationRequested();
            CheckReplayFocus(environment, step.Target, requireEditable: true);
            if (string.Equals(actual, text, StringComparison.Ordinal))
            {
                return;
            }

            DelayCheckpoint(clock, options.TimeoutMs, cancellationToken);
        }
        while (clock.ElapsedMilliseconds < options.TimeoutMs);

        throw new UiActionException("text_not_applied", "Replacement text was not observed in the target field before the timeout.");
    }

    private static void CheckReplayFocus(IUiActionEnvironment environment, UiActionTarget target, bool requireEditable)
    {
        CheckEnvironment(environment);
        var element = environment.Resolve(target)
            ?? throw new UiActionException("target_not_found", "The target is no longer available.");
        CheckPrivacy(element);
        if (!element.IsEnabled || element.IsOffscreen)
        {
            throw new UiActionException("target_unavailable", "The target must be enabled and visible.");
        }

        if (requireEditable)
        {
            RequireWritable(element);
        }

        var focused = environment.Focused();
        if (focused is not null)
        {
            CheckPrivacy(focused);
        }

        if (focused is null || !SameTarget(element.Target, focused.Target))
        {
            throw new UiActionException("focus_mismatch", "Keyboard focus changed; no further input was sent.");
        }

        if (!focused.IsEnabled || focused.IsOffscreen)
        {
            throw new UiActionException("target_unavailable", "The focused target must be enabled and visible.");
        }

        if (requireEditable)
        {
            RequireWritable(focused);
        }

        CheckEnvironment(environment);
    }

    private static void RequireWritable(UiActionElement element)
    {
        if (!element.IsEditable)
        {
            throw new UiActionException("readonly_target", "Replacement text requires a supported writable text field.");
        }
    }

    private static void CheckAssertion(IUiActionEnvironment environment, UiActionStep step,
        int timeoutMs, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        bool found = false;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckEnvironment(environment);
            var element = environment.Resolve(step.Target);
            if (element is not null)
            {
                found = true;
                CheckPrivacy(element);
                string? actual = environment.ReadProperty(step.Target, step.Property!);
                CheckEnvironment(environment);
                if (actual is not null && string.Equals(actual, step.Expected, StringComparison.Ordinal))
                {
                    return;
                }
            }

            DelayCheckpoint(clock, timeoutMs, cancellationToken);
        }
        while (clock.ElapsedMilliseconds < timeoutMs);

        throw new UiActionException(found ? "assertion_failed" : "target_not_found",
            "The reviewed assertion was not satisfied before the timeout.");
    }

    private static void DelayCheckpoint(Stopwatch clock, int timeoutMs, CancellationToken cancellationToken)
        => Delay((int)Math.Max(0, Math.Min(PollIntervalMs, timeoutMs - clock.ElapsedMilliseconds)),
            cancellationToken);

    private static void SettleReplayInput(IUiActionEnvironment environment, CancellationToken cancellationToken)
    {
        for (int elapsed = 0; elapsed < InputSettleMs; elapsed += PollIntervalMs)
        {
            Delay(PollIntervalMs, cancellationToken);
            CheckEnvironment(environment);
        }
    }

    private static void SeparateClick(IUiActionEnvironment environment, Stopwatch previousClick,
        CancellationToken cancellationToken)
    {
        // This preserves distinct click actions, rather than replaying their diagnostic timestamps.
        while (previousClick.IsRunning && previousClick.ElapsedMilliseconds <= DoubleClickIntervalMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckEnvironment(environment);
            Delay(PollIntervalMs, cancellationToken);
        }
    }
}
