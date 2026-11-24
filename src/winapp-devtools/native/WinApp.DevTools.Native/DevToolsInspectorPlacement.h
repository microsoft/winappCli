// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <algorithm>

// Where the DevTools inspector window opens. Pure geometry so it can be tested without a desktop.
namespace DevToolsInspectorPlacement
{
struct Rect
{
    int left;
    int top;
    int right;
    int bottom;
};

constexpr int kDefaultWidthDip = 780;
constexpr int kDefaultHeightDip = 620;
constexpr int kMinWidthDip = 640;
constexpr int kMinHeightDip = 400;

// The inspector as the user last closed it during this app run, in physical pixels at `dpi`. `frame` is the
// invisible resize border around the visible window on each side, so a window flush with a screen edge reopens
// flush with it instead of being pushed inward by the border's width.
struct Remembered
{
    Rect rect;
    unsigned dpi;
    Rect frame;
};

inline int ScaleDpi(int value, unsigned toDpi, unsigned fromDpi)
{
    if (fromDpi == 0 || toDpi == fromDpi) return value;
    const long long scaled = static_cast<long long>(value) * toDpi;
    return static_cast<int>(scaled >= 0 ? (scaled + fromDpi / 2) / fromDpi : -((-scaled + fromDpi / 2) / fromDpi));
}

// The physical rect the inspector should end up at on a monitor with work area `work` and DPI `dpi`.
// With `remembered`, the window keeps the size the user gave it (same DIPs at the new DPI). It also returns to
// the same spot when `restorePosition` is set, which the caller does only when that spot is still on a monitor.
// Otherwise it opens beside `app` (or centred when there is no app window). The result is clamped on-screen.
inline Rect Target(unsigned dpi, const Rect& work, const Rect* app, const Remembered* remembered, bool restorePosition)
{
    if (dpi == 0) dpi = 96;
    const double s = dpi / 96.0;
    int w = static_cast<int>(kDefaultWidthDip * s);
    int h = static_cast<int>(kDefaultHeightDip * s);
    if (remembered) {
        w = (std::max)(ScaleDpi(remembered->rect.right - remembered->rect.left, dpi, remembered->dpi),
                       ScaleDpi(kMinWidthDip, dpi, 96));
        h = (std::max)(ScaleDpi(remembered->rect.bottom - remembered->rect.top, dpi, remembered->dpi),
                       ScaleDpi(kMinHeightDip, dpi, 96));
    }

    const int gap  = static_cast<int>(12 * s);
    const int edge = static_cast<int>(12 * s);

    // Positions clamp against `safe`: the work area inset by a margin for a placement we chose, collapsing to the
    // work area when the window can't fit. A restored window may sit where the user put it, up to its invisible
    // border past the work area's edge.
    Rect safe = work;
    if (remembered && restorePosition) {
        safe.left   -= ScaleDpi(remembered->frame.left, dpi, remembered->dpi);
        safe.top    -= ScaleDpi(remembered->frame.top, dpi, remembered->dpi);
        safe.right  += ScaleDpi(remembered->frame.right, dpi, remembered->dpi);
        safe.bottom += ScaleDpi(remembered->frame.bottom, dpi, remembered->dpi);
    } else if (safe.right - safe.left > w + 2 * edge && safe.bottom - safe.top > h + 2 * edge) {
        safe.left += edge; safe.top += edge; safe.right -= edge; safe.bottom -= edge;
    }
    const Rect& bounds = (remembered && restorePosition) ? safe : work;

    int x, y;
    if (remembered && restorePosition) {
        x = remembered->rect.left;
        y = remembered->rect.top;
    } else if (app) {
        y = app->top;
        if (app->right + gap + w <= safe.right)      x = app->right + gap;
        else if (app->left - gap - w >= safe.left)   x = app->left - gap - w;
        else                                         x = safe.right - w;
    } else {
        x = work.left + (work.right - work.left - w) / 2;
        y = work.top + (work.bottom - work.top - h) / 2;
    }

    if (w > bounds.right - bounds.left) w = bounds.right - bounds.left;
    if (h > bounds.bottom - bounds.top) h = bounds.bottom - bounds.top;
    if (x < safe.left) x = safe.left;
    if (y < safe.top)  y = safe.top;
    if (x + w > safe.right)  x = safe.right - w;
    if (y + h > safe.bottom) y = safe.bottom - h;
    if (x < bounds.left) x = bounds.left;
    if (y < bounds.top)  y = bounds.top;
    if (x + w > bounds.right)  x = bounds.right - w;
    if (y + h > bounds.bottom) y = bounds.bottom - h;
    return Rect{ x, y, x + w, y + h };
}
}
