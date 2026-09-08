// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Production <see cref="IPollDelay"/> — a straight passthrough to <see cref="Task.Delay(int, CancellationToken)"/>.
/// It contains only the real timer call and is therefore left uncovered by design, the same honest-ceiling
/// category as the other real OS-boundary wrappers. Behavior is identical to the inline delay it replaced.
/// </summary>
internal sealed class RealPollDelay : IPollDelay
{
    public Task DelayAsync(int milliseconds, CancellationToken ct) => Task.Delay(milliseconds, ct);
}
