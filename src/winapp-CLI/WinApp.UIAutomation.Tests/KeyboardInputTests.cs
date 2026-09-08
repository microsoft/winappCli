// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class KeyboardInputTests
{
    [TestInitialize]
    public void Initialize() => ResetSeams();

    [TestCleanup]
    public void Cleanup() => KeyboardInput.ResetNativeSeams();

    [TestMethod]
    public void BuildKeyLParam_ComposesScanExtendedSysAndKeyUpBits()
    {
        var down = KeyboardInput.BuildKeyLParam(isSys: true, keyUp: false, extended: true, scan: 0x1Du);
        Assert.AreEqual(1u | (0x1Du << 16) | (1u << 24) | (1u << 29), down);

        var up = KeyboardInput.BuildKeyLParam(isSys: false, keyUp: true, extended: false, scan: 0x2Eu);
        Assert.AreEqual(1u | (0x2Eu << 16) | (1u << 30) | (1u << 31), up);
    }

    [TestMethod]
    public void IsExtended_RecognizesWindowsMenuKeysOnly()
    {
        Assert.IsTrue(KeyboardInput.IsExtended(0x5B));
        Assert.IsTrue(KeyboardInput.IsExtended(0x5C));
        Assert.IsTrue(KeyboardInput.IsExtended(0x5D));
        Assert.IsFalse(KeyboardInput.IsExtended(0x41));
    }

    [TestMethod]
    public void BuildSendInputBatch_ChordOrdersModifierDownKeyDownKeyUpModifierUp()
    {
        var inputs = KeyboardInput.BuildSendInputBatch([
            new KeyChord([0x11, 0x5B], 0x2E, Extended: true)
        ], _ => 0);

        Assert.AreEqual(6, inputs.Length);
        AssertKey(inputs[0], 0x11, (KEYBD_EVENT_FLAGS)0);
        AssertKey(inputs[1], 0x5B, KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY);
        AssertKey(inputs[2], 0x2E, KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY);
        AssertKey(inputs[3], 0x2E, KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY | KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertKey(inputs[4], 0x5B, KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY | KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertKey(inputs[5], 0x11, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
    }

    [TestMethod]
    public void BuildSendInputBatch_TextUsesMappableShiftAndUnicodeFallbackBranches()
    {
        short Scan(char ch) => ch switch
        {
            'a' => 0x41,
            'A' => 0x0141,
            '€' => unchecked((short)0x0645),
            _ => -1
        };

        var inputs = KeyboardInput.BuildSendInputBatch([new TextInput("aA€☃")], Scan);

        Assert.AreEqual(10, inputs.Length);
        AssertKey(inputs[0], 0x41, (KEYBD_EVENT_FLAGS)0);
        AssertKey(inputs[1], 0x41, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertKey(inputs[2], 0x10, (KEYBD_EVENT_FLAGS)0);
        AssertKey(inputs[3], 0x41, (KEYBD_EVENT_FLAGS)0);
        AssertKey(inputs[4], 0x41, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertKey(inputs[5], 0x10, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertUnicode(inputs[6], '€', keyUp: false);
        AssertUnicode(inputs[7], '€', keyUp: true);
        AssertUnicode(inputs[8], '☃', keyUp: false);
        AssertUnicode(inputs[9], '☃', keyUp: true);
    }

    [TestMethod]
    public void Send_PostMessage_EmitsExactKeyAndCharMessages()
    {
        var posted = new List<(uint Message, nuint WParam, nint LParam)>();
        var sleeps = new List<int>();
        KeyboardInput.s_mapVirtualKey = vk => vk + 1u;
        KeyboardInput.s_sleep = sleeps.Add;
        KeyboardInput.s_postMessage = (_, message, wParam, lParam) => posted.Add((message, wParam.Value, lParam.Value));

        KeyboardInput.Send(123, [new KeyChord([0x12], 0x73, Extended: false), new TextInput("x")], KeyTransport.PostMessage);

        Assert.AreEqual(5, posted.Count);
        Assert.AreEqual(PInvoke.WM_KEYDOWN, posted[0].Message);
        Assert.AreEqual((nuint)0x12, posted[0].WParam);
        Assert.AreEqual((nint)(1u | (0x13u << 16)), posted[0].LParam);
        Assert.AreEqual(PInvoke.WM_SYSKEYDOWN, posted[1].Message);
        Assert.AreEqual((nuint)0x73, posted[1].WParam);
        Assert.AreEqual((nint)(int)(1u | (0x74u << 16) | (1u << 29)), posted[1].LParam);
        Assert.AreEqual(PInvoke.WM_SYSKEYUP, posted[2].Message);
        Assert.AreEqual(PInvoke.WM_KEYUP, posted[3].Message);
        Assert.AreEqual(PInvoke.WM_CHAR, posted[4].Message);
        Assert.AreEqual((nuint)'x', posted[4].WParam);
        Assert.AreEqual(2, sleeps.Count);
        Assert.AreEqual(5, sleeps[0]);
        Assert.AreEqual(5, sleeps[1]);
    }

    [TestMethod]
    public void Send_PostMessage_RequiresTargetWindow()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            KeyboardInput.Send(0, [new TextInput("x")], KeyTransport.PostMessage));
        StringAssert.Contains(ex.Message, "requires a target window");
    }

    [TestMethod]
    public void Send_UnknownTransportThrows()
    {
        var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            KeyboardInput.Send(1, [], (KeyTransport)99));
        Assert.AreEqual("transport", ex.ParamName);
    }

    [TestMethod]
    public void Send_SendInput_EmptyBatchDoesNotInvokeNativeSeam()
    {
        var calls = 0;
        KeyboardInput.s_sendInput = inputs => { calls++; return (uint)inputs.Length; };

        KeyboardInput.Send(0, [], KeyTransport.SendInput);

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void Send_SendInput_InvokesSeamWithExpectedBatch()
    {
        INPUT[]? observed = null;
        KeyboardInput.s_sendInput = inputs => { observed = inputs; return (uint)inputs.Length; };

        KeyboardInput.Send(0, [new KeyChord([], 0x41, Extended: false)], KeyTransport.SendInput);

        Assert.IsNotNull(observed);
        Assert.AreEqual(2, observed.Length);
        AssertKey(observed[0], 0x41, (KEYBD_EVENT_FLAGS)0);
        AssertKey(observed[1], 0x41, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
    }

    [TestMethod]
    public void Send_SendInput_ShortWriteReleasesPressedKeysInReverseOrder()
    {
        var batches = new List<INPUT[]>();
        KeyboardInput.s_foregroundWindowIsNull = () => false;
        KeyboardInput.s_sendInput = inputs =>
        {
            batches.Add(inputs.ToArray());
            return batches.Count == 1 ? 1u : (uint)inputs.Length;
        };

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            KeyboardInput.Send(0, [new KeyChord([0x11], 0x41, Extended: false)], KeyTransport.SendInput));

        StringAssert.Contains(ex.Message, "partially applied");
        // This first segment failed with nothing typed before it, so the message must NOT warn about
        // already-applied input and the plain retry hint stands.
        Assert.IsFalse(ex.Message.Contains("verify or clear it before retrying"),
            "a first-segment failure typed nothing beforehand, so it must not warn about already-applied input.");
        StringAssert.Contains(ex.Message, "retry the gesture");
        Assert.AreEqual(2, batches.Count);
        Assert.AreEqual(4, batches[0].Length);
        Assert.AreEqual(2, batches[1].Length);
        AssertKey(batches[1][0], 0x41, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertKey(batches[1][1], 0x11, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
    }

    [TestMethod]
    [DataRow(true, "no interactive desktop")]
    [DataRow(false, "higher integrity level")]
    public void Send_SendInput_ZeroWriteReportsPreciseReason(bool noForeground, string expected)
    {
        KeyboardInput.s_foregroundWindowIsNull = () => noForeground;
        KeyboardInput.s_sendInput = inputs => 0;

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            KeyboardInput.Send(0, [new KeyChord([], 0x41, Extended: false)], KeyTransport.SendInput));

        StringAssert.Contains(ex.Message, expected);
    }

    [TestMethod]
    public void ReleaseHeldKeys_DoesNothingWhenNoDownKeyboardEvents()
    {
        var calls = 0;
        KeyboardInput.s_sendInput = inputs => { calls++; return (uint)inputs.Length; };
        var mouse = new INPUT { type = INPUT_TYPE.INPUT_MOUSE };
        var keyUp = KeyboardInput.KeyEvent(0x41, extended: false, keyUp: true);

        KeyboardInput.ReleaseHeldKeys([mouse, keyUp]);

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void BuildSendInputSegments_ChordIsOneSegmentAndTextSplitsByChunkSize()
    {
        KeyboardInput.s_textChunkChars = 2;

        var segments = KeyboardInput.BuildSendInputSegments([
            new KeyChord([0x11], 0x41, Extended: false),
            new TextInput("abcd")
        ], _ => 0x41); // every char maps to vk 0x41 with no shift -> 2 events each

        Assert.AreEqual(3, segments.Count); // chord, "ab", "cd"
        Assert.AreEqual(4, segments[0].Events.Length); // ctrl-down, a-down, a-up, ctrl-up
        Assert.AreEqual(4, segments[1].Events.Length); // 2 chars * 2 events
        Assert.AreEqual(4, segments[2].Events.Length);

        // Only the 2nd chunk of the text run is a throttled continuation: the chord and the run's first
        // chunk inject immediately (no pacing, no foreground re-check).
        Assert.IsFalse(segments[0].ThrottleBefore, "a chord is never throttled.");
        Assert.IsFalse(segments[1].ThrottleBefore, "the first chunk of a text run is not throttled.");
        Assert.IsTrue(segments[2].ThrottleBefore, "a continuation chunk of a split text run is throttled.");
    }

    [TestMethod]
    public void Send_SendInput_LongTextSplitsIntoThrottledChunksSleepingBetweenOnly()
    {
        var batches = new List<INPUT[]>();
        var sleeps = new List<int>();
        KeyboardInput.s_vkKeyScan = _ => 0x41; // no shift -> 2 events per char
        KeyboardInput.s_sleep = sleeps.Add;
        KeyboardInput.s_textChunkChars = 4;
        KeyboardInput.s_chunkDelayMs = 15;
        KeyboardInput.s_sendInput = inputs => { batches.Add(inputs.ToArray()); return (uint)inputs.Length; };

        // 10 chars -> chunks of 4, 4, 2 -> three SendInput calls.
        KeyboardInput.Send(0, [new TextInput(new string('a', 10))], KeyTransport.SendInput);

        Assert.AreEqual(3, batches.Count);
        Assert.AreEqual(8, batches[0].Length); // 4 chars * 2 events
        Assert.AreEqual(8, batches[1].Length);
        Assert.AreEqual(4, batches[2].Length); // final 2 chars
        // A sleep occurs only *between* segments, never after the last one.
        Assert.AreEqual(2, sleeps.Count);
        Assert.IsTrue(sleeps.TrueForAll(s => s == 15));
    }

    [TestMethod]
    public void Send_SendInput_ChordStaysAtomicWhenInterleavedWithChunkedText()
    {
        var batches = new List<INPUT[]>();
        var sleeps = new List<int>();
        KeyboardInput.s_vkKeyScan = _ => 0x41;
        KeyboardInput.s_sleep = sleeps.Add;
        KeyboardInput.s_textChunkChars = 4;
        KeyboardInput.s_chunkDelayMs = 7;
        KeyboardInput.s_sendInput = inputs => { batches.Add(inputs.ToArray()); return (uint)inputs.Length; };

        KeyboardInput.Send(0, [
            new KeyChord([0x11], 0x41, Extended: false),
            new TextInput(new string('a', 5))
        ], KeyTransport.SendInput);

        Assert.AreEqual(3, batches.Count); // chord, text[0..4], text[4..5]
        Assert.AreEqual(4, batches[0].Length); // whole chord in one atomic segment
        AssertKey(batches[0][0], 0x11, (KEYBD_EVENT_FLAGS)0);
        AssertKey(batches[0][3], 0x11, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        Assert.AreEqual(8, batches[1].Length);
        Assert.AreEqual(2, batches[2].Length);
        // Pacing is scoped to the split text run: the only sleep is between the two text chunks. The chord
        // itself and the chord → text boundary add NO delay, so short chord sequences never gain latency
        // (issue #657 follow-up).
        Assert.AreEqual(1, sleeps.Count);
        Assert.AreEqual(7, sleeps[0]);
    }

    [TestMethod]
    public void Send_SendInput_TextShorterThanOneChunkIsSingleCallWithNoThrottleSleep()
    {
        var batches = new List<INPUT[]>();
        var sleeps = new List<int>();
        KeyboardInput.s_vkKeyScan = _ => 0x41;
        KeyboardInput.s_sleep = sleeps.Add;
        KeyboardInput.s_textChunkChars = 16;
        KeyboardInput.s_sendInput = inputs => { batches.Add(inputs.ToArray()); return (uint)inputs.Length; };

        KeyboardInput.Send(0, [new TextInput("abc")], KeyTransport.SendInput);

        Assert.AreEqual(1, batches.Count);
        Assert.AreEqual(6, batches[0].Length);
        Assert.AreEqual(0, sleeps.Count, "text that fits in one chunk must not add throttle latency");
    }

    [TestMethod]
    public void Send_SendInput_ShortWriteOnLaterChunkReleasesOnlyThatChunk()
    {
        var batches = new List<INPUT[]>();
        KeyboardInput.s_foregroundWindowIsNull = () => false;
        KeyboardInput.s_vkKeyScan = _ => 0x0141; // vk 0x41 requiring Shift -> 4 events per char
        KeyboardInput.s_textChunkChars = 1;       // one char per segment
        KeyboardInput.s_chunkDelayMs = 0;
        KeyboardInput.s_sendInput = inputs =>
        {
            batches.Add(inputs.ToArray());
            return batches.Count == 2 ? 1u : (uint)inputs.Length; // second segment short-writes
        };

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            KeyboardInput.Send(0, [new TextInput("ab")], KeyTransport.SendInput));

        StringAssert.Contains(ex.Message, "partially applied");
        // A later chunk failed after the first already landed, so the message must warn that a literal retry
        // would duplicate the text already applied — and must drop the bare "retry the gesture" hint that
        // would invite exactly that duplication (issue #657 follow-up).
        StringAssert.Contains(ex.Message, "verify or clear it before retrying");
        Assert.IsFalse(ex.Message.Contains("retry the gesture"),
            "once earlier input has landed, the duplicate-inviting bare retry hint must be dropped.");
        // batches: [0] first char fully sent, [1] second char short write, [2] release of ONLY the second char.
        Assert.AreEqual(3, batches.Count);
        Assert.AreEqual(4, batches[0].Length);
        Assert.AreEqual(4, batches[1].Length);
        Assert.AreEqual(2, batches[2].Length); // only the stranded segment's keys, reversed
        AssertKey(batches[2][0], 0x41, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertKey(batches[2][1], 0x10, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
    }

    [TestMethod]
    public void Send_SendInput_ZeroWriteAfterEarlierChunkWarnsInputAlreadyApplied()
    {
        // If a later chunk is refused outright (0 written) after an earlier chunk already landed, the failure
        // must not read as if nothing was typed. It leads with a caveat that earlier input applied — so a
        // retry doesn't silently duplicate it — while still carrying the precise zero-write reason
        // (issue #657 follow-up).
        var batches = new List<INPUT[]>();
        KeyboardInput.s_foregroundWindowIsNull = () => false;
        KeyboardInput.s_vkKeyScan = _ => 0x41; // no shift -> two balanced events per char
        KeyboardInput.s_textChunkChars = 1;    // one char per segment
        KeyboardInput.s_chunkDelayMs = 0;
        KeyboardInput.s_sendInput = inputs =>
        {
            batches.Add(inputs.ToArray());
            return batches.Count == 1 ? (uint)inputs.Length : 0u; // first char lands, second is refused
        };

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            KeyboardInput.Send(0, [new TextInput("ab")], KeyTransport.SendInput));

        StringAssert.Contains(ex.Message, "earlier keystrokes already landed");
        StringAssert.Contains(ex.Message, "higher integrity level"); // precise zero-write reason preserved
    }

    [TestMethod]
    public void Send_SendInput_ZeroWriteOnFirstSegmentDoesNotWarnInputAlreadyApplied()
    {
        // The partial-apply caveat must be scoped: a zero write on the very first segment typed nothing, so
        // the message stays the plain reason with no "already landed" wording (issue #657 follow-up).
        KeyboardInput.s_foregroundWindowIsNull = () => false;
        KeyboardInput.s_sendInput = _ => 0;

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            KeyboardInput.Send(0, [new KeyChord([], 0x41, Extended: false)], KeyTransport.SendInput));

        StringAssert.Contains(ex.Message, "higher integrity level");
        Assert.IsFalse(ex.Message.Contains("earlier keystrokes already landed"),
            "a first-segment failure typed nothing beforehand, so it must not warn about already-applied input.");
    }

    [TestMethod]
    public void Send_SendInput_ForegroundDriftMidInjectionAbortsWithoutSprayingRemainingKeys()
    {
        // H1 (issue #657 follow-up): a long literal-text payload is paced over many SendInput calls spanning
        // seconds. If the target loses the foreground partway through, the remaining chunks must NOT be
        // sprayed into whatever window grabbed focus. The loop re-verifies the target owns the foreground
        // before each throttled continuation chunk (the 2nd+ chunk of a split text run) and aborts on drift
        // with a ForegroundLostException (mapped to foreground_not_target). The first chunk injects under the
        // command's one-time pre-send gate, so a 3-chunk run performs two re-checks.
        var batches = new List<INPUT[]>();
        long? checkedHwnd = null;
        var checks = 0;
        KeyboardInput.s_vkKeyScan = _ => 0x41; // no shift -> two balanced events per char
        KeyboardInput.s_textChunkChars = 2;    // 6 chars -> three segments
        KeyboardInput.s_chunkDelayMs = 0;
        KeyboardInput.s_sendInput = inputs => { batches.Add(inputs.ToArray()); return (uint)inputs.Length; };
        KeyboardInput.s_foregroundBelongsToTarget = hwnd =>
        {
            checkedHwnd = hwnd;
            checks++;
            return checks < 2; // owns the foreground for the first continuation chunk, then focus drifts before the next
        };

        var ex = Assert.ThrowsExactly<ForegroundLostException>(() =>
            KeyboardInput.Send(0x1234, [new TextInput(new string('a', 6))], KeyTransport.SendInput));

        StringAssert.Contains(ex.Message, "lost the foreground");
        Assert.AreEqual(0x1234L, checkedHwnd, "the target hwnd must be what is re-verified against the foreground.");

        // Only the first chunk and the one that passed its re-check were injected; the third was withheld and
        // — crucially — no further SendInput (not even a release) was sprayed into the window that stole focus.
        Assert.AreEqual(2, batches.Count);

        // Nothing is left held after the abort: across every injected event, key-downs and key-ups balance
        // (each already-sent segment is self-contained), so no modifier/key is stranded in the down state.
        int downs = 0, ups = 0;
        foreach (var batch in batches)
        {
            foreach (var input in batch)
            {
                if (input.type != INPUT_TYPE.INPUT_KEYBOARD) { continue; }
                if ((input.Anonymous.ki.dwFlags & KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP) != 0) { ups++; } else { downs++; }
            }
        }

        Assert.AreEqual(downs, ups, "focus-drift abort must not strand any key in the down state.");
        Assert.AreEqual(4, downs); // two 2-char segments, each char one key-down
    }

    [TestMethod]
    public void Send_SendInput_ForegroundHeldThroughoutInjectsEveryChunk()
    {
        // Guard the happy path of the H1 re-check: while the target keeps the foreground, every segment is
        // injected (the per-segment verification never aborts a legitimate long send).
        var batches = new List<INPUT[]>();
        KeyboardInput.s_vkKeyScan = _ => 0x41;
        KeyboardInput.s_textChunkChars = 2;
        KeyboardInput.s_chunkDelayMs = 0;
        KeyboardInput.s_sendInput = inputs => { batches.Add(inputs.ToArray()); return (uint)inputs.Length; };
        KeyboardInput.s_foregroundBelongsToTarget = _ => true;

        KeyboardInput.Send(0x1234, [new TextInput(new string('a', 6))], KeyTransport.SendInput);

        Assert.AreEqual(3, batches.Count); // all three 2-char segments delivered
    }

    [TestMethod]
    public void Send_SendInput_ProductionDefaultsChunkSeventeenCharsIntoTwoCallsWithDefaultDelay()
    {
        // Pins the shipping throttle defaults so a future change to DefaultTextChunkChars / DefaultChunkDelayMs
        // can't silently reintroduce #657 (long text injected as one over-queued burst that drops characters).
        // Intentionally does NOT override the chunk/delay seams — this test must read the real production
        // defaults (restored by ResetSeams in TestInitialize). The default chunk size is pinned *behaviorally*
        // below (17 chars must split into a 16-char + 1-char pair); guard here that the throttle keeps a real
        // pause between bursts (a 0ms default would remove the drain window and risk regressing #657). The
        // local copy keeps the assertion off a compile-time constant so the analyzer doesn't flag it.
        var defaultChunkDelayMs = KeyboardInput.DefaultChunkDelayMs;
        Assert.IsTrue(defaultChunkDelayMs > 0,
            "send-input throttle delay must stay positive so the target can drain its input queue between bursts (#657).");

        var batches = new List<INPUT[]>();
        var sleeps = new List<int>();
        KeyboardInput.s_vkKeyScan = _ => 0x41; // no shift -> two events per char
        KeyboardInput.s_sleep = sleeps.Add;
        KeyboardInput.s_sendInput = inputs => { batches.Add(inputs.ToArray()); return (uint)inputs.Length; };

        // 17 chars at the default 16-char chunk -> [16, 1] -> two SendInput calls and exactly one throttle sleep.
        KeyboardInput.Send(0, [new TextInput(new string('a', 17))], KeyTransport.SendInput);

        Assert.AreEqual(2, batches.Count);
        Assert.AreEqual(32, batches[0].Length); // 16 chars * 2 events
        Assert.AreEqual(2, batches[1].Length);  // final 1 char * 2 events
        Assert.AreEqual(1, sleeps.Count);
        Assert.AreEqual(KeyboardInput.DefaultChunkDelayMs, sleeps[0]);
    }

    [TestMethod]
    [DataRow("abc", "abc")]           // no line feed → unchanged
    [DataRow("a\nb", "a\rb")]         // bare LF → CR
    [DataRow("a\r\nb", "a\rb")]       // CRLF collapses to a single CR (one newline)
    [DataRow("a\rb", "a\rb")]         // lone CR is left as-is
    [DataRow("a\n\nb", "a\r\rb")]     // two LFs → two CRs (two newlines preserved)
    [DataRow("a\r\n\r\nb", "a\r\rb")] // two CRLFs → two CRs (two newlines preserved)
    [DataRow("\n", "\r")]
    public void NormalizeNewlines_ConvertsEveryNewlineFormToCarriageReturn(string input, string expected)
    {
        // Edit controls insert a line break only on \r, so \n (and \r\n) must be normalized to \r
        // before delivery, otherwise a bare line feed is silently dropped (issue #658).
        Assert.AreEqual(expected, KeyboardInput.NormalizeNewlines(input));
    }

    [TestMethod]
    [DataRow("a\nb")]   // bare line feed — was silently dropped before the fix
    [DataRow("a\r\nb")] // CRLF collapses to a single newline
    [DataRow("a\rb")]   // lone carriage return already worked — regression guard
    public void BuildSendInputBatch_NewlineFormsDeliverAsSingleReturnKey(string text)
    {
        // After NormalizeNewlines runs, every newline form arrives as '\r', which VkKeyScan maps to
        // VK_RETURN (0x0D) — a real Enter key. (Without it a bare '\n' would take the Unicode-packet
        // fallback that edit controls ignore: on a US layout VkKeyScan('\n') returns 0x020D =
        // VK_RETURN+Ctrl, which the Ctrl/Alt guard routes to a raw 0x0A packet.)
        short Scan(char ch) => ch switch
        {
            'a' => 0x41,
            'b' => 0x42,
            '\r' => 0x0D,
            _ => -1,
        };

        var inputs = KeyboardInput.BuildSendInputBatch([new TextInput(text)], Scan);

        // 'a' down/up, one VK_RETURN down/up (never a Unicode 0x0A packet), 'b' down/up.
        Assert.AreEqual(6, inputs.Length);
        AssertKey(inputs[0], 0x41, (KEYBD_EVENT_FLAGS)0);
        AssertKey(inputs[1], 0x41, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertKey(inputs[2], 0x0D, (KEYBD_EVENT_FLAGS)0);
        AssertKey(inputs[3], 0x0D, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        AssertKey(inputs[4], 0x42, (KEYBD_EVENT_FLAGS)0);
        AssertKey(inputs[5], 0x42, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
    }

    [TestMethod]
    [DataRow("a\nb")]   // bare line feed — was silently dropped before the fix
    [DataRow("a\r\nb")] // CRLF collapses to a single newline
    [DataRow("a\rb")]   // lone carriage return already worked — regression guard
    public void Send_PostMessage_NewlineFormsDeliverAsSingleCarriageReturnChar(string text)
    {
        var chars = new List<char>();
        KeyboardInput.s_postMessage = (_, message, wParam, _) =>
        {
            if (message == PInvoke.WM_CHAR) { chars.Add((char)wParam.Value); }
        };

        KeyboardInput.Send(123, [new TextInput(text)], KeyTransport.PostMessage);

        // WM_CHAR carries a single carriage return (0x0D) for the newline — never a bare 0x0A.
        Assert.AreEqual("a\rb", new string(chars.ToArray()));
    }

    [TestMethod]
    public void ParseThenBuildSendInputBatch_TextNewlineEscape_DeliversReturnKeyNotDroppedUnicode()
    {
        // End-to-end repro of issue #658: `text=A\nB` decodes to a line feed, which must reach the
        // target as a real Enter key (VK_RETURN) instead of a Unicode 0x0A packet that edit controls
        // silently drop.
        short Scan(char ch) => ch switch
        {
            'A' => 0x41,
            'B' => 0x42,
            '\r' => 0x0D,
            _ => -1,
        };

        var actions = KeyStringParser.Parse(@"text=A\nB");
        var inputs = KeyboardInput.BuildSendInputBatch(actions, Scan);

        Assert.AreEqual(1, inputs.Count(i =>
            i.type == INPUT_TYPE.INPUT_KEYBOARD &&
            i.Anonymous.ki.wVk == (VIRTUAL_KEY)0x0D &&
            (i.Anonymous.ki.dwFlags & KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP) == 0),
            "The \\n must be delivered as exactly one VK_RETURN key-down.");
        Assert.AreEqual(0, inputs.Count(i =>
            (i.Anonymous.ki.dwFlags & KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE) != 0),
            "The \\n must not fall through to a Unicode packet — that's the dropped-newline bug.");
    }

    private static void AssertKey(INPUT input, ushort vk, KEYBD_EVENT_FLAGS flags)
    {
        Assert.AreEqual(INPUT_TYPE.INPUT_KEYBOARD, input.type);
        Assert.AreEqual((VIRTUAL_KEY)vk, input.Anonymous.ki.wVk);
        Assert.AreEqual(flags, input.Anonymous.ki.dwFlags);
    }

    private static void AssertUnicode(INPUT input, char ch, bool keyUp)
    {
        Assert.AreEqual(INPUT_TYPE.INPUT_KEYBOARD, input.type);
        Assert.AreEqual((VIRTUAL_KEY)0, input.Anonymous.ki.wVk);
        Assert.AreEqual(ch, input.Anonymous.ki.wScan);
        var expected = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | (keyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0);
        Assert.AreEqual(expected, input.Anonymous.ki.dwFlags);
    }

    [TestMethod]
    public void RealKeyboardInput_DelegatesToKeyboardInput()
    {
        var sent = new List<INPUT[]>();
        KeyboardInput.s_sendInput = inputs => { sent.Add(inputs.ToArray()); return (uint)inputs.Length; };

        new RealKeyboardInput().Send(0, [new KeyChord([], 0x41, Extended: false)], KeyTransport.SendInput);

        Assert.AreEqual(1, sent.Count,
            "RealKeyboardInput.Send must delegate to KeyboardInput.Send, which batches the chord into one SendInput seam call.");
    }

    private static void ResetSeams()
    {
        KeyboardInput.s_sendInput = inputs => (uint)inputs.Length;
        KeyboardInput.s_postMessage = (_, _, _, _) => { };
        KeyboardInput.s_vkKeyScan = PInvoke.VkKeyScan;
        KeyboardInput.s_mapVirtualKey = vk => PInvoke.MapVirtualKey(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);
        KeyboardInput.s_foregroundWindowIsNull = () => false;
        KeyboardInput.s_foregroundBelongsToTarget = _ => true;
        KeyboardInput.s_sleep = _ => { };
        KeyboardInput.s_textChunkChars = KeyboardInput.DefaultTextChunkChars;
        KeyboardInput.s_chunkDelayMs = KeyboardInput.DefaultChunkDelayMs;
    }
}


