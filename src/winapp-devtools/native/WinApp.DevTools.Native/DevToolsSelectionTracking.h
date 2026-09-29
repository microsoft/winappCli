// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <cmath>

namespace DevToolsSelection
{
struct TrackingRect
{
    long left;
    long top;
    long right;
    long bottom;
};

struct ScrollAnchor
{
    TrackingRect rect;
    double horizontalOffset;
    double verticalOffset;
};

inline TrackingRect TrackScroll(const ScrollAnchor& anchor, double horizontalOffset, double verticalOffset)
{
    const long dx = std::lround(anchor.horizontalOffset - horizontalOffset);
    const long dy = std::lround(anchor.verticalOffset - verticalOffset);
    return TrackingRect{
        anchor.rect.left + dx,
        anchor.rect.top + dy,
        anchor.rect.right + dx,
        anchor.rect.bottom + dy,
    };
}

inline bool IntersectsViewport(const TrackingRect& rect, long width, long height)
{
    return rect.right > rect.left && rect.bottom > rect.top &&
           rect.right > 0 && rect.bottom > 0 && rect.left < width && rect.top < height;
}
}
