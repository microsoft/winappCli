// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
public sealed class UiAuditObservationServiceTests
{
    private static readonly UiTarget Target = new() { ProcessId = 12, WindowHandle = 34 };
    private static readonly bool[] ExpectedProbeInputs = [false, false, true, true];
    private static readonly string[] ExpectedProbeDirections = ["initial", "forward", "forward", "reverse", "reverse"];
    private static readonly int[] ExpectedProbeSequences = [0, 1, 2, 3, 4];
    private static readonly bool[] ExpectedProbeRepeats = [false, false, false, true, true];
    private static readonly int[] ExpectedLimitedEventSequences = [1, 2];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ZeroTabStepsNeverActivateOrSendInput(bool acceptInput)
    {
        using var h = new Harness();
        var result = await h.Run(new UiAuditObservationOptions { AcceptInput = acceptInput });

        Assert.AreEqual("completed", result.Status);
        Assert.AreEqual("duration_elapsed", result.StopReason);
        Assert.AreEqual(5, result.DurationSec);
        Assert.AreEqual(500, result.MaxEvents);
        Assert.AreEqual(0, result.RequestedTabSteps);
        Assert.AreEqual(0, result.CompletedTabSteps);
        Assert.AreEqual(0, h.Environment.Activations);
        Assert.IsEmpty(h.Environment.Inputs);
        Assert.HasCount(1, result.FocusTrace);
        Assert.IsEmpty(result.Events);
        Assert.AreEqual(0, result.FocusTrace[0].Sequence);
        Assert.AreEqual("initial", result.FocusTrace[0].Direction);
        Assert.AreEqual(result.StartedAtUtc.AddSeconds(5), result.EndedAtUtc);
        AssertOwnerContext(h.Environment);
    }

    [TestMethod]
    public void ConsentIsCheckedBeforeNativeEnvironmentCreation()
    {
        using var h = new Harness();
        var failure = Assert.ThrowsExactly<UiAuditObservationException>(() =>
            h.Service.ObserveAsync(Target, new UiAuditObservationOptions { TabSteps = 1 }));

        Assert.AreEqual("input_opt_in_required", failure.Code);
        Assert.AreEqual(0, h.Factory.Calls);
        Assert.IsEmpty(h.Environment.Accesses);
    }

    [TestMethod]
    [DataRow(0, 0, 500)]
    [DataRow(61, 0, 500)]
    [DataRow(5, -1, 500)]
    [DataRow(5, 31, 500)]
    [DataRow(5, 0, 0)]
    [DataRow(5, 0, 5001)]
    public void LimitsAreCheckedBeforeNativeEnvironmentCreation(int duration, int steps, int events)
    {
        using var h = new Harness();
        var failure = Assert.ThrowsExactly<UiAuditObservationException>(() =>
            h.Service.ObserveAsync(Target, new UiAuditObservationOptions
            {
                DurationSec = duration, TabSteps = steps, MaxEvents = events, AcceptInput = true,
            }));

        Assert.AreEqual("invalid_arguments", failure.Code);
        Assert.AreEqual(0, h.Factory.Calls);
    }

    [TestMethod]
    public async Task ForwardAndReverseAreBoundedAndNormalRepeatsAreNotFailures()
    {
        using var h = new Harness();
        var result = await h.Probe(2);

        Assert.AreEqual("completed", result.Status);
        Assert.AreEqual(4, result.CompletedTabSteps);
        Assert.AreEqual(2, result.RequestedTabSteps);
        CollectionAssert.AreEqual(ExpectedProbeInputs, h.Environment.Inputs);
        Assert.AreEqual(1, h.Environment.Activations);
        CollectionAssert.AreEqual(ExpectedProbeDirections,
            result.FocusTrace.Select(point => point.Direction).ToArray());
        CollectionAssert.AreEqual(ExpectedProbeSequences, result.FocusTrace.Select(point => point.Sequence).ToArray());
        CollectionAssert.AreEqual(ExpectedProbeRepeats, result.FocusTrace.Select(point => point.Repeat).ToArray());
        Assert.IsTrue(result.FocusTrace.All(point => point.AutomationId is null));
        Assert.IsEmpty(result.Issues);
        Assert.IsEmpty(result.Events);
        AssertOwnerContext(h.Environment);
    }

