// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

/// <summary>Maps physical screen pixels to pixels in one encoded frame.</summary>
/// <param name="SourceLeft">Screen X of the recorded area's left edge.</param>
/// <param name="SourceTop">Screen Y of the recorded area's top edge.</param>
/// <param name="SourceWidth">Width of the recorded area in screen pixels.</param>
/// <param name="SourceHeight">Height of the recorded area in screen pixels.</param>
/// <param name="ContentX">Left edge of the scaled content inside the frame.</param>
/// <param name="ContentY">Top edge of the scaled content inside the frame.</param>
/// <param name="ContentWidth">Width of the scaled content inside the frame.</param>
/// <param name="ContentHeight">Height of the scaled content inside the frame.</param>
internal readonly record struct CursorFrameMapping(
    int SourceLeft, int SourceTop, int SourceWidth, int SourceHeight,
    int ContentX, int ContentY, int ContentWidth, int ContentHeight)
{
    public static CursorFrameMapping Create(
        int sourceLeft, int sourceTop, int sourceWidth, int sourceHeight,
        int encoderWidth, int encoderHeight, int displayWidth, int displayHeight)
    {
        var (x, y, width, height) = CaptureGeometry.ComputeFittedContentRect(
            sourceWidth, sourceHeight, encoderWidth, encoderHeight, displayWidth, displayHeight);
        return new CursorFrameMapping(sourceLeft, sourceTop, sourceWidth, sourceHeight, x, y, width, height);
    }

    /// <summary>How many frame pixels one screen pixel becomes.</summary>
    public double Scale => SourceWidth > 0 ? (double)ContentWidth / SourceWidth : 1.0;

    public (int X, int Y) Map(int screenX, int screenY)
    {
        var scaleX = SourceWidth > 0 ? (double)ContentWidth / SourceWidth : 1.0;
        var scaleY = SourceHeight > 0 ? (double)ContentHeight / SourceHeight : 1.0;
        return (
            ContentX + (int)Math.Floor((screenX - SourceLeft) * scaleX),
            ContentY + (int)Math.Floor((screenY - SourceTop) * scaleY));
    }

    public bool Contains(int screenX, int screenY)
        => screenX >= SourceLeft && screenX < SourceLeft + SourceWidth
            && screenY >= SourceTop && screenY < SourceTop + SourceHeight;
}

/// <summary>Draws the pointer and click rings into recorded frames and reports where they were.</summary>
internal sealed unsafe class RecordCursorOverlay : IDisposable
{
    /// <summary>Test seam: creates the tracker that reads the pointer and its button events.</summary>
    internal static Func<bool, ILogger, IPointerTracker> s_createPointerTracker =
        static (trackClicks, logger) => new MousePointerTracker(trackClicks, logger);

    internal const double RingDurationMs = 320;

    // A ring that has not appeared yet is drawn no further along than this, so a click that
    // lands between two slow frames is still visible in the next one.
    internal const double MaxFirstShownProgress = 0.5;

    private readonly IPointerTracker _tracker;
    private readonly CursorRenderer _renderer = new();
    private readonly bool _clickHighlight;
    private readonly bool _trackClicks;
    private readonly List<Ring> _rings = [];
    private long _startTimestamp;

    public RecordCursorOverlay(bool clickHighlight, bool trackClicks, ILogger logger)
    {
        _clickHighlight = clickHighlight;
        _trackClicks = trackClicks;
        _tracker = s_createPointerTracker(trackClicks, logger);
    }

    /// <summary>True when clicks were wanted but the mouse hook could not be installed.</summary>
    public bool ClickTrackingUnavailable => _trackClicks && !_tracker.TracksClicks;

    /// <summary>Starts the recording clock; button events before this are dropped.</summary>
    public void Start(long startTimestamp)
    {
        _startTimestamp = startTimestamp;
        _tracker.DrainButtonEvents();
    }

