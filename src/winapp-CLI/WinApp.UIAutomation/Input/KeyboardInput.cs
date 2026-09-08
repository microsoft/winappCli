// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Linq;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Synthesizes keyboard input via either PostMessage (HWND-targeted) or SendInput (OS-wide).
/// </summary>
/// <remarks>
/// Known limits:
/// <list type="bullet">
/// <item><see cref="KeyTransport.PostMessage"/> posts to a window's message queue and cannot trigger
/// <c>WH_KEYBOARD_LL</c> global hotkeys (low-level hooks tap upstream of any HWND queue). Apps that read
/// raw key state via <c>GetAsyncKeyState</c> may not observe held modifiers.</item>
/// <item><see cref="KeyTransport.SendInput"/> is subject to UIPI: input injected from a lower-integrity
/// process does not reach a higher-integrity window, so a normal process cannot drive an elevated app.
/// Run at an integrity level at least as high as the target.</item>
/// </list>
/// </remarks>
public static class KeyboardInput
{
    private const ushort VkMenu = 0x12; // ALT

    internal delegate uint SendInputHook(INPUT[] inputs);
    internal delegate void PostMessageHook(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam);

    /// <remarks>
    /// Native adapter seam for issue #630: the default body is the innermost <c>SendInput</c> OS
    /// boundary, which cannot be unit-tested without injecting real keyboard input into the desktop.
    /// Unit tests replace the delegate and cover all surrounding batching/error logic.
    /// </remarks>
    internal static SendInputHook s_sendInput = DefaultSendInput;

    /// <remarks>
    /// Native adapter seam for issue #630: the default body posts to a real HWND queue and is left as
    /// an honest native ceiling; tests inject a fake to verify exact messages and lParams.
    /// </remarks>
    internal static PostMessageHook s_postMessage = DefaultPostMessage;

    /// <remarks>
    /// Native adapter seam for issue #630: keyboard-layout mapping is provided by User32. Tests inject
    /// deterministic mappings so character batching branches are covered without depending on the
    /// machine's active layout.
    /// </remarks>
    internal static Func<char, short> s_vkKeyScan = PInvoke.VkKeyScan;

    /// <remarks>
    /// Native adapter seam for issue #630: scan-code mapping is provided by User32. Tests inject known
    /// values to cover lParam construction deterministically.
    /// </remarks>
    internal static Func<ushort, uint> s_mapVirtualKey = DefaultMapVirtualKey;

    /// <remarks>
    /// Native adapter seam for issue #630: foreground detection probes the live desktop only when
    /// formatting a native SendInput failure message. Tests inject this predicate.
    /// </remarks>
    internal static Func<bool> s_foregroundWindowIsNull = DefaultForegroundWindowIsNull;

    /// <remarks>
    /// Native adapter seam for issue #657 (follow-up H1): re-verifies before each throttled SendInput
    /// burst that the injection target still owns the foreground (its GA_ROOT is the foreground window).
    /// A long payload is paced over many SendInput calls spanning seconds, so a focus change mid-injection
    /// would otherwise spray the remaining keystrokes into whatever window is now foreground. Defaults to the
    /// shared <see cref="ForegroundGuard.ForegroundBelongsTo"/> ancestry check; tests inject a predicate to
    /// drive the drift-abort branch deterministically.
    /// </remarks>
    internal static Func<long, bool> s_foregroundBelongsToTarget = ForegroundGuard.ForegroundBelongsTo;

    internal static Action<int> s_sleep = Thread.Sleep;

    /// <summary>
    /// Number of characters injected per <c>SendInput</c> call for literal typed text. Long text sent as
    /// one unbroken burst overruns the target thread's input queue, which silently drops characters even
    /// though <c>SendInput</c> reports success (issue #657). Splitting the text into small chunks paced by
    /// <see cref="s_chunkDelayMs"/> lets the target drain its queue between bursts so every character lands.
    /// </summary>
    public const int DefaultTextChunkChars = 16;

