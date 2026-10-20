// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed class NativeUiActionEnvironmentFactory(IKeyboardInput keyboard, IMouseInput mouse) : IUiActionEnvironmentFactory
{
    public IUiActionEnvironment Create(UiTarget target, CancellationToken cancellationToken = default)
        => new NativeUiActionEnvironment(target, keyboard, mouse, cancellationToken);
}

internal sealed class NativeUiActionEnvironment : IUiActionEnvironment
{
    private readonly NativeUiActionScope _scope;
    private readonly IKeyboardInput _keyboard;
    private readonly IMouseInput _mouse;
    private readonly CancellationToken _cancellationToken;
    private IUIAutomation? _automation;
    private IUIAutomationElement? _root;
    private IUIAutomationTreeWalker? _walker;
    private UiActionTarget? _lastObservedFocus;
    private bool _disposed;

    public NativeUiActionEnvironment(UiTarget target, IKeyboardInput keyboard, IMouseInput mouse,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _scope = new NativeUiActionScope(target);
        _keyboard = keyboard;
        _mouse = mouse;
        _cancellationToken = cancellationToken;
        try
        {
            _automation = CUIAutomation8.CreateInstance<IUIAutomation>();
            _root = _automation.ElementFromHandle(_scope.Hwnd);
            _walker = _automation.get_RawViewWalker();
            if (_root is null || _root.get_CurrentProcessId() != _scope.ProcessId)
            {
                throw new UiActionException("target_out_of_scope", "UI Automation could not verify the selected window.");
            }
        }
        catch (UiActionException)
        {
            Dispose();
            throw;
        }
        catch
        {
            Dispose();
            throw new UiActionException("uia_unavailable", "UI Automation could not open the selected window.");
        }
    }

    public string ProcessName => _scope.ProcessName;
    public bool TargetExists => !_disposed && _scope.TargetExists;
    public bool IsForeground => TargetExists && _scope.IsForeground;
    public bool HasInteractiveDesktop => !_disposed && NativeUiActionScope.HasInteractiveDesktop();

    public UiActionElement? Focused()
    {
        var result = WithAutomation(owned =>
        {
            if (!_scope.IsForeground)
            {
                return null;
            }

            var element = owned.Add(_automation!.GetFocusedElement());
            var path = ScopedPath(element, owned);
            // The service resolves uniqueness at action boundaries; a subtree search per
            // typed character can otherwise make the recorder lag behind normal input.
            return path is null ? null : Describe(element!, path, owned, verifyUnique: false);
        });
        _lastObservedFocus = result?.Target;
        return result;
    }

    public UiActionElement? AtPoint(int x, int y, bool forScroll = false) => WithAutomation(owned =>
    {
        if (!_scope.IsForeground || !_scope.OwnsPoint(x, y))
        {
            return null;
        }

        var element = owned.Add(_automation!.ElementFromPoint(new System.Drawing.Point(x, y)));
        var path = ScopedPath(element, owned);
        if (path is null)
        {
            return null;
        }

        // Inspect privacy on the original hit, including all its hosts, before promoting a
        // decorative child to an actionable parent. A password child must not become a button.
        if (IsPassword(path))
        {
            return Describe(element!, path, owned, password: true);
        }

        var index = NativeUiActionRules.FindPointerAncestor(path, forScroll,
            static candidate => PatternAvailable(candidate, UIA_PROPERTY_ID.UIA_IsScrollPatternAvailablePropertyId),
            IsInteractive);
        if (index >= 0)
        {
            var candidate = path[index];
            return Describe(candidate, path.GetRange(index, path.Count - index), owned);
        }

        throw new UiActionException("unstable_target", "The pointer is not over a supported, identifiable control.");
    });

    public UiActionElement? Resolve(UiActionTarget target) => WithAutomation(owned =>
    {
        var element = Find(target, owned);
        if (element is null)
        {
            return null;
        }

        var resolved = Describe(element, RequirePath(element, owned), owned,
            verifyUnique: target.AutomationId is null);
        NativeUiActionRules.RequireResolvedId(target, resolved.Target);
        return resolved;
    });

    public string ReadValue(UiActionTarget target) => WithAutomation(owned =>
    {
        var element = RequireElement(target, owned);
        return ReadValue(element, RequirePath(element, owned), target, owned);
    });

