// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

/// <summary>
/// Unit coverage for the <see cref="ValueSetter"/> fallback ordering, including the
/// LegacyIAccessible (<c>put_accValue</c>) success path that reaches TextPattern-only edit controls
/// such as RichEditBox (issue #620). The live UIA COM path itself is exercised by the WinUI e2e
/// script; these tests pin the ordering/decision logic with a fake strategy.
/// </summary>
[TestClass]
public class ValueSetterTests
{
    // Records which mechanisms were attempted and simulates a control that supports only some of
    // them, so the fallback ordering can be asserted without a live UIA COM element.
    private sealed class FakeValueSetStrategy : IValueSetStrategy
    {
        public bool ValuePatternSucceeds { get; init; }
        public bool RangeValuePatternSucceeds { get; init; }
        public bool LegacySucceeds { get; init; }

        public List<string> Calls { get; } = [];
        public string? ValueTextReceived { get; private set; }
        public double? RangeValueReceived { get; private set; }
        public string? LegacyTextReceived { get; private set; }

        public bool TrySetViaValuePattern(string text)
        {
            Calls.Add("value");
            ValueTextReceived = text;
            return ValuePatternSucceeds;
        }

        public bool TrySetViaRangeValuePattern(double value)
        {
            Calls.Add("range");
            RangeValueReceived = value;
            return RangeValuePatternSucceeds;
        }

        public bool TrySetViaLegacyIAccessible(string text)
        {
            Calls.Add("legacy");
            LegacyTextReceived = text;
            return LegacySucceeds;
        }
    }

    private static UiElement Element(string? selector = null, string? name = null, string? automationId = null)
        => new() { Id = "e1", Type = "Edit", Selector = selector, Name = name, AutomationId = automationId };

    [TestMethod]
    public void Apply_UsesValuePattern_AndDoesNotTryFallbacks()
    {
        var strategy = new FakeValueSetStrategy { ValuePatternSucceeds = true };

        ValueSetter.Apply(strategy, Element(), "hello");

        Assert.AreEqual("value", string.Join(",", strategy.Calls));
    }

    [TestMethod]
    public void Apply_FallsBackToRangeValue_ForNumericText_WhenValuePatternFails()
    {
        var strategy = new FakeValueSetStrategy { RangeValuePatternSucceeds = true };

        ValueSetter.Apply(strategy, Element(), "42");

        Assert.AreEqual("value,range", string.Join(",", strategy.Calls));
        Assert.AreEqual(42d, strategy.RangeValueReceived!.Value);
    }

    [TestMethod]
    public void Apply_SkipsRangeValue_ForNonNumericText()
    {
        // RangeValue would succeed, but non-numeric text must never attempt it; legacy handles it.
        var strategy = new FakeValueSetStrategy { RangeValuePatternSucceeds = true, LegacySucceeds = true };

        ValueSetter.Apply(strategy, Element(), "hello");

        Assert.AreEqual("value,legacy", string.Join(",", strategy.Calls));
    }

    [TestMethod]
    public void Apply_SkipsRangeValue_ForLocaleGroupedNumber()
    {
        // "1,2" must not be reinterpreted as 12 via thousands grouping — it is not a valid invariant
        // number, so RangeValue is skipped and legacy receives the raw text.
        var strategy = new FakeValueSetStrategy { RangeValuePatternSucceeds = true, LegacySucceeds = true };

        ValueSetter.Apply(strategy, Element(), "1,2");

        Assert.AreEqual("value,legacy", string.Join(",", strategy.Calls));
        Assert.IsNull(strategy.RangeValueReceived);
        Assert.AreEqual("1,2", strategy.LegacyTextReceived);
    }

    [TestMethod]
    public void Apply_FallsBackToLegacyIAccessible_WhenValueAndRangeUnavailable()
    {
        // The put_accValue success path for TextPattern-only edit controls (issue #620).
        var strategy = new FakeValueSetStrategy { LegacySucceeds = true };

        ValueSetter.Apply(strategy, Element(), "hello richedit");

        Assert.AreEqual("value,legacy", string.Join(",", strategy.Calls));
        Assert.AreEqual("hello richedit", strategy.LegacyTextReceived);
    }

