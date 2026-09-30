// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <algorithm>
#include <climits>
#include <cstdlib>

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

inline bool Overlaps(const Rect& a, const Rect& b)
{
    return a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
}

// `avoid` (the DevTools toolbar) is kept clear with the smallest change: move the panel above or below it, move it
// sideways without covering the element, or end it above/below the toolbar where it stands (its rows scroll).
// A panel too cramped for any of those shortens to the larger band above or below the toolbar.
inline Placement PlacePanel(const Rect& element, const Rect& viewport, int panelWidth,
                            int preferredHeight, int horizontalGap = 14, int edgeGap = 8,
                            const Rect* avoid = nullptr)
{
    const int viewportWidth = (std::max)(0, viewport.right - viewport.left);
    const int viewportHeight = (std::max)(0, viewport.bottom - viewport.top);
    int panelHeight = (std::max)(0, (std::min)(preferredHeight, viewportHeight - 2 * edgeGap));
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

    if (avoid && avoid->right > avoid->left && avoid->bottom > avoid->top) {
        const Rect keepOut{ avoid->left - edgeGap, avoid->top - edgeGap, avoid->right + edgeGap, avoid->bottom + edgeGap };
        auto panelAt = [&](int px, int py, int height) { return Rect{ px, py, px + panelWidth, py + height }; };
        if (Overlaps(panelAt(x, y, panelHeight), keepOut)) {
            struct Band { int top, bottom; };
            struct Candidate { int x, y, height, cost; };
            constexpr int readableHeight = 280, minimumHeight = 200;
            const Band bands[] = { { minY, keepOut.top }, { keepOut.bottom, viewport.bottom - edgeGap } };
            Candidate best{ x, y, panelHeight, INT_MAX };
            auto consider = [&](Candidate candidate) { if (candidate.cost < best.cost) best = candidate; };
            for (const auto& band : bands) {
                if (band.bottom - band.top < panelHeight) continue;
                const int movedY = std::clamp(y, band.top, band.bottom - panelHeight);
                consider({ x, movedY, panelHeight, std::abs(movedY - y) });
            }
            for (const int sideX : { keepOut.left - panelWidth, keepOut.right }) {
                if (sideX >= minX && sideX <= maxX && !Overlaps(panelAt(sideX, y, panelHeight), element))
                    consider({ sideX, y, panelHeight, std::abs(sideX - x) });
            }
            for (const auto& band : bands) {
                const int height = band.bottom - y;
                if (y >= band.top && height >= readableHeight && height < panelHeight)
                    consider({ x, y, height, panelHeight - height });
            }
            if (best.cost == INT_MAX) {
                const Band& larger = (bands[0].bottom - bands[0].top >= bands[1].bottom - bands[1].top) ? bands[0] : bands[1];
                const int height = larger.bottom - larger.top;
                if (height >= (std::min)(panelHeight, minimumHeight)) best = { x, larger.top, height, 0 };
            }
            x = best.x; y = best.y; panelHeight = best.height;
        }
    }

    return Placement{ x, y, panelHeight, onRight };
}
}