    public string? ReadProperty(UiActionTarget target, string property) => WithAutomation<string?>(owned =>
    {
        if (property is not ("Name" or "Value" or "IsEnabled" or "ToggleState"))
        {
            throw new UiActionException("unsupported_property", "That assertion property is not supported.");
        }

        var element = RequireElement(target, owned);
        var path = RequirePath(element, owned);
        RejectPassword(path);
        return property switch
        {
            "Name" => ReadString(element.get_CurrentName(), NativeUiActionRules.MaxStringLength),
            "Value" => ReadValue(element, path, target, owned),
            "IsEnabled" => ((bool)element.get_CurrentIsEnabled()).ToString(),
            "ToggleState" => ReadToggle(element, owned),
            _ => null,
        };
    });

    public void Focus(UiActionTarget target) => WithAutomation(owned =>
    {
        RequireDesktop();
        var element = RequireElement(target, owned);
        var description = Describe(element, RequirePath(element, owned), owned);
        NativeUiActionRules.RequireUsable(description);
        _cancellationToken.ThrowIfCancellationRequested();
        PInvoke.SetForegroundWindow(_scope.Hwnd);
        RequireForeground();
        _cancellationToken.ThrowIfCancellationRequested();
        element.SetFocus();
        RequireForeground();
        var focused = owned.Add(_automation!.GetFocusedElement());
        if (focused is null || !_automation.CompareElements(element, focused))
        {
            throw new UiActionException("focus_not_target", "The selected control did not receive keyboard focus.");
        }

        RejectPassword(RequirePath(focused, owned));
        _lastObservedFocus = description.Target;
        return true;
    });

    public void Click(UiActionElement element, double xFraction, double yFraction, bool right, bool twice)
    {
        var (point, bounds) = PreparePointer(element.Target, xFraction, yFraction);
        Deliver(() => _mouse.Click(point.X, point.Y, rightClick: right, settleMs: 0));
        if (twice)
        {
            // IMouseInput's built-in double click has an unguarded inter-click delay.
            Thread.Sleep(50);
            ConfirmPointer(element.Target, point, bounds);
            Deliver(() => _mouse.Click(point.X, point.Y, rightClick: right, settleMs: 0));
        }
    }

    public void Wheel(UiActionElement element, double xFraction, double yFraction, int delta)
    {
        var (point, _) = PreparePointer(element.Target, xFraction, yFraction, forScroll: true);
        Deliver(() => _mouse.ScrollWheel(point.X, point.Y, delta, settleMs: 0));
    }

    public void SendKeys(IReadOnlyList<KeyAction> keys) => WithAutomation(owned =>
    {
        RequireForeground();
        var focused = owned.Add(_automation!.GetFocusedElement());
        var anchor = Describe(focused ?? throw MissingFocus(), RequirePath(focused, owned), owned);
        NativeUiActionRules.RequireUsable(anchor);
        if (_lastObservedFocus is not null && anchor.Target != _lastObservedFocus)
        {
            throw MissingFocus();
        }

        foreach (var action in keys)
        {
            if (action is TextInput text)
            {
                if (text.Text.Length > NativeUiActionRules.MaxStringLength)
                {
                    throw new UiActionException("value_too_long", "The text exceeds the supported length.");
                }

                for (var offset = 0; offset < text.Text.Length;)
                {
                    var count = NativeUiActionRules.TextChunkLength(text.Text, offset);
                    ConfirmKeyboardTarget(focused, anchor.Target, requireEditable: true);
                    Deliver(() => _keyboard.Send(_scope.WindowHandle,
                        [new TextInput(text.Text.Substring(offset, count))], KeyTransport.SendInput));
                    offset += count;
                }
            }
            else if (action is KeyChord)
            {
                ConfirmKeyboardTarget(focused, anchor.Target, requireEditable: false);
                Deliver(() => _keyboard.Send(_scope.WindowHandle, [action], KeyTransport.SendInput));
            }
            else
            {
                throw new UiActionException("unsupported_input", "That keyboard action is not supported.");
            }
        }

        return true;
    });

    private void ConfirmKeyboardTarget(IUIAutomationElement original, UiActionTarget target, bool requireEditable)
        => WithAutomation(owned =>
        {
            RequireForeground();
            var current = owned.Add(_automation!.GetFocusedElement());
            if (current is null || !_automation.CompareElements(original, current))
            {
                throw MissingFocus();
            }

            var resolved = Find(target, owned);
            if (resolved is null || !_automation.CompareElements(current, resolved))
            {
                throw MissingFocus();
            }

            var description = Describe(current, RequirePath(current, owned), owned, target);
            NativeUiActionRules.RequireUsable(description);
            if (requireEditable && !description.IsEditable)
            {
                throw new UiActionException("unsupported_input", "Text input requires a writable UI Automation ValuePattern.");
            }

            return true;
        });

