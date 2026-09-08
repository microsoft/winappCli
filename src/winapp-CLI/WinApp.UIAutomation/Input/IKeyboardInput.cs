// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Abstraction over synthetic keyboard input for testability.
/// Real implementation calls SendInput / PostMessage P/Invoke; fakes record the actions.
/// </summary>
public interface IKeyboardInput
{
    /// <summary>
    /// Sends the parsed key actions to the target window using the requested transport.
    /// </summary>
    /// <param name="hwnd">Target window handle (used by <see cref="KeyTransport.PostMessage"/>).</param>
    /// <param name="actions">Ordered key actions to deliver.</param>
    /// <param name="transport">Delivery transport.</param>
    void Send(long hwnd, IReadOnlyList<KeyAction> actions, KeyTransport transport);
}
