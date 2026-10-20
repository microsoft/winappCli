// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed class NativeUiAuditObservationFactory(IKeyboardInput keyboard) : IUiAuditObservationEnvironmentFactory
{
    public IUiAuditObservationEnvironment Create(UiTarget target, UiAuditObservationBudget budget, UiAuditEventBuffer events)
        => new NativeUiAuditObservation(target, keyboard, budget, events);
}

internal sealed class NativeUiAuditObservation : IUiAuditObservationEnvironment
{
    private const int MaximumAncestry = 64;
    private const int CallbackBudgetMs = 250;
    private static readonly IReadOnlyList<KeyAction> ForwardTab = [new KeyChord([], 0x09, false)];
    private static readonly IReadOnlyList<KeyAction> ReverseTab = [new KeyChord([0x10], 0x09, false)];
    private readonly IKeyboardInput _keyboard;
    private readonly UiAuditObservationBudget _budget;
    private readonly UiAuditEventBuffer _events;
    private readonly object _callbackGate = new();
    private readonly UiAuditEventSubscriptions _subscriptions;
    private NativeUiAuditObservationScope? _scope;
    private NativeUiAuditObservationCallContext? _context;
    private IUIAutomation? _automation;
    private IUIAutomationElement? _root;
    private IUIAutomationTreeWalker? _walker;
    private IUIAutomationCacheRequest? _cache;
    private NativeUiAuditObservationEventSink? _sink;
    private bool _closed;
    private bool _probeArmed;