    private (PointerPoint Point, PointerRect Bounds) PreparePointer(UiActionTarget target, double xFraction, double yFraction,
        bool forScroll = false)
    {
        RequireForeground();
        var before = Resolve(target) ?? throw MissingElement();
        NativeUiActionRules.RequireUsable(before);
        var point = NativeUiActionRules.Point(before.Bounds, xFraction, yFraction);
        RequirePoint(point);
        Deliver(() => _mouse.MoveCursor(point.X, point.Y));
        Thread.Sleep(GestureTargeting.DefaultReadDelayMs);

        ConfirmPointer(target, point, before.Bounds, forScroll);
        return (point, before.Bounds);
    }

    private unsafe void ConfirmPointer(UiActionTarget target, PointerPoint point, PointerRect bounds, bool forScroll = false)
    {
        RequireForeground();
        var after = Resolve(target) ?? throw new UiActionException("target_moved", "The target disappeared after moving the pointer.");
        NativeUiActionRules.RequireUsable(after);
        NativeUiActionRules.RequireSameBounds(bounds, after.Bounds);
        RequirePoint(point);
        // A popup or child control may have appeared without changing the original bounds.
        var hit = AtPoint(point.X, point.Y, forScroll);
        if (hit is null || hit.Target != target)
        {
            throw new UiActionException("target_moved", "The target under the pointer changed before input could be delivered.");
        }

        RequireForeground();
        RequirePoint(point);
        System.Drawing.Point cursor;
        if (!PInvoke.GetCursorPos(&cursor) || cursor.X != point.X || cursor.Y != point.Y)
        {
            throw new UiActionException("target_moved", "The pointer moved or Windows could not position it over the target.");
        }
    }

    private void RequirePoint(PointerPoint point)
    {
        if (!_scope.WindowBounds.Contains(point) || !_scope.OwnsPoint(point.X, point.Y))
        {
            throw new UiActionException("target_occluded", "The action point is outside the selected window or is covered by another window.");
        }
    }

    private void Deliver(Action send)
    {
        RequireForeground();
        _cancellationToken.ThrowIfCancellationRequested();
        try
        {
            send();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UiActionException)
        {
            throw;
        }
        catch
        {
            RequireForeground();
            throw new UiActionException("input_failed", "Windows did not accept the requested input.");
        }
    }

    private UiActionElement Describe(IUIAutomationElement element, List<IUIAutomationElement> path,
        OwnedComObjects owned, UiActionTarget? target = null, bool password = false, bool verifyUnique = true)
    {
        password |= IsPassword(path);
        var type = NativeUiActionRules.ControlTypeName((int)element.get_CurrentControlType());
        var value = password ? null : GetValuePattern(element, owned);
        var editable = value is not null && !value.get_CurrentIsReadOnly();
        if (editable)
        {
            RejectComposition(element, owned);
        }

        var textNameUnsafe = value is not null || (!password && PatternAvailable(element, UIA_PROPERTY_ID.UIA_IsTextPatternAvailablePropertyId));
        if (target is null)
        {
            var id = ReadString(element.get_CurrentAutomationId(), NativeUiActionRules.MaxLocatorLength);
            target = NativeUiActionRules.CreateLocator(id, type, password, textNameUnsafe,
                () => ReadString(element.get_CurrentName(), NativeUiActionRules.MaxLocatorLength));

            if (verifyUnique)
            {
                var unique = Find(target, owned);
                if (unique is null || !_automation!.CompareElements(element, unique))
                {
                    throw new UiActionException("unstable_target", "The control's locator changed while it was being identified.");
                }
            }
        }
        else if (string.IsNullOrEmpty(target.AutomationId))
        {
            NativeUiActionRules.RequireNameFallback(password, textNameUnsafe, type);
        }

        if (target.ControlType != type)
        {
            throw new UiActionException("target_moved", "The control type changed before the action could be completed.");
        }

        var rectangle = element.get_CurrentBoundingRectangle();
        return new UiActionElement(target,
            new PointerRect(rectangle.left, rectangle.top, rectangle.right, rectangle.bottom),
            password, editable, element.get_CurrentIsEnabled(), element.get_CurrentIsOffscreen());
    }

