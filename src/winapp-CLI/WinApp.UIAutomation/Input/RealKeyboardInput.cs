// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Production implementation — delegates to <see cref="KeyboardInput"/> static P/Invoke helpers.
/// </summary>
internal class RealKeyboardInput : IKeyboardInput
{
    public void Send(long hwnd, IReadOnlyList<KeyAction> actions, KeyTransport transport)
        => KeyboardInput.Send(hwnd, actions, transport);
}
