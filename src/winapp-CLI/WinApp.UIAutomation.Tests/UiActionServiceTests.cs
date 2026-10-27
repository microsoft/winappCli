// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Threading.Channels;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
public partial class UiActionServiceTests
{
    private static readonly UiTarget App = new() { ProcessId = 10, ProcessName = "SampleApp", WindowHandle = 20 };
    private static readonly UiActionTarget Button = new() { AutomationId = "submit", ControlType = "Button" };
    private static readonly UiActionTarget Edit = new() { AutomationId = "editor", ControlType = "Edit" };
    private static readonly UiActionTarget NextEdit = new() { AutomationId = "nextEditor", ControlType = "Edit" };
    private static readonly UiActionReplayOptions Accepted = new() { AcceptRecording = true, TimeoutMs = 100 };

    private static UiActionElement Element(UiActionTarget target, bool editable = false)
        => new(target, new PointerRect(10, 20, 210, 120), false, editable, true, false);

    private static UiActionStep KeyStep(string keys = "enter", UiActionTarget? target = null)
        => new() { Kind = "key", Target = target ?? Button, Keys = keys };

    private static UiActionStep TextStep(string? text = null, string? parameter = null, UiActionTarget? target = null)
        => new() { Kind = "text", Target = target ?? Edit, Text = text, Parameter = parameter };

    private static UiActionStep ClickStep(UiActionTarget? target = null)
        => new() { Kind = "click", Target = target ?? Button, Button = "left" };

    private static UiActionFlow Flow(params UiActionStep[] steps)
        => new() { ProcessName = "SampleApp", InitialFocus = steps[0].Target, Steps = steps };

    private static UiActionInput Key(ushort key, UiActionModifiers modifiers = UiActionModifiers.None)
        => new(UiActionInputKind.KeyDown, 0, VirtualKey: key, Modifiers: modifiers);

    private static UiActionInput Down(int x = 110, int y = 70, bool right = false)
        => new(UiActionInputKind.MouseDown, 0, X: x, Y: y, RightButton: right);

    private static UiActionInput Up(int x = 110, int y = 70, bool right = false)
        => new(UiActionInputKind.MouseUp, 0, X: x, Y: y, RightButton: right);

    private static UiActionInput Wheel(int delta = 120, int x = 110, int y = 70)
        => new(UiActionInputKind.Wheel, 0, X: x, Y: y, WheelDelta: delta);

    private static void AssertStopped(UiActionRecordResult result, string reason)
    {
        Assert.AreEqual("incomplete", result.Flow.Status);
        Assert.AreEqual(reason, result.StopReason);
        UiActionFlowDocument.Validate(result.Flow);
    }

    private static KeyChord AssertChord(IReadOnlyList<KeyAction> actions, ushort key, params ushort[] modifiers)
    {
        Assert.HasCount(1, actions);
        var chord = actions[0] as KeyChord;
        Assert.IsNotNull(chord);
        Assert.AreEqual(key, chord.Vk);
        CollectionAssert.AreEqual(modifiers, chord.Modifiers.ToArray());
        return chord;
    }

    private static void AssertDedicatedMta(FakeEnvironment environment)
    {
        Assert.IsNotEmpty(environment.AccessThreads);
        Assert.AreEqual(1, environment.AccessThreads.Select(t => t.Id).Distinct().Count());
        Assert.IsTrue(environment.AccessThreads.All(t => t.Apartment == ApartmentState.MTA && !t.Pool));
    }

    private sealed class Harness : IDisposable
    {
        internal CancellationTokenSource Cancellation { get; } = new();
        internal FakeEnvironment Environment { get; } = new();
        internal FakeEnvironmentFactory Environments { get; }
        internal FakeInputFactory Inputs { get; }
        internal ScriptedInputSource Source { get; }
        internal UiActionService Service { get; }
        internal bool Ready { get; private set; }

        internal Harness()
        {
            Environments = new FakeEnvironmentFactory(Environment);
            Source = new ScriptedInputSource(Cancellation);
            Inputs = new FakeInputFactory(Source);
            Service = new UiActionService(Environments, Inputs);
        }

        internal Task<UiActionRecordResult> RecordAsync(UiActionRecordOptions? options = null)
            => Service.RecordAsync(App, options ?? new UiActionRecordOptions(), Cancellation.Token, () =>
            {
                Assert.IsTrue(Source.Started);
                Ready = true;
            });

        internal Task<UiActionReplayResult> ReplayAsync(UiActionFlow flow, UiActionReplayOptions? options = null,
            UiTarget? target = null)
            => Service.ReplayAsync(target ?? App, flow, options ?? Accepted, Cancellation.Token);

        public void Dispose()
        {
            if (Source.DisposeCalls == 0)
            {
                Source.Dispose();
            }

            if (Environment.DisposeCalls == 0)
            {
                Environment.Dispose();
            }

            Cancellation.Dispose();
        }
    }