    private IUIAutomationElement? Find(UiActionTarget target, OwnedComObjects owned)
    {
        var type = NativeUiActionRules.ValidateLocator(target);
        using var locatorValue = ComVariant.Create(target.AutomationId ?? target.Name!);
        var locator = owned.Add(_automation!.CreatePropertyCondition(
            target.AutomationId is not null ? UIA_PROPERTY_ID.UIA_AutomationIdPropertyId : UIA_PROPERTY_ID.UIA_NamePropertyId,
            locatorValue))!;
        IUIAutomationCondition condition = locator;
        if (target.AutomationId is null)
        {
            using var typeValue = ComVariant.Create(type);
            var typeCondition = owned.Add(_automation.CreatePropertyCondition(UIA_PROPERTY_ID.UIA_ControlTypePropertyId, typeValue))!;
            condition = owned.Add(_automation.CreateAndCondition(locator, typeCondition))!;
        }

        var matches = owned.Add(_root!.FindAll(TreeScope.TreeScope_Subtree, condition));
        var count = matches?.get_Length() ?? 0;
        if (count > 1)
        {
            throw new UiActionException("ambiguous_target", "More than one control matches the locator in the selected window.");
        }

        if (count == 0)
        {
            return null;
        }

        var element = owned.Add(matches!.GetElement(0))!;
        RequirePath(element, owned);
        return (int)element.get_CurrentControlType() == type ? element : null;
    }

    private IUIAutomationElement RequireElement(UiActionTarget target, OwnedComObjects owned)
        => Find(target, owned) ?? throw MissingElement();

    private List<IUIAutomationElement> RequirePath(IUIAutomationElement? element, OwnedComObjects owned)
        => ScopedPath(element, owned) ??
            throw new UiActionException("target_out_of_scope", "The control no longer belongs to the selected window.");

    private List<IUIAutomationElement>? ScopedPath(IUIAutomationElement? element, OwnedComObjects owned)
    {
        var path = new List<IUIAutomationElement>();
        for (var current = element; current is not null && path.Count < 128;
            current = owned.Add(_walker!.GetParentElement(current)))
        {
            // No names, values, IDs or other content are read until the whole ancestry is verified.
            if (current.get_CurrentProcessId() != _scope.ProcessId)
            {
                return null;
            }

            var hwnd = current.get_CurrentNativeWindowHandle();
            if (!hwnd.IsNull && !_scope.OwnsWindow(hwnd))
            {
                return null;
            }

            path.Add(current);
            if (_automation!.CompareElements(current, _root!))
            {
                return path;
            }
        }

        return null;
    }

    private static bool IsPassword(List<IUIAutomationElement> path)
    {
        try
        {
            var password = false;
            foreach (var element in path)
            {
                var direct = (bool)element.get_CurrentIsPassword();
                if (direct)
                {
                    password = true;
                    continue;
                }

                // A provider's "not supported" sentinel must not inherit UIA's default false
                // password value. Only an explicit boolean allows subsequent content reads.
                using var property = element.GetCurrentPropertyValueEx(UIA_PROPERTY_ID.UIA_IsPasswordPropertyId, true);
                NativeUiActionRules.RequireKnownPrivacy(property.VarType);
                password |= property.As<bool>();
            }

            return password;
        }
        catch
        {
            throw new UiActionException("privacy_unavailable", "The control's password status could not be verified. Capture or replay has stopped.");
        }
    }

    private static void RejectPassword(List<IUIAutomationElement> path)
    {
        if (IsPassword(path))
        {
            throw new UiActionException("password_target", "Password controls are excluded from action recording and replay.");
        }
    }

    private static bool PatternAvailable(IUIAutomationElement element, UIA_PROPERTY_ID property)
    {
        using var value = element.GetCurrentPropertyValue(property);
        if (value.VarType != VarEnum.VT_BOOL)
        {
            throw new UiActionException("uia_unavailable", "The control's supported input patterns could not be verified.");
        }

        return value.As<bool>();
    }