    /// <summary>
    /// Pause (ms) between injected chunks, giving the target time to pump its input queue. Chosen so the
    /// injection rate (<see cref="DefaultTextChunkChars"/> chars per delay) stays comfortably below the
    /// rate a target drains its input queue, with margin at both the coarse (~15.6&#160;ms) and fine
    /// (~1&#160;ms) Windows timer resolutions <c>Thread.Sleep</c> may run at.
    /// </summary>
    internal const int DefaultChunkDelayMs = 15;

    /// <remarks>Overridable seam so tests can drive the chunking branches deterministically.</remarks>
    internal static int s_textChunkChars = DefaultTextChunkChars;

    /// <remarks>Overridable seam so tests can drive the throttle branches deterministically.</remarks>
    internal static int s_chunkDelayMs = DefaultChunkDelayMs;

    private static unsafe uint DefaultSendInput(INPUT[] inputs)
    {
        fixed (INPUT* pInputs = inputs)
        {
            return PInvoke.SendInput((uint)inputs.Length, pInputs, sizeof(INPUT));
        }
    }

    private static void DefaultPostMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam) =>
        PInvoke.PostMessage(hwnd, message, wParam, lParam);

    private static uint DefaultMapVirtualKey(ushort vk) =>
        PInvoke.MapVirtualKey(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);

    private static bool DefaultForegroundWindowIsNull() => PInvoke.GetForegroundWindow().IsNull;

    /// <summary>
    /// Restores every native seam to its production delegate. Test cleanup calls this so a faked
    /// seam never leaks into a later test that exercises real keyboard input (issue #630).
    /// </summary>
    internal static void ResetNativeSeams()
    {
        s_sendInput = DefaultSendInput;
        s_postMessage = DefaultPostMessage;
        s_vkKeyScan = PInvoke.VkKeyScan;
        s_mapVirtualKey = DefaultMapVirtualKey;
        s_foregroundWindowIsNull = DefaultForegroundWindowIsNull;
        s_foregroundBelongsToTarget = ForegroundGuard.ForegroundBelongsTo;
        s_sleep = Thread.Sleep;
        s_textChunkChars = DefaultTextChunkChars;
        s_chunkDelayMs = DefaultChunkDelayMs;
    }

    /// <summary>
    /// Sends a parsed key sequence to a window. Modifiers in a chord are pressed before, and released
    /// after, the main key.
    /// </summary>
    /// <param name="hwnd">Target window handle. Only used by <see cref="KeyTransport.PostMessage"/>;
    /// <see cref="KeyTransport.SendInput"/> goes to whichever window has focus.</param>
    /// <param name="actions">The chords and literal text to send, in order.</param>
    /// <param name="transport">How to deliver the input.</param>
    public static void Send(long hwnd, IReadOnlyList<KeyAction> actions, KeyTransport transport)
    {
        switch (transport)
        {
            case KeyTransport.PostMessage:
                SendViaPostMessage(new HWND((nint)hwnd), actions);
                break;
            case KeyTransport.SendInput:
                SendViaSendInput(hwnd, actions);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unknown key transport.");
        }
    }

    private static void SendViaPostMessage(HWND hwnd, IReadOnlyList<KeyAction> actions)
    {
        if (hwnd.IsNull)
        {
            throw new InvalidOperationException(
                "PostMessage transport requires a target window. Specify --target/-a/-w to resolve one, or use --via send-input.");
        }

        foreach (var action in actions)
        {
            switch (action)
            {
                case KeyChord chord:
                    // ALT combos (without Ctrl) are delivered as WM_SYSKEY* so menus/accelerators fire.
                    bool sys = chord.Modifiers.Contains(VkMenu);

                    foreach (var mod in chord.Modifiers)
                    {
                        Post(hwnd, isSys: false, keyUp: false, mod, IsExtended(mod));
                    }

                    Post(hwnd, sys, keyUp: false, chord.Vk, chord.Extended);
                    Post(hwnd, sys, keyUp: true, chord.Vk, chord.Extended);

                    for (int i = chord.Modifiers.Count - 1; i >= 0; i--)
                    {
                        Post(hwnd, isSys: false, keyUp: true, chord.Modifiers[i], IsExtended(chord.Modifiers[i]));
                    }
                    break;

                case TextInput text:
                    foreach (var ch in NormalizeNewlines(text.Text))
                    {
                        s_postMessage(hwnd, PInvoke.WM_CHAR, new WPARAM(ch), new LPARAM(1));
                    }
                    break;
            }

            s_sleep(5);
        }
    }

    private static void Post(HWND hwnd, bool isSys, bool keyUp, ushort vk, bool extended)
    {
        uint msg = isSys
            ? (keyUp ? PInvoke.WM_SYSKEYUP : PInvoke.WM_SYSKEYDOWN)
            : (keyUp ? PInvoke.WM_KEYUP : PInvoke.WM_KEYDOWN);

        var lParam = BuildKeyLParam(isSys, keyUp, extended, s_mapVirtualKey(vk));

        s_postMessage(hwnd, msg, new WPARAM(vk), new LPARAM((nint)(int)lParam));
    }

    internal static uint BuildKeyLParam(bool isSys, bool keyUp, bool extended, uint scan)
    {
        uint lParam = 1u                       // repeat count
            | (scan << 16)                     // scan code
            | (extended ? 1u << 24 : 0u)       // extended key
            | (isSys ? 1u << 29 : 0u);         // context code (ALT down)

        if (keyUp)
        {
            lParam |= (1u << 30) | (1u << 31);  // previous-state + transition-state
        }

        return lParam;
    }

    private static void SendViaSendInput(long hwnd, IReadOnlyList<KeyAction> actions)
    {
        // A single SendInput call carrying the whole payload overruns the target thread's input queue,
        // which silently drops characters even though SendInput returns success (issue #657). Split the
        // work into small self-contained segments (each chord, and literal text in chunks of
        // s_textChunkChars) and pace only the *continuation* chunks of a split text run — the sole place the
        // #657 overrun happens — so short text and chord sequences (e.g. "ctrl+a delete") add no latency.
        var segments = BuildSendInputSegments(actions, s_vkKeyScan);

        // Track whether any earlier segment already landed. If a later segment then fails, the caller must be
        // warned that a naive retry would re-type the text that already went in (issue #657 follow-up).
        bool anyDelivered = false;

        foreach (var segment in segments)
        {
            var events = segment.Events;
            if (events.Length == 0)
            {
                continue;
            }

            if (segment.ThrottleBefore)
            {
                // This is a 2nd-or-later chunk of one long literal-text run. Pace it so the target can drain
                // its input queue between bursts (issue #657).
                s_sleep(s_chunkDelayMs);

                // That pause widens the interval since the command's one-time pre-send foreground check, and
                // SendInput is OS-wide — it lands on whatever window holds the foreground. If focus left the
                // target during the pause (a popup stole it, the user clicked away), the rest of the literal
                // text would be typed into the wrong window and could leak secrets (issue #657 follow-up H1).
                // Re-verify the target still owns the foreground before each paced chunk and abort on drift.
                // The guard is scoped to literal-text continuation chunks on purpose: a chord may legitimately
                // move the foreground (alt+tab, win+d) and must not be treated as drift. We deliberately inject
                // no releases here — every completed segment is self-balanced (nothing is held), and any
                // SendInput now would go to the window that stole focus, the exact misdirection we're
                // preventing. (hwnd == 0 means there is no target to verify against; direct callers passing 0
                // opt out.)
                if (hwnd != 0 && !s_foregroundBelongsToTarget(hwnd))
                {
                    throw BuildForegroundLostFailure();
                }
            }

            var sent = s_sendInput(events);
            if (sent != (uint)events.Length)
            {
                // A zero or short write can strand a key/modifier in the down state (e.g. a Shift-down
                // whose matching up never fired), which corrupts the whole session. Every already-sent
                // segment is balanced (its downs and ups are contained within it), so only this failing
                // segment can leave a key held — best-effort release it before surfacing the failure.
                ReleaseHeldKeys(events);
                throw BuildSendInputFailure(sent, events.Length, anyDelivered);
            }

            anyDelivered = true;
        }
    }

    /// <summary>
    /// Builds the failure thrown when the foreground window drifts away from the injection target partway
    /// through a throttled send-input sequence (issue #657 follow-up H1). Surfaced as a
    /// <see cref="ForegroundLostException"/> so the command maps it to the same foreground_not_target
    /// contract as the pre-send foreground check, rather than a generic error.
    /// </summary>
    private static ForegroundLostException BuildForegroundLostFailure() =>
        new("SendInput aborted — the target window lost the foreground partway through sending, so the " +
            "remaining keystrokes were withheld to avoid injecting them into whatever window took focus.");

    /// <summary>
    /// Builds the SendInput failure surfaced when a chunk is only partially injected. A zero write means
    /// the injection was refused outright (no interactive desktop, or UIPI/integrity mismatch); a short
    /// write means input was partially applied and the held keys were already released. When
    /// <paramref name="priorInputDelivered"/> is <see langword="true"/> an earlier segment of a split payload
    /// already landed, so the message leads with a caveat: the base text would otherwise imply nothing was
    /// typed (zero write) or invite a literal retry (short write) that duplicates the text already applied
    /// — e.g. retyping a password as "passpass…word" (issue #657 follow-up).
    /// </summary>
    private static InvalidOperationException BuildSendInputFailure(uint sent, int expected, bool priorInputDelivered)
    {
        string prefix = priorInputDelivered
            ? "SendInput stopped partway through — earlier keystrokes already landed in the target window, so " +
              "verify or clear it before retrying to avoid duplicated text. "
            : string.Empty;

        if (sent == 0)
        {
            return new(prefix + (s_foregroundWindowIsNull()
                // No foreground window → the session is locked or on a secure desktop, where a
                // user-session process can't inject. That's not an elevation/UIPI problem.
                ? "SendInput failed — no interactive desktop is available (the session is locked " +
                  "or on a secure desktop). Unlock the session and retry."
                : "SendInput failed — the target window may be running at a higher integrity level (elevated) " +
                  "or be an AppContainer/AppX app blocked by UIPI. Try --via post-message, or run this CLI as administrator."));
        }

        // Short write: this chunk's held keys were already released so the keyboard state is clean. Only offer
        // the bare "retry the gesture" hint when nothing landed before this chunk — once earlier input has
        // applied, the caveat above owns the retry guidance so a literal retry doesn't duplicate text.
        string retryHint = priorInputDelivered ? " Held keys were released." : " Held keys were released; retry the gesture.";
        return new(prefix + $"SendInput delivered only {sent} of {expected} key events — input was partially applied." + retryHint);
    }

    /// <summary>
    /// One self-contained SendInput burst plus whether it must be paced before injection.
    /// <see cref="ThrottleBefore"/> is set only on the 2nd..Nth chunk of a single split literal-text run —
    /// the sole place the #657 queue overrun occurs — so a pacing delay and a foreground re-check run before
    /// it. Chords and the first chunk of any text run carry <see langword="false"/>: they inject immediately
    /// with no added latency, and a focus-changing chord (alt+tab, win+d) is never mistaken for drift.
    /// </summary>
    internal readonly record struct SendInputSegment(INPUT[] Events, bool ThrottleBefore);

    /// <summary>
    /// Flattens key actions into ordered, self-contained SendInput segments for throttled delivery: one
    /// segment per chord, and literal text split into chunks of <see cref="s_textChunkChars"/> characters.
    /// Each segment carries balanced key-down/key-up pairs so pacing (or a failure) between segments can
    /// never strand a modifier, and each is tagged (<see cref="SendInputSegment.ThrottleBefore"/>) with
    /// whether it is a continuation chunk of a split text run that must be paced and foreground-rechecked.
    /// This is the single owner of the action→INPUT encoding; <see cref="BuildSendInputBatch"/> is the
    /// flattened (un-chunked) view over it.
    /// </summary>
    internal static List<SendInputSegment> BuildSendInputSegments(IReadOnlyList<KeyAction> actions, Func<char, short>? vkKeyScan = null)
    {
        vkKeyScan ??= s_vkKeyScan;
        int chunkChars = Math.Max(1, s_textChunkChars);
        var segments = new List<SendInputSegment>();

        foreach (var action in actions)
        {
            switch (action)
            {
                case KeyChord chord:
                    var chordEvents = new List<INPUT>();
                    AppendChordEvents(chordEvents, chord);
                    if (chordEvents.Count > 0)
                    {
                        // A chord is atomic and small; it never overruns the queue, and it may intentionally
                        // change the foreground (alt+tab, win+d), so it is never paced or drift-checked.
                        segments.Add(new SendInputSegment(chordEvents.ToArray(), ThrottleBefore: false));
                    }
                    break;

                case TextInput text:
                    // Normalize newlines (\n and \r\n → \r) so a line break actually inserts instead of being
                    // silently dropped (issue #658), then split the normalized run into chunks for throttled
                    // delivery (issue #657). Normalizing before chunking keeps the newline handling identical
                    // regardless of where a chunk boundary falls.
                    var normalized = NormalizeNewlines(text.Text);
                    for (int start = 0; start < normalized.Length; start += chunkChars)
                    {
                        int end = Math.Min(start + chunkChars, normalized.Length);
                        var chunk = new List<INPUT>();
                        for (int j = start; j < end; j++)
                        {
                            AppendCharEvents(chunk, normalized[j], vkKeyScan);
                        }

                        if (chunk.Count > 0)
                        {
                            // Pace + foreground-recheck only the continuation chunks (start > 0). The first
                            // chunk of a run injects immediately under the command's one-time pre-send
                            // foreground gate, so short text and the leading burst add no latency.
                            segments.Add(new SendInputSegment(chunk.ToArray(), ThrottleBefore: start > 0));
                        }
                    }
                    break;
            }
        }

        return segments;
    }

    /// <summary>
    /// Flattens key actions into a single un-chunked SendInput batch — the flat view over
    /// <see cref="BuildSendInputSegments"/> (the single owner of the action→INPUT encoding), so callers and
    /// tests that need the whole ordered event sequence share exactly the same per-action encoding as
    /// throttled delivery. Chunk size only groups the events into segments; flattening yields the same
    /// sequence regardless of <see cref="s_textChunkChars"/>.
    /// </summary>
    internal static INPUT[] BuildSendInputBatch(IReadOnlyList<KeyAction> actions, Func<char, short>? vkKeyScan = null) =>
        BuildSendInputSegments(actions, vkKeyScan).SelectMany(segment => segment.Events).ToArray();

    /// <summary>
    /// Appends a chord's events in order: each modifier down, the main key down then up, then each
    /// modifier up in reverse — so the modifiers wrap the key press symmetrically.
    /// </summary>
    private static void AppendChordEvents(List<INPUT> inputs, KeyChord chord)
    {
        foreach (var mod in chord.Modifiers)
        {
            inputs.Add(KeyEvent(mod, IsExtended(mod), keyUp: false));
        }

        inputs.Add(KeyEvent(chord.Vk, chord.Extended, keyUp: false));
        inputs.Add(KeyEvent(chord.Vk, chord.Extended, keyUp: true));

        for (int i = chord.Modifiers.Count - 1; i >= 0; i--)
        {
            inputs.Add(KeyEvent(chord.Modifiers[i], IsExtended(chord.Modifiers[i]), keyUp: true));
        }
    }

    /// <summary>
    /// Best-effort release of every key pressed in <paramref name="batch"/> — emits a matching key-up for
    /// each key-down event, in reverse order, so a partial <c>SendInput</c> can't leave a
    /// modifier or key logically stuck down. Failures here are swallowed (we're already on the error path).
    /// </summary>
    internal static void ReleaseHeldKeys(INPUT[] batch)
    {
        var ups = new List<INPUT>();
        for (int i = batch.Length - 1; i >= 0; i--)
        {
            if (batch[i].type != INPUT_TYPE.INPUT_KEYBOARD)
            {
                continue;
            }

            var ki = batch[i].Anonymous.ki;
            if ((ki.dwFlags & KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP) != 0)
            {
                continue; // already an up event
            }

            ups.Add(new INPUT
            {
                type = INPUT_TYPE.INPUT_KEYBOARD,
                Anonymous = { ki = new KEYBDINPUT
                {
                    wVk = ki.wVk,
                    wScan = ki.wScan,
                    dwFlags = ki.dwFlags | KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP
                }}
            });
        }

        if (ups.Count == 0)
        {
            return;
        }

        s_sendInput(ups.ToArray());
    }

    internal static INPUT KeyEvent(ushort vk, bool extended, bool keyUp)
    {
        var flags = (KEYBD_EVENT_FLAGS)0;
        if (keyUp) { flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP; }
        if (extended) { flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY; }

        return new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = { ki = new KEYBDINPUT { wVk = (VIRTUAL_KEY)vk, dwFlags = flags } }
        };
    }

    internal static INPUT UnicodeEvent(char ch, bool keyUp)
    {
        var flags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE;
        if (keyUp) { flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP; }

        return new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = flags } }
        };
    }

    /// <summary>
    /// Normalizes newline characters in synthesized text to a carriage return (<c>\r</c>, 0x0D) so a
    /// line break is actually inserted. Windows edit controls create a new line only on a carriage
    /// return: it maps to <c>VK_RETURN</c> (a real Enter key for SendInput) and is honored as
    /// <c>WM_CHAR 0x0D</c> for PostMessage. A bare line feed (<c>\n</c>, 0x0A) does not map to a plain
    /// Enter — on a US layout <c>VkKeyScan('\n')</c> returns 0x020D (VK_RETURN + Ctrl), so SendInput takes
    /// the Ctrl/Alt Unicode-packet fallback and PostMessage sends <c>WM_CHAR 0x0A</c>; either way edit
    /// controls silently ignore the raw 0x0A, so the newline would otherwise vanish (issue #658). Collapsing
    /// <c>\r\n</c> to a single <c>\r</c> keeps a CRLF as one newline rather than two.
    /// </summary>
    internal static string NormalizeNewlines(string text)
    {
        // No line feed → nothing to normalize (a lone \r already inserts a newline correctly).
        if (text.IndexOf('\n') < 0)
        {
            return text;
        }

        return text.Replace("\r\n", "\r").Replace('\n', '\r');
    }

    /// <summary>
    /// Appends real per-character key events for SendInput. Maps the character to a virtual key (plus Shift)
    /// via the active keyboard layout so the target sees a genuine WM_KEYDOWN (KeyDown event with the correct
    /// virtual key) and the OS composes the matching WM_CHAR (TextChanged) — i.e. per-keystroke fidelity.
    /// Characters not reachable on the current layout, or requiring Ctrl/AltGr, fall back to a Unicode packet
    /// so the exact character still lands.
    /// </summary>
    private static void AppendCharEvents(List<INPUT> inputs, char ch, Func<char, short> vkKeyScan)
    {
        short scan = vkKeyScan(ch);
        int lo = scan & 0xFF;
        int hi = (scan >> 8) & 0xFF;

        bool mappable = scan != -1 && lo != 0xFF;
        bool needsCtrlOrAlt = (hi & 0x02) != 0 || (hi & 0x04) != 0; // Ctrl / Alt (AltGr) — layout-specific

        if (!mappable || needsCtrlOrAlt)
        {
            inputs.Add(UnicodeEvent(ch, keyUp: false));
            inputs.Add(UnicodeEvent(ch, keyUp: true));
            return;
        }

        var vk = (ushort)lo;
        bool needsShift = (hi & 0x01) != 0;

        if (needsShift) { inputs.Add(KeyEvent(0x10, extended: false, keyUp: false)); } // Shift down
        inputs.Add(KeyEvent(vk, extended: false, keyUp: false));
        inputs.Add(KeyEvent(vk, extended: false, keyUp: true));
        if (needsShift) { inputs.Add(KeyEvent(0x10, extended: false, keyUp: true)); }  // Shift up
    }

    internal static bool IsExtended(ushort vk) => vk is 0x5B or 0x5C or 0x5D;
}
