// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed partial class UiActionService
{
    private UiActionRecordResult RecordCore(UiTarget target, UiActionRecordOptions options,
        Action? onReady, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Stopping capture must still allow the final safe-field snapshot to be flushed.
        using var environment = environments.Create(target, CancellationToken.None);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var recording = new Recording(environment, options);
        IUiActionInputSource? source = null;
        try
        {
            CheckEnvironment(environment, requireForeground: false);
            recording.EnterScope();
            source = inputs.Create(target, options.IncludeInjected);
            source.Start();
            if (options.DurationSec > 0)
            {
                stop.CancelAfter(TimeSpan.FromSeconds(options.DurationSec));
            }

            stop.Token.ThrowIfCancellationRequested();
            onReady?.Invoke();
            while (source.Reader.WaitToReadAsync(stop.Token).AsTask().GetAwaiter().GetResult())
            {
                while (true)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (!source.Reader.TryRead(out var input))
                    {
                        break;
                    }

                    recording.Process(input, source.Reader, stop.Token);
                }
            }

            if (!stop.IsCancellationRequested)
            {
                recording.Fail("input_source_closed");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Explicit cancellation is the normal stop gesture, not a failed recording.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            recording.Fail(ex is UiActionException action ? action.Code : "input_error");
        }
        finally
        {
            try
            {
                source?.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                recording.Fail("input_error");
            }
        }

        try
        {
            if (!recording.IsIncomplete)
            {
                if (source is not null)
                {
                    recording.CheckStoppedSource(source.Reader);
                }

                recording.Finish();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            recording.Fail(ex is UiActionException action ? action.Code : "input_error");
        }

        return recording.Result(cancellationToken.IsCancellationRequested ? "cancelled" : "duration_elapsed");
    }

    private sealed class Recording(IUiActionEnvironment environment, UiActionRecordOptions options)
    {
        private const int MaximumEditBatchSize = 64;
        private const int ClickMovementPixels = 4;
        private readonly List<UiActionStep> _steps = [];
        private readonly List<string> _warnings = [];
        private readonly string _processName = environment.ProcessName;
        private UiActionTarget? _initialFocus;
        private UiActionElement? _focus;
        private PendingText? _text;
        private PendingClick? _click;
        private PendingClick? _previousClick;
        private int _previousClickIndex = -1;
        private int _previousWheelIndex = -1;
        private int _wheelX;
        private int _wheelY;
        private int _parameterCount;
        private long _lastTime = -1;
        private bool _inScope;
        private bool _enteredOnce;
        private string? _failure;

        internal bool IsIncomplete => _failure is not null;

        internal void Fail(string code)
            => _failure ??= code is "password_target" or "privacy_unavailable" ? "sensitive_input" : SafeCode(code);

        internal UiActionRecordResult Result(string reason)
        {
            if (_steps.Count == 0)
            {
                Fail("empty_recording");
            }

            return new UiActionRecordResult(new UiActionFlow
            {
                Status = IsIncomplete ? "incomplete" : "recorded",
                ProcessName = _processName,
                InitialFocus = _initialFocus,
                Steps = _steps.ToArray(),
                Warnings = _warnings.ToArray(),
            }, _failure ?? reason);
        }

        internal void EnterScope()
        {
            if (_inScope || !environment.IsForeground)
            {
                return;
            }

            CheckEnvironment(environment);
            var focused = environment.Focused();
            _focus = focused is null ? null : Stable(focused);
            if (!_enteredOnce)
            {
                UiActionFlowDocument.Validate(new UiActionFlow
                {
                    Status = "incomplete",
                    ProcessName = _processName,
                    InitialFocus = _focus?.Target,
                });
                _initialFocus = _focus?.Target;
                _enteredOnce = true;
            }

            _inScope = true;
        }

        private void LoseScope()
        {
            FlushText();
            if (_click is not null)
            {
                throw new UiActionException("incomplete_click", "The selected window lost focus during a mouse press.");
            }

            _inScope = false;
            _focus = null;
            _previousClickIndex = -1;
            _previousWheelIndex = -1;
            Warn(ScopeWarning);
        }

        internal void Process(UiActionInput input, ChannelReader<UiActionInput> reader,
            CancellationToken cancellationToken)
        {
            if (input.Kind == UiActionInputKind.Error)
            {
                throw new UiActionException(SafeCode(input.ErrorCode ?? "input_error"), "Input capture stopped.");
            }

            if (input.ElapsedMs < _lastTime || input.ElapsedMs < 0)
            {
                throw new UiActionException("input_order", "Input ordering could not be established.");
            }

            _lastTime = input.ElapsedMs;
            switch (input.Kind)
            {
                case UiActionInputKind.ScopeLost:
                    LoseScope();
                    return;
                case UiActionInputKind.ScopeEntered:
                    if (!_inScope)
                    {
                        EnsureNoQueuedInput(reader);
                    }

                    EnterScope();
                    return;
            }

            if (!_inScope)
            {
                throw new UiActionException("input_scope_changed",
                    "Scoped input arrived without a verified recording context.");
            }

            if (input.ElapsedMs > 3_600_000)
            {
                throw new UiActionException("duration_limit", "Record a shorter flow; action timestamps are limited to one hour.");
            }

            CheckEnvironment(environment, requireForeground: false);
            if (!environment.IsForeground)
            {
                throw new UiActionException("input_scope_changed",
                    "A recorded input crossed a foreground boundary before its target could be verified.");
            }

            if (input.Kind is UiActionInputKind.MouseDown or UiActionInputKind.MouseUp or UiActionInputKind.Wheel
                && input.Modifiers != UiActionModifiers.None)
            {
                throw new UiActionException("unsupported_input", "Modified mouse input is not supported by action recording.");
            }

            CheckObservationTimestamp(input.ObservedTimestamp, Stopwatch.GetTimestamp());

            switch (input.Kind)
            {
                case UiActionInputKind.KeyDown:
                    RecordKey(input, reader, cancellationToken);
                    break;
                case UiActionInputKind.MouseDown:
                    BeginClick(input);
                    break;
                case UiActionInputKind.MouseUp:
                    CompleteClick(input);
                    SettleFocus(reader, cancellationToken);
                    break;
                case UiActionInputKind.Wheel:
                    RecordWheel(input);
                    break;
                default:
                    throw new UiActionException("unsupported_input", "This input is not supported by action recording.");
            }
        }

        private void RecordKey(UiActionInput input, ChannelReader<UiActionInput> reader,
            CancellationToken cancellationToken)
        {
            if (_click is not null)
            {
                throw new UiActionException("unsupported_input", "Keyboard input during a mouse press is not supported.");
            }

            var observed = environment.Focused()
                ?? throw new UiActionException("sensitive_input", "Keyboard focus could not be checked safely.");
            observed = Normalize(observed);
            if (IsModifierKey(input.VirtualKey))
            {
                return;
            }

            var before = _focus
                ?? throw new UiActionException("sensitive_input", "The previous keyboard focus is unknown.");
            CheckPrivacy(before);
            if (input.VirtualKey is 0xE5 or 0xE7 or >= 0x15 and <= 0x19 or 0x1C or 0x1D or 0x1F)
            {
                throw new UiActionException("unsupported_input", "IME composition and Unicode packet input are not supported.");
            }

            if ((input.Modifiers & UiActionModifiers.Windows) != 0 || input.VirtualKey is 0x5B or 0x5C)
            {
                throw new UiActionException("system_key", "System-reserved keys cannot be recorded.");
            }

            if ((input.Modifiers & (UiActionModifiers.Control | UiActionModifiers.Alt))
                    == (UiActionModifiers.Control | UiActionModifiers.Alt)
                && IsPrintableKey(input.VirtualKey))
            {
                throw new UiActionException("unsupported_input", "AltGr and other ambiguous modified typing are not supported.");
            }

            bool control = input.Modifiers.HasFlag(UiActionModifiers.Control);
            if ((control && input.VirtualKey is 0x43 or 0x2D)
                || (!before.IsEditable && ((control && input.VirtualKey is 0x56 or 0x58)
                    || (input.Modifiers == UiActionModifiers.Shift && input.VirtualKey == 0x2D))))
            {
                throw new UiActionException("unsupported_clipboard", "This clipboard operation cannot be recorded as replacement text.");
            }

            if (IsEditOperation(before, input))
            {
                if (!SameTarget(before.Target, observed.Target)
                    || (_text is not null && !SameTarget(_text.Target, before.Target)))
                {
                    throw new UiActionException("focus_changed", "An edit could not be attributed to its original field.");
                }

                RequireWritable(observed);
                if (_text is null)
                {
                    var stable = Stable(observed);
                    RequireWritable(stable);
                    _text = new PendingText(stable.Target, input.ElapsedMs);
                }

                for (int count = 1; count < MaximumEditBatchSize
                    && reader.TryPeek(out var next)
                    && next.Kind == UiActionInputKind.KeyDown && IsEditOperation(before, next); count++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!reader.TryRead(out var edit))
                    {
                        break;
                    }

                    CheckInputTime(edit);
                }

                CheckEnvironment(environment, requireForeground: false);
                if (!environment.IsForeground)
                {
                    throw new UiActionException("input_scope_changed",
                        "The selected window lost foreground while an edit was being recorded.");
                }

                var after = environment.Focused()
                    ?? throw new UiActionException("sensitive_input", "Keyboard focus could not be checked safely.");
                after = Normalize(after);
                RequireWritable(after);
                if (!SameTarget(before.Target, after.Target))
                {
                    throw new UiActionException("focus_changed", "An edit could not be attributed to its original field.");
                }

                return;
            }

            EnsureNoQueuedInput(reader);
            string keys = DescribeKey(input, before);
            ParseSingleChord(keys);
            FlushText();
            Stable(before);
            Append(new UiActionStep
            {
                Kind = "key",
                Target = before.Target,
                ElapsedMs = input.ElapsedMs,
                Keys = keys,
            });
            SettleFocus(reader, cancellationToken);
        }

        private void CheckInputTime(UiActionInput input)
        {
            if (input.ElapsedMs < _lastTime || input.ElapsedMs < 0)
            {
                throw new UiActionException("input_order", "Input ordering could not be established.");
            }

            _lastTime = input.ElapsedMs;
            if (input.ElapsedMs > 3_600_000)
            {
                throw new UiActionException("duration_limit", "Record a shorter flow; action timestamps are limited to one hour.");
            }

            CheckObservationTimestamp(input.ObservedTimestamp, Stopwatch.GetTimestamp());
        }

        private static bool IsModifierKey(ushort key)
            => key is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5;

        private static bool IsPrintableKey(ushort key)
            => key is 0x20 or >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A
                or >= 0x60 and <= 0x6F or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xE2;

        private static bool IsEditOperation(UiActionElement focus, UiActionInput input)
        {
            if (!focus.IsEditable || (input.Modifiers & (UiActionModifiers.Alt | UiActionModifiers.Windows)) != 0)
            {
                return false;
            }

            if (input.VirtualKey is 0x08 or 0x2E)
            {
                return true;
            }

            if (input.Modifiers == UiActionModifiers.Control)
            {
                return input.VirtualKey is 0x41 or 0x56 or 0x58 or 0x59 or 0x5A;
            }

            return (input.Modifiers == UiActionModifiers.Shift && input.VirtualKey == 0x2D)
                || (!input.Modifiers.HasFlag(UiActionModifiers.Control) && IsPrintableKey(input.VirtualKey));
        }

        private static string DescribeKey(UiActionInput input, UiActionElement focus)
        {
            bool shortcut = (input.Modifiers & (UiActionModifiers.Control | UiActionModifiers.Alt)) != 0;
            string? name = input.VirtualKey switch
            {
                0x08 => "backspace",
                0x09 => "tab",
                0x0D => "enter",
                0x1B => "esc",
                0x20 when shortcut || focus.Target.ControlType is "Button" or "CheckBox" or "RadioButton" => "space",
                0x21 => "pageup",
                0x22 => "pagedown",
                0x23 => "end",
                0x24 => "home",
                0x25 => "left",
                0x26 => "up",
                0x27 => "right",
                0x28 => "down",
                0x2C => "printscreen",
                0x2D => "insert",
                0x2E => "delete",
                >= 0x70 and <= 0x7F => "f" + (input.VirtualKey - 0x6F).ToString(CultureInfo.InvariantCulture),
                >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A when shortcut
                    => char.ToLowerInvariant((char)input.VirtualKey).ToString(),
                _ => null,
            };
            if (name is null || (IsPrintableKey(input.VirtualKey) && !shortcut && input.VirtualKey != 0x20))
            {
                throw new UiActionException("unsupported_input", "Printable input requires a supported writable text field.");
            }

            string prefix = input.Modifiers.HasFlag(UiActionModifiers.Control) ? "ctrl+" : "";
            prefix += input.Modifiers.HasFlag(UiActionModifiers.Shift) ? "shift+" : "";
            prefix += input.Modifiers.HasFlag(UiActionModifiers.Alt) ? "alt+" : "";
            return prefix + name;
        }

        private void BeginClick(UiActionInput input)
        {
            if (_click is not null)
            {
                throw new UiActionException("unsupported_drag", "Overlapping mouse presses and drags are not supported.");
            }

            FlushText();
            var element = ElementAt(input.X, input.Y);
            _click = new PendingClick(element.Target, element.Bounds, input.X, input.Y, input.RightButton,
                input.ElapsedMs);
        }

        private void CompleteClick(UiActionInput input)
        {
            var down = _click
                ?? throw new UiActionException("incomplete_click", "A mouse release had no recorded press.");
            _click = null;
            if (down.Right != input.RightButton || !Near(down.X, down.Y, input.X, input.Y))
            {
                throw new UiActionException("unsupported_drag", "Mouse drags and mismatched mouse buttons are not supported.");
            }

            var up = ElementAt(input.X, input.Y);
            if (!SameTarget(down.Target, up.Target))
            {
                throw new UiActionException("target_changed", "The mouse press and release did not identify the same control.");
            }

            if (_previousClickIndex == _steps.Count - 1 && _previousClick is { } previous
                && SameTarget(previous.Target, down.Target) && previous.Right == down.Right
                && down.ElapsedMs - previous.ElapsedMs <= DoubleClickIntervalMs
                && Near(previous.X, previous.Y, down.X, down.Y))
            {
                if (_steps[^1].ClickCount != 1)
                {
                    throw new UiActionException("unsupported_input", "More than two consecutive rapid clicks are not supported.");
                }

                _steps[^1] = _steps[^1] with { ClickCount = 2 };
            }
            else
            {
                Append(new UiActionStep
                {
                    Kind = "click",
                    Target = down.Target,
                    ElapsedMs = down.ElapsedMs,
                    Button = down.Right ? "right" : "left",
                    XFraction = Fraction(down.X, down.Bounds.Left, down.Bounds.Right),
                    YFraction = Fraction(down.Y, down.Bounds.Top, down.Bounds.Bottom),
                });
            }

            Warn("Click counts use a 500 ms double-click interval; custom mouse timing is not supported.");
            _previousClickIndex = _steps.Count - 1;
            _previousClick = down;
            _previousWheelIndex = -1;
        }

        private void RecordWheel(UiActionInput input)
        {
            if (_click is not null)
            {
                throw new UiActionException("unsupported_drag", "Scrolling during a mouse press is not supported.");
            }

            if (input.WheelDelta == 0 || Math.Abs((long)input.WheelDelta) > MaximumWheelDelta)
            {
                throw new UiActionException("unsupported_input", "The mouse wheel amount is not supported.");
            }

            FlushText();
            var element = ElementAt(input.X, input.Y, forScroll: true);
            if (_previousWheelIndex == _steps.Count - 1 && _previousWheelIndex >= 0
                && SameTarget(_steps[^1].Target, element.Target) && _wheelX == input.X && _wheelY == input.Y
                && Math.Sign(_steps[^1].WheelDelta) == Math.Sign(input.WheelDelta)
                && Math.Abs((long)_steps[^1].WheelDelta + input.WheelDelta) <= MaximumWheelDelta)
            {
                _steps[^1] = _steps[^1] with { WheelDelta = _steps[^1].WheelDelta + input.WheelDelta };
            }
            else
            {
                Append(new UiActionStep
                {
                    Kind = "wheel",
                    Target = element.Target,
                    ElapsedMs = input.ElapsedMs,
                    XFraction = Fraction(input.X, element.Bounds.Left, element.Bounds.Right),
                    YFraction = Fraction(input.Y, element.Bounds.Top, element.Bounds.Bottom),
                    WheelDelta = input.WheelDelta,
                });
            }

            _previousWheelIndex = _steps.Count - 1;
            _wheelX = input.X;
            _wheelY = input.Y;
            _previousClickIndex = -1;
        }

        private UiActionElement ElementAt(int x, int y, bool forScroll = false)
        {
            var element = environment.AtPoint(x, y, forScroll)
                ?? throw new UiActionException("target_not_found", "The pointer target could not be identified.");
            element = Stable(element);
            if (!element.Bounds.Contains(new PointerPoint(x, y)))
            {
                throw new UiActionException("target_changed", "The pointer is no longer inside the identified target.");
            }

            return element;
        }

        private static bool Near(int firstX, int firstY, int secondX, int secondY)
            => Math.Abs((long)firstX - secondX) <= ClickMovementPixels
                && Math.Abs((long)firstY - secondY) <= ClickMovementPixels;

        private static double Fraction(int point, int start, int end)
            => ((double)point - start) / ((double)end - start);

        private void SettleFocus(ChannelReader<UiActionInput> reader, CancellationToken cancellationToken)
        {
            UiActionElement? previous = null;
            for (int read = 0; read < 5; read++)
            {
                Delay(PollIntervalMs, cancellationToken);
                CheckEnvironment(environment, requireForeground: false);
                if (!environment.IsForeground)
                {
                    throw new UiActionException("input_scope_changed",
                        "The selected window lost foreground before an action's focus checkpoint completed.");
                }

                EnsureNoQueuedInput(reader);
                var observed = environment.Focused();
                observed = observed is null ? null : Normalize(observed);
                EnsureNoQueuedInput(reader);
                if (read > 0 && ((previous is null && observed is null)
                    || (previous is not null && observed is not null && SameTarget(previous.Target, observed.Target))))
                {
                    _focus = observed;
                    return;
                }

                previous = observed;
            }

            throw new UiActionException("focus_changed", "Keyboard focus did not settle before another action could be recorded.");
        }

        private static void EnsureNoQueuedInput(ChannelReader<UiActionInput> reader)
        {
            if (!reader.TryPeek(out var next))
            {
                return;
            }

            if (next.Kind == UiActionInputKind.Error)
            {
                throw new UiActionException(SafeCode(next.ErrorCode ?? "input_error"), "Input capture stopped.");
            }

            if (next.Kind != UiActionInputKind.ScopeLost || (reader.CanCount && reader.Count > 1))
            {
                throw new UiActionException("input_backlog", "Input crossed a focus checkpoint before its target could be identified.");
            }
        }

        private UiActionElement Stable(UiActionElement element)
        {
            element = Normalize(element);
            var resolved = environment.Resolve(element.Target)
                ?? throw new UiActionException("target_not_found", "The recorded target is no longer available.");
            resolved = Normalize(resolved);
            if (!SameTarget(element.Target, resolved.Target))
            {
                throw new UiActionException("target_changed", "The recorded target could not be identified uniquely.");
            }

            return resolved;
        }

        private static UiActionElement Normalize(UiActionElement element, bool requireAvailable = true)
        {
            CheckPrivacy(element);
            if (requireAvailable && (!element.IsEnabled || element.IsOffscreen))
            {
                throw new UiActionException("target_unavailable", "Recorded input requires an enabled, visible control.");
            }

            if (string.IsNullOrWhiteSpace(element.Target.ControlType))
            {
                throw new UiActionException("unstable_target", "The control type could not be identified.");
            }

            if (!string.IsNullOrWhiteSpace(element.Target.AutomationId))
            {
                return element with { Target = element.Target with { Name = null } };
            }

            // Edit names can be their current values. Never retain them, even in placeholder mode.
            if (element.IsEditable || element.Target.ControlType is "Edit" or "Document"
                || string.IsNullOrWhiteSpace(element.Target.Name))
            {
                throw new UiActionException("unstable_target", "Text fields require an exact AutomationId; other controls require an ID or name and type.");
            }

            return element;
        }

        private void FlushText()
        {
            if (_text is not { } pending)
            {
                return;
            }

            CheckEnvironment(environment, requireForeground: false);
            var element = environment.Resolve(pending.Target)
                ?? throw new UiActionException("target_not_found", "The original edit field is no longer available.");
            element = Normalize(element, requireAvailable: false);
            RequireWritable(element);
            if (!SameTarget(element.Target, pending.Target))
            {
                throw new UiActionException("target_changed", "The original edit field could not be identified.");
            }

            string? text = options.CaptureText ? environment.ReadValue(pending.Target) : null;
            if (options.CaptureText && text is null)
            {
                throw new UiActionException("sensitive_input", "The field value could not be checked safely.");
            }

            _text = null;
            Warn(TextWarning);
            Append(new UiActionStep
            {
                Kind = "text",
                Target = pending.Target,
                ElapsedMs = pending.ElapsedMs,
                Text = text,
                Parameter = options.CaptureText ? null : "text" + (++_parameterCount).ToString(CultureInfo.InvariantCulture),
            });
        }

        private void Append(UiActionStep step)
        {
            UiActionFlowDocument.Validate(new UiActionFlow { ProcessName = _processName, Steps = [step] });
            _steps.Add(step);
            if (_steps.Count >= options.MaxSteps)
            {
                throw new UiActionException("step_limit", "The recording reached its maximum step count.");
            }
        }

        private void Warn(string warning)
        {
            if (!_warnings.Contains(warning, StringComparer.Ordinal))
            {
                _warnings.Add(warning);
            }
        }

        internal void CheckStoppedSource(ChannelReader<UiActionInput> reader)
        {
            while (reader.TryRead(out var pending))
            {
                if (pending.Kind == UiActionInputKind.Error)
                {
                    throw new UiActionException(SafeCode(pending.ErrorCode ?? "input_error"), "Input capture stopped.");
                }

                if (pending.Kind == UiActionInputKind.ScopeLost)
                {
                    LoseScope();
                }
                else if (pending.Kind == UiActionInputKind.ScopeEntered)
                {
                    throw new UiActionException("input_backlog", "Capture stopped before a scope transition could be processed.");
                }
                else if (pending.Kind != UiActionInputKind.KeyDown || !IsModifierKey(pending.VirtualKey))
                {
                    throw new UiActionException("input_backlog", "Capture stopped with unprocessed input.");
                }
            }

            if (reader.Completion.IsFaulted)
            {
                throw new UiActionException("input_error", "Input capture stopped unexpectedly.");
            }
        }

        internal void Finish()
        {
            if (_click is not null)
            {
                throw new UiActionException("incomplete_click", "Capture stopped during a mouse press.");
            }

            if (_text is not null)
            {
                // Low-level hooks run before the app receives the final key. Let its value settle.
                Delay(InputSettleMs, CancellationToken.None);
                FlushText();
            }
        }

        private sealed record PendingText(UiActionTarget Target, long ElapsedMs);
        private sealed record PendingClick(UiActionTarget Target, PointerRect Bounds, int X, int Y, bool Right,
            long ElapsedMs);
    }
}