    private static IUIAutomationValuePattern? GetValuePattern(IUIAutomationElement element, OwnedComObjects owned)
    {
        if (!PatternAvailable(element, UIA_PROPERTY_ID.UIA_IsValuePatternAvailablePropertyId))
        {
            return null;
        }

        return owned.Add(element.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId)) as IUIAutomationValuePattern
            ?? throw new UiActionException("unsupported_input", "The control did not provide its advertised ValuePattern.");
    }

    private void RejectComposition(IUIAutomationElement element, OwnedComObjects owned)
    {
        if (!PatternAvailable(element, UIA_PROPERTY_ID.UIA_IsTextEditPatternAvailablePropertyId))
        {
            var hwnd = element.get_CurrentNativeWindowHandle();
            _scope.RequireNonComposingInputLayout(hwnd.IsNull ? _scope.Hwnd : hwnd);
            return;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10) ||
            owned.Add(element.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextEditPatternId)) is not IUIAutomationTextEditPattern pattern ||
            owned.Add(pattern.GetActiveComposition()) is not null)
        {
            throw new UiActionException("unsupported_input", "Input-method composition is not supported. Finish the composition before recording or replaying.");
        }
    }

    private static bool IsInteractive(IUIAutomationElement element)
    {
        var type = (int)element.get_CurrentControlType();
        return NativeUiActionRules.IsInteractiveControlType(type)
            || PatternAvailable(element, UIA_PROPERTY_ID.UIA_IsValuePatternAvailablePropertyId)
            || PatternAvailable(element, UIA_PROPERTY_ID.UIA_IsInvokePatternAvailablePropertyId)
            || PatternAvailable(element, UIA_PROPERTY_ID.UIA_IsTogglePatternAvailablePropertyId)
            || PatternAvailable(element, UIA_PROPERTY_ID.UIA_IsSelectionItemPatternAvailablePropertyId)
            || PatternAvailable(element, UIA_PROPERTY_ID.UIA_IsScrollPatternAvailablePropertyId);
    }

    private string ReadValue(IUIAutomationElement element, List<IUIAutomationElement> path,
        UiActionTarget target, OwnedComObjects owned)
    {
        RejectPassword(path);
        RejectComposition(element, owned);
        if (string.IsNullOrEmpty(target.AutomationId))
        {
            throw new UiActionException("unstable_target", "Reading an edit value requires a unique AutomationId.");
        }

        var pattern = GetValuePattern(element, owned);
        if (pattern is null || pattern.get_CurrentIsReadOnly())
        {
            throw new UiActionException("unsupported_input", "Text capture requires a writable UI Automation ValuePattern; composition and read-only rich text are not supported.");
        }

        RejectPassword(path);
        return ReadString(pattern.get_CurrentValue(), NativeUiActionRules.MaxStringLength);
    }

    private static string ReadToggle(IUIAutomationElement element, OwnedComObjects owned)
    {
        if (!PatternAvailable(element, UIA_PROPERTY_ID.UIA_IsTogglePatternAvailablePropertyId) ||
            owned.Add(element.GetCurrentPattern(UIA_PATTERN_ID.UIA_TogglePatternId)) is not IUIAutomationTogglePattern pattern)
        {
            throw new UiActionException("unsupported_property", "The selected control does not support ToggleState.");
        }

        return pattern.get_CurrentToggleState() switch
        {
            ToggleState.ToggleState_On => "on",
            ToggleState.ToggleState_Off => "off",
            ToggleState.ToggleState_Indeterminate => "indeterminate",
            _ => throw new UiActionException("unsupported_property", "The control returned an unknown ToggleState."),
        };
    }

    private static string ReadString(BSTR value, int maximum)
    {
        try
        {
            if (value.Length > maximum)
            {
                throw new UiActionException("value_too_long", "The control's property exceeds the supported length.");
            }

            return value.ToString() ?? "";
        }
        finally
        {
            PInvoke.SysFreeString(value);
        }
    }

    private T WithAutomation<T>(Func<OwnedComObjects, T> operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cancellationToken.ThrowIfCancellationRequested();
        if (!_scope.TargetExists)
        {
            throw new UiActionException("target_closed", "The selected window is no longer available.");
        }

        using var owned = new OwnedComObjects(_automation, _root, _walker);
        try
        {
            return operation(owned);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UiActionException)
        {
            throw;
        }
        catch
        {
            throw new UiActionException("uia_unavailable", "UI Automation could not safely inspect the selected control.");
        }
    }

    private void RequireDesktop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_scope.TargetExists)
        {
            throw new UiActionException("target_closed", "The selected window is no longer available.");
        }

        if (!HasInteractiveDesktop)
        {
            throw new UiActionException("no_interactive_desktop", "An unlocked, interactive Windows desktop is required.");
        }
    }

    private void RequireForeground()
    {
        RequireDesktop();
        if (ForegroundGuard.CheckForeground(_scope.WindowHandle) != ForegroundCheck.Proceed || !_scope.IsForeground)
        {
            throw new UiActionException("foreground_not_target", "The selected window must remain in the foreground. No input was sent.");
        }
    }

    private static UiActionException MissingElement() => new("element_not_found", "The control was not found in the selected window.");
    private static UiActionException MissingFocus() => new("focus_not_target", "Keyboard focus changed before the input could be delivered.");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        OwnedComObjects.Release(_walker);
        OwnedComObjects.Release(_root);
        OwnedComObjects.Release(_automation);
        _walker = null;
        _root = null;
        _automation = null;
        _lastObservedFocus = null;
        _scope.Dispose();
    }

    private sealed class OwnedComObjects(params object?[] borrowed) : IDisposable
    {
        private readonly HashSet<object> _objects = new(ReferenceEqualityComparer.Instance);

        public T? Add<T>(T? value) where T : class
        {
            if (value is not null && !borrowed.Any(item => ReferenceEquals(item, value)))
            {
                _objects.Add(value);
            }

            return value;
        }

        public static void Release(object? value)
        {
            // FinalRelease is the source-generated wrapper API, not Marshal.ReleaseComObject.
            // Shared (non-unique) wrappers retain their normal GC-managed COM lifetime.
            if (value is ComObject wrapper)
            {
                wrapper.FinalRelease();
            }
            else if (value is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        public void Dispose()
        {
            foreach (var value in _objects)
            {
                Release(value);
            }
        }
    }
}

internal sealed class NativeUiActionScope : IDisposable
{
    private readonly Process _process;
    private readonly nint _processHandle;
    private readonly uint _windowThread;
    private bool _disposed;

    public HWND Hwnd { get; }
    public long WindowHandle => (nint)Hwnd;
    public int ProcessId { get; }
    public string ProcessName { get; }

    public unsafe NativeUiActionScope(UiTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Hwnd = new HWND(checked((nint)target.WindowHandle));
        uint pid = 0;
        _windowThread = PInvoke.GetWindowThreadProcessId(Hwnd, &pid);
        if (Hwnd.IsNull || _windowThread == 0 || pid == 0 ||
            PInvoke.GetAncestor(Hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) != Hwnd ||
            !PInvoke.IsWindowVisible(Hwnd) || (target.ProcessId != 0 && target.ProcessId != pid))
        {
            throw new UiActionException("invalid_target", "Select one visible top-level application window.");
        }

        ProcessId = checked((int)pid);
        try
        {
            _process = Process.GetProcessById(ProcessId);
        }
        catch
        {
            throw new UiActionException("target_unavailable", "Windows could not verify the selected application's identity.");
        }
        try
        {
            // Keep the original process object alive; a later process with a reused PID is not
            // an acceptable replacement for the process selected by the user.
            _processHandle = _process.Handle;
            ProcessName = _process.ProcessName;
            if (string.IsNullOrWhiteSpace(ProcessName) || ProcessName.Length > 260 || ProcessName.Any(char.IsControl) || !TargetExists)
            {
                throw new UiActionException("target_closed", "The selected application is no longer available.");
            }
        }
        catch
        {
            _process.Dispose();
            throw new UiActionException("target_unavailable", "Windows could not verify the selected application's identity.");
        }
    }

    public unsafe bool TargetExists
    {
        get
        {
            if (_disposed || PInvoke.WaitForSingleObject(new HANDLE(_processHandle), 0) != WAIT_EVENT.WAIT_TIMEOUT)
            {
                return false;
            }

            uint pid = 0;
            return PInvoke.GetWindowThreadProcessId(Hwnd, &pid) == _windowThread && pid == ProcessId
                && PInvoke.GetAncestor(Hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) == Hwnd
                && PInvoke.IsWindowVisible(Hwnd);
        }
    }

    public bool IsForeground => TargetExists && PInvoke.GetForegroundWindow() == Hwnd;

    public unsafe bool OwnsWindow(HWND hwnd)
    {
        uint pid = 0;
        return !hwnd.IsNull && PInvoke.GetWindowThreadProcessId(hwnd, &pid) != 0 && pid == ProcessId
            && PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) == Hwnd;
    }

    public bool OwnsPoint(int x, int y) => OwnsWindow(PInvoke.WindowFromPoint(new System.Drawing.Point(x, y)));

    public unsafe void RequireNonComposingInputLayout(HWND target)
    {
        if (!TargetExists || !OwnsWindow(target))
        {
            throw new UiActionException("target_out_of_scope", "The text control no longer belongs to the selected window.");
        }

        var targetThread = PInvoke.GetWindowThreadProcessId(target, null);
        if (targetThread == 0)
        {
            throw new UiActionException("target_closed", "The text control is no longer available.");
        }

        var layout = PInvoke.GetKeyboardLayout(targetThread);
        NativeUiActionRules.RequireCommittedNativeInput((nuint)(nint)layout);
    }

    public unsafe PointerRect WindowBounds
    {
        get
        {
            RECT rect;
            if (!TargetExists || !PInvoke.GetWindowRect(Hwnd, &rect))
            {
                throw new UiActionException("target_closed", "The selected window's bounds are no longer available.");
            }

            return new PointerRect(rect.left, rect.top, rect.right, rect.bottom);
        }
    }

    public static unsafe bool HasInteractiveDesktop()
    {
        if (PInvoke.GetForegroundWindow().IsNull)
        {
            return false;
        }

        var input = PInvoke.OpenInputDesktop(0, false, DESKTOP_ACCESS_FLAGS.DESKTOP_READOBJECTS);
        if (input.IsNull)
        {
            return false;
        }

        try
        {
            var current = PInvoke.GetThreadDesktop(PInvoke.GetCurrentThreadId());
            Span<char> inputName = stackalloc char[256];
            Span<char> currentName = stackalloc char[256];
            return ReadDesktopName(input, inputName, out var inputLength)
                && ReadDesktopName(current, currentName, out var currentLength)
                && inputName[..inputLength].SequenceEqual(currentName[..currentLength]);
        }
        finally
        {
            PInvoke.CloseDesktop(input);
        }
    }

    private static unsafe bool ReadDesktopName(HDESK desktop, Span<char> name, out int length)
    {
        uint bytes = 0;
        fixed (char* buffer = name)
        {
            var success = PInvoke.GetUserObjectInformation(new HANDLE((nint)desktop),
                USER_OBJECT_INFORMATION_INDEX.UOI_NAME, buffer, checked((uint)(name.Length * sizeof(char))), &bytes);
            length = (int)(bytes / sizeof(char));
            return success && length > 1 && length <= name.Length;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _process.Dispose();
        }
    }
}

