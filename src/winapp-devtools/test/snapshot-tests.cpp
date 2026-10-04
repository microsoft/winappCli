// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsSnapshotImage.h"

#include <cstdio>

using namespace DevToolsSnapshot;

static int g_failures = 0;

static void Check(bool cond, const char* what)
{
    if (!cond) { ++g_failures; std::printf("  FAIL  %s\n", what); }
    else       {              std::printf("  ok    %s\n", what); }
}

static bool Same(const Rect& a, const Rect& b)
{
    return std::abs(a.x - b.x) < 0.01 && std::abs(a.y - b.y) < 0.01 && std::abs(a.w - b.w) < 0.01 && std::abs(a.h - b.h) < 0.01;
}

static void TestContextStopsAtPageLayout()
{
    std::printf("context: the nearest small ancestor frames the element, page layout does not\n");
    const Rect root{ 0, 0, 1000, 800 };
    const Rect pill{ 100, 300, 60, 24 };
    const Rect row{ 40, 296, 600, 32 };
    const Rect page{ 0, 0, 1000, 2000 };
    const Rect context = ContextRect(pill, { row, page }, root);
    Check(Same(context, Rect{ 16, 272, 648, 80 }), "row plus padding, not the page");
    Check(Same(ContextRect(pill, {}, root), Rect{ 76, 276, 108, 72 }), "no ancestor: the element plus padding");
    // The window-share rule: a 300-tall card that covers half of a small window is layout, not context.
    const Rect small{ 0, 0, 400, 400 };
    const Rect card{ 0, 50, 400, 300 };
    Check(Same(ContextRect(Rect{ 10, 60, 50, 20 }, { card }, small), Rect{ 0, 36, 84, 68 }), "an ancestor over 35% of the window is skipped");
    Check(Same(ContextRect(Rect{ -50, -50, 100, 100 }, {}, root), Rect{ 0, 0, 74, 74 }), "the frame never leaves the window");
}

static void TestOnScreenClipsToScrollViewport()
{
    std::printf("on screen: a scrolled element is framed as the reviewer saw it\n");
    const Rect root{ 0, 0, 1000, 800 };
    const std::vector<Ancestor> chain{
        { Rect{ 20, 0, 600, 1200 }, false },   // items panel: taller than its viewport
        { Rect{ 20, 500, 600, 110 }, true },   // the list's scroll viewer
        { Rect{ 0, 0, 1000, 800 }, false },
    };
    Rect visible;
    auto shown = OnScreen(Rect{ 20, 590, 600, 33 }, chain, root, &visible);
    Check(Same(visible, Rect{ 20, 590, 600, 20 }), "a partly scrolled item keeps only its visible part");
    Check(Same(shown[0], Rect{ 20, 500, 600, 110 }), "an ancestor inside the viewport is clipped to it");
    Check(Same(shown[2], root), "an ancestor above the viewport is not clipped by it");
    OnScreen(Rect{ 20, 700, 600, 33 }, chain, root, &visible);
    Check(visible.Empty(), "an item scrolled out of view has nothing to show");
    OnScreen(Rect{ 20, 700, 600, 33 }, { { Rect{ 20, 500, 600, 110 }, false } }, root, &visible);
    Check(!visible.Empty(), "a non-scrolling ancestor does not clip (control)");
}

static void TestComposeFlattensMasksAndOutlines()
{
    std::printf("compose: flatten on the theme backdrop, mask secrets, outline the element\n");
    const int w = 20, h = 10;
    std::vector<uint8_t> source((size_t)w * h * 4, 0);   // fully transparent, as Mica renders
    auto at = [&](int x, int y) { return source.data() + ((size_t)y * w + x) * 4; };
    at(2, 2)[0] = 10; at(2, 2)[1] = 20; at(2, 2)[2] = 30; at(2, 2)[3] = 255;   // opaque pixel
    at(15, 5)[2] = 255; at(15, 5)[3] = 255;                                    // red "password" pixel
    const PixelRect crop{ 1, 1, 19, 9 };
    const auto frame = Compose(source.data(), w, h, crop, BackdropColor(true), { PixelRect{ 14, 4, 17, 7 } }, PixelRect{ 6, 4, 9, 6 });
    auto px = [&](int x, int y) { return frame.pixels[(size_t)(y - crop.top) * frame.width + (x - crop.left)]; };
    Check(frame.width == 18 && frame.height == 8, "the frame is the crop");
    Check(px(16, 1) == BackdropColor(true), "transparent pixels take the dark backdrop");
    Check(px(2, 2) == 0xFF1E140Au, "opaque pixels keep their color");
    Check(px(15, 5) == kMaskColor, "a masked pixel never shows the secret");
    Check(px(7, 4) == BackdropColor(true), "the outline does not cover the element");
    Check(px(5, 4) == kOutlineColor && px(9, 5) == kOutlineColor && px(7, 3) == kOutlineColor, "the outline surrounds the element");
    const auto light = Compose(source.data(), w, h, crop, BackdropColor(false), {}, PixelRect{});
    Check(light.pixels[0] == 0xFFF3F3F3u, "light theme flattens onto the light backdrop");
    Check(Compose(source.data(), w, h, PixelRect{ 0, 0, 21, 5 }, 0, {}, {}).pixels.empty(), "a crop outside the render is refused");
}

static void TestFitAndEncodeHelpers()
{
    std::printf("output: longest edge capped, base64 exact\n");
    int ow = 0, oh = 0;
    FitSize(3000, 60, &ow, &oh);
    Check(ow == 1024 && oh == 20, "a wide frame is scaled to 1024 px");
    FitSize(800, 600, &ow, &oh);
    Check(ow == 800 && oh == 600, "a small frame is kept as is");
    Check(Base64({ 'M', 'a', 'n' }) == L"TWFu" && Base64({ 'M', 'a' }) == L"TWE=" && Base64({ 'M' }) == L"TQ==", "base64 padding");
}

int RunSnapshotTests()
{
    std::printf("DevToolsSnapshot tests\n");
    TestContextStopsAtPageLayout();
    TestOnScreenClipsToScrollViewport();
    TestComposeFlattensMasksAndOutlines();
    TestFitAndEncodeHelpers();
    return g_failures;
}
