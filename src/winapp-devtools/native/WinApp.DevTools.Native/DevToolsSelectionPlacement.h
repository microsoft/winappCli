// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <algorithm>

namespace DevToolsSelection
{
struct Rect
{
    int left;
    int top;
    int right;
    int bottom;
};

struct Placement
{
    int panelX;
    int panelY;
    int panelHeight;
    bool onRight;
};

struct PinPlacement
{
    int x;
    int y;
};

inline PinPlacement PlacePin(const Rect& element, const Rect& viewport)
{
    constexpr int size = 20, gap = 4;
    const int maxX = (std::max)(viewport.left, viewport.right - size);
    const int maxY = (std::max)(viewport.top, viewport.bottom - size);
    const PinPlacement candidates[] = {
        { element.right + gap, element.top },
        { element.left - size - gap, element.top },
        { element.right - size, element.top - size - gap },
        { element.right - size, element.bottom + gap },
    };
    PinPlacement best{};
    int leastOverlap = size * size + 1;
    for (auto candidate : candidates) {
        candidate.x = std::clamp(candidate.x, viewport.left, maxX);
        candidate.y = std::clamp(candidate.y, viewport.top, maxY);
        const int width = (std::max)(0, (std::min)(candidate.x + size, element.right) -
            (std::max)(candidate.x, element.left));
        const int height = (std::max)(0, (std::min)(candidate.y + size, element.bottom) -
            (std::max)(candidate.y, element.top));
        const int overlap = width * height;
        if (overlap < leastOverlap) {
            best = candidate;
            leastOverlap = overlap;
            if (overlap == 0) break;
        }
    }
    return best;
}

inline Placement PlacePanel(const Rect& element, const Rect& viewport, int panelWidth,
                            int preferredHeight, int horizontalGap = 14, int edgeGap = 8)
{
    const int viewportWidth = (std::max)(0, viewport.right - viewport.left);
    const int viewportHeight = (std::max)(0, viewport.bottom - viewport.top);
    const int panelHeight = (std::max)(0, (std::min)(preferredHeight, viewportHeight - 2 * edgeGap));
    const int roomRight = viewport.right - (element.right + horizontalGap);
    const int roomLeft = element.left - horizontalGap - viewport.left;

    bool onRight = roomRight >= roomLeft;
    if (roomRight >= panelWidth + edgeGap) onRight = true;
    else if (roomLeft >= panelWidth + edgeGap) onRight = false;

    int x = onRight ? element.right + horizontalGap : element.left - panelWidth - horizontalGap;
    const int minX = viewport.left + edgeGap;
    const int maxX = viewport.right - panelWidth - edgeGap;
    if (viewportWidth >= panelWidth + 2 * edgeGap) x = std::clamp(x, minX, maxX);
    else x = minX;

    int y = element.top - 10;
    const int minY = viewport.top + edgeGap;
    const int maxY = viewport.bottom - panelHeight - edgeGap;
    y = std::clamp(y, minY, (std::max)(minY, maxY));

    return Placement{ x, y, panelHeight, onRight };
}
}