internal static class NativeUiActionRules
{
    internal const int MaxStringLength = UiActionFlowDocument.MaximumTextLength;
    internal const int MaxLocatorLength = 512;
    private static readonly string[] ControlTypes =
    [
        "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image",
        "ListItem", "List", "Menu", "MenuBar", "MenuItem", "ProgressBar", "RadioButton",
        "ScrollBar", "Slider", "Spinner", "StatusBar", "Tab", "TabItem", "Text", "ToolBar",
        "ToolTip", "Tree", "TreeItem", "Custom", "Group", "Thumb", "DataGrid", "DataItem",
        "Document", "SplitButton", "Window", "Pane", "Header", "HeaderItem", "Table",
        "TitleBar", "Separator", "SemanticZoom", "AppBar",
    ];

    internal static string ControlTypeName(int id)
        => id is >= 50000 and <= 50040 ? ControlTypes[id - 50000]
            : throw new UiActionException("unstable_target", "The control type is not supported by action recording.");

    internal static UiActionTarget CreateLocator(string? automationId, string controlType, bool password,
        bool textNameUnsafe, Func<string> readName)
    {
        UiActionTarget target;
        if (!string.IsNullOrEmpty(automationId))
        {
            target = new UiActionTarget { AutomationId = automationId, ControlType = controlType };
        }
        else
        {
            RequireNameFallback(password, textNameUnsafe, controlType);
            target = new UiActionTarget { Name = readName(), ControlType = controlType };
        }

        ValidateLocator(target);
        return target;
    }