    internal NativeUiAuditObservation(UiTarget target, IKeyboardInput keyboard,
        UiAuditObservationBudget budget, UiAuditEventBuffer events)
    {
        _keyboard = keyboard;
        _budget = budget;
        _events = events;
        _subscriptions = new UiAuditEventSubscriptions(events);
        try
        {
            budget.Check();
            _scope = new NativeUiAuditObservationScope(target);
            _context = new NativeUiAuditObservationCallContext(budget.Token);
            _automation = CUIAutomation8.CreateInstance<IUIAutomation>();
            if (!OperatingSystem.IsWindowsVersionAtLeast(8) || _automation is not IUIAutomation2 timeouts)
            {
                throw new UiAuditObservationException("uia_timeouts_unavailable",
                    "This UI Automation client cannot bound provider calls; observation did not start.");
            }
            timeouts.put_ConnectionTimeout(250);
            timeouts.put_TransactionTimeout(250);
            budget.Check();
            _root = _automation.ElementFromHandle(_scope.Hwnd);
            _walker = _automation.get_RawViewWalker();
            if (_root is null || _root.get_CurrentProcessId() != _scope.ProcessId)
            {
                throw new UiAuditObservationException("target_scope_unavailable",
                    "UI Automation could not verify the selected window's process.");
            }
            _cache = _automation.CreateCacheRequest();
            // Global focus callbacks must not pre-cache names or other foreign application content.
            _cache.AddProperty(UIA_PROPERTY_ID.UIA_ProcessIdPropertyId);
            _cache.AddProperty(UIA_PROPERTY_ID.UIA_NativeWindowHandlePropertyId);
            _sink = new NativeUiAuditObservationEventSink(this);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not UiAuditObservationException)
        {
            Dispose();
            budget.Check();
            throw new UiAuditObservationException("uia_unavailable",
                "UI Automation could not initialize bounded observation for this window. Check desktop and process access permissions.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int ProcessId => _scope!.ProcessId;
    public bool TargetExists => !Volatile.Read(ref _closed) && _scope is { TargetExists: true };
    public bool HasInteractiveDesktop => !Volatile.Read(ref _closed) && NativeUiAuditObservationScope.HasInteractiveDesktop();
    public bool IsForeground => TargetExists && _scope!.IsForeground
        && ForegroundGuard.CheckForeground(_scope.WindowHandle) == ForegroundCheck.Proceed;

    public unsafe void Subscribe()
    {
        RequireLive();
        _subscriptions.Add("focus",
            () => _automation!.AddFocusChangedEventHandler(_cache!, _sink!),
            () => _automation!.RemoveFocusChangedEventHandler(_sink!));
        RequireLive();
        _subscriptions.Add("property", () =>
        {
            UIA_PROPERTY_ID* properties = stackalloc UIA_PROPERTY_ID[]
            {
                UIA_PROPERTY_ID.UIA_HasKeyboardFocusPropertyId,
                UIA_PROPERTY_ID.UIA_IsKeyboardFocusablePropertyId,
                UIA_PROPERTY_ID.UIA_IsEnabledPropertyId,
                UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId,
                UIA_PROPERTY_ID.UIA_LiveSettingPropertyId,
            };
            _automation!.AddPropertyChangedEventHandlerNativeArray(_root!, TreeScope.TreeScope_Subtree,
                _cache!, _sink!, properties, 5);
        }, () => _automation!.RemovePropertyChangedEventHandler(_root!, _sink!));
        RequireLive();
        _subscriptions.Add("structure",
            () => _automation!.AddStructureChangedEventHandler(_root!, TreeScope.TreeScope_Subtree, _cache!, _sink!),
            () => _automation!.RemoveStructureChangedEventHandler(_root!, _sink!));
        RequireLive();
        _subscriptions.Add("live-region",
            () => _automation!.AddAutomationEventHandler(UIA_EVENT_ID.UIA_LiveRegionChangedEventId,
                _root!, TreeScope.TreeScope_Subtree, _cache!, _sink!),
            () => _automation!.RemoveAutomationEventHandler(UIA_EVENT_ID.UIA_LiveRegionChangedEventId, _root!, _sink!));
        RequireLive();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299) && _automation is IUIAutomation5 notifications)
        {
            _subscriptions.Add("notification",
                () =>
                {
                    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
                    {
                        throw new PlatformNotSupportedException();
                    }
                    notifications.AddNotificationEventHandler(_root!, TreeScope.TreeScope_Subtree, _cache!, _sink!);
                },
                () =>
                {
                    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
                    {
                        throw new PlatformNotSupportedException();
                    }
                    notifications.RemoveNotificationEventHandler(_root!, _sink!);
                });
        }
        else
        {
            _events.Issue("notification_events_unavailable",
                "Native notification event subscriptions are unavailable on this Windows/UI Automation version.");
        }
    }

    public void Activate()
    {
        RequireLive();
        RequireDesktop();
        _budget.Check();
        PInvoke.SetForegroundWindow(_scope!.Hwnd);
        RequireInputScope();
        Volatile.Write(ref _probeArmed, true);
    }

    public UiAuditFocusPoint ReadFocus()
    {
        RequireLive();
        var observed = _budget.UtcNow;
        using var owned = new OwnedObjects(_automation, _root, _walker, _cache);
        var element = owned.Add(_automation!.GetFocusedElement());
        var path = ScopedPath(element, owned);
        return path is null
            ? new UiAuditFocusPoint { ObservedAtUtc = observed, NameWithheld = true }
            : Describe(element!, path) with { ObservedAtUtc = observed };
    }

    public void SendTab(bool reverse)
    {
        RequireInputScope();
        using var owned = new OwnedObjects(_automation, _root, _walker, _cache);
        var focus = owned.Add(_automation!.GetFocusedElement());
        if (ScopedPath(focus, owned) is null)
        {
            throw new UiAuditObservationException("focus_left_target",
                "Keyboard focus left the selected window or its scope is uncertain. No further input was sent.");
        }

        RequireInputScope();
        // Held real modifiers could turn the authorized Tab into Alt+Tab, Ctrl+Tab, or a shortcut.
        foreach (var key in new ushort[] { 0x09, 0x10, 0x11, 0x12, 0x5B, 0x5C })
        {
            if ((PInvoke.GetAsyncKeyState((int)(VIRTUAL_KEY)key) & 0x8000) != 0)
            {
                throw new UiAuditObservationException("keys_held",
                    "Release Tab and modifier keys before starting a focus probe. No further input was sent.");
            }
        }
        RequireInputScope();
        try
        {
            _keyboard.Send(_scope!.WindowHandle, reverse ? ReverseTab : ForwardTab, KeyTransport.SendInput);
        }
        catch
        {
            throw new UiAuditObservationException("input_delivery_uncertain",
                "Windows did not confirm a complete Tab chord. No further input was sent; verify the target's current state.");
        }
    }

    private void RequireLive()
    {
        _budget.Check();
        if (_events.StopReason is { } reason)
        {
            throw new UiAuditObservationException(reason, "Observation stopped; no further input was sent.");
        }
        if (!TargetExists)
        {
            throw new UiAuditObservationException("target_closed", "The selected window or original process is no longer available.");
        }
    }

    private void RequireDesktop()
    {
        if (!HasInteractiveDesktop)
        {
            throw new UiAuditObservationException("no_interactive_desktop",
                "An unlocked interactive Windows desktop is required. This is an environment limitation.");
        }
    }

    private void RequireInputScope()
    {
        RequireLive();
        RequireDesktop();
        if (!IsForeground)
        {
            throw new UiAuditObservationException("foreground_not_target",
                "The selected window is not in the foreground; no further input was sent.");
        }
        _budget.Check();
    }

    private void CheckTime(long? callbackStart)
    {
        _budget.Check();
        if (callbackStart is { } start && Stopwatch.GetElapsedTime(start).TotalMilliseconds >= CallbackBudgetMs)
        {
            throw new UiAuditObservationException("event_processing_timeout",
                "A native event source could not be inspected within its callback budget.");
        }
    }

    private List<IUIAutomationElement>? ScopedPath(IUIAutomationElement? element, OwnedObjects owned,
        long? callbackStart = null)
    {
        var path = new List<IUIAutomationElement>();
        for (var current = element; current is not null && path.Count < MaximumAncestry;
            current = owned.Add(_walker!.GetParentElement(current)))
        {
            CheckTime(callbackStart);
            // Exact PID and complete selected-window ancestry precede every content/identity read.
            if (current.get_CurrentProcessId() != _scope!.ProcessId)
            {
                return null;
            }
            CheckTime(callbackStart);
            var hwnd = current.get_CurrentNativeWindowHandle();
            if (!hwnd.IsNull && !_scope.OwnsWindow(hwnd))
            {
                return null;
            }
            path.Add(current);
            CheckTime(callbackStart);
            if (_automation!.CompareElements(current, _root!))
            {
                return _scope.TargetExists ? path : null;
            }
        }
        if (path.Count == MaximumAncestry)
        {
            _events.Issue("scope_depth_limit", "An event or focus source exceeded the bounded ancestry check.");
        }
        return null;
    }

    private UiAuditFocusPoint Describe(IUIAutomationElement element, List<IUIAutomationElement> path,
        long? callbackStart = null)
    {
        bool? password = false;
        bool? textBearing = false;
        int? sourceType = null;
        foreach (var ancestor in path)
        {
            CheckTime(callbackStart);
            var ancestorPassword = ReadBoolean(ancestor, UIA_PROPERTY_ID.UIA_IsPasswordPropertyId, callbackStart);
            password = Combine(password, ancestorPassword);
            var type = ReadType(ancestor, callbackStart);
            sourceType ??= ReferenceEquals(ancestor, element) ? type : null;
            var value = ReadBoolean(ancestor, UIA_PROPERTY_ID.UIA_IsValuePatternAvailablePropertyId, callbackStart);
            var text = ReadBoolean(ancestor, UIA_PROPERTY_ID.UIA_IsTextPatternAvailablePropertyId, callbackStart);
            var textType = type.HasValue ? UiAuditObservationPrivacy.TextControlType(type.Value) : (bool?)null;
            textBearing = Combine(textBearing, Combine(textType, Combine(value, text)));
        }

        if (password is null || textBearing is null)
        {
            _events.Issue("privacy_unknown",
                "Some source privacy properties were unavailable; names and all notification text were withheld.",
                limitsCoverage: false);
        }
        var withheld = UiAuditObservationPrivacy.WithholdName(password, textBearing);
        var runtime = ReadRuntimeId(element, callbackStart);
        var id = ReadString(element.get_CurrentAutomationId, callbackStart);
        var name = UiAuditObservationPrivacy.ReadName(password, textBearing,
            () => ReadString(element.get_CurrentName, callbackStart));
        CheckTime(callbackStart);
        return new UiAuditFocusPoint
        {
            NodeId = _events.Identity(runtime),
            RuntimeId = runtime,
            AutomationId = string.IsNullOrEmpty(id) ? null : id,
            Name = name,
            NameWithheld = withheld,
            ControlType = sourceType.HasValue ? UiAuditObservationPrivacy.ControlType(sourceType.Value) : null,
            IsPassword = password,
            IsInTarget = true,
        };
    }

    private static bool? Combine(bool? left, bool? right)
        => left == true || right == true ? true : left is null || right is null ? null : false;

    private bool? ReadBoolean(IUIAutomationElement element, UIA_PROPERTY_ID propertyId, long? callbackStart)
    {
        CheckTime(callbackStart);
        try
        {
            using var value = element.GetCurrentPropertyValueEx(propertyId, true);
            return UiAuditObservationPrivacy.Boolean(value);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not UiAuditObservationException)
        {
            return null;
        }
    }

    private int? ReadType(IUIAutomationElement element, long? callbackStart)
    {
        CheckTime(callbackStart);
        try
        {
            return (int)element.get_CurrentControlType();
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not UiAuditObservationException)
        {
            _events.Issue("control_type_unavailable", "A verified source did not provide its control type.");
            return null;
        }
    }

    private string? ReadString(Func<BSTR> read, long? callbackStart)
    {
        CheckTime(callbackStart);
        try
        {
            var value = read();
            try
            {
                if (value.Length > UiAuditObservationPrivacy.MaximumStringLength)
                {
                    _events.Issue("metadata_length_limit", "An overlong source name or AutomationId was omitted.");
                    return null;
                }
                return value.ToString();
            }
            finally
            {
                PInvoke.SysFreeString(value);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not UiAuditObservationException)
        {
            _events.Issue("metadata_unavailable", "A verified source did not provide some optional metadata.");
            return null;
        }
    }

    private unsafe string? ReadRuntimeId(IUIAutomationElement element, long? callbackStart)
    {
        CheckTime(callbackStart);
        try
        {
            var runtime = element.GetRuntimeId();
            try
            {
                var result = ReadRuntimeId(runtime);
                if (result is null)
                {
                    _events.Issue("runtime_id_unavailable",
                        "Some sources have only observation-local identities; repeats cannot be confirmed for those sources.",
                        limitsCoverage: false);
                }
                return result;
            }
            finally
            {
                if (runtime != null)
                {
                    PInvoke.SafeArrayDestroy(runtime);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not UiAuditObservationException)
        {
            _events.Issue("runtime_id_unavailable",
                "Some sources have only observation-local identities; repeats cannot be confirmed for those sources.",
                limitsCoverage: false);
            return null;
        }
    }

    internal static unsafe string? ReadRuntimeId(SAFEARRAY* array)
    {
        if (array == null || array->cDims != 1 || array->cbElements != sizeof(int) || array->pvData == null ||
            array->rgsabound[0].cElements is 0 or > UiAuditObservationPrivacy.MaximumRuntimeIdLength)
        {
            return null;
        }
        return UiAuditObservationPrivacy.RuntimeId(new ReadOnlySpan<int>(array->pvData, (int)array->rgsabound[0].cElements));
    }

    internal unsafe void OnEvent(IUIAutomationElement sender, string kind, int? propertyId = null,
        int? structureChangeType = null, SAFEARRAY* changedRuntimeId = null,
        int? notificationKind = null, int? notificationProcessing = null)
    {
        if (Volatile.Read(ref _closed) || !_events.CanAccept)
        {
            return;
        }
        var received = _budget.UtcNow;
        if (!Monitor.TryEnter(_callbackGate))
        {
            _events.Issue("event_callbacks_dropped",
                "Concurrent native callbacks exceeded the bounded event-processing capacity; some events were not retained.");
            if (kind == "focus" && Volatile.Read(ref _probeArmed))
            {
                _events.Stop("focus_scope_uncertain", "A focus event could not be verified; no further input was sent.");
            }
            return;
        }
        try
        {
            if (Volatile.Read(ref _closed) || !_events.CanAccept)
            {
                return;
            }
            using var calls = new NativeUiAuditObservationCallContext(_budget.Token);
            var start = Stopwatch.GetTimestamp();
            using var owned = new OwnedObjects(_automation, _root, _walker, _cache, sender);
            var path = ScopedPath(sender, owned, start);
            if (path is null)
            {
                if (kind == "focus" && Volatile.Read(ref _probeArmed))
                {
                    _events.Stop("focus_left_target",
                        "Native focus moved outside the selected window or its scope became uncertain. No further input was sent.");
                }
                return;
            }
            var source = Describe(sender, path, start);
            CheckTime(start);
            _events.Add(new UiAuditEvent
            {
                ReceivedAtUtc = received,
                Kind = kind,
                NodeId = source.NodeId,
                RuntimeId = source.RuntimeId,
                AutomationId = source.AutomationId,
                Name = source.Name,
                NameWithheld = source.NameWithheld,
                ControlType = source.ControlType,
                PropertyId = propertyId,
                StructureChangeType = structureChangeType,
                ChangedRuntimeId = ReadRuntimeId(changedRuntimeId),
                NotificationKind = notificationKind,
                NotificationProcessing = notificationProcessing,
                DisplayStringWithheld = kind == "notification",
                ActivityIdWithheld = kind == "notification",
            });
        }
        catch
        {
            if (!_budget.Token.IsCancellationRequested && !_budget.Expired && !Volatile.Read(ref _closed))
            {
                _events.Issue("event_source_unavailable",
                    "A native callback source could not be safely inspected within its bounds; that event was not retained.");
                if (kind == "focus" && Volatile.Read(ref _probeArmed))
                {
                    _events.Stop("focus_scope_uncertain", "A focus event could not be verified; no further input was sent.");
                }
            }
        }
        finally
        {
            Monitor.Exit(_callbackGate);
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _closed))
        {
            return;
        }
        Volatile.Write(ref _closed, true);
        _subscriptions.Dispose();
        var drained = Monitor.TryEnter(_callbackGate, millisecondsTimeout: 500);
        try
        {
            if (drained)
            {
                OwnedObjects.Release(_cache);
                OwnedObjects.Release(_walker);
                OwnedObjects.Release(_root);
                OwnedObjects.Release(_automation);
                _scope?.Dispose();
                _cache = null;
                _walker = null;
                _root = null;
                _automation = null;
            }
            else
            {
                // Do not release a wrapper while a provider is still using it on a callback thread.
                _events.Issue("event_callbacks_pending",
                    "A native callback outlasted cleanup's wait. Capture is disabled; its resources remain alive until it returns.");
            }
            GC.KeepAlive(_sink);
        }
        finally
        {
            if (drained)
            {
                Monitor.Exit(_callbackGate);
            }
            _context?.Dispose();
            _context = null;
        }
    }

    private sealed class OwnedObjects(params object?[] borrowed) : IDisposable
    {
        private readonly HashSet<object> _owned = new(ReferenceEqualityComparer.Instance);

        internal T? Add<T>(T? value) where T : class
        {
            if (value is not null && !borrowed.Any(item => ReferenceEquals(item, value)))
            {
                _owned.Add(value);
            }
            return value;
        }

        internal static void Release(object? value)
        {
            if (value is ComObject wrapper)
            {
                wrapper.FinalRelease();
            }
        }

        public void Dispose()
        {
            foreach (var value in _owned)
            {
                Release(value);
            }
        }
    }
}
