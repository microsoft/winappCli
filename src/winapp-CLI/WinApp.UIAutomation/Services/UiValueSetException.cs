// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>A control could not be set through any of the supported programmatic value patterns.</summary>
public sealed class UiValueSetException : InvalidOperationException
{
    private readonly string _elementDescription;
    private readonly string _sendKeysTarget;

    /// <summary>Creates a failure with a safe command suggestion for the specified element.</summary>
    public UiValueSetException(UiElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        _elementDescription = $"Element {element.Id} ({element.Type})";
        var target = element.Selector ?? element.Name ?? element.AutomationId;
        _sendKeysTarget = !string.IsNullOrEmpty(target) &&
            target.All(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' or ':' or '/' or '\\')
            ? target
            : "<selector>";
    }

    /// <summary>The local command advice, also used by library callers outside the CLI.</summary>
    public override string Message => FormatMessage(static command => command);

    /// <summary>
    /// Formats only the owned command suggestions, allowing a CLI host to retain execution context
    /// without rewriting element data or other exception text.
    /// </summary>
    /// <param name="formatCommand">Formats a complete winapp command for display; never executes it.</param>
    public string FormatMessage(Func<string, string> formatCommand)
    {
        ArgumentNullException.ThrowIfNull(formatCommand);
        return _elementDescription + " could not be set via ValuePattern, RangeValuePattern, or " +
            "LegacyIAccessible (put_accValue). This control may not support setting a value programmatically. " +
            $"As a last resort, type the value with '{formatCommand("winapp ui send-keys")}' — for example: " +
            $"{formatCommand($"winapp ui send-keys --verbatim \"<value>\" --target \"{_sendKeysTarget}\" --via send-input -a <app>")}. " +
            "WinUI 3 / WPF rich text controls need '--via send-input' (types real keystrokes; requires the app " +
            "foregrounded on an unlocked desktop) — the default post-message transport is silently dropped by the " +
            "XAML input pipeline. The post-message default (no foreground needed) works for classic Win32 edit controls.";
    }
}