    internal static int ValidateLocator(UiActionTarget target)
    {
        var type = Array.IndexOf(ControlTypes, target.ControlType);
        var locator = target.AutomationId ?? target.Name;
        if (type < 0 || string.IsNullOrWhiteSpace(locator) || locator.Length > MaxLocatorLength ||
            locator.Any(char.IsControl) || (target.AutomationId is not null && target.Name is not null))
        {
            throw new UiActionException("unstable_target", "Use a unique AutomationId or an exact name and control type.");
        }

        if (target.AutomationId is null)
        {
            RequireNameFallback(false, false, target.ControlType);
        }

        return type + 50000;
    }

    internal static void RequireResolvedId(UiActionTarget requested, UiActionTarget actual)
    {
        if (requested.AutomationId is not null && requested != actual)
        {
            throw new UiActionException("unstable_target", "The control's identifier changed during the uniqueness check.");
        }
    }

    internal static bool IsInteractiveControlType(int type)
        => type is 50000 or 50002 or 50003 or 50004 or 50005 or 50007 or 50008 or 50011
            or 50013 or 50014 or 50015 or 50016 or 50019 or 50023 or 50024 or 50028 or 50029 or 50031;

    internal static int FindPointerAncestor<T>(IReadOnlyList<T> scopedPath, bool forScroll,
        Predicate<T> supportsScrollPattern, Predicate<T> isInteractive)
    {
        var matches = forScroll ? supportsScrollPattern : isInteractive;
        for (var index = 0; index < scopedPath.Count; index++)
        {
            if (matches(scopedPath[index]))
            {
                return index;
            }
        }

        return -1;
    }