    private sealed class FakeEnvironmentFactory(FakeEnvironment environment) : IUiActionEnvironmentFactory
    {
        internal List<UiTarget> Targets { get; } = [];
        internal List<CancellationToken> Tokens { get; } = [];
        public IUiActionEnvironment Create(UiTarget target, CancellationToken cancellationToken = default)
        {
            environment.TrackAccess();
            Targets.Add(target);
            Tokens.Add(cancellationToken);
            return environment;
        }
    }

    private sealed class FakeInputFactory(ScriptedInputSource source) : IUiActionInputSourceFactory
    {
        internal List<(UiTarget Target, bool IncludeInjected)> Calls { get; } = [];
        public IUiActionInputSource Create(UiTarget target, bool includeInjected)
        {
            Calls.Add((target, includeInjected));
            return source;
        }
    }

    private sealed class FakeEnvironment : IUiActionEnvironment
    {
        private string? _selectedEditor;
        internal List<UiActionElement> Elements { get; } = [Element(Button), Element(Edit, true), Element(NextEdit, true)];
        internal UiActionElement? CurrentFocus { get; set; } = Element(Button);
        internal UiActionElement? PointerElement { get; set; } = Element(Button);
        internal Dictionary<string, string> Values { get; } = new() { ["editor"] = "private text", ["nextEditor"] = "other text" };
        internal List<UiActionTarget> ResolveCalls { get; } = [];
        internal List<UiActionTarget> FocusCalls { get; } = [];
        internal List<UiActionTarget> ValueCalls { get; } = [];
        internal List<(UiActionTarget Target, string Property)> PropertyCalls { get; } = [];
        internal List<IReadOnlyList<KeyAction>> SentKeys { get; } = [];
        internal List<(UiActionElement Element, double X, double Y, bool Right, bool Twice)> Clicks { get; } = [];
        internal List<(UiActionElement Element, double X, double Y, int Delta)> Wheels { get; } = [];
        internal List<(int Id, ApartmentState Apartment, bool Pool)> AccessThreads { get; } = [];
        internal List<(int X, int Y, bool ForScroll)> PointCalls { get; } = [];
        internal Func<UiActionTarget, UiActionElement?>? ResolveOverride { get; set; }
        internal Func<UiActionTarget, string, string?>? PropertyOverride { get; set; }
        internal Func<UiActionTarget, string>? ValueOverride { get; set; }
        internal bool DeliverTextInput { get; set; } = true;
        internal Action<IReadOnlyList<KeyAction>>? OnSend { get; set; }
        internal Action? OnClick { get; set; }
        internal Action? OnFocused { get; set; }
        internal int FocusedCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        public string ProcessName { get; set; } = "SampleApp";
        public bool TargetExists { get; set; } = true;
        public bool IsForeground { get; set; } = true;
        public bool HasInteractiveDesktop { get; set; } = true;

        internal void TrackAccess()
            => AccessThreads.Add((System.Environment.CurrentManagedThreadId,
                Thread.CurrentThread.GetApartmentState(), Thread.CurrentThread.IsThreadPoolThread));

        internal void Replace(UiActionElement replacement)
        {
            Elements.RemoveAll(e => e.Target == replacement.Target);
            Elements.Add(replacement);
            if (CurrentFocus?.Target == replacement.Target)
            {
                CurrentFocus = replacement;
            }

            if (PointerElement?.Target == replacement.Target)
            {
                PointerElement = replacement;
            }
        }

        public UiActionElement? Focused()
        {
            TrackAccess();
            FocusedCalls++;
            OnFocused?.Invoke();
            return CurrentFocus;
        }

        public UiActionElement? AtPoint(int x, int y, bool forScroll = false)
        {
            TrackAccess();
            PointCalls.Add((x, y, forScroll));
            return PointerElement;
        }

        public UiActionElement? Resolve(UiActionTarget target)
        {
            TrackAccess();
            ResolveCalls.Add(target);
            if (ResolveOverride is not null)
            {
                return ResolveOverride(target);
            }

            var matches = Elements.Where(e => e.Target.ControlType == target.ControlType
                && (target.AutomationId is not null ? e.Target.AutomationId == target.AutomationId : e.Target.Name == target.Name))
                .ToArray();
            if (matches.Length > 1)
            {
                throw new UiActionException("ambiguous_target", "Sensitive provider details must not escape.");
            }

            return matches.SingleOrDefault();
        }

        public string ReadValue(UiActionTarget target)
        {
            TrackAccess();
            ValueCalls.Add(target);
            var element = Resolve(target);
            if (element is null || element.IsPassword)
            {
                throw new UiActionException("sensitive_input", "Sensitive provider details must not escape.");
            }

            if (!element.IsEditable)
            {
                throw new UiActionException("readonly_target", "Value is not writable.");
            }

            return ValueOverride is null ? Values[target.AutomationId!] : ValueOverride(target);
        }

