// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Pure geometry helpers shared by <c>ui touch</c> and <c>ui pen</c>. Expands a high-level touch
/// gesture into the per-finger waypoint paths that <see cref="IPointerInput.Touch"/> replays.
/// </summary>
public static class PointerGesturePlanner
{
    /// <summary>Contact spread (px) between extra fingers for multi-finger tap/swipe gestures.</summary>
    private const int FingerSpacingPx = 24;

    /// <summary>Residual gap (px) each pinch finger keeps from the center at full contraction.</summary>
    private const int PinchCenterGapPx = 4;

    /// <summary>
    /// Maximum simultaneous touch contacts the pointer-injection subsystem supports
    /// (<c>MAX_TOUCH_COUNT</c> / the count registered with <c>InitializeTouchInjection</c>).
    /// <c>ui touch --fingers</c> is rejected above this.
    /// </summary>
    public const int MaxContacts = 10;

    /// <summary>
    /// Returns the first point in <paramref name="points"/> that lies outside <paramref name="rect"/>,
    /// or <see langword="null"/> when every point is inside. Used to reject touch/pen gestures whose
    /// coordinates fall outside the target window before any OS-wide injection.
    /// </summary>
    public static PointerPoint? FirstOutOfBounds(PointerRect rect, IEnumerable<PointerPoint> points)
    {
        foreach (var p in points)
        {
            if (!rect.Contains(p))
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses a single <c>x,y</c> integer pair (screen coordinates as reported by <c>ui inspect</c>).
    /// </summary>
    public static bool TryParsePoint(string? value, out PointerPoint point)
        => CoordinateParser.TryParsePoint(value, out point);

    /// <summary>
    /// Whether a token that failed to parse as a point was nonetheless meant as coordinates (has a comma
    /// and an integer first field). Mirrors <c>ui drag</c>'s heuristic so malformed coordinates surface
    /// a precise error instead of a misleading "element not found".
    /// </summary>
    public static bool LooksLikeCoordinates(string token)
        => CoordinateParser.LooksLikeCoordinates(token);

    /// <summary>
    /// Parses a pen ink path — a whitespace-separated list of <c>x,y</c> pairs
    /// (<c>"x1,y1 x2,y2 ..."</c>). Returns <see langword="false"/> if any token is not a valid pair.
    /// </summary>
    public static bool TryParsePath(string? value, out List<PointerPoint> points)
    {
        points = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var token in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParsePoint(token, out var p))
            {
                return false;
            }
            points.Add(p);
        }

        return points.Count > 0;
    }

    /// <summary>
    /// Expands a touch gesture into per-finger waypoint paths plus the flattened point list reported in
    /// JSON. <paramref name="start"/> is the anchor (selector center or <c>--at</c>). For pinch/stretch
    /// <paramref name="fingers"/> is coerced to at least 2. <paramref name="direction"/> controls the
    /// swipe axis when no explicit <paramref name="end"/> is given; defaults to <c>"right"</c>.
    /// </summary>
    public static (List<IReadOnlyList<PointerPoint>> ContactPaths, List<PointerPoint> Points, int Fingers) PlanTouch(
        TouchGesture gesture,
        PointerPoint start,
        PointerPoint? end,
        int distance,
        int fingers,
        string? direction = null)
        => PlanTouch(gesture, start, end, distance, fingers, direction, angleDegrees: 0);

    /// <summary>
    /// Same as <see cref="PlanTouch(TouchGesture, PointerPoint, PointerPoint?, int, int, string?)"/>,
    /// with <paramref name="angleDegrees"/> setting the pinch/stretch finger axis: 0 is horizontal,
    /// 90 is vertical, measured counter-clockwise on screen (45 runs from lower-left to upper-right).
    /// Ignored for other gestures.
    /// </summary>
    public static (List<IReadOnlyList<PointerPoint>> ContactPaths, List<PointerPoint> Points, int Fingers) PlanTouch(
        TouchGesture gesture,
        PointerPoint start,
        PointerPoint? end,
        int distance,
        int fingers,
        string? direction,
        double angleDegrees)
    {
        var contactPaths = new List<IReadOnlyList<PointerPoint>>();

        switch (gesture)
        {
            case TouchGesture.Swipe:
            {
                var to = end ?? ComputeSwipeEnd(start, distance, direction);
                int count = Math.Max(1, fingers);
                for (int i = 0; i < count; i++)
                {
                    int dy = i * FingerSpacingPx;
                    contactPaths.Add([new PointerPoint(start.X, start.Y + dy), new PointerPoint(to.X, to.Y + dy)]);
                }
                break;
            }

            case TouchGesture.Pinch:
            case TouchGesture.Stretch:
            {
                fingers = Math.Max(2, fingers);
                // At least 2 px beyond the center gap, so rounding at any angle can't collapse a
                // finger's travel to zero.
                int half = Math.Max(PinchCenterGapPx + 2, distance / 2);

                // Two opposing fingers along the angled axis. Pinch converges toward the center;
                // stretch diverges away from it. Screen Y grows downward, so a counter-clockwise
                // angle subtracts from Y.
                double radians = angleDegrees * Math.PI / 180.0;
                double cos = Math.Cos(radians);
                double sin = Math.Sin(radians);
                var apart = Offset(half, cos, sin);
                var near = Offset(PinchCenterGapPx, cos, sin);

                var leftApart = new PointerPoint(start.X - apart.X, start.Y - apart.Y);
                var rightApart = new PointerPoint(start.X + apart.X, start.Y + apart.Y);
                var leftNear = new PointerPoint(start.X - near.X, start.Y - near.Y);
                var rightNear = new PointerPoint(start.X + near.X, start.Y + near.Y);

                if (gesture == TouchGesture.Pinch)
                {
                    contactPaths.Add([leftApart, leftNear]);
                    contactPaths.Add([rightApart, rightNear]);
                }
                else
                {
                    contactPaths.Add([leftNear, leftApart]);
                    contactPaths.Add([rightNear, rightApart]);
                }
                break;
            }

            case TouchGesture.Tap:
            case TouchGesture.DoubleTap:
            case TouchGesture.LongPress:
            default:
            {
                int count = Math.Max(1, fingers);
                for (int i = 0; i < count; i++)
                {
                    contactPaths.Add([new PointerPoint(start.X + i * FingerSpacingPx, start.Y)]);
                }
                break;
            }
        }

        var points = new List<PointerPoint>();
        foreach (var path in contactPaths)
        {
            points.AddRange(path);
        }

        return (contactPaths, points, contactPaths.Count);
    }

    private static PointerPoint Offset(int length, double cos, double sin)
        => new(
            (int)Math.Round(length * cos, MidpointRounding.AwayFromZero),
            (int)Math.Round(-length * sin, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Computes a swipe end point from <paramref name="start"/> by moving <paramref name="distance"/>
    /// pixels along <paramref name="direction"/> (right/left/up/down; defaults to right).
    /// </summary>
    private static PointerPoint ComputeSwipeEnd(PointerPoint start, int distance, string? direction)
    {
        return (direction ?? "right").ToLowerInvariant() switch
        {
            "left"  => new PointerPoint(start.X - distance, start.Y),
            "up"    => new PointerPoint(start.X, start.Y - distance),
            "down"  => new PointerPoint(start.X, start.Y + distance),
            _       => new PointerPoint(start.X + distance, start.Y), // "right" + default
        };
    }
}
