// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Keyboard modifier keys held down while a mouse click is sent (for example Ctrl+click to add an item
/// to a multi-selection, or Shift+click to select a range).
/// </summary>
[Flags]
public enum ClickModifiers
{
    /// <summary>No modifier keys.</summary>
    None = 0,

    /// <summary>The Ctrl key.</summary>
    Control = 1,

    /// <summary>The Shift key.</summary>
    Shift = 2,

    /// <summary>The Alt key.</summary>
    Alt = 4,
}