    /// <summary>
    /// Draws click rings and the pointer into <paramref name="frame"/> (BGRA, top-down) and returns
    /// the pointer position and button events since the previous call, in frame pixels.
    /// </summary>
    public (RecordFrameCursor Cursor, RecordFrameClick[]? Clicks) Apply(
        byte[] frame, int frameWidth, int frameHeight, CursorFrameMapping mapping, long nowTimestamp)
    {
        var state = _tracker.GetState();
        var events = _tracker.DrainButtonEvents();

        RecordFrameClick[]? clicks = null;
        if (events.Count > 0)
        {
            var kept = new List<RecordFrameClick>(events.Count);
            foreach (var item in events)
            {
                if (item.Timestamp < _startTimestamp)
                {
                    continue;
                }

                var (x, y) = mapping.Map(item.X, item.Y);
                kept.Add(new RecordFrameClick
                {
                    Button = item.Button,
                    Kind = item.Down ? "down" : "up",
                    ElapsedMs = (long)Math.Round(ElapsedMs(_startTimestamp, item.Timestamp)),
                    X = x,
                    Y = y,
                });
                if (item.Down && _clickHighlight)
                {
                    _rings.Add(new Ring(item.X, item.Y, item.Timestamp, item.Button));
                }
            }
            clicks = kept.Count > 0 ? [.. kept] : null;
        }

        if (_rings.Count > 0)
        {
            DrawRings(frame, frameWidth, frameHeight, mapping, nowTimestamp);
        }

        var inFrame = mapping.Contains(state.X, state.Y);
        if (state.Visible && inFrame)
        {
            var (tipX, tipY) = mapping.Map(state.X, state.Y);
            _renderer.Draw(
                frame, frameWidth, frameHeight, state.Cursor, tipX, tipY, mapping.Scale,
                (mapping.ContentX, mapping.ContentY, mapping.ContentWidth, mapping.ContentHeight));
        }

        var (cursorX, cursorY) = mapping.Map(state.X, state.Y);
        return (new RecordFrameCursor { X = cursorX, Y = cursorY, Visible = state.Visible, InFrame = inFrame }, clicks);
    }

    public void Dispose()
    {
        _tracker.Dispose();
        _renderer.Dispose();
    }

    /// <summary>How far through its fade a ring is, from 0 to 1, or null once it should disappear.</summary>
    internal static double? RingProgress(double ageMs, bool shownBefore)
    {
        var progress = Math.Clamp(ageMs / RingDurationMs, 0, 1);
        if (!shownBefore)
        {
            return Math.Min(progress, MaxFirstShownProgress);
        }
        return progress >= 1 ? null : progress;
    }

    private static double ElapsedMs(long from, long to)
        => (to - from) * 1000.0 / Stopwatch.Frequency;

    private void DrawRings(byte[] frame, int frameWidth, int frameHeight, CursorFrameMapping mapping, long nowTimestamp)
    {
        if (frame.Length < frameWidth * frameHeight * 4)
        {
            return;
        }

        fixed (byte* pixels = frame)
        {
            var info = new SKImageInfo(frameWidth, frameHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var bitmap = new SKBitmap();
            if (!bitmap.InstallPixels(info, (nint)pixels, info.RowBytes))
            {
                return;
            }

            using var canvas = new SKCanvas(bitmap);
            canvas.ClipRect(SKRect.Create(mapping.ContentX, mapping.ContentY, mapping.ContentWidth, mapping.ContentHeight));
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
            var scale = mapping.Scale;
            stroke.StrokeWidth = (float)Math.Max(1.5, 2.0 * scale);

            for (var i = _rings.Count - 1; i >= 0; i--)
            {
                var ring = _rings[i];
                var progress = RingProgress(ElapsedMs(ring.Timestamp, nowTimestamp), ring.Shown);
                if (progress is not { } p)
                {
                    _rings.RemoveAt(i);
                    continue;
                }

                _rings[i] = ring with { Shown = true };
                var alpha = (byte)Math.Round(190 * (1 - p));
                var radius = (float)Math.Max(4.0, (8 + (10 * p)) * scale);
                var (x, y) = mapping.Map(ring.X, ring.Y);
                stroke.Color = ButtonColor(ring.Button).WithAlpha(alpha);
                canvas.DrawCircle(x, y, radius, stroke);
            }
            canvas.Flush();
        }
    }

    private static SKColor ButtonColor(string button) => button switch
    {
        "left" => new SKColor(0xFF, 0xC1, 0x07),
        "right" => new SKColor(0x29, 0xB6, 0xF6),
        _ => new SKColor(0xAB, 0x47, 0xBC),
    };

    private readonly record struct Ring(int X, int Y, long Timestamp, string Button, bool Shown = false);
}
