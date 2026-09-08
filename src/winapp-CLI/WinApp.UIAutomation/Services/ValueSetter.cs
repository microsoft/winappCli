// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Abstraction over the three programmatic value-set mechanisms used by
/// <see cref="UiAutomationService.SetValueAsync"/>. Each method attempts one mechanism against a
/// resolved element and returns <c>true</c> on success, or <c>false</c> if the element does not
/// support that mechanism (or the underlying COM call failed). Extracting these behind an interface
/// lets the fallback ordering — including the LegacyIAccessible (<c>put_accValue</c>) success path —
/// be unit-tested without a live UI Automation COM element.
/// </summary>
internal interface IValueSetStrategy
{
    /// <summary>Attempts to set the value via the UIA ValuePattern (TextBox, ComboBox, ...).</summary>
    bool TrySetViaValuePattern(string text);

    /// <summary>Attempts to set the value via the UIA RangeValuePattern (numeric controls).</summary>
    bool TrySetViaRangeValuePattern(double value);

    /// <summary>Attempts to set the value via LegacyIAccessible (<c>IAccessible::put_accValue</c>).</summary>
    bool TrySetViaLegacyIAccessible(string text);
}

/// <summary>
/// Applies a text value to an editable UI element using a well-defined fallback chain. The
/// COM-specific mechanics live in an <see cref="IValueSetStrategy"/> implementation so this ordering
/// logic stays pure and testable.
/// </summary>
internal static class ValueSetter
{
    /// <summary>
    /// Sets <paramref name="text"/> on the element behind <paramref name="strategy"/> using the
    /// fallback chain ValuePattern → RangeValuePattern (numeric only) → LegacyIAccessible
    /// (<c>put_accValue</c>). Throws <see cref="InvalidOperationException"/> with an actionable
    /// <c>send-keys</c> hint when no mechanism succeeds.
    /// </summary>
    public static void Apply(IValueSetStrategy strategy, UiElement element, string text)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(element);

        // Preferred path for TextBox / ComboBox and most editable controls.
        if (strategy.TrySetViaValuePattern(text))
        {
            return;
        }

        // ValuePattern not supported — try RangeValuePattern for numeric controls (sliders/progress bars).
        // Parse with the invariant culture and no thousands grouping so "3.5" is interpreted consistently
        // regardless of the machine's locale, and locale-specific inputs such as "1,2" fall through to the
        // next mechanism rather than being silently reinterpreted (e.g. as "12").
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue) &&
            strategy.TrySetViaRangeValuePattern(numericValue))
        {
            return;
        }

        // Neither worked — fall back to LegacyIAccessible (IAccessible::put_accValue), which reaches
        // TextPattern-only edit controls such as RichEditBox / Document editors that expose no
        // ValuePattern, and works programmatically without bringing the app to the foreground
        // (unlike keystroke injection).
        if (strategy.TrySetViaLegacyIAccessible(text))
        {
            return;
        }

        // Echo the element's own selector/name/id in the copy-pasteable example only when it consists of
        // safe characters; otherwise use a placeholder. This keeps app-controlled text from injecting shell
        // metacharacters (quotes, $(), backticks, %VAR%, newlines, ...) into the example if a user pastes
        // the hint into a shell. The hint is only displayed, never executed by winapp.
        var rawTarget = element.Selector ?? element.Name ?? element.AutomationId;
        var sendKeysTarget = !string.IsNullOrEmpty(rawTarget) && IsSafeHintTarget(rawTarget)
            ? rawTarget
            : "<selector>";
        throw new InvalidOperationException(
            $"Element {element.Id} ({element.Type}) could not be set via ValuePattern, RangeValuePattern, or " +
            "LegacyIAccessible (put_accValue). This control may not support setting a value programmatically. " +
            "As a last resort, type the value with 'winapp ui send-keys' — for example: " +
            $"winapp ui send-keys --verbatim \"<value>\" --target \"{sendKeysTarget}\" --via send-input -a <app>. " +
            "WinUI 3 / WPF rich text controls need '--via send-input' (types real keystrokes; requires the app " +
            "foregrounded on an unlocked desktop) — the default post-message transport is silently dropped by the " +
            "XAML input pipeline. The post-message default (no foreground needed) works for classic Win32 edit controls.");
    }

    // A hint target is safe to echo into the copy-pasteable example only if it contains no shell
    // metacharacters — letters, digits, spaces, and a few path/separator punctuation marks. Anything
    // else (quotes, $, `, %, ;, |, &, parentheses, newlines, ...) forces the "<selector>" placeholder.
    private static bool IsSafeHintTarget(string value)
    {
        foreach (var c in value)
        {
            if (!(char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' or ':' or '/' or '\\'))
            {
                return false;
            }
        }

        return true;
    }
}
