// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Threading.Channels;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed record UiActionElement(UiActionTarget Target, PointerRect Bounds, bool IsPassword, bool IsEditable, bool IsEnabled, bool IsOffscreen);

internal interface IUiActionEnvironmentFactory
{
    IUiActionEnvironment Create(UiTarget target, CancellationToken cancellationToken = default);
}

internal interface IUiActionEnvironment : IDisposable
{
    string ProcessName { get; }
    bool TargetExists { get; }
    bool IsForeground { get; }
    bool HasInteractiveDesktop { get; }
    UiActionElement? Focused();
    UiActionElement? AtPoint(int x, int y, bool forScroll = false);
    UiActionElement? Resolve(UiActionTarget target);
    string ReadValue(UiActionTarget target);
    string? ReadProperty(UiActionTarget target, string property);
    void Focus(UiActionTarget target);
    void Click(UiActionElement element, double xFraction, double yFraction, bool right, bool twice);
    void Wheel(UiActionElement element, double xFraction, double yFraction, int delta);
    void SendKeys(IReadOnlyList<KeyAction> keys);
}

internal enum UiActionInputKind { KeyDown, MouseDown, MouseUp, Wheel, ScopeLost, ScopeEntered, Error }

[Flags]
internal enum UiActionModifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Windows = 8 }

internal sealed record UiActionInput(
    UiActionInputKind Kind, long ElapsedMs, int X = 0, int Y = 0, ushort VirtualKey = 0,
    UiActionModifiers Modifiers = UiActionModifiers.None, bool RightButton = false,
    int WheelDelta = 0, string? ErrorCode = null, long ObservedTimestamp = 0);

internal interface IUiActionInputSourceFactory
{
    IUiActionInputSource Create(UiTarget target, bool includeInjected);
}

internal interface IUiActionInputSource : IDisposable
{
    ChannelReader<UiActionInput> Reader { get; }
    void Start();
}