    [TestMethod]
    public async Task UpperBoundIsThirtyStepsInEachDirection()
    {
        using var h = new Harness();
        var result = await h.Probe(30, duration: 60);

        Assert.AreEqual("completed", result.Status);
        Assert.AreEqual(60, result.CompletedTabSteps);
        Assert.AreEqual(30, h.Environment.Inputs.Count(reverse => !reverse));
        Assert.AreEqual(30, h.Environment.Inputs.Count(reverse => reverse));
        Assert.HasCount(61, result.FocusTrace);
        Assert.AreEqual(1, h.Environment.Activations);
    }

    [TestMethod]
    public async Task ExpiredBudgetRetainsActuallyDeliveredStepsWithoutFinishingThePath()
    {
        using var h = new Harness();
        var result = await h.Probe(30, duration: 1);

        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual("duration_elapsed", result.StopReason);
        Assert.AreEqual(h.Environment.Inputs.Count, result.CompletedTabSteps);
        Assert.IsTrue(result.CompletedTabSteps is > 0 and < 60);
        Assert.IsTrue(result.FocusTrace.Length <= result.CompletedTabSteps + 1);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "focus_probe_incomplete"));
        Assert.AreEqual(result.StartedAtUtc.AddSeconds(1), result.EndedAtUtc);
        Assert.AreEqual(1, h.Environment.Disposals);
    }

    [TestMethod]
    public async Task MissingRuntimeAndAutomationIdsStillProduceLocalObservations()
    {
        using var h = new Harness();
        h.Environment.RuntimeIdsAvailable = false;
        var result = await h.Probe(2);

        Assert.AreEqual("completed", result.Status);
        Assert.HasCount(5, result.FocusTrace);
        Assert.IsTrue(result.FocusTrace.All(point => point.RuntimeId is null && point.AutomationId is null));
        Assert.AreEqual(5, result.FocusTrace.Select(point => point.NodeId).Distinct().Count());
        Assert.IsFalse(result.FocusTrace.Any(point => point.Repeat));
    }

    [TestMethod]
    public async Task FocusLeavingSelectedWindowStopsInputAndNeverRetainsForeignMetadata()
    {
        using var h = new Harness();
        h.Environment.AfterInput = () => h.Environment.ForeignFocus = true;
        var result = await h.Probe(3);

        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual("focus_left_target", result.StopReason);
        Assert.HasCount(1, h.Environment.Inputs);
        Assert.AreEqual(1, result.CompletedTabSteps);
        var outside = result.FocusTrace.Last();
        Assert.IsFalse(outside.IsInTarget);
        Assert.IsNull(outside.NodeId);
        Assert.IsNull(outside.RuntimeId);
        Assert.IsNull(outside.AutomationId);
        Assert.IsNull(outside.Name);
        Assert.IsNull(outside.ControlType);
        Assert.IsNull(outside.IsPassword);
        Assert.AreEqual(1, h.Environment.Activations);
    }

    [TestMethod]
    public async Task ForeignInitialFocusDoesNotActivateInReadOnlyMode()
    {
        using var h = new Harness();
        h.Environment.ForeignFocus = true;
        h.Environment.Foreground = false;
        var result = await h.Run();

        Assert.AreEqual("completed", result.Status);
        Assert.HasCount(1, result.FocusTrace);
        Assert.IsFalse(result.FocusTrace[0].IsInTarget);
        Assert.IsNull(result.FocusTrace[0].Name);
        Assert.AreEqual(0, h.Environment.Activations);
        Assert.IsEmpty(h.Environment.Inputs);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NonInteractivePassiveModeCollectsEventsForTheRequestedInterval(bool acceptInput)
    {
        using var h = new Harness();
        ConfigurePassiveEventInterval(h);
        var result = await h.Run(new UiAuditObservationOptions { DurationSec = 3, AcceptInput = acceptInput });

        Assert.AreEqual("completed", result.Status);
        Assert.AreEqual("duration_elapsed", result.StopReason);
        Assert.AreEqual(result.StartedAtUtc.AddSeconds(3), result.EndedAtUtc);
        Assert.HasCount(3, result.Events);
        Assert.IsTrue(result.Events.All(item => item.Kind == "structure" && item.NodeId == "target-node"));
        Assert.HasCount(1, result.FocusTrace);
        Assert.IsFalse(result.FocusTrace[0].IsInTarget);
        Assert.IsNull(result.FocusTrace[0].Name);
        Assert.IsNull(result.FocusTrace[0].RuntimeId);
        Assert.IsNull(result.FocusTrace[0].AutomationId);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "initial_focus_unavailable"));
        Assert.AreEqual(0, h.Environment.DesktopChecks);
        Assert.AreEqual(0, h.Environment.ForegroundChecks);
        Assert.AreEqual(0, h.Environment.Activations);
        Assert.AreEqual(1, h.Environment.Subscriptions);
        Assert.AreEqual(1, h.Environment.FocusReads);
        Assert.IsEmpty(h.Environment.Inputs);
        AssertOwnerContext(h.Environment);
    }

    [TestMethod]
    public async Task UnavailablePassiveInitialFocusDoesNotEndNativeEventCollection()
    {
        using var h = new Harness();
        ConfigurePassiveEventInterval(h);
        h.Environment.FocusFailure = new System.Runtime.InteropServices.COMException("private-focus-provider-detail");
        var result = await h.Run(new UiAuditObservationOptions { DurationSec = 3 });

        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual("coverage_limited", result.StopReason);
        Assert.AreEqual(result.StartedAtUtc.AddSeconds(3), result.EndedAtUtc);
        Assert.HasCount(3, result.Events);
        Assert.HasCount(1, result.FocusTrace);
        Assert.IsFalse(result.FocusTrace[0].IsInTarget);
        Assert.IsNull(result.FocusTrace[0].Name);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "initial_focus_unavailable"));
        Assert.IsFalse(result.Issues.Any(issue => issue.Message.Contains("private-focus-provider-detail", StringComparison.Ordinal)));
        Assert.AreEqual(1, h.Environment.FocusReads);
        Assert.AreEqual(0, h.Environment.DesktopChecks);
        Assert.AreEqual(0, h.Environment.ForegroundChecks);
        Assert.AreEqual(0, h.Environment.Activations);
        Assert.IsEmpty(h.Environment.Inputs);
        AssertOwnerContext(h.Environment);
    }

    [TestMethod]
    [DataRow("notification_events_unavailable")]
    [DataRow("event_source_unavailable")]
    public async Task NonInteractivePassiveModeRetainsNativeCoverageErrors(string issueCode)
    {
        using var h = new Harness();
        ConfigurePassiveEventInterval(h);
        var deliverEvents = h.Environment.DuringWait!;
        h.Environment.DuringWait = () =>
        {
            deliverEvents();
            h.Environment.Events.Issue(issueCode, "Native event coverage is incomplete.");
        };
        var result = await h.Run(new UiAuditObservationOptions { DurationSec = 3 });

        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual("coverage_limited", result.StopReason);
        Assert.AreEqual(result.StartedAtUtc.AddSeconds(3), result.EndedAtUtc);
        Assert.HasCount(3, result.Events);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == issueCode));
        Assert.AreEqual(0, h.Environment.Activations);
        Assert.IsEmpty(h.Environment.Inputs);
    }

    [TestMethod]
    public async Task UnavailableActiveFocusStillStopsBeforeInput()
    {
        using var h = new Harness();
        h.Environment.FocusFailure = new System.Runtime.InteropServices.COMException("private-focus-provider-detail");
        var result = await h.Probe(2);

        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual("observation_failed", result.StopReason);
        Assert.AreEqual(0, result.CompletedTabSteps);
        Assert.IsEmpty(h.Environment.Inputs);
        Assert.IsFalse(result.Issues.Any(issue => issue.Message.Contains("private-focus-provider-detail", StringComparison.Ordinal)));
        AssertOwnerContext(h.Environment);
    }

    [TestMethod]
    public async Task ForegroundLossStopsBeforeAnotherChordAndNeverReactivates()
    {
        using var h = new Harness();
        h.Environment.AfterInput = () => h.Environment.Foreground = false;
        var result = await h.Probe(3);

        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual("foreground_not_target", result.StopReason);
        Assert.HasCount(1, h.Environment.Inputs);
        Assert.AreEqual(1, h.Environment.Activations);
    }

    [TestMethod]
    public async Task OriginalProcessExitStopsBeforeAnotherChord()
    {
        using var h = new Harness();
        h.Environment.AfterInput = () => h.Environment.Exists = false;
        var result = await h.Probe(3);

        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual("target_closed", result.StopReason);
        Assert.HasCount(1, h.Environment.Inputs);
        Assert.AreEqual(1, h.Environment.Disposals);
    }

    [TestMethod]
    public async Task LockedDesktopIsAnExplicitEnvironmentResult()
    {
        using var h = new Harness();
        h.Environment.Interactive = false;
        var result = await h.Probe(2);

        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual("no_interactive_desktop", result.StopReason);
        Assert.AreEqual(0, h.Environment.Activations);
        Assert.AreEqual(0, h.Environment.Subscriptions);
        Assert.IsTrue(h.Environment.DesktopChecks > 0);
        Assert.AreEqual(0, h.Environment.FocusReads);
        Assert.IsEmpty(h.Environment.Inputs);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "no_interactive_desktop"));
        Assert.AreEqual(1, h.Environment.Disposals);
    }

    [TestMethod]
    public async Task CancellationBeforeActivationStopsAndRemovesSubscriptionsOnTheOwnerContext()
    {
        using var h = new Harness();
        h.Environment.AfterSubscribe = _ => h.Cancellation.Cancel();
        var result = await h.Probe(2);

        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual("cancelled", result.StopReason);
        Assert.AreEqual(0, h.Environment.Activations);
        Assert.IsEmpty(h.Environment.Inputs);
        Assert.AreEqual(1, h.Environment.Subscriptions);
        AssertOwnerContext(h.Environment);
    }

    [TestMethod]
    public async Task CancellationAfterDeliveryRetainsTheDeliveredCountAndStopsInput()
    {
        using var h = new Harness();
        h.Environment.AfterInput = h.Cancellation.Cancel;
        var result = await h.Probe(3);

        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual("cancelled", result.StopReason);
        Assert.AreEqual(1, result.CompletedTabSteps);
        Assert.HasCount(1, h.Environment.Inputs);
        Assert.HasCount(1, result.FocusTrace);
        AssertOwnerContext(h.Environment);
    }

    [TestMethod]
    public async Task EventLimitStopsObservationAndCapsStorageExactly()
    {
        using var h = new Harness();
        h.Environment.AfterSubscribe = buffer =>
        {
            for (var i = 0; i < 10; i++)
            {
                buffer.Add(new UiAuditEvent { Kind = "structure" });
            }
        };
        var result = await h.Run(new UiAuditObservationOptions { MaxEvents = 2 });

        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual("event_limit", result.StopReason);
        Assert.IsTrue(result.EventLimitReached);
        Assert.HasCount(2, result.Events);
        CollectionAssert.AreEqual(ExpectedLimitedEventSequences, result.Events.Select(item => item.Sequence).ToArray());
        Assert.IsEmpty(h.Environment.Inputs);
        Assert.AreEqual(1, h.Environment.Disposals);
    }

    [TestMethod]
    public async Task UnsupportedEventCoverageIsExplicitWithoutSyntheticEvents()
    {
        using var h = new Harness();
        h.Environment.AfterSubscribe = buffer => buffer.Issue("notification_events_unavailable",
            "Notification event subscriptions are unavailable.");
        var result = await h.Run();

        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual("coverage_limited", result.StopReason);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "notification_events_unavailable"));
        Assert.IsEmpty(result.Events);
        Assert.HasCount(1, result.FocusTrace);
    }

    [TestMethod]
    public async Task ClosingCaptureRejectsLateCallbacksDuringCleanup()
    {
        using var h = new Harness();
        h.Environment.OnDispose = buffer => buffer.Add(new UiAuditEvent { Kind = "notification" });
        var result = await h.Run();

        Assert.IsEmpty(result.Events);
        Assert.AreEqual("completed", result.Status);
        Assert.AreEqual(1, h.Environment.Disposals);
    }

    [TestMethod]
    public async Task FailedCleanupMakesTheResultPartial()
    {
        using var h = new Harness();
        h.Environment.OnDispose = _ => throw new InvalidOperationException("provider-private-detail");
        var result = await h.Run();

        Assert.AreEqual("partial", result.Status);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "event_cleanup_failed"));
        Assert.IsFalse(result.Issues.Any(issue => issue.Message.Contains("provider-private-detail", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ExpiredSetupNeverLooksLikeCompletedObservation()
    {
        using var h = new Harness();
        h.Environment.AfterSubscribe = _ => h.Clock.Advance(5000);
        var result = await h.Run();

        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual("duration_elapsed", result.StopReason);
        Assert.IsEmpty(result.FocusTrace);
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "initial_focus_unavailable"));
        Assert.AreEqual(1, h.Environment.Disposals);
    }

    private static void ConfigurePassiveEventInterval(Harness h)
    {
        h.Environment.Interactive = false;
        h.Environment.Foreground = false;
        h.Environment.ForeignFocus = true;
        var nextEventAt = 500L;
        h.Environment.DuringWait = () =>
        {
            if (h.Clock.GetTimestamp() >= nextEventAt)
            {
                h.Environment.Events.Add(new UiAuditEvent
                {
                    Kind = "structure",
                    NodeId = "target-node",
                    ReceivedAtUtc = h.Clock.GetUtcNow(),
                });
                nextEventAt += 1000;
            }
        };
    }

    private static void AssertOwnerContext(FakeEnvironment environment)
    {
        Assert.AreEqual(1, environment.Disposals);
        Assert.AreEqual(1, environment.Accesses.Select(access => access.Thread).Distinct().Count());
        Assert.IsTrue(environment.Accesses.All(access => access.Apartment == ApartmentState.MTA && !access.Pool));
    }

    private sealed class Harness : IDisposable
    {
        internal TestClock Clock { get; } = new();
        internal CancellationTokenSource Cancellation { get; } = new();
        internal FakeEnvironment Environment { get; } = new();
        internal FakeFactory Factory { get; }
        internal UiAuditObservationService Service { get; }

        internal Harness()
        {
            Factory = new FakeFactory(Environment, Clock);
            Service = new UiAuditObservationService(Factory, Clock, (milliseconds, token) =>
            {
                token.ThrowIfCancellationRequested();
                Clock.Advance(milliseconds);
                Environment.DuringWait?.Invoke();
                token.ThrowIfCancellationRequested();
            });
        }

        internal Task<UiAuditObservationResult> Run(UiAuditObservationOptions? options = null)
            => Service.ObserveAsync(Target, options ?? new UiAuditObservationOptions(), Cancellation.Token);

        internal Task<UiAuditObservationResult> Probe(int steps, int duration = 5)
            => Run(new UiAuditObservationOptions { TabSteps = steps, AcceptInput = true, DurationSec = duration });

        public void Dispose() => Cancellation.Dispose();
    }

    private sealed class TestClock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(GetTimestamp());
        internal void Advance(int milliseconds) => Interlocked.Add(ref _milliseconds, milliseconds);
    }

    private sealed class FakeFactory(FakeEnvironment environment, TestClock clock) : IUiAuditObservationEnvironmentFactory
    {
        internal int Calls { get; private set; }

        public IUiAuditObservationEnvironment Create(UiTarget target, UiAuditObservationBudget budget, UiAuditEventBuffer events)
        {
            Calls++;
            environment.Track();
            environment.Clock = clock;
            environment.Events = events;
            return environment;
        }
    }

    private sealed class FakeEnvironment : IUiAuditObservationEnvironment
    {
        private int _position;
        internal TestClock Clock { get; set; } = null!;
        internal UiAuditEventBuffer Events { get; set; } = null!;
        internal bool RuntimeIdsAvailable { get; set; } = true;
        internal bool ForeignFocus { get; set; }
        internal bool Foreground { get; set; } = true;
        internal bool Exists { get; set; } = true;
        internal bool Interactive { get; set; } = true;
        internal Exception? FocusFailure { get; set; }
        internal int DesktopChecks { get; private set; }
        internal int ForegroundChecks { get; private set; }
        internal int FocusReads { get; private set; }
        internal int Activations { get; private set; }
        internal int Subscriptions { get; private set; }
        internal int Disposals { get; private set; }
        internal List<bool> Inputs { get; } = [];
        internal List<(int Thread, ApartmentState Apartment, bool Pool)> Accesses { get; } = [];
        internal Action? AfterInput { get; set; }
        internal Action? DuringWait { get; set; }
        internal Action<UiAuditEventBuffer>? AfterSubscribe { get; set; }
        internal Action<UiAuditEventBuffer>? OnDispose { get; set; }

        public int ProcessId => Target.ProcessId;
        public bool TargetExists { get { Track(); return Exists; } }
        public bool HasInteractiveDesktop { get { Track(); DesktopChecks++; return Interactive; } }
        public bool IsForeground { get { Track(); ForegroundChecks++; return Foreground; } }

        internal void Track() => Accesses.Add((System.Environment.CurrentManagedThreadId,
            Thread.CurrentThread.GetApartmentState(), Thread.CurrentThread.IsThreadPoolThread));

        public void Subscribe()
        {
            Track();
            Subscriptions++;
            AfterSubscribe?.Invoke(Events);
        }

        public void Activate()
        {
            Track();
            Activations++;
            Foreground = true;
        }

        public UiAuditFocusPoint ReadFocus()
        {
            Track();
            FocusReads++;
            if (FocusFailure is not null)
            {
                throw FocusFailure;
            }
            var runtime = RuntimeIdsAvailable ? $"[2A,22,{_position:X}]" : null;
            return new UiAuditFocusPoint
            {
                IsInTarget = !ForeignFocus,
                RuntimeId = runtime,
                NodeId = Events.Identity(runtime),
                Name = ForeignFocus ? "foreign-private-name" : "Static label",
                AutomationId = ForeignFocus ? "foreign-private-id" : null,
                IsPassword = false,
                ControlType = "Button",
                ObservedAtUtc = Clock.GetUtcNow(),
            };
        }

        public void SendTab(bool reverse)
        {
            Track();
            Inputs.Add(reverse);
            _position = (_position + (reverse ? 2 : 1)) % 3;
            AfterInput?.Invoke();
        }

        public void Dispose()
        {
            Track();
            Disposals++;
            OnDispose?.Invoke(Events);
        }
    }
}
