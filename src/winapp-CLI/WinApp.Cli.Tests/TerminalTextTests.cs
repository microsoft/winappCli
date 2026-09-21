// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// What happens to text a target chose when winapp prints it to a terminal.
/// </summary>
/// <remarks>
/// Window titles come from software the caller is deliberately testing, including software that is
/// misbehaving on purpose. A terminal reads escape sequences in that text as instructions, so a
/// title printed verbatim can overwrite the lines above it, clear the screen, or retitle the user's
/// terminal window — letting the thing being reported on edit the report.
/// </remarks>
[TestClass]
public class TerminalTextTests
{
    [TestMethod]
    public void Sanitize_OrdinaryTitle_IsLeftExactlyAsItWas()
    {
        Assert.AreEqual("Calculator — 3 × 4", TerminalText.Sanitize("Calculator — 3 × 4"));
    }

    [TestMethod]
    [DataRow(null, DisplayName = "no title")]
    [DataRow("", DisplayName = "empty title")]
    public void Sanitize_NothingToPrint_IsEmpty(string? text)
    {
        Assert.AreEqual(string.Empty, TerminalText.Sanitize(text));
    }

    /// <summary>
    /// OSC 0 is the sequence that renames the user's terminal window. It runs to a BEL, so dropping
    /// only the escape character would print the payload and still leave the shell's title changed
    /// on any terminal that had already consumed it.
    /// </summary>
    [TestMethod]
    public void Sanitize_OscWindowTitleSequence_IsRemovedWholeIncludingItsPayload()
    {
        Assert.AreEqual(
            "Notepad",
            TerminalText.Sanitize("\u001b]0;pwned\u0007Notepad"));
    }

    [TestMethod]
    public void Sanitize_OscTerminatedByStringTerminator_IsRemovedWhole()
    {
        Assert.AreEqual("Notepad", TerminalText.Sanitize("\u001b]8;;http://evil\u001b\\Notepad"));
    }

    [TestMethod]
    public void Sanitize_UnterminatedOsc_TakesTheRestOfTheValueWithIt()
    {
        Assert.AreEqual("safe", TerminalText.Sanitize("safe\u001b]0;never closed"));
    }

    [TestMethod]
    [DataRow("\u001b[2J\u001b[Hgone", "gone", DisplayName = "clear screen and home the cursor")]
    [DataRow("\u001b[1;31mred\u001b[0m", "red", DisplayName = "colour on and off")]
    [DataRow("keep\u001b[10Amoved", "keepmoved", DisplayName = "cursor moved up ten rows")]
    public void Sanitize_CsiSequence_IsRemovedWholeIncludingItsParameters(string input, string expected)
    {
        Assert.AreEqual(expected, TerminalText.Sanitize(input));
    }

    [TestMethod]
    public void Sanitize_UnterminatedCsi_TakesTheRestOfTheValueWithIt()
    {
        Assert.AreEqual("safe", TerminalText.Sanitize("safe\u001b[38;5;"));
    }

    /// <summary>
    /// A UTF-8 terminal acts on the single-character C1 forms exactly as it does on the ESC pairs,
    /// so a value that uses them must not slip past a filter that only looks for ESC.
    /// </summary>
    [TestMethod]
    [DataRow("a\u009b2Jb", "ab", DisplayName = "C1 CSI")]
    [DataRow("a\u009d0;title\u0007b", "ab", DisplayName = "C1 OSC")]
    public void Sanitize_SingleCharacterC1Introducers_AreRemovedToo(string input, string expected)
    {
        Assert.AreEqual(expected, TerminalText.Sanitize(input));
    }

    [TestMethod]
    public void Sanitize_TwoCharacterEscape_IsRemoved()
    {
        // ESC c is a full terminal reset.
        Assert.AreEqual("title", TerminalText.Sanitize("\u001bctitle"));
    }

    [TestMethod]
    public void Sanitize_TrailingEscape_IntroducesNothingAndIsDropped()
    {
        Assert.AreEqual("title", TerminalText.Sanitize("title\u001b"));
    }

    /// <summary>
    /// A report puts one window on one row. A title containing newlines would otherwise fake extra
    /// rows of output, and a carriage return would overwrite the row it was printed on.
    /// </summary>
    [TestMethod]
    [DataRow("a\r\nb", "a\u21b5b", DisplayName = "CRLF counts once")]
    [DataRow("a\nb", "a\u21b5b", DisplayName = "LF")]
    [DataRow("a\rb", "a\u21b5b", DisplayName = "CR alone")]
    [DataRow("a\tb", "a\u2192b", DisplayName = "tab would realign the row")]
    public void Sanitize_LayoutCharacters_BecomeOneVisibleGlyph(string input, string expected)
    {
        Assert.AreEqual(expected, TerminalText.Sanitize(input));
    }

    [TestMethod]
    public void Sanitize_BareControlCharacters_AreDropped()
    {
        Assert.AreEqual("ab", TerminalText.Sanitize("a\u0000\u0007\u007f\u0085b"));
    }

    [TestMethod]
    public void Sanitize_BackspaceRewrite_CannotRewriteTheLine()
    {
        // "safe" then eight backspaces then a replacement is how a title erases what precedes it.
        Assert.AreEqual("safemalware", TerminalText.Sanitize("safe\b\b\b\b\b\b\b\bmalware"));
    }
}
