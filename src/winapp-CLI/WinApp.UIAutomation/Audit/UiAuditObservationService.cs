// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>Observes native events and, only with consent, a bounded keyboard focus path in one window.</summary>
public interface IUiAuditObservationService
{
    /// <summary>Collects observations without invoking controls, setting values, or restoring application state.</summary>
    /// <param name="target">One selected top-level window and its owning process.</param>
    /// <param name="options">Time/event limits and optional keyboard-input consent.</param>
    /// <param name="cancellationToken">Stops observation while preserving the bounded partial result.</param>
    /// <returns>Actual focus samples and native callbacks, with explicit collection limitations.</returns>
    Task<UiAuditObservationResult> ObserveAsync(UiTarget target, UiAuditObservationOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Native Windows UI Automation observation with no input by default.</summary>
internal sealed class UiAuditObservationService : IUiAuditObservationService
{
    private const int PollIntervalMs = 25;
    private const int FocusSettleMs = 75;
    private readonly IUiAuditObservationEnvironmentFactory _environments;
    private readonly TimeProvider _clock;
    private readonly Action<int, CancellationToken> _wait;

    /// <summary>Creates the native observer.</summary>
    /// <param name="keyboard">Foreground-protected keyboard transport used only for consented Tab/Shift+Tab chords.</param>
    public UiAuditObservationService(IKeyboardInput keyboard)
        : this(new NativeUiAuditObservationFactory(keyboard), TimeProvider.System, Wait)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
    }

    internal UiAuditObservationService(IUiAuditObservationEnvironmentFactory environments,
        TimeProvider clock, Action<int, CancellationToken> wait)
    {
        _environments = environments;
        _clock = clock;
        _wait = wait;
    }

    /// <inheritdoc />
    public Task<UiAuditObservationResult> ObserveAsync(UiTarget target, UiAuditObservationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = new UiTarget
        {
            ProcessId = target.ProcessId,
            WindowHandle = target.WindowHandle,
            IsExplicitWindow = true,
        };
        var completion = new TaskCompletionSource<UiAuditObservationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Observe(selected, options, cancellationToken));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "winapp UI audit observation",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return completion.Task;
    }

    internal static void Validate(UiAuditObservationOptions options)
    {
        if (options.TabSteps > 0 && !options.AcceptInput)
        {
            throw new UiAuditObservationException("input_opt_in_required",
                "--tab-steps requires --accept-input. Tab and Shift+Tab can change application state.");
        }
        if (options.DurationSec is < 1 or > 60 || options.TabSteps is < 0 or > 30 ||
            options.MaxEvents is < 1 or > 5000)
        {
            throw new UiAuditObservationException("invalid_arguments",
                "Duration must be 1-60 seconds, Tab steps 0-30 per direction, and max events 1-5000.");
        }
    }

