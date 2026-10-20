// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed partial class UiActionService(
    IUiActionEnvironmentFactory environments,
    IUiActionInputSourceFactory inputs) : IUiActionService
{
    private const int PollIntervalMs = 20;
    private const int InputSettleMs = 40;
    private const int DoubleClickIntervalMs = 500;
    private const int MaximumRecordingSteps = 10_000;
    private const int MaximumWheelDelta = 12_000;
    private const string TextWarning =
        "Editable input is recorded as replacement text; per-keystroke behavior is not preserved.";
    private const string ScopeWarning =
        "Recording paused when the selected window lost foreground focus. Outside input was not recorded; review the resumed focus targets.";

    internal static void CheckObservationTimestamp(long observedTimestamp, long now)
    {
        if (observedTimestamp <= 0 || observedTimestamp > now)
        {
            throw new UiActionException("input_timestamp", "Input capture did not provide a valid monotonic observation timestamp.");
        }

        if (now - observedTimestamp > System.Diagnostics.Stopwatch.Frequency / 2)
        {
            throw new UiActionException("input_backlog", "Input arrived too late to attribute safely.");
        }
    }

    public Task<UiActionRecordResult> RecordAsync(UiTarget target, UiActionRecordOptions options,
        CancellationToken cancellationToken, Action? onReady = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        if (options.DurationSec is < 0 or > 3_600 || options.MaxSteps is < 1 or > MaximumRecordingSteps)
        {
            throw new UiActionException("invalid_options",
                "Recording duration must be between 0 and 3600 seconds and max steps between 1 and 10000.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return RunOnMta(() => RecordCore(target, options, onReady, cancellationToken));
    }

    public Task<UiActionReplayResult> ReplayAsync(UiTarget target, UiActionFlow flow,
        UiActionReplayOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        UiActionFlowDocument.ValidateReplay(flow, options);
        cancellationToken.ThrowIfCancellationRequested();

        var reviewedFlow = flow with { Steps = flow.Steps.ToArray() };
        var reviewedOptions = options with
        {
            Parameters = new Dictionary<string, string>(options.Parameters, StringComparer.Ordinal),
        };
        UiActionFlowDocument.ValidateReplay(reviewedFlow, reviewedOptions);
        return RunOnMta(() => ReplayCore(target, reviewedFlow, reviewedOptions, cancellationToken));
    }

    private static Task<TResult> RunOnMta<TResult>(Func<TResult> operation)
    {
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                completion.SetResult(operation());
            }
            catch (OperationCanceledException ex)
            {
                completion.SetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "winapp UI action flow",
        };
        // Keep the creating COM apartment alive through every wait and the environment's disposal.
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        return completion.Task;
    }

    private static void Delay(int milliseconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cancellationToken.CanBeCanceled)
        {
            if (cancellationToken.WaitHandle.WaitOne(milliseconds))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        else
        {
            Thread.Sleep(milliseconds);
        }
    }

    private static bool SameTarget(UiActionTarget left, UiActionTarget right)
    {
        if (!string.Equals(left.ControlType, right.ControlType, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(left.AutomationId) || !string.IsNullOrEmpty(right.AutomationId))
        {
            return !string.IsNullOrEmpty(left.AutomationId)
                && string.Equals(left.AutomationId, right.AutomationId, StringComparison.Ordinal);
        }

        return !string.IsNullOrEmpty(left.Name)
            && string.Equals(left.Name, right.Name, StringComparison.Ordinal);
    }

    private static void CheckEnvironment(IUiActionEnvironment environment, bool requireForeground = true)
    {
        if (!environment.HasInteractiveDesktop)
        {
            throw new UiActionException("no_interactive_desktop", "An unlocked interactive desktop is required.");
        }

        if (!environment.TargetExists)
        {
            throw new UiActionException("target_closed", "The selected window is no longer available.");
        }

        if (requireForeground && !environment.IsForeground)
        {
            throw new UiActionException("foreground_not_target", "The selected window is not in the foreground; no further input was sent.");
        }
    }

    private static void CheckPrivacy(UiActionElement element)
    {
        if (element.IsPassword)
        {
            throw new UiActionException("sensitive_input", "Password or uncertain input cannot be recorded or replayed.");
        }
    }

    private static IReadOnlyList<KeyAction> ParseSingleChord(string keys)
    {
        IReadOnlyList<KeyAction> actions;
        try
        {
            actions = KeyStringParser.Parse(keys);
        }
        catch (FormatException)
        {
            throw new UiActionException("unsupported_input", "The action must contain one supported key chord, not literal text.");
        }

        if (actions.Count != 1 || actions[0] is not KeyChord)
        {
            throw new UiActionException("unsupported_input", "The action must contain one supported key chord, not literal text.");
        }

        if (SystemKeyGuard.FindSystemCombos(actions).Count != 0
            || SystemKeyGuard.FindNeverBypassableCombos(actions).Count != 0)
        {
            throw new UiActionException("system_key", "System-reserved keys cannot be recorded or replayed.");
        }

        return actions;
    }

    private static string SafeCode(string? code)
        => code is { Length: > 0 and <= 64 }
            && code.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            ? code : "action_failed";
}
