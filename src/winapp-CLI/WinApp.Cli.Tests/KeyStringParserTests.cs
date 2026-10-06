// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class KeyStringParserTests
{
    [TestMethod]
    public void Parse_NamedKey_ProducesSingleChord()
    {
        var actions = KeyStringParser.Parse("enter");
        Assert.AreEqual(1, actions.Count);
        var chord = (KeyChord)actions[0];
        Assert.AreEqual(0, chord.Modifiers.Count);
        Assert.AreEqual(0x0D, chord.Vk);   // VK_RETURN
        Assert.IsFalse(chord.Extended);
    }

    [TestMethod]
    public void Parse_ArrowKey_IsExtended()
    {
        var actions = KeyStringParser.Parse("down");
        var chord = (KeyChord)actions[0];
        Assert.AreEqual(0x28, chord.Vk);   // VK_DOWN
        Assert.IsTrue(chord.Extended);
    }

    [TestMethod]
    public void Parse_Sequence_ProducesOrderedChords()
    {
        var actions = KeyStringParser.Parse("down down enter");
        Assert.AreEqual(3, actions.Count);
        Assert.AreEqual(0x28, ((KeyChord)actions[0]).Vk);
        Assert.AreEqual(0x28, ((KeyChord)actions[1]).Vk);
        Assert.AreEqual(0x0D, ((KeyChord)actions[2]).Vk);
    }

    [TestMethod]
    public void Parse_ModifierCombo_CapturesModifiersAndMainKey()
    {
        var actions = KeyStringParser.Parse("ctrl+shift+a");
        Assert.AreEqual(1, actions.Count);
        var chord = (KeyChord)actions[0];
        CollectionAssert.AreEqual(new ushort[] { 0x11, 0x10 }, chord.Modifiers.ToArray()); // CONTROL, SHIFT
        Assert.AreEqual(0x41, chord.Vk); // 'A'
    }

    [TestMethod]
    public void Parse_AltCombo_MapsAltModifier()
    {
        var actions = KeyStringParser.Parse("alt+f4");
        var chord = (KeyChord)actions[0];
        CollectionAssert.AreEqual(new ushort[] { 0x12 }, chord.Modifiers.ToArray()); // MENU (ALT)
        Assert.AreEqual(0x73, chord.Vk); // VK_F4
    }

    [TestMethod]
    public void Parse_LiteralText_ProducesTextInput()
    {
        var actions = KeyStringParser.Parse("hello");
        Assert.AreEqual(1, actions.Count);
        Assert.IsInstanceOfType<TextInput>(actions[0]);
        Assert.AreEqual("hello", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    [DataRow("vk=0x42", (ushort)0x42)]
    [DataRow("vk=66", (ushort)66)]
    [DataRow("VK=0X1B", (ushort)0x1B)]
    public void Parse_RawVirtualKey_ParsesValue(string token, ushort expected)
    {
        var actions = KeyStringParser.Parse(token);
        Assert.AreEqual(expected, ((KeyChord)actions[0]).Vk);
    }

    [TestMethod]
    public void Parse_MixedTokens_PreservesOrderAndTypes()
    {
        var actions = KeyStringParser.Parse("ctrl+a delete world");
        Assert.AreEqual(3, actions.Count);
        Assert.IsInstanceOfType<KeyChord>(actions[0]);
        Assert.AreEqual(0x2E, ((KeyChord)actions[1]).Vk); // VK_DELETE
        Assert.IsInstanceOfType<TextInput>(actions[2]);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void Parse_Empty_Throws(string keys)
    {
        Assert.ThrowsExactly<FormatException>(() => KeyStringParser.Parse(keys));
    }

    [TestMethod]
    [DataRow("vk=0xZZ")]
    [DataRow("vk=99999")]
    [DataRow("vk=0")]
    public void Parse_InvalidVirtualKey_Throws(string token)
    {
        Assert.ThrowsExactly<FormatException>(() => KeyStringParser.Parse(token));
    }

    [TestMethod]
    public void Parse_ModifierLedComboWithUnknownMainKey_Throws()
    {
        // Leading segment IS a real modifier, so the user clearly intended a combo — an unknown main
        // key is an actionable error rather than something to type literally.
        Assert.ThrowsExactly<FormatException>(() => KeyStringParser.Parse("ctrl+bogus"));
    }

    [TestMethod]
    [DataRow("hyper+a")]   // unknown leading "modifier" → not a combo
    [DataRow("a+b")]       // plain literal that happens to contain '+'
    [DataRow("C++")]       // language name, not a combo
    [DataRow("1+1")]
    public void Parse_PlusToken_WithoutLeadingModifier_IsLiteralText(string token)
    {
        var actions = KeyStringParser.Parse(token);
        Assert.AreEqual(1, actions.Count);
        Assert.IsInstanceOfType<TextInput>(actions[0]);
        Assert.AreEqual(token, ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    [DataRow("ctrl++a")]        // doubled '+'
    [DataRow("ctrl+")]          // trailing '+'
    [DataRow("ctrl++")]         // trailing doubled '+'
    [DataRow("shift++enter")]   // doubled '+' before a named key
    [DataRow("ctrl+shift++t")]  // doubled '+' mid-combo
    public void Parse_MalformedModifierChord_Throws(string token)
    {
        // A modifier-led token with an empty segment around '+' is an unambiguously malformed combo.
        // Surface it instead of silently dropping the empty segment and pressing a *different* chord
        // (e.g. "ctrl++a" must not quietly become "ctrl+a") (M1).
        Assert.ThrowsExactly<FormatException>(() => KeyStringParser.Parse(token));
    }

    [TestMethod]
    [DataRow("ctrl+a+b")]
    [DataRow("ctrl+bogus+f12")]
    [DataRow("shift+numlock+a")]
    public void Parse_ModifierLedWithNonModifierMiddle_Throws(string token)
    {
        // Issue #952: the token is modifier-led, so it was meant as a combo. Typing its spelling into the
        // target would be a silent surprise; text= and --verbatim remain the explicit literal escapes.
        var ex = Assert.ThrowsExactly<FormatException>(() => KeyStringParser.Parse(token));
        StringAssert.Contains(ex.Message, $"text={token}");
    }

    [TestMethod]
    [DataRow("ctrl+capslock+f12", new ushort[] { 0x11, 0x14 }, (ushort)0x7B)]
    [DataRow("capslock+ctrl+f12", new ushort[] { 0x14, 0x11 }, (ushort)0x7B)]
    [DataRow("insert+t", new ushort[] { 0x2D }, (ushort)0x54)]
    [DataRow("ins+space", new ushort[] { 0x2D }, (ushort)0x20)]
    public void Parse_ScreenReaderKeyAsModifier_IsChord(string token, ushort[] expectedModifiers, ushort expectedVk)
    {
        var actions = KeyStringParser.Parse(token);
        Assert.AreEqual(1, actions.Count);
        var chord = (KeyChord)actions[0];
        CollectionAssert.AreEqual(expectedModifiers, chord.Modifiers.ToArray());
        Assert.AreEqual(expectedVk, chord.Vk);
        Assert.IsTrue(KeyStringParser.HoldsScreenReaderKey(actions));
    }

    [TestMethod]
    [DataRow("capslock")]
    [DataRow("insert")]
    [DataRow("ctrl+insert")]
    [DataRow("shift+capslock")]
    public void Parse_ScreenReaderKeyAsMainKey_DoesNotHoldIt(string token)
    {
        // Pressing capslock/insert as the main key (alone or as ctrl+insert = copy) is unchanged and
        // doesn't count as holding a screen-reader key.
        var actions = KeyStringParser.Parse(token);
        Assert.IsInstanceOfType<KeyChord>(actions[0]);
        Assert.IsFalse(KeyStringParser.HoldsScreenReaderKey(actions));
        Assert.IsFalse(KeyStringParser.HoldsCapsLock(actions));
    }

    [TestMethod]
    public void HoldsCapsLock_OnlyForCapsLockModifier()
    {
        Assert.IsTrue(KeyStringParser.HoldsCapsLock(KeyStringParser.Parse("ctrl+capslock+f12")));
        Assert.IsFalse(KeyStringParser.HoldsCapsLock(KeyStringParser.Parse("ctrl+insert+f12")));
    }

    [TestMethod]
    public void Parse_QuotedPhrase_PreservesSpacesAsSingleLiteral()
    {
        // Regression for the "Hello world" → "Helloworld" whitespace-loss bug: adjacent literal words
        // coalesce into one TextInput with the space preserved.
        var actions = KeyStringParser.Parse("Hello world");
        Assert.AreEqual(1, actions.Count);
        Assert.IsInstanceOfType<TextInput>(actions[0]);
        Assert.AreEqual("Hello world", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    public void Parse_LiteralRunSeparatedByKey_StaysDistinct()
    {
        // A key between literal runs breaks the coalescing: "Hello enter world" → text, key, text.
        var actions = KeyStringParser.Parse("Hello enter world");
        Assert.AreEqual(3, actions.Count);
        Assert.AreEqual("Hello", ((TextInput)actions[0]).Text);
        Assert.AreEqual(0x0D, ((KeyChord)actions[1]).Vk); // VK_RETURN
        Assert.AreEqual("world", ((TextInput)actions[2]).Text);
    }

    [TestMethod]
    public void Parse_TrailingLiteralAfterCombo_CoalescesOnlyLiterals()
    {
        // "ctrl+a Hello world" → chord, then the two literal words merge into one TextInput.
        var actions = KeyStringParser.Parse("ctrl+a Hello world");
        Assert.AreEqual(2, actions.Count);
        Assert.IsInstanceOfType<KeyChord>(actions[0]);
        Assert.AreEqual("Hello world", ((TextInput)actions[1]).Text);
    }

    [TestMethod]
    [DataRow("control")]
    [DataRow("ctrl")]
    public void Parse_CtrlAliases_MapToControlVk(string alias)
    {
        var chord = (KeyChord)KeyStringParser.Parse($"{alias}+a")[0];
        CollectionAssert.AreEqual(new ushort[] { 0x11 }, chord.Modifiers.ToArray()); // VK_CONTROL
    }

    [TestMethod]
    [DataRow("alt")]
    [DataRow("menu")]
    public void Parse_AltAliases_MapToMenuVk(string alias)
    {
        var chord = (KeyChord)KeyStringParser.Parse($"{alias}+a")[0];
        CollectionAssert.AreEqual(new ushort[] { 0x12 }, chord.Modifiers.ToArray()); // VK_MENU
    }

    [TestMethod]
    [DataRow("win")]
    [DataRow("cmd")]
    [DataRow("super")]
    [DataRow("meta")]
    public void Parse_WinAliases_MapToLWinVk(string alias)
    {
        var chord = (KeyChord)KeyStringParser.Parse($"{alias}+a")[0];
        CollectionAssert.AreEqual(new ushort[] { 0x5B }, chord.Modifiers.ToArray()); // VK_LWIN
    }

    [TestMethod]
    [DataRow("enter", (ushort)0x0D)]
    [DataRow("return", (ushort)0x0D)]
    [DataRow("esc", (ushort)0x1B)]
    [DataRow("escape", (ushort)0x1B)]
    [DataRow("tab", (ushort)0x09)]
    [DataRow("space", (ushort)0x20)]
    [DataRow("backspace", (ushort)0x08)]
    [DataRow("bksp", (ushort)0x08)]
    [DataRow("del", (ushort)0x2E)]
    [DataRow("delete", (ushort)0x2E)]
    [DataRow("ins", (ushort)0x2D)]
    [DataRow("home", (ushort)0x24)]
    [DataRow("end", (ushort)0x23)]
    [DataRow("pgup", (ushort)0x21)]
    [DataRow("pagedown", (ushort)0x22)]
    [DataRow("printscreen", (ushort)0x2C)]
    [DataRow("apps", (ushort)0x5D)]
    public void Parse_NamedKeyAliases_ResolveToExpectedVk(string name, ushort expected)
    {
        var chord = (KeyChord)KeyStringParser.Parse(name)[0];
        Assert.AreEqual(0, chord.Modifiers.Count);
        Assert.AreEqual(expected, chord.Vk);
    }

    [TestMethod]
    [DataRow("home")]
    [DataRow("end")]
    [DataRow("pageup")]
    [DataRow("pagedown")]
    [DataRow("insert")]
    [DataRow("delete")]
    [DataRow("up")]
    [DataRow("down")]
    [DataRow("left")]
    [DataRow("right")]
    [DataRow("printscreen")]
    public void Parse_ExtendedKeys_SetExtendedFlag(string name)
    {
        Assert.IsTrue(((KeyChord)KeyStringParser.Parse(name)[0]).Extended);
    }

    [TestMethod]
    [DataRow("enter")]
    [DataRow("tab")]
    [DataRow("space")]
    [DataRow("f5")]
    [DataRow("capslock")]
    public void Parse_NonExtendedKeys_DoNotSetExtendedFlag(string name)
    {
        Assert.IsFalse(((KeyChord)KeyStringParser.Parse(name)[0]).Extended);
    }

    [TestMethod]
    [DataRow("f1", (ushort)0x70)]
    [DataRow("f5", (ushort)0x74)]
    [DataRow("f12", (ushort)0x7B)]
    [DataRow("f16", (ushort)0x7F)]
    public void Parse_FunctionKeys_ResolveToExpectedVk(string name, ushort expected)
    {
        Assert.AreEqual(expected, ((KeyChord)KeyStringParser.Parse(name)[0]).Vk);
    }

    [TestMethod]
    public void Parse_NamedKeysAreCaseInsensitive()
    {
        Assert.AreEqual(0x0D, ((KeyChord)KeyStringParser.Parse("ENTER")[0]).Vk);
        Assert.AreEqual(0x73, ((KeyChord)KeyStringParser.Parse("Alt+F4")[0]).Vk);
    }

    [TestMethod]
    public void Parse_MultipleModifiers_PreserveOrder()
    {
        var chord = (KeyChord)KeyStringParser.Parse("ctrl+alt+shift+del")[0];
        CollectionAssert.AreEqual(new ushort[] { 0x11, 0x12, 0x10 }, chord.Modifiers.ToArray());
        Assert.AreEqual(0x2E, chord.Vk); // VK_DELETE
        Assert.IsTrue(chord.Extended);
    }

    [TestMethod]
    public void Parse_ChordWithRawVkMainKey_Resolves()
    {
        var chord = (KeyChord)KeyStringParser.Parse("ctrl+vk=0x42")[0];
        CollectionAssert.AreEqual(new ushort[] { 0x11 }, chord.Modifiers.ToArray());
        Assert.AreEqual(0x42, chord.Vk);
    }

    [TestMethod]
    public void Parse_RawVkForExtendedKey_SetsExtendedFlag()
    {
        // vk=0x2E is Delete, an extended key — the flag must be inferred from the code.
        Assert.IsTrue(((KeyChord)KeyStringParser.Parse("vk=0x2E")[0]).Extended);
    }

    [TestMethod]
    public void Parse_LoneSingleCharacter_IsLiteralText()
    {
        // A bare single character is not a named/vk/chord token, so it is typed as literal text.
        // (Single-char → virtual-key resolution only applies to a chord's main key, e.g. ctrl+a.)
        var actions = KeyStringParser.Parse("a");
        Assert.AreEqual(1, actions.Count);
        Assert.IsInstanceOfType<TextInput>(actions[0]);
        Assert.AreEqual("a", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    public void Parse_ChordSingleCharMainKey_MapsViaKeyboardLayout()
    {
        // The single-char → VK path runs for a chord's main key; 'a' resolves to 0x41 on a US layout.
        var chord = (KeyChord)KeyStringParser.Parse("ctrl+a")[0];
        Assert.AreEqual(0x41, chord.Vk);
    }

    [TestMethod]
    public void Parse_ExtraWhitespaceBetweenTokens_IsIgnored()
    {
        var actions = KeyStringParser.Parse("  down    enter  ");
        Assert.AreEqual(2, actions.Count);
        Assert.AreEqual(0x28, ((KeyChord)actions[0]).Vk);
        Assert.AreEqual(0x0D, ((KeyChord)actions[1]).Vk);
    }

    [TestMethod]
    public void Parse_TabSeparatedTokens_AreSplit()
    {
        var actions = KeyStringParser.Parse("down\tenter");
        Assert.AreEqual(2, actions.Count);
        Assert.AreEqual(0x28, ((KeyChord)actions[0]).Vk);
        Assert.AreEqual(0x0D, ((KeyChord)actions[1]).Vk);
    }

    [TestMethod]
    public void Parse_ThreeLiteralWords_CoalesceWithSingleSpaces()
    {
        var actions = KeyStringParser.Parse("the quick brown");
        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual("the quick brown", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    public void Parse_NamedSpaceToken_IsAKeyNotText()
    {
        // "space" is a named key (VK_SPACE), so it breaks a literal run rather than coalescing.
        var actions = KeyStringParser.Parse("Hello space world");
        Assert.AreEqual(3, actions.Count);
        Assert.AreEqual("Hello", ((TextInput)actions[0]).Text);
        Assert.AreEqual(0x20, ((KeyChord)actions[1]).Vk); // VK_SPACE
        Assert.AreEqual("world", ((TextInput)actions[2]).Text);
    }

    [TestMethod]
    public void Parse_BarePlusToken_IsLiteralText()
    {
        // A token that is only "+" has no main key after splitting — treated as literal text.
        var actions = KeyStringParser.Parse("+");
        Assert.AreEqual(1, actions.Count);
        Assert.IsInstanceOfType<TextInput>(actions[0]);
        Assert.AreEqual("+", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    [DataRow("text=enter", "enter")]   // collides with a named key
    [DataRow("text=del", "del")]
    [DataRow("text=up", "up")]
    [DataRow("text=ctrl+a", "ctrl+a")] // collides with a modifier combo
    [DataRow("text=vk=0x42", "vk=0x42")]
    [DataRow("TEXT=Enter", "Enter")]   // prefix is case-insensitive; value is preserved verbatim
    public void Parse_TextEscape_ForcesLiteralEvenWhenColliding(string token, string expected)
    {
        var actions = KeyStringParser.Parse(token);
        Assert.AreEqual(1, actions.Count);
        Assert.IsInstanceOfType<TextInput>(actions[0]);
        Assert.AreEqual(expected, ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    public void Parse_TextEscape_CoalescesWithAdjacentLiterals()
    {
        // "text=down" escapes "down" to literal; the following plain word joins it with a space.
        var actions = KeyStringParser.Parse("text=down low");
        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual("down low", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    public void Parse_TextEscape_BreaksFromRealKeys()
    {
        // A real key between escaped-literal tokens stays a distinct key action.
        var actions = KeyStringParser.Parse("text=enter tab text=down");
        Assert.AreEqual(3, actions.Count);
        Assert.AreEqual("enter", ((TextInput)actions[0]).Text);
        Assert.AreEqual(0x09, ((KeyChord)actions[1]).Vk); // VK_TAB
        Assert.AreEqual("down", ((TextInput)actions[2]).Text);
    }

    [TestMethod]
    public void Parse_TextEscape_TypesLiteralPhraseThatCollidesWithKeyNames()
    {
        // The phrase "down down enter" is all key names; escaping each word with text= types it
        // verbatim as one literal instead of pressing Down, Down, Enter.
        var actions = KeyStringParser.Parse("text=down text=down text=enter");
        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual("down down enter", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    public void Parse_TextEscape_EmptyValue_IsEmptyLiteral()
    {
        var actions = KeyStringParser.Parse("text=");
        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual("", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    [DataRow(@"text=a\sb", "a b")]            // \s → single space inside one token
    [DataRow(@"text=a\s\sb", "a  b")]         // double space — not expressible without the escape
    [DataRow(@"text=a\tb", "a\tb")]           // \t → tab
    [DataRow(@"text=line1\nline2", "line1\nline2")] // \n → newline
    [DataRow(@"text=a\rb", "a\rb")]           // \r → carriage return
    [DataRow(@"text=a\\b", @"a\b")]           // \\ → literal backslash
    public void Parse_TextEscape_DecodesWhitespaceEscapes(string token, string expected)
    {
        // N2: the tokenizer splits on whitespace and re-joins literal runs with a single space, so
        // multiple/leading/tab/newline whitespace can't survive as raw spaces. Backslash escapes in a
        // text= value restore exact whitespace fidelity.
        var actions = KeyStringParser.Parse(token);
        Assert.AreEqual(1, actions.Count);
        Assert.IsInstanceOfType<TextInput>(actions[0]);
        Assert.AreEqual(expected, ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    public void Parse_TextEscape_UnknownEscape_KeepsBackslashLiteral()
    {
        // An unrecognised escape (\x) is left verbatim so real backslashes in text don't vanish.
        var actions = KeyStringParser.Parse(@"text=a\xb");
        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual(@"a\xb", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    public void Parse_TextEscape_LeadingSpace_IsPreserved()
    {
        // A leading space (which the whitespace tokenizer would otherwise drop) survives via \s.
        var actions = KeyStringParser.Parse(@"text=\shi");
        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual(" hi", ((TextInput)actions[0]).Text);
    }

    // --- ParseVerbatim: whole-argument literal (the --verbatim flag) ---

    [TestMethod]
    public void ParseVerbatim_TypesEntireStringAsSingleLiteral()
    {
        // The phrase is all key names; verbatim types it as one literal instead of pressing the keys.
        var actions = KeyStringParser.ParseVerbatim("down down enter");
        Assert.AreEqual(1, actions.Count);
        Assert.IsInstanceOfType<TextInput>(actions[0]);
        Assert.AreEqual("down down enter", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    [DataRow("enter")]            // collides with a named key
    [DataRow("ctrl+a")]          // collides with a modifier combo
    [DataRow("vk=0x42")]         // collides with the vk= escape
    [DataRow("text=enter")]      // the text= prefix itself is typed literally, not interpreted
    public void ParseVerbatim_DoesNotInterpretKeyTokens(string keys)
    {
        var actions = KeyStringParser.ParseVerbatim(keys);
        Assert.AreEqual(1, actions.Count);
        Assert.IsInstanceOfType<TextInput>(actions[0]);
        Assert.AreEqual(keys, ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    public void ParseVerbatim_PreservesExactWhitespace()
    {
        // Unlike Parse (which splits on whitespace and re-joins literal runs with a single space),
        // verbatim keeps the string byte-for-byte: double spaces and tabs survive.
        var actions = KeyStringParser.ParseVerbatim("a  b\tc");
        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual("a  b\tc", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    public void ParseVerbatim_DoesNotDecodeBackslashEscapes()
    {
        // Backslash escapes are a text= feature; verbatim types backslashes literally.
        var actions = KeyStringParser.ParseVerbatim(@"a\sb");
        Assert.AreEqual(1, actions.Count);
        Assert.AreEqual(@"a\sb", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    public void ParseVerbatim_WhitespaceOnly_IsTypedLiterally()
    {
        // Whitespace is legitimate verbatim content (the whole point of --verbatim is exact preservation),
        // so a whitespace-only argument types those spaces rather than erroring as "nothing to send" (M8).
        var actions = KeyStringParser.ParseVerbatim("   ");
        Assert.AreEqual(1, actions.Count);
        Assert.IsInstanceOfType<TextInput>(actions[0]);
        Assert.AreEqual("   ", ((TextInput)actions[0]).Text);
    }

    [TestMethod]
    [DataRow("")]
    public void ParseVerbatim_Empty_Throws(string keys)
    {
        // Only a genuinely empty argument is "nothing to send". (Whitespace is covered above.)
        Assert.ThrowsExactly<FormatException>(() => KeyStringParser.ParseVerbatim(keys));
    }
}

