// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>

namespace DevToolsToolbar {

enum Corner {
    CornerBR = 0,
    CornerBL = 1,
    CornerTL = 2,
    CornerTR = 3,
    TopCenter = 4,
    BottomCenter = 5,
};
const int kCornerCount = 6;   // enum size; also the ToolbarCorner setting's valid range

const double kEdgeGap = 12.0;

inline bool CornerIsLeft(int c) { return c == CornerBL || c == CornerTL; }
inline bool CornerIsTop(int c)  { return c == CornerTL || c == CornerTR || c == TopCenter; }
inline bool CornerIsCenter(int c) { return c == TopCenter || c == BottomCenter; }

struct Client
{
    double w = 0.0;
    double h = 0.0;
    double titleBarInsetDip = 0.0;
};

inline double TopInset(const Client& c)
{
    double inset = c.titleBarInsetDip;
    if (inset < 0.0) inset = 0.0;
    const double cap = c.h / 3.0;
    if (c.h > 0.0 && inset > cap) inset = cap;
    return inset + kEdgeGap;
}

inline void Anchor(int c, const Client& client, double w, double h, double* x, double* y)
{
    *x = CornerIsCenter(c) ? (client.w - w) / 2.0
                           : (CornerIsLeft(c) ? kEdgeGap : (client.w - w - kEdgeGap));
    *y = CornerIsTop(c)  ? TopInset(client) : (client.h - h - kEdgeGap);
}

inline int NearestCorner(const Client& client, double pillW, double pillH, double px, double py)
{
    int best = CornerBR; double bestD = -1.0;
    for (int i = 0; i < kCornerCount; ++i) {
        double cx = 0.0, cy = 0.0;
        Anchor(i, client, pillW, pillH, &cx, &cy);
        const double d = (cx - px) * (cx - px) + (cy - py) * (cy - py);
        if (bestD < 0.0 || d < bestD) { bestD = d; best = i; }
    }
    return best;
}

// The collapsed toolbar names a mode only while clicks go to DevTools instead of the app. At rest it says DevTools,
// or which window it is on when the app has several.
inline std::wstring ChipLabel(bool picking, bool commentMode, const std::wstring& surface)
{
    if (picking) return commentMode ? L"Commenting" : L"Picking";
    return surface.empty() ? L"DevTools" : surface;
}

inline std::wstring ChipAutomationName(bool picking, bool commentMode)
{
    std::wstring name = L"DevTools (open toolbar)";
    if (picking) name += commentMode ? L", Commenting" : L", Picking";
    return name;
}

} // namespace DevToolsToolbar
