// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake <see cref="IPointerTracker"/>: reports a fixed pointer and, on its first read, the button
/// events a test queued, so recordings can be checked without moving the real mouse.
/// </summary>
internal sealed class FakePointerTracker(bool tracksClicks) : IPointerTracker
{
    private readonly List<PointerButtonEvent> _pending = [];
    private bool _queuedReleased;

    public PointerState State { get; set; }

    /// <summary>Button events to deliver on the first frame, as (button, down, x, y).</summary>
    public List<(string Button, bool Down, int X, int Y)> QueuedEvents { get; } = [];

    public bool TracksClicks { get; } = tracksClicks;

    public bool Disposed { get; private set; }

    public PointerState GetState()
    {
        if (!_queuedReleased)
        {
            _queuedReleased = true;
            var now = Stopwatch.GetTimestamp();
            _pending.AddRange(QueuedEvents.Select(item => new PointerButtonEvent(now, item.Button, item.Down, item.X, item.Y)));
        }
        return State;
    }

    public IReadOnlyList<PointerButtonEvent> DrainButtonEvents()
    {
        var drained = _pending.ToArray();
        _pending.Clear();
        return drained;
    }

    public void Dispose() => Disposed = true;
}
