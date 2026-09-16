// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Retains the live provider element alongside its serializable model for the duration of an
/// in-process operation. The context is internal so serialized or externally-created models
/// continue to use the established re-resolution path.
/// </summary>
internal sealed class UiElementContext(IUIAutomationElement automationElement)
{
    public IUIAutomationElement AutomationElement { get; } = automationElement;
}
