// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

// Pure framing and pixel work behind Internal.elementSnapshot: what part of the rendered window a comment's
// screenshot shows, and how it is masked, outlined and flattened. No XAML, so it is unit tested without an app.

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <string>
#include <vector>

namespace DevToolsSnapshot
{
struct Rect
{
    double x = 0, y = 0, w = 0, h = 0;
    double Right() const { return x + w; }
    double Bottom() const { return y + h; }
    bool Empty() const { return w <= 0 || h <= 0; }
};

inline Rect Intersect(const Rect& a, const Rect& b)
{
    const double left = (std::max)(a.x, b.x), top = (std::max)(a.y, b.y);
    const double right = (std::min)(a.Right(), b.Right()), bottom = (std::min)(a.Bottom(), b.Bottom());
    return right > left && bottom > top ? Rect{ left, top, right - left, bottom - top } : Rect{};
}

inline Rect Union(const Rect& a, const Rect& b)
{
    if (a.Empty()) return b;
    if (b.Empty()) return a;
    const double left = (std::min)(a.x, b.x), top = (std::min)(a.y, b.y);
    return Rect{ left, top, (std::max)(a.Right(), b.Right()) - left, (std::max)(a.Bottom(), b.Bottom()) - top };
}

inline Rect Inflate(const Rect& r, double by) { return Rect{ r.x - by, r.y - by, r.w + 2 * by, r.h + 2 * by }; }

struct Ancestor
{
    Rect bounds;
    bool clips = false;   // a scrolling viewport: what lies outside it is not on screen
};

// `ancestors` are nearest first, in root coordinates. Returns the on-screen part of each ancestor and sets `visible`
// to the on-screen part of the element, so a scrolled or clipped element is framed as the reviewer saw it.
inline std::vector<Rect> OnScreen(const Rect& element, const std::vector<Ancestor>& ancestors, const Rect& root, Rect* visible)
{
    std::vector<Rect> shown(ancestors.size());
    Rect viewport = root;
    for (size_t i = ancestors.size(); i-- > 0;) {
        shown[i] = Intersect(ancestors[i].bounds, viewport);
        if (ancestors[i].clips) viewport = Intersect(viewport, ancestors[i].bounds);
    }
    *visible = Intersect(element, viewport);
    return shown;
}

// Framing limits, in DIP. A nearby ancestor shows the element's row, card or group and its siblings; one that is
// taller than this, or covers this much of the window, is page layout and only makes the element smaller.
inline constexpr double kContextMaxHeight = 360;
inline constexpr double kContextMaxWindowShare = 0.35;
inline constexpr double kContextPadding = 24;
inline constexpr int kMaxEdgePixels = 1024;
inline constexpr int kOutlinePixels = 3;
inline constexpr uint32_t kOutlineColor = 0xFFFF00FF;   // BGRA as little-endian 0xAARRGGBB: magenta
inline constexpr uint32_t kMaskColor = 0xFF808080;

// `visible` is the on-screen part of the element; `ancestors` are its ancestors' bounds, nearest first, all in
// root coordinates. Returns the area to crop, in DIP: the largest nearby ancestor within the limits, padded and
// kept inside the root.
inline Rect ContextRect(const Rect& visible, const std::vector<Rect>& ancestors, const Rect& root)
{
    const double maxArea = kContextMaxWindowShare * root.w * root.h;
    Rect best = visible;
    for (const auto& ancestor : ancestors) {
        const Rect shown = Intersect(ancestor, root);
        if (shown.Empty()) continue;
        if (shown.h > kContextMaxHeight || shown.w * shown.h > maxArea) break;
        best = Union(best, shown);
    }
    return Intersect(Inflate(best, kContextPadding), root);
}

// Physical-pixel rectangle, half-open.
struct PixelRect
{
    int left = 0, top = 0, right = 0, bottom = 0;
    int Width() const { return right - left; }
    int Height() const { return bottom - top; }
    bool Empty() const { return right <= left || bottom <= top; }
};

inline PixelRect ToPixels(const Rect& r, double scale, int width, int height)
{
    PixelRect p{ (int)std::floor(r.x * scale), (int)std::floor(r.y * scale),
                 (int)std::ceil(r.Right() * scale), (int)std::ceil(r.Bottom() * scale) };
    p.left = std::clamp(p.left, 0, width); p.right = std::clamp(p.right, 0, width);
    p.top = std::clamp(p.top, 0, height); p.bottom = std::clamp(p.bottom, 0, height);
    return p;
}

// Flat backdrop for transparent pixels. Mica and desktop acrylic render transparent, so the window's theme decides
// what the reviewer saw behind its content.
inline uint32_t BackdropColor(bool dark) { return dark ? 0xFF202020u : 0xFFF3F3F3u; }

struct Frame
{
    std::vector<uint32_t> pixels;   // BGRA, opaque
    int width = 0, height = 0;
};

// `source` is the root's premultiplied BGRA render (width x height). Crops to `crop`, flattens onto `backdrop`, paints
// every mask solid, then draws the outline just outside `outline`. All rectangles are in source pixels.
inline Frame Compose(const uint8_t* source, int width, int height, const PixelRect& crop, uint32_t backdrop,
                     const std::vector<PixelRect>& masks, const PixelRect& outline)
{
    Frame frame;
    if (crop.Empty() || crop.right > width || crop.bottom > height || crop.left < 0 || crop.top < 0) return frame;
    frame.width = crop.Width();
    frame.height = crop.Height();
    frame.pixels.resize((size_t)frame.width * frame.height);
    const uint32_t bgB = backdrop & 0xFF, bgG = (backdrop >> 8) & 0xFF, bgR = (backdrop >> 16) & 0xFF;
    for (int y = 0; y < frame.height; ++y) {
        const uint8_t* row = source + ((size_t)(crop.top + y) * width + crop.left) * 4;
        uint32_t* out = frame.pixels.data() + (size_t)y * frame.width;
        for (int x = 0; x < frame.width; ++x) {
            const uint32_t b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2], a = row[x * 4 + 3];
            const uint32_t inverse = 255 - a;
            const uint32_t ob = (std::min)(255u, b + (bgB * inverse + 127) / 255);
            const uint32_t og = (std::min)(255u, g + (bgG * inverse + 127) / 255);
            const uint32_t orr = (std::min)(255u, r + (bgR * inverse + 127) / 255);
            out[x] = 0xFF000000u | (orr << 16) | (og << 8) | ob;
        }
    }
    auto fill = [&](PixelRect r, uint32_t color) {
        r.left = (std::max)(r.left, crop.left) - crop.left; r.right = (std::min)(r.right, crop.right) - crop.left;
        r.top = (std::max)(r.top, crop.top) - crop.top; r.bottom = (std::min)(r.bottom, crop.bottom) - crop.top;
        for (int y = r.top; y < r.bottom; ++y)
            for (int x = r.left; x < r.right; ++x) frame.pixels[(size_t)y * frame.width + x] = color;
    };
    for (const auto& mask : masks) fill(mask, kMaskColor);
    if (!outline.Empty()) {
        const int t = kOutlinePixels;
        fill({ outline.left - t, outline.top - t, outline.right + t, outline.top }, kOutlineColor);
        fill({ outline.left - t, outline.bottom, outline.right + t, outline.bottom + t }, kOutlineColor);
        fill({ outline.left - t, outline.top, outline.left, outline.bottom }, kOutlineColor);
        fill({ outline.right, outline.top, outline.right + t, outline.bottom }, kOutlineColor);
    }
    return frame;
}

