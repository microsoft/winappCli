// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>Outcome of resolving a stable on-screen target for a coordinate gesture.</summary>
public enum TargetStatus
{
    /// <summary>The element settled; <see cref="StableTarget.Element"/> holds its current bounds.</summary>
    Ok,

    /// <summary>The element could no longer be resolved — it was removed or re-templated mid-gesture.</summary>
    NotFound,

    /// <summary>The element resolved but now has zero width or height.</summary>
    ZeroSize,

    /// <summary>The element kept moving/resizing and never settled within the budget.</summary>
    Moving,
}

/// <summary>A resolved gesture target plus its current center.</summary>
public readonly record struct StableTarget(TargetStatus Status, UiElement Element, int CenterX, int CenterY);

/// <summary>
/// Re-resolves a coordinate gesture's target element immediately before injection so click / drag /
/// scroll --wheel hit where the element actually is *now*, not a rectangle that went stale while the
/// window was being foregrounded. If the element is animating (a moving/resizing target), the gesture
/// would otherwise land on empty space yet still report success — so we confirm the bounds have
/// stopped changing and surface a <see cref="TargetStatus.Moving"/> result instead of a false ✅.
/// </summary>
public static class GestureTargeting
{
    /// <summary>Per-axis tolerance (px) within which two reads count as "the same position".</summary>
    private const int StabilityTolerancePx = 2;

    /// <summary>Default extra reads after the initial resolve before giving up as "still moving".</summary>
    public const int DefaultMaxReads = 3;

    /// <summary>Default pause between stability reads.</summary>
    public const int DefaultReadDelayMs = 40;

    /// <summary>
    /// Re-reads <paramref name="selector"/> until two consecutive bounding rects agree within
    /// <see cref="StabilityTolerancePx"/> (target settled) or the read budget is exhausted (still
    /// moving). <paramref name="initial"/> is the bounds the caller already resolved before
    /// foregrounding and seeds the comparison, so a static element settles after a single confirming
    /// read.
    /// </summary>
    public static async Task<StableTarget> ResolveStableAsync(
        IUiAutomation uiAutomation,
        UiTarget uiTarget,
        UiSelector selector,
        UiElement initial,
        int maxReads,
        int readDelayMs,
        Func<int, CancellationToken, Task>? delay,
        CancellationToken cancellationToken)
    {
        delay ??= (ms, ct) => Task.Delay(ms, ct);

        var previous = initial;
        for (int read = 0; read < maxReads; read++)
        {
            await delay(readDelayMs, cancellationToken);

            var current = await uiAutomation.FindSingleElementAsync(uiTarget, selector, cancellationToken);
            if (current is null)
            {
                return new StableTarget(TargetStatus.NotFound, initial, 0, 0);
            }

            if (current.Width == 0 || current.Height == 0)
            {
                return new StableTarget(TargetStatus.ZeroSize, current, 0, 0);
            }

            if (Settled(previous, current))
            {
                return Ok(current);
            }

            previous = current;
        }

        // Never settled within the budget — the element is still animating. Report the latest known
        // bounds so the caller can log where it last was, but flag the gesture as not delivered.
        return new StableTarget(TargetStatus.Moving, previous, Center(previous).X, Center(previous).Y);
    }

    private static StableTarget Ok(UiElement element)
    {
        var (cx, cy) = Center(element);
        return new StableTarget(TargetStatus.Ok, element, cx, cy);
    }

    /// <summary>
    /// Performs a single confirming read of <paramref name="selector"/> and checks it still occupies the
    /// bounds the caller already settled on (<paramref name="expected"/>). Used immediately before a
    /// button-down — after the cursor-settle delay — to close the residual race where a continuously
    /// animating target drifts during the settle window <see cref="ResolveStableAsync"/> couldn't see,
    /// so a reported success no longer hides a silent miss. Returns the element's current bounds on
    /// success, or a <see cref="TargetStatus.Moving"/> / <see cref="TargetStatus.NotFound"/> /
    /// <see cref="TargetStatus.ZeroSize"/> result (feed to <c>TryReport</c>) when it shifted,
    /// vanished, or collapsed in that final window.
    /// </summary>
    public static async Task<StableTarget> ConfirmStillAsync(
        IUiAutomation uiAutomation,
        UiTarget uiTarget,
        UiSelector selector,
        UiElement expected,
        CancellationToken cancellationToken)
    {
        var current = await uiAutomation.FindSingleElementAsync(uiTarget, selector, cancellationToken);
        if (current is null)
        {
            return new StableTarget(TargetStatus.NotFound, expected, 0, 0);
        }

        if (current.Width == 0 || current.Height == 0)
        {
            return new StableTarget(TargetStatus.ZeroSize, current, 0, 0);
        }

        if (!Settled(expected, current))
        {
            var (mx, my) = Center(current);
            return new StableTarget(TargetStatus.Moving, current, mx, my);
        }

        return Ok(current);
    }

    private static (int X, int Y) Center(UiElement element)
        => ((int)(element.X + element.Width / 2.0), (int)(element.Y + element.Height / 2.0));

    private static bool Settled(UiElement a, UiElement b)
        => Math.Abs(a.X - b.X) <= StabilityTolerancePx
        && Math.Abs(a.Y - b.Y) <= StabilityTolerancePx
        && Math.Abs(a.Width - b.Width) <= StabilityTolerancePx
        && Math.Abs(a.Height - b.Height) <= StabilityTolerancePx;
}