    internal static void RequireNameFallback(bool password, bool editable, string controlType)
    {
        if (password)
        {
            throw new UiActionException("password_target", "Password controls are excluded from action recording and replay.");
        }

        if (editable || controlType is "Edit" or "TextBox" or "Document" or "ComboBox")
        {
            throw new UiActionException("unstable_target", "Text controls require a unique AutomationId; their accessible names are not recorded.");
        }
    }

    internal static void RequireKnownPrivacy(VarEnum valueType)
    {
        if (valueType != VarEnum.VT_BOOL)
        {
            throw new UiActionException("privacy_unavailable", "The control's password status could not be verified. Capture or replay has stopped.");
        }
    }

    internal static void RequireCommittedNativeInput(nuint layout)
    {
        // ImmIsIME can report true for a loaded US keyboard on TSF systems. Without
        // provider composition state, accept only the directly validated standard US layout.
        if (layout != 0x04090409)
        {
            throw new UiActionException("unsupported_input",
                "This text control does not expose composition state. The first version supports only the standard US English keyboard profile for this control; select that profile for the app before recording or replaying.");
        }
    }

    internal static void RequireUsable(UiActionElement element)
    {
        if (element.IsPassword)
        {
            throw new UiActionException("password_target", "Password controls are excluded from action recording and replay.");
        }

        if (!element.IsEnabled || element.IsOffscreen || element.Bounds.Right <= element.Bounds.Left || element.Bounds.Bottom <= element.Bounds.Top)
        {
            throw new UiActionException("target_not_ready", "The control must be enabled, on screen, and have positive bounds.");
        }
    }

    internal static PointerPoint Point(PointerRect bounds, double xFraction, double yFraction)
    {
        if (!double.IsFinite(xFraction) || !double.IsFinite(yFraction) ||
            xFraction is < 0 or > 1 || yFraction is < 0 or > 1 || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            throw new UiActionException("invalid_coordinates", "Action coordinates must be fractions inside a nonempty control.");
        }

        var x = (int)Math.Min((double)bounds.Right - 1, bounds.Left + Math.Floor(((double)bounds.Right - bounds.Left) * xFraction));
        var y = (int)Math.Min((double)bounds.Bottom - 1, bounds.Top + Math.Floor(((double)bounds.Bottom - bounds.Top) * yFraction));
        return new PointerPoint(x, y);
    }

    internal static void RequireSameBounds(PointerRect before, PointerRect after)
    {
        if (before != after)
        {
            throw new UiActionException("target_moved", "The target moved or resized after moving the pointer. No input was sent.");
        }
    }

    internal static int TextChunkLength(string text, int offset)
    {
        var count = Math.Min(KeyboardInput.DefaultTextChunkChars, text.Length - offset);
        if (offset + count < text.Length &&
            ((char.IsHighSurrogate(text[offset + count - 1]) && char.IsLowSurrogate(text[offset + count])) ||
             (text[offset + count - 1] == '\r' && text[offset + count] == '\n')))
        {
            count--;
        }

        return count;
    }
}
