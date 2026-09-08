// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Live UI Automation COM backing for the <see cref="ValueSetter"/> fallback chain. Kept in its own
/// partial file so the COM/marshalling mechanics stay separate from the rest of the service and from
/// the pure, unit-tested <see cref="ValueSetter"/> ordering logic.
/// </summary>
internal sealed partial class UiAutomationService
{
    /// <summary>
    /// <see cref="IValueSetStrategy"/> backed by a live UI Automation COM element. Each method
    /// acquires the relevant UIA pattern and performs the set, returning <c>false</c> (and logging at
    /// debug level) when the pattern is unavailable or the COM call throws so the caller falls through
    /// to the next mechanism.
    /// </summary>
    private sealed class ComValueSetStrategy(IUIAutomationElement comElement, ILogger logger) : IValueSetStrategy
    {
        public bool TrySetViaValuePattern(string text)
        {
            try
            {
                var pattern = (IUIAutomationValuePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId);
                unsafe
                {
                    var bstrPtr = Marshal.StringToBSTR(text);
                    try
                    {
                        pattern.SetValue(new global::Windows.Win32.Foundation.BSTR((char*)bstrPtr));
                    }
                    finally
                    {
                        Marshal.FreeBSTR(bstrPtr);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                logger.LogDebug("ValuePattern.SetValue failed, trying fallbacks: {Message}", ex.Message);
                return false;
            }
        }

        public bool TrySetViaRangeValuePattern(double value)
        {
            try
            {
                var rangePattern = (IUIAutomationRangeValuePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_RangeValuePatternId);
                rangePattern.SetValue(value);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogDebug("RangeValuePattern.SetValue failed: {Message}", ex.Message);
                return false;
            }
        }

        public bool TrySetViaLegacyIAccessible(string text)
        {
            try
            {
                var legacyPattern = (IUIAutomationLegacyIAccessiblePattern)comElement.GetCurrentPattern(UIA_PATTERN_ID.UIA_LegacyIAccessiblePatternId);
                unsafe
                {
                    fixed (char* valuePtr = text)
                    {
                        legacyPattern.SetValue(new global::Windows.Win32.Foundation.PCWSTR(valuePtr));
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                logger.LogDebug("LegacyIAccessible.SetValue failed: {Message}", ex.Message);
                return false;
            }
        }
    }
}