// Output size for a frame whose longest edge must not exceed kMaxEdgePixels.
inline void FitSize(int width, int height, int* outWidth, int* outHeight)
{
    const int longest = (std::max)(width, height);
    if (longest <= kMaxEdgePixels) { *outWidth = width; *outHeight = height; return; }
    const double factor = (double)kMaxEdgePixels / longest;
    *outWidth = (std::max)(1, (int)std::lround(width * factor));
    *outHeight = (std::max)(1, (int)std::lround(height * factor));
}

inline std::wstring Base64(const std::vector<uint8_t>& bytes)
{
    static const wchar_t table[] = L"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::wstring out;
    out.reserve((bytes.size() + 2) / 3 * 4);
    size_t i = 0;
    for (; i + 2 < bytes.size(); i += 3) {
        const uint32_t v = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
        out += table[(v >> 18) & 63]; out += table[(v >> 12) & 63]; out += table[(v >> 6) & 63]; out += table[v & 63];
    }
    if (i < bytes.size()) {
        const uint32_t v = (bytes[i] << 16) | (i + 1 < bytes.size() ? bytes[i + 1] << 8 : 0);
        out += table[(v >> 18) & 63]; out += table[(v >> 12) & 63];
        out += i + 1 < bytes.size() ? table[(v >> 6) & 63] : L'=';
        out += L'=';
    }
    return out;
}
} // namespace DevToolsSnapshot
