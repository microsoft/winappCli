// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>Names accepted by <see cref="IUiAutomation.GetPropertiesAsync"/>.</summary>
public static class UiPropertyNames
{
    /// <summary>Tests whether a case-sensitive property name is recognized, regardless of provider support.</summary>
    public static bool IsSupported(string name) => name is
        "Name" or "AutomationId" or "ControlType" or "ClassName" or "IsEnabled" or
        "IsOffscreen" or "BoundingRectangle" or "Value" or "HasKeyboardFocus" or
        "IsKeyboardFocusable" or "AcceleratorKey" or "AccessKey" or "HelpText" or
        "IsPassword" or "ToggleState" or "IsReadOnly" or "IsSelected" or
        "ExpandCollapseState" or "ScrollHorizontalPercent" or "ScrollVerticalPercent" or
        "HorizontallyScrollable" or "VerticallyScrollable" or
        "FontWeight" or "FontName" or "FontSize" or "ForegroundColor" or "IsItalic" or "StrikethroughStyle";
}
