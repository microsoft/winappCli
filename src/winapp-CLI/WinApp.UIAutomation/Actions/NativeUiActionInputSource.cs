// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed class NativeUiActionInputSourceFactory : IUiActionInputSourceFactory
{
    public IUiActionInputSource Create(UiTarget target, bool includeInjected)
        => new NativeUiActionInputSource(target, includeInjected);
}

internal sealed class NativeUiActionInputSource : IUiActionInputSource
{
    private const uint WmQuit = 0x0012;
    private const uint WmTimer = 0x0113;
    private const int StartupTimeoutMs = 10000;
    private const int ShutdownTimeoutMs = 5000;
    private readonly NativeUiActionScope _scope;
    private readonly bool _includeInjected;
    private readonly Channel<UiActionInput> _channel = Channel.CreateBounded<UiActionInput>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false,
    });
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Stopwatch _clock = new();
    private readonly NativeUiActionModifierState _modifiers = new();
    private readonly NativeUiActionMouseState _buttons = new();
    private readonly object _lifecycle = new();
    private Thread? _thread;
    private uint _threadId;
    private nint _keyboardHook;
    private nint _mouseHook;
    private UiActionException? _failure;
    private bool? _inside;
    private bool _interactiveDesktop;
    private volatile bool _stopRequested;
    private volatile bool _callbackFailure;
    private bool _disposed;

    // WH_*_LL callbacks execute on the installing thread. The thread's delegate roots this
    // instance; the static unmanaged entry points need neither dynamic thunks nor COM marshalling.
    [ThreadStatic]
    private static NativeUiActionInputSource? t_source;

    public NativeUiActionInputSource(UiTarget target, bool includeInjected)
    {
        _scope = new NativeUiActionScope(target);
        _includeInjected = includeInjected;
    }

    public ChannelReader<UiActionInput> Reader => _channel.Reader;

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null)
            {
                throw new UiActionException("hook_start_failed", "Action input capture can only be started once.");
            }

            _thread = new Thread(Run) { IsBackground = true, Name = "winapp UI action input" };
            _thread.SetApartmentState(ApartmentState.MTA);
            try
            {
                _thread.Start();
            }
            catch
            {
                _thread = null;
                Fail("hook_start_failed");
                throw _failure!;
            }
        }

        if (!_ready.Wait(StartupTimeoutMs))
        {
            Fail("hook_start_failed");
            StopThread();
        }

        if (_failure is { } failure)
        {
            StopThread();
            throw failure;
        }

        if (_stopRequested)
        {
            throw new UiActionException("hook_start_failed", "Action capture stopped before it became ready.");
        }
    }

    private unsafe void Run()
    {
        nuint timer = 0;
        try
        {
            t_source = this;
            _threadId = PInvoke.GetCurrentThreadId();
            PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
            _clock.Start();
            _interactiveDesktop = NativeUiActionScope.HasInteractiveDesktop();
            UpdateScope(Stopwatch.GetTimestamp());
            if (_stopRequested)
            {
                return;
            }

            var module = PInvoke.GetModuleHandle(default(PCWSTR));
            _keyboardHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &KeyboardHook, module, 0);
            if (_keyboardHook == 0)
            {
                Fail("hook_start_failed");
                return;
            }

            _mouseHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, &MouseHook, module, 0);
            if (_mouseHook == 0)
            {
                Fail("hook_start_failed");
                return;
            }

            timer = PInvoke.SetTimer(HWND.Null, 0, 100, null);
            if (timer == 0)
            {
                Fail("hook_start_failed");
                return;
            }

            _ready.Set();
            while (!_stopRequested)
            {
                var result = PInvoke.GetMessage(out var message, HWND.Null, 0, 0).Value;
                if (result <= 0)
                {
                    if (!_stopRequested)
                    {
                        Fail("hook_stopped");
                    }

                    break;
                }

                if (message.message == WmTimer && message.wParam.Value == timer)
                {
                    _interactiveDesktop = NativeUiActionScope.HasInteractiveDesktop();
                    UpdateScope(Stopwatch.GetTimestamp());
                }
            }
        }
        catch
        {
            Fail("hook_failed");
        }
        finally
        {
            if (_callbackFailure)
            {
                Fail("hook_failed");
            }

            if (timer != 0 && !PInvoke.KillTimer(HWND.Null, timer))
            {
                Fail("hook_stop_failed");
            }

            Unhook();
            t_source = null;
            _scope.Dispose();
            _channel.Writer.TryComplete(_failure);
            _ready.Set();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT KeyboardHook(int code, WPARAM message, LPARAM data)
    {
        var source = t_source;
        try
        {
            var observedTimestamp = Stopwatch.GetTimestamp();
            if (code >= 0 && source is not null && !source._stopRequested)
            {
                source.OnKeyboard((uint)message.Value, *(KBDLLHOOKSTRUCT*)data.Value, observedTimestamp);
            }
        }
        catch
        {
            source?.CallbackFailed();
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, code, message, data);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT MouseHook(int code, WPARAM message, LPARAM data)
    {
        var source = t_source;
        try
        {
            var observedTimestamp = Stopwatch.GetTimestamp();
            if (code >= 0 && source is not null && !source._stopRequested)
            {
                source.OnMouse((uint)message.Value, *(MSLLHOOKSTRUCT*)data.Value, observedTimestamp);
            }
        }
        catch
        {
            source?.CallbackFailed();
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, code, message, data);
    }

    private void CallbackFailed()
    {
        _callbackFailure = true;
        _stopRequested = true;
        try
        {
            Fail("hook_failed");
        }
        catch
        {
            // The message-pump finally block also observes the failure flag. No exception may
            // cross an unmanaged hook boundary, even if reporting the original failure fails.
        }
    }

    private void OnKeyboard(uint message, KBDLLHOOKSTRUCT data, long observedTimestamp)
    {
        if (!UpdateScope(observedTimestamp))
        {
            return;
        }

        if (!NativeUiActionInputRules.KnownKeyboardFlags((uint)data.flags))
        {
            Fail("unsupported_input");
            return;
        }

        if (!NativeUiActionInputRules.AcceptKeyboard((uint)data.flags, _includeInjected))
        {
            return;
        }

        var down = message is 0x0100 or 0x0104;
        if (!down && message is not (0x0101 or 0x0105))
        {
            Fail("unsupported_input");
            return;
        }

        if (down && (_buttons.HasActivePress || _buttons.HasPendingActivation))
        {
            Fail("unsupported_input");
            return;
        }

        var key = NativeUiActionInputRules.NormalizeKey(data.vkCode, data.scanCode, (uint)data.flags);
        _modifiers.Update(key, down, ReadHeldModifiers());
        if (NativeUiActionModifierState.ModifierBit(key) != 0 || !down)
        {
            return;
        }

        if (key is 0 or > 0xfe or 0xe5 or 0xe7 ||
            !_modifiers.TryGetModifiers(ReadHeldModifiers(), out var modifiers))
        {
            Fail("unsupported_input");
            return;
        }

        if (UpdateScope(observedTimestamp))
        {
            Publish(new UiActionInput(UiActionInputKind.KeyDown, _clock.ElapsedMilliseconds,
                VirtualKey: key, Modifiers: modifiers, ObservedTimestamp: observedTimestamp));
        }
    }

    private void OnMouse(uint message, MSLLHOOKSTRUCT data, long observedTimestamp)
    {
        var kind = NativeUiActionInputRules.MouseKind(message);
        var right = message is 0x0204 or 0x0205;
        var accepted = NativeUiActionInputRules.KnownMouseFlags(data.flags) &&
            NativeUiActionInputRules.AcceptMouse(data.flags, _includeInjected);
        if (!UpdateScope(observedTimestamp))
        {
            if (!_stopRequested && accepted && kind is UiActionInputKind.MouseDown or UiActionInputKind.MouseUp)
            {
                _buttons.ActivationBoundary(kind.Value, right, foreground: false,
                    pointBelongs: kind == UiActionInputKind.MouseDown && _scope.OwnsPoint(data.pt.X, data.pt.Y));
            }

            return;
        }

        var pointBelongs = _scope.OwnsPoint(data.pt.X, data.pt.Y);
        var activationRelease = accepted && kind is not null &&
            _buttons.ActivationBoundary(kind.Value, right, foreground: true, pointBelongs);
        if (!AcceptMousePoint(pointBelongs) || activationRelease)
        {
            return;
        }

        if (!NativeUiActionInputRules.KnownMouseFlags(data.flags))
        {
            Fail("unsupported_input");
            return;
        }

        var point = new PointerPoint(data.pt.X, data.pt.Y);
        if (_buttons.HasDragExcursion(point))
        {
            Fail("unsupported_drag");
            return;
        }

        if (!NativeUiActionInputRules.AcceptMouse(data.flags, _includeInjected) || message == 0x0200)
        {
            return;
        }

        if (kind is null || !_modifiers.TryGetModifiers(ReadHeldModifiers(), out var modifiers))
        {
            Fail("unsupported_input");
            return;
        }

        var delta = kind == UiActionInputKind.Wheel ? NativeUiActionInputRules.WheelDelta(data.mouseData) : 0;
        if (kind == UiActionInputKind.Wheel && (delta == 0 || delta is < -12000 or > 12000))
        {
            Fail("unsupported_input");
            return;
        }

        if (UpdateScope(observedTimestamp) && AcceptMousePoint(_scope.OwnsPoint(data.pt.X, data.pt.Y)))
        {
            _buttons.Observe(kind.Value, right, point);
            Publish(new UiActionInput(kind.Value, _clock.ElapsedMilliseconds, data.pt.X, data.pt.Y,
                Modifiers: modifiers, RightButton: right, WheelDelta: delta, ObservedTimestamp: observedTimestamp));
        }
    }

    private bool AcceptMousePoint(bool pointBelongs)
    {
        var disposition = NativeUiActionInputRules.ClassifyPointer(pointBelongs, _buttons.HasActivePress);
        if (disposition == NativeUiActionPointerScope.InterruptedPress)
        {
            Fail("unsupported_drag");
        }

        return disposition == NativeUiActionPointerScope.Inside;
    }

    private bool UpdateScope(long observedTimestamp)
    {
        if (_stopRequested)
        {
            return false;
        }

        var foreground = PInvoke.GetForegroundWindow();
        var state = NativeUiActionInputRules.ClassifyScope(_scope.TargetExists,
            _interactiveDesktop && !foreground.IsNull, foreground == _scope.Hwnd);
        if (state is NativeUiActionInputScope.TargetClosed or NativeUiActionInputScope.NoInteractiveDesktop)
        {
            Fail(state == NativeUiActionInputScope.TargetClosed ? "target_closed" : "no_interactive_desktop");
            return false;
        }

        var inside = state == NativeUiActionInputScope.Inside;
        if (_inside != inside)
        {
            var interruptedPress = !inside && _buttons.HasActivePress;
            _inside = inside;
            _modifiers.Reset(inside ? ReadHeldModifiers() : (byte)0);
            _buttons.Reset(preserveActivation: inside);
            Publish(new UiActionInput(inside ? UiActionInputKind.ScopeEntered : UiActionInputKind.ScopeLost,
                _clock.ElapsedMilliseconds, ObservedTimestamp: observedTimestamp));
            if (interruptedPress)
            {
                Fail("unsupported_drag");
            }
        }

        return inside && !_stopRequested;
    }

    private static byte ReadHeldModifiers()
    {
        ReadOnlySpan<ushort> keys = [0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0x5b, 0x5c];
        byte held = 0;
        for (var i = 0; i < keys.Length; i++)
        {
            if (PInvoke.GetAsyncKeyState(keys[i]) < 0)
            {
                held |= (byte)(1 << i);
            }
        }

        return held;
    }

    private void Publish(UiActionInput input)
    {
        if (!_stopRequested && !NativeUiActionInputRules.TryPublish(_channel.Writer, input))
        {
            Fail("input_overflow");
        }
    }

    private void Fail(string code)
    {
        var failure = new UiActionException(code, NativeUiActionInputRules.FailureMessage(code));
        _stopRequested = true;
        if (Interlocked.CompareExchange(ref _failure, failure, null) is null)
        {
            _channel.Writer.TryWrite(new UiActionInput(UiActionInputKind.Error, _clock.ElapsedMilliseconds,
                ErrorCode: code, ObservedTimestamp: Stopwatch.GetTimestamp()));
            _channel.Writer.TryComplete(failure);
        }

        if (_threadId != 0 && Thread.CurrentThread == _thread)
        {
            PInvoke.PostThreadMessage(_threadId, WmQuit, default, default);
        }
    }

    private void Unhook()
    {
        var keyboard = Interlocked.Exchange(ref _keyboardHook, 0);
        var mouse = Interlocked.Exchange(ref _mouseHook, 0);
        if (keyboard != 0 && !PInvoke.UnhookWindowsHookEx(new HHOOK(keyboard)))
        {
            Fail("hook_stop_failed");
        }

        if (mouse != 0 && !PInvoke.UnhookWindowsHookEx(new HHOOK(mouse)))
        {
            Fail("hook_stop_failed");
        }
    }

    private void StopThread()
    {
        _stopRequested = true;
        var thread = _thread;
        if (thread is null || thread == Thread.CurrentThread)
        {
            return;
        }

        // The thread's 100-ms timer wakes GetMessage. Do not post to a saved thread ID from
        // another thread: after a spontaneous hook failure, Windows can recycle that ID.
        if (!thread.Join(ShutdownTimeoutMs))
        {
            Unhook();
            Fail("hook_stop_failed");
            throw new UiActionException("hook_stop_failed", "Action input capture did not stop within the shutdown deadline.");
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        StopThread();
        if (_thread is null)
        {
            _scope.Dispose();
            _channel.Writer.TryComplete(_failure);
        }

        _ready.Dispose();
        if (_failure?.Code == "hook_stop_failed")
        {
            throw _failure;
        }
    }
}

internal enum NativeUiActionInputScope { Inside, Outside, TargetClosed, NoInteractiveDesktop }
internal enum NativeUiActionPointerScope { Inside, Outside, InterruptedPress }

internal sealed class NativeUiActionMouseState
{
    private byte _pressed;
    private byte _activationButtons;
    private PointerPoint _origin;
    private bool _dragged;

    internal bool HasActivePress => _pressed != 0;
    internal bool HasPendingActivation => _activationButtons != 0;

    internal void Reset(bool preserveActivation = false)
    {
        _pressed = 0;
        _origin = default;
        _dragged = false;
        if (!preserveActivation)
        {
            _activationButtons = 0;
        }
    }

    internal bool ActivationBoundary(UiActionInputKind kind, bool right, bool foreground, bool pointBelongs)
    {
        var bit = right ? (byte)2 : (byte)1;
        if (kind == UiActionInputKind.MouseDown)
        {
            _activationButtons &= (byte)~bit;
            if (!foreground && pointBelongs)
            {
                // Only the button is retained, never an excluded event's coordinates.
                _activationButtons |= bit;
            }
        }
        else if (kind == UiActionInputKind.MouseUp)
        {
            var activationRelease = (_activationButtons & bit) != 0;
            _activationButtons &= (byte)~bit;
            return activationRelease && foreground && pointBelongs;
        }

        return false;
    }

    internal bool HasDragExcursion(PointerPoint point)
    {
        if (HasActivePress)
        {
            _dragged |= Math.Abs((long)point.X - _origin.X) > 4 || Math.Abs((long)point.Y - _origin.Y) > 4;
        }

        return _dragged;
    }

    internal void Observe(UiActionInputKind kind, bool right, PointerPoint point = default)
    {
        var bit = right ? (byte)2 : (byte)1;
        if (kind == UiActionInputKind.MouseDown)
        {
            if (!HasActivePress)
            {
                _origin = point;
                _dragged = false;
            }

            _pressed |= bit;
        }
        else if (kind == UiActionInputKind.MouseUp)
        {
            _pressed &= (byte)~bit;
            if (!HasActivePress)
            {
                Reset();
            }
        }

    }
}

internal static class NativeUiActionInputRules
{
    internal static NativeUiActionInputScope ClassifyScope(bool targetExists, bool interactiveDesktop, bool foregroundBelongs)
        => !targetExists ? NativeUiActionInputScope.TargetClosed
            : !interactiveDesktop ? NativeUiActionInputScope.NoInteractiveDesktop
            : foregroundBelongs ? NativeUiActionInputScope.Inside : NativeUiActionInputScope.Outside;

    internal static NativeUiActionPointerScope ClassifyPointer(bool pointBelongs, bool hasScopedPress)
        => pointBelongs ? NativeUiActionPointerScope.Inside
            : hasScopedPress ? NativeUiActionPointerScope.InterruptedPress : NativeUiActionPointerScope.Outside;

    internal static bool AcceptKeyboard(uint flags, bool includeInjected) => includeInjected || (flags & 0x12) == 0;
    internal static bool AcceptMouse(uint flags, bool includeInjected) => includeInjected || (flags & 0x03) == 0;
    internal static bool KnownKeyboardFlags(uint flags) => (flags & ~0xb3u) == 0;
    internal static bool KnownMouseFlags(uint flags) => (flags & ~0x03u) == 0;

    internal static ushort NormalizeKey(uint key, uint scanCode, uint flags) => key switch
    {
        0x10 => scanCode == 0x36 ? (ushort)0xa1 : (ushort)0xa0,
        0x11 => (flags & 1) != 0 ? (ushort)0xa3 : (ushort)0xa2,
        0x12 => (flags & 1) != 0 ? (ushort)0xa5 : (ushort)0xa4,
        <= 0xfe => (ushort)key,
        _ => 0,
    };

    internal static UiActionInputKind? MouseKind(uint message) => message switch
    {
        0x0201 or 0x0204 => UiActionInputKind.MouseDown,
        0x0202 or 0x0205 => UiActionInputKind.MouseUp,
        0x020a => UiActionInputKind.Wheel,
        _ => null,
    };

    internal static int WheelDelta(uint mouseData) => unchecked((short)(mouseData >> 16));

    internal static bool TryPublish(ChannelWriter<UiActionInput> writer, UiActionInput input)
    {
        if (writer.TryWrite(input))
        {
            return true;
        }

        writer.TryComplete(new UiActionException("input_overflow", FailureMessage("input_overflow")));
        return false;
    }

    internal static string FailureMessage(string code) => code switch
    {
        "target_closed" => "The selected window closed or is no longer visible.",
        "no_interactive_desktop" => "Capture requires an unlocked, interactive Windows desktop.",
        "unsupported_input" => "The selected window received an unsupported input or a modifier held across a scope change.",
        "unsupported_drag" => "The pointer moved during a press or the press left the selected window. Mouse drags are not supported.",
        "input_overflow" => "Capture could not keep up with input. The recording is incomplete.",
        "hook_start_failed" => "Windows could not start the action input hooks.",
        "hook_stop_failed" => "Windows could not confirm that action input capture stopped cleanly.",
        _ => "Windows action input capture failed. The recording is incomplete.",
    };
}

internal sealed class NativeUiActionModifierState
{
    private byte _pressed;
    private byte _blocked;

    internal void Reset(byte physicallyHeld)
    {
        _pressed = 0;
        _blocked = physicallyHeld;
    }

    internal void Update(ushort key, bool down, byte physicallyHeld)
    {
        var bit = ModifierBit(key);
        // Low-level keyboard hooks run before GetAsyncKeyState reflects this event.
        physicallyHeld = down ? (byte)(physicallyHeld | bit) : (byte)(physicallyHeld & ~bit);
        _pressed &= physicallyHeld;
        _blocked &= physicallyHeld;
        if (down && (bit & _blocked) == 0)
        {
            _pressed |= bit;
        }
    }

    internal bool TryGetModifiers(byte physicallyHeld, out UiActionModifiers modifiers)
    {
        _pressed &= physicallyHeld;
        _blocked &= physicallyHeld;
        modifiers = UiActionModifiers.None;
        if ((physicallyHeld & ~_pressed) != 0)
        {
            return false;
        }

        if ((_pressed & 0x03) != 0) { modifiers |= UiActionModifiers.Shift; }
        if ((_pressed & 0x0c) != 0) { modifiers |= UiActionModifiers.Control; }
        if ((_pressed & 0x30) != 0) { modifiers |= UiActionModifiers.Alt; }
        if ((_pressed & 0xc0) != 0) { modifiers |= UiActionModifiers.Windows; }
        return true;
    }

    internal static byte ModifierBit(ushort key) => key switch
    {
        0xa0 => 1,
        0xa1 => 2,
        0xa2 => 4,
        0xa3 => 8,
        0xa4 => 16,
        0xa5 => 32,
        0x5b => 64,
        0x5c => 128,
        _ => 0,
    };
}
