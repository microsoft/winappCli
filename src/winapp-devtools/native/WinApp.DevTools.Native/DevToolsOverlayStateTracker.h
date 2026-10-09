// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Compare flat overlay state against the last broadcast snapshot. Repeated observations alone
// must not publish duplicates. DevToolsTap builds selection/overlay events from the returned delta;
// this helper has no COM or wire dependencies.
#pragma once

// The flat set of axes makes observable. `highlightHandle` is the caller's WIRE handle (already packed
// under the census lock before it reaches here) -- this header never sees a raw InstanceHandle, so it stays
// free of DevToolsTap.cpp's census/locking concerns.
struct DevToolsOverlayStateSnapshot
{
    bool armed = false;
    bool toolbarVisible = false;
    unsigned long long highlightHandle = 0;
    bool layoutAdornersOn = false;

    bool operator==(const DevToolsOverlayStateSnapshot& other) const
    {
        return armed == other.armed &&
               toolbarVisible == other.toolbarVisible &&
               highlightHandle == other.highlightHandle &&
               layoutAdornersOn == other.layoutAdornersOn;
    }
    bool operator!=(const DevToolsOverlayStateSnapshot& other) const { return !(*this == other); }
};

// Which DevTools event(s) a fresh snapshot warrants, relative to the last snapshot this tracker recorded as
// broadcast. Both may be true together (e.g. a single in-app gesture that both disarms pick AND clears the
// highlight): the two events are independent, not mutually exclusive.
struct DevToolsOverlayStateDelta
{
    bool armedChanged = false;   // Selection.armChanged should fire
    bool overlayChanged = false; // Overlay.stateChanged should fire (toolbarVisible/highlightHandle/layoutAdornersOn)

    bool Any() const { return armedChanged || overlayChanged; }
};

// Owns the "last broadcast" snapshot and decides + records transitions. NOT thread-safe by design: DevToolsOverlay's
// state-changed sink only ever fires on the app UI thread (every mutator it wraps already asserts that -- see
// DevToolsOverlay.h), and a WinUI app's DispatcherQueue runs one queued item at a time, so there is exactly one
// "current" caller. Adding a lock here would be dead weight for a type with exactly one legitimate caller
// (DevToolsTap.cpp's OnOverlayStateChanged).
class DevToolsOverlayStateTracker
{
public:
    // Diffs `current` against the last snapshot recorded here, updates that record to `current` regardless of
    // which axis moved (so a later diff never compares against a stale MIXED snapshot -- e.g. one field from
    // three calls ago and another from just now), and reports which event(s) should go out. Before the first
    // call, the recorded snapshot is all-default (matching DevToolsOverlay's own pre-build defaults: nothing armed,
    // toolbar not yet visible, nothing highlighted, layout adorners off) -- so the very first reconcile after
    // a real change correctly reports it, and a defensive call that happens to match the current defaults
    // (e.g. a subscriber that enables mid-session with nothing having happened yet) correctly reports nothing.
    DevToolsOverlayStateDelta Reconcile(const DevToolsOverlayStateSnapshot& current)
    {
        DevToolsOverlayStateDelta delta;
        delta.armedChanged = current.armed != _last.armed;
        delta.overlayChanged = current.toolbarVisible != _last.toolbarVisible ||
                               current.highlightHandle != _last.highlightHandle ||
                               current.layoutAdornersOn != _last.layoutAdornersOn;
        _last = current;
        return delta;
    }

    // The last snapshot recorded as broadcast (i.e. the value every subscriber should now agree on).
    const DevToolsOverlayStateSnapshot& Last() const { return _last; }

private:
    DevToolsOverlayStateSnapshot _last{};
};