    [TestMethod]
    public void Apply_PreservesEmptyString_ViaValuePattern()
    {
        // Clearing a field: an empty string must flow through ValuePattern unchanged.
        var strategy = new FakeValueSetStrategy { ValuePatternSucceeds = true };

        ValueSetter.Apply(strategy, Element(), "");

        Assert.AreEqual("value", string.Join(",", strategy.Calls));
        Assert.AreEqual("", strategy.ValueTextReceived);
    }

    [TestMethod]
    public void Apply_PreservesEmptyString_ViaLegacy_AndSkipsRangeValue()
    {
        // An empty string is not numeric, so RangeValuePattern is skipped; legacy receives "" intact.
        var strategy = new FakeValueSetStrategy { LegacySucceeds = true };

        ValueSetter.Apply(strategy, Element(), "");

        Assert.AreEqual("value,legacy", string.Join(",", strategy.Calls));
        Assert.AreEqual("", strategy.LegacyTextReceived);
    }

    [TestMethod]
    public void Apply_FallsBackToLegacy_WhenNumericRangeValueFails()
    {
        // Numeric text, but the control's RangeValuePattern set fails → fall through to legacy.
        var strategy = new FakeValueSetStrategy { LegacySucceeds = true };

        ValueSetter.Apply(strategy, Element(), "42");

        Assert.AreEqual("value,range,legacy", string.Join(",", strategy.Calls));
    }

    [TestMethod]
    public void Apply_Throws_WhenAllMechanismsFail()
    {
        var strategy = new FakeValueSetStrategy();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => ValueSetter.Apply(strategy, Element(selector: "Rich Text Editor"), "hello"));

        StringAssert.Contains(ex.Message, "put_accValue");
        StringAssert.Contains(ex.Message, "send-keys");
        StringAssert.Contains(ex.Message, "Rich Text Editor");
        Assert.AreEqual("value,legacy", string.Join(",", strategy.Calls));
    }

    [TestMethod]
    public void Apply_ThrowMessage_UsesNameThenAutomationId_ForSendKeysTarget()
    {
        var byName = Assert.ThrowsExactly<InvalidOperationException>(
            () => ValueSetter.Apply(new FakeValueSetStrategy(), Element(name: "My Field"), "x"));
        StringAssert.Contains(byName.Message, "--target \"My Field\"");

        var byAutomationId = Assert.ThrowsExactly<InvalidOperationException>(
            () => ValueSetter.Apply(new FakeValueSetStrategy(), Element(automationId: "field-1"), "x"));
        StringAssert.Contains(byAutomationId.Message, "--target \"field-1\"");
    }

    [TestMethod]
    [DataRow("a\"b", "a\"b")]                 // double quote
    [DataRow("line1\nline2", "line1\nline2")] // newline
    [DataRow("a$b", "a$b")]                   // $ / $() expansion
    [DataRow("a`b", "a`b")]                   // backtick command substitution
    [DataRow("a;b", "a;b")]                   // command separator
    [DataRow("a|b", "a|b")]                   // pipe
    [DataRow("a&b", "a&b")]                   // background / AND
    [DataRow("a(b)c", "a(b)c")]               // subshell parentheses
    [DataRow("a%b", "a%b")]                   // %VAR% expansion
    public void Apply_ThrowMessage_UsesSelectorPlaceholder_WhenTargetHasUnsafeChars(string unsafeName, string mustNotAppear)
    {
        // Each app-controlled name carries exactly one shell metacharacter surrounded by safe text, so a
        // regression that let that specific character through would echo the raw name and fail this case.
        // Unsafe targets must fall back to the "<selector>" placeholder in the copy-pasteable example.
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => ValueSetter.Apply(new FakeValueSetStrategy(), Element(name: unsafeName), "x"));

        StringAssert.Contains(ex.Message, "--target \"<selector>\"");
        Assert.IsFalse(ex.Message.Contains(mustNotAppear), "Unsafe target text must not appear in the hint.");
    }

    [TestMethod]
    [DataRow("My Field 2")]
    [DataRow("C:\\My App\\field-1")]
    public void Apply_ThrowMessage_KeepsSafeTarget(string safeName)
    {
        // Benign names (spaces, path separators, drive colon, digits, hyphens) are safe and echoed verbatim.
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => ValueSetter.Apply(new FakeValueSetStrategy(), Element(name: safeName), "x"));

        StringAssert.Contains(ex.Message, $"--target \"{safeName}\"");
    }
}