        public string? ReadProperty(UiActionTarget target, string property)
        {
            TrackAccess();
            PropertyCalls.Add((target, property));
            if (PropertyOverride is not null)
            {
                return PropertyOverride(target, property);
            }

            var element = Resolve(target)!;
            return property switch
            {
                "Name" => element.Target.Name ?? "Submit",
                "Value" => Values[target.AutomationId!],
                "IsEnabled" => element.IsEnabled.ToString(),
                "ToggleState" => "on",
                _ => null,
            };
        }

        public void Focus(UiActionTarget target)
        {
            TrackAccess();
            FocusCalls.Add(target);
            IsForeground = true;
            CurrentFocus = Resolve(target);
        }

        public void Click(UiActionElement element, double xFraction, double yFraction, bool right, bool twice)
        {
            TrackAccess();
            Clicks.Add((element, xFraction, yFraction, right, twice));
            OnClick?.Invoke();
        }

        public void Wheel(UiActionElement element, double xFraction, double yFraction, int delta)
        {
            TrackAccess();
            Wheels.Add((element, xFraction, yFraction, delta));
        }

        public void SendKeys(IReadOnlyList<KeyAction> keys)
        {
            TrackAccess();
            SentKeys.Add(keys.ToArray());
            if (DeliverTextInput && CurrentFocus is { IsEditable: true, IsPassword: false } focus
                && focus.Target.AutomationId is { } id)
            {
                foreach (var key in keys)
                {
                    if (key is KeyChord { Vk: 0x41, Modifiers.Count: 1 } select && select.Modifiers[0] == 0x11)
                    {
                        _selectedEditor = id;
                    }
                    else if (key is TextInput text)
                    {
                        Values[id] = (_selectedEditor == id ? "" : Values[id]) + text.Text;
                        _selectedEditor = null;
                    }
                    else if (key is KeyChord { Vk: 0x2E, Modifiers.Count: 0 } && _selectedEditor == id)
                    {
                        Values[id] = "";
                        _selectedEditor = null;
                    }
                }
            }

            OnSend?.Invoke(keys);
        }

        public void Dispose()
        {
            TrackAccess();
            DisposeCalls++;
        }
    }

    private sealed class ScriptedInputSource(CancellationTokenSource cancellation) : IUiActionInputSource
    {
        internal ScriptedReader Events { get; } = new(cancellation);
        internal Action? OnStart { get; set; }
        internal bool Started { get; private set; }
        internal int DisposeCalls { get; private set; }
        public ChannelReader<UiActionInput> Reader => Events;
        internal void Add(params UiActionInput[] inputs) => Events.Batches.Enqueue((null, inputs));
        internal void Add(Action before, params UiActionInput[] inputs) => Events.Batches.Enqueue((before, inputs));
        public void Start()
        {
            Started = true;
            Events.Clock.Restart();
            OnStart?.Invoke();
        }

        public void Dispose()
        {
            DisposeCalls++;
            Events.Channel.Writer.TryComplete();
        }
    }

    private sealed class ScriptedReader(CancellationTokenSource cancellation) : ChannelReader<UiActionInput>
    {
        internal Channel<UiActionInput> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<UiActionInput>(32);
        internal Queue<(Action? Before, UiActionInput[] Inputs)> Batches { get; } = new();
        internal Stopwatch Clock { get; } = new();
        internal Action? BeforeEnd { get; set; }
        internal Func<UiActionInput, long>? ObservationTimestamp { get; set; }
        internal bool CloseInsteadOfCancel { get; set; }
        internal bool WaitInsteadOfCancel { get; set; }
        public override Task Completion => Channel.Reader.Completion;
        public override bool CanCount => true;
        public override int Count => Channel.Reader.Count;
        public override bool TryPeek(out UiActionInput item) => Channel.Reader.TryPeek(out item!);
        public override bool TryRead(out UiActionInput item) => Channel.Reader.TryRead(out item!);
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Count > 0)
            {
                return ValueTask.FromResult(true);
            }

            if (Batches.TryDequeue(out var batch))
            {
                batch.Before?.Invoke();
                foreach (var input in batch.Inputs)
                {
                    Assert.IsTrue(Channel.Writer.TryWrite(input with
                    {
                        ElapsedMs = input.ElapsedMs == 0 ? Clock.ElapsedMilliseconds : input.ElapsedMs,
                        ObservedTimestamp = ObservationTimestamp?.Invoke(input) ?? Stopwatch.GetTimestamp(),
                    }));
                }

                return ValueTask.FromResult(true);
            }

            BeforeEnd?.Invoke();
            if (CloseInsteadOfCancel)
            {
                Channel.Writer.TryComplete();
                return ValueTask.FromResult(false);
            }

            if (WaitInsteadOfCancel)
            {
                return Channel.Reader.WaitToReadAsync(cancellationToken);
            }

            cancellation.Cancel();
            return ValueTask.FromCanceled<bool>(cancellation.Token);
        }
    }
}