    private UiAuditObservationResult Observe(UiTarget target, UiAuditObservationOptions options,
        CancellationToken cancellationToken)
    {
        using var budget = new UiAuditObservationBudget(options.DurationSec, _clock, cancellationToken);
        var events = new UiAuditEventBuffer(options.MaxEvents, budget);
        var focus = new List<UiAuditFocusPoint>(1 + 2 * options.TabSteps);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var completedSteps = 0;
        var processId = target.ProcessId;
        IUiAuditObservationEnvironment? environment = null;

        void Check(bool requireForeground)
        {
            budget.Check();
            if (events.StopReason is { } reason)
            {
                throw new UiAuditObservationException(reason, "Observation stopped; no further input was sent.");
            }
            if (!environment!.TargetExists)
            {
                throw new UiAuditObservationException("target_closed",
                    "The selected window or original process is no longer available.");
            }
            if (options.TabSteps > 0 && !environment.HasInteractiveDesktop)
            {
                throw new UiAuditObservationException("no_interactive_desktop",
                    "An unlocked interactive Windows desktop is required. This is an environment limitation.");
            }
            if (options.TabSteps > 0 && requireForeground && !environment.IsForeground)
            {
                throw new UiAuditObservationException("foreground_not_target",
                    "The selected window lost foreground focus; no further input was sent.");
            }
        }

        void Sample(string direction)
        {
            UiAuditFocusPoint point;
            try
            {
                point = environment!.ReadFocus();
            }
            catch (Exception exception) when (options.TabSteps == 0 &&
                exception is not OperationCanceledException and not UiAuditObservationException)
            {
                Check(requireForeground: false);
                events.Issue("initial_focus_unavailable",
                    "The initial focus query was unavailable. Native event observation continued without outside-window focus metadata.");
                point = new UiAuditFocusPoint { ObservedAtUtc = budget.UtcNow, NameWithheld = true };
            }
            if (!point.IsInTarget)
            {
                // A fake or future adapter must not accidentally persist metadata from foreign focus.
                point = new UiAuditFocusPoint { ObservedAtUtc = point.ObservedAtUtc, NameWithheld = true };
                if (options.TabSteps == 0)
                {
                    events.Issue("initial_focus_unavailable",
                        "Initial keyboard focus is outside the selected window or unavailable; no outside focus metadata was retained.",
                        limitsCoverage: false);
                }
            }
            var identity = point.RuntimeId ?? point.NodeId;
            focus.Add(point with
            {
                Sequence = completedSteps,
                Direction = direction,
                Repeat = identity is not null && !seen.Add(identity),
            });
            if (!point.IsInTarget && options.TabSteps > 0)
            {
                throw new UiAuditObservationException("focus_left_target",
                    "Keyboard focus is outside the selected window or its scope cannot be verified. No further input was sent.");
            }
        }

        void Pause(int milliseconds)
        {
            var remaining = Math.Min(milliseconds, budget.RemainingMs);
            if (remaining > 0)
            {
                _wait(remaining, budget.Token);
            }
            budget.Check();
        }

        try
        {
            budget.Check();
            environment = _environments.Create(target, budget, events);
            processId = environment.ProcessId;
            Check(requireForeground: false);
            environment.Subscribe();
            Check(requireForeground: false);
            if (options.TabSteps > 0)
            {
                environment.Activate();
                Check(requireForeground: true);
            }
            Sample("initial");

            foreach (var reverse in new[] { false, true })
            {
                for (var step = 0; step < options.TabSteps; step++)
                {
                    Check(requireForeground: true);
                    environment.SendTab(reverse);
                    completedSteps++;
                    Pause(FocusSettleMs);
                    Check(requireForeground: false);
                    Sample(reverse ? "reverse" : "forward");
                }
            }

            while (true)
            {
                Check(requireForeground: options.TabSteps > 0);
                Pause(PollIntervalMs);
            }
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                events.Stop("cancelled", "Observation was cancelled; the retained samples and events are partial.");
            }
            else if (!budget.Expired)
            {
                events.Stop("observation_cancelled", "A native observation operation was cancelled before completion.");
            }
        }
        catch (UiAuditObservationException exception) when (exception.Code == "duration_elapsed")
        {
            // Time expiry is the expected endpoint of read-only observation.
        }
        catch (UiAuditObservationException exception)
        {
            events.Stop(exception.Code, exception.Message);
        }
        catch
        {
            if (cancellationToken.IsCancellationRequested)
            {
                events.Stop("cancelled", "Observation was cancelled; the retained samples and events are partial.");
            }
            else if (!budget.Expired)
            {
                events.Stop("observation_failed",
                    "UI Automation could not complete this observation. No further input was sent; application state was not restored.");
            }
        }
        finally
        {
            events.Close();
            try
            {
                environment?.Dispose();
            }
            catch
            {
                events.Issue("event_cleanup_failed", "Some native event subscriptions could not be confirmed removed.");
            }
        }

        if (completedSteps < 2 * options.TabSteps || (options.TabSteps > 0 && focus.Count < completedSteps + 1))
        {
            events.Issue("focus_probe_incomplete",
                "The bounded focus probe ended before every requested input and resulting focus sample completed.");
        }
        if (focus.Count == 0)
        {
            events.Issue("initial_focus_unavailable", "Collection ended before the initial focus could be sampled.");
        }

        var stopReason = events.StopReason ?? (events.CoverageLimited ? "coverage_limited" : "duration_elapsed");
        if (budget.Expired && (completedSteps < 2 * options.TabSteps || focus.Count < completedSteps + 1)
            && events.StopReason is null)
        {
            stopReason = "duration_elapsed";
        }
        return new UiAuditObservationResult
        {
            ProcessId = processId,
            WindowHandle = target.WindowHandle,
            StartedAtUtc = budget.StartedAtUtc,
            EndedAtUtc = budget.UtcNow,
            Status = events.StopReason is null && !events.CoverageLimited ? "completed" : "partial",
            DurationSec = options.DurationSec,
            RequestedTabSteps = options.TabSteps,
            CompletedTabSteps = completedSteps,
            MaxEvents = options.MaxEvents,
            EventLimitReached = events.EventLimitReached,
            StopReason = stopReason,
            FocusTrace = [.. focus],
            Events = events.Events(),
            Issues = events.Issues(),
        };
    }

    private static void Wait(int milliseconds, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(milliseconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
