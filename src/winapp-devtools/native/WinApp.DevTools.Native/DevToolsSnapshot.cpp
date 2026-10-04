// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsSnapshot.h"
#include "DevToolsProjected.h"
#include "DevToolsSnapshotImage.h"

#include <wincodec.h>
#include <winrt/Microsoft.UI.Xaml.Media.Imaging.h>
#include <winrt/Windows.Storage.Streams.h>
#include <atomic>
#include <vector>

void DevToolsOverlayLog(const wchar_t* fmt, ...);
namespace
{
using DevToolsSnapshot::Rect;
using DevToolsSnapshot::PixelRect;

// Larger images are dropped rather than stored: a comment screenshot is context, not an asset.
constexpr size_t kMaxPngBytes = 512 * 1024;
constexpr int kMaxWalkedNodes = 20000;

double Milliseconds(LONGLONG from, LONGLONG to)
{
    LARGE_INTEGER frequency; QueryPerformanceFrequency(&frequency);
    return (double)(to - from) * 1000.0 / (double)frequency.QuadPart;
}

LONGLONG Now() { LARGE_INTEGER t; QueryPerformanceCounter(&t); return t.QuadPart; }

Rect BoundsIn(const DevToolsX::UIElement& element, const DevToolsX::UIElement& root)
{
    const auto size = element.ActualSize();
    if (size.x <= 0 || size.y <= 0) return {};
    const auto r = element.TransformToVisual(root).TransformBounds({ 0, 0, size.x, size.y });
    return Rect{ r.X, r.Y, r.Width, r.Height };
}

bool Clips(const DevToolsX::DependencyObject& node)
{
    return node.try_as<DevToolsXC::ScrollViewer>() || node.try_as<DevToolsXC::ScrollContentPresenter>() ||
           node.try_as<DevToolsXCP::ScrollPresenter>();
}

const wchar_t* UncapturedKind(const DevToolsX::DependencyObject& node)
{
    if (node.try_as<DevToolsXC::WebView2>()) return L"WebView2";
    if (node.try_as<DevToolsXC::SwapChainPanel>()) return L"SwapChainPanel";
    if (node.try_as<DevToolsXC::MediaPlayerElement>()) return L"MediaPlayerElement";
    return nullptr;
}

std::wstring Number(double value)
{
    wchar_t text[32];
    swprintf_s(text, L"%.1f", value);
    return text;
}

bool EncodePng(const DevToolsSnapshot::Frame& frame, int width, int height, std::vector<uint8_t>* png)
{
    winrt::com_ptr<IWICImagingFactory> factory;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(factory.put()))))
        return false;
    winrt::com_ptr<IWICBitmap> bitmap;
    if (FAILED(factory->CreateBitmapFromMemory(frame.width, frame.height, GUID_WICPixelFormat32bppBGRA,
            frame.width * 4, (UINT)(frame.pixels.size() * 4),
            reinterpret_cast<BYTE*>(const_cast<uint32_t*>(frame.pixels.data())), bitmap.put())))
        return false;
    winrt::com_ptr<IWICBitmapSource> source = bitmap;
    if (width != frame.width || height != frame.height) {
        winrt::com_ptr<IWICBitmapScaler> scaler;
        if (FAILED(factory->CreateBitmapScaler(scaler.put())) ||
            FAILED(scaler->Initialize(bitmap.get(), width, height, WICBitmapInterpolationModeFant)))
            return false;
        source = scaler;
    }
    winrt::com_ptr<IStream> stream;
    if (FAILED(CreateStreamOnHGlobal(nullptr, TRUE, stream.put()))) return false;
    winrt::com_ptr<IWICBitmapEncoder> encoder;
    winrt::com_ptr<IWICBitmapFrameEncode> encodeFrame;
    WICPixelFormatGUID format = GUID_WICPixelFormat24bppBGR;
    if (FAILED(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, encoder.put())) ||
        FAILED(encoder->Initialize(stream.get(), WICBitmapEncoderNoCache)) ||
        FAILED(encoder->CreateNewFrame(encodeFrame.put(), nullptr)) ||
        FAILED(encodeFrame->Initialize(nullptr)) ||
        FAILED(encodeFrame->SetSize(width, height)) ||
        FAILED(encodeFrame->SetPixelFormat(&format)) ||
        FAILED(encodeFrame->WriteSource(source.get(), nullptr)) ||
        FAILED(encodeFrame->Commit()) || FAILED(encoder->Commit()))
        return false;
    STATSTG stat{};
    if (FAILED(stream->Stat(&stat, STATFLAG_NONAME)) || stat.cbSize.QuadPart > kMaxPngBytes) return false;
    png->resize((size_t)stat.cbSize.QuadPart);
    LARGE_INTEGER zero{};
    ULONG read = 0;
    return SUCCEEDED(stream->Seek(zero, STREAM_SEEK_SET, nullptr)) &&
           SUCCEEDED(stream->Read(png->data(), (ULONG)png->size(), &read)) && read == png->size();
}
} // namespace

struct DevToolsSnapshotJob
{
    HANDLE done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    ~DevToolsSnapshotJob() { if (done) CloseHandle(done); }

    // Read on the UI thread before rendering starts.
    Rect root, visible, context, element;
    std::vector<Rect> masks;
    std::vector<std::wstring> uncaptured;
    bool dark = false;
    LONGLONG started = 0;
    double uiMs = 0;

    // Written by the render completions, then published by `done`.
    std::vector<uint8_t> pixels;
    int pixelWidth = 0, pixelHeight = 0;
    double renderMs = 0;
    const wchar_t* error = nullptr;
    std::atomic<bool> finished{ false };

    void Complete(const wchar_t* failure)
    {
        if (finished.exchange(true)) return;
        error = failure;
        DevToolsOverlayLog(L"snapshot: render %s %dx%d bytes=%zu", failure ? failure : L"ok", pixelWidth, pixelHeight, pixels.size());
        SetEvent(done);
    }
};

namespace
{
// The render completion fires before the frame that fills the bitmap is committed, so the first reads can come back
// empty. Retry on later frames for a bounded time.
constexpr int kPixelReadAttempts = 20;

void ReadPixels(const std::shared_ptr<DevToolsSnapshotJob>& job, const DevToolsXM::Imaging::RenderTargetBitmap& bitmap,
                const winrt::Microsoft::UI::Dispatching::DispatcherQueue& dispatcher, int attempt)
{
    const bool queued = dispatcher.TryEnqueue(winrt::Microsoft::UI::Dispatching::DispatcherQueuePriority::Low,
        [job, bitmap, dispatcher, attempt]() {
        try {
            bitmap.GetPixelsAsync().Completed([job, bitmap, dispatcher, attempt](auto const& read, winrt::Windows::Foundation::AsyncStatus status) {
                try {
                    if (status != winrt::Windows::Foundation::AsyncStatus::Completed) { job->Complete(L"render-failed"); return; }
                    const auto buffer = read.GetResults();
                    if (!buffer || !buffer.Length()) {
                        DevToolsOverlayLog(L"snapshot: pixels attempt %d empty (buffer=%d)", attempt, buffer ? 1 : 0);
                        if (attempt + 1 < kPixelReadAttempts) ReadPixels(job, bitmap, dispatcher, attempt + 1);
                        else job->Complete(L"render-empty");
                        return;
                    }
                    job->pixels.assign(buffer.data(), buffer.data() + buffer.Length());
                    job->renderMs = Milliseconds(job->started, Now()) - job->uiMs;
                    job->Complete(nullptr);
                } catch (...) { job->Complete(L"render-failed"); }
            });
        } catch (...) { job->Complete(L"render-failed"); }
    });
    if (!queued) job->Complete(L"render-failed");
}
} // namespace

std::shared_ptr<DevToolsSnapshotJob> DevToolsSnapshot_Begin(IInspectable* raw, const wchar_t** error)
{
    *error = nullptr;
    auto job = std::make_shared<DevToolsSnapshotJob>();
    if (!job->done) { *error = L"internal"; return nullptr; }
    job->started = Now();
    try {
        winrt::Windows::Foundation::IInspectable inspectable;
        winrt::copy_from_abi(inspectable, raw);
        const auto element = inspectable.try_as<DevToolsX::UIElement>();
        const auto xamlRoot = element ? element.XamlRoot() : nullptr;
        if (!xamlRoot) { *error = L"not-in-window"; return nullptr; }

        // Ancestors, nearest first. Rendering the window's content (or a flyout's popup child) instead of the
        // element is what keeps the DevTools highlight out, shows real clipping, and gives the context crop.
        std::vector<DevToolsX::DependencyObject> chain;
        for (auto node = DevToolsXM::VisualTreeHelper::GetParent(element); node;
             node = DevToolsXM::VisualTreeHelper::GetParent(node))
            chain.push_back(node);
        auto inChain = [&](const DevToolsX::UIElement& candidate) {
            if (!candidate) return false;
            if (candidate == element) return true;
            for (const auto& node : chain) if (node == candidate) return true;
            return false;
        };
        DevToolsX::UIElement root = xamlRoot.Content();
        if (!inChain(root)) {
            root = nullptr;
            for (const auto& popup : DevToolsXM::VisualTreeHelper::GetOpenPopupsForXamlRoot(xamlRoot))
                if (inChain(popup.Child())) { root = popup.Child(); break; }
        }
        if (!root) { *error = L"not-in-window"; return nullptr; }
        const auto rootSize = root.ActualSize();
        job->root = Rect{ 0, 0, rootSize.x, rootSize.y };
        if (const auto fe = root.try_as<DevToolsX::FrameworkElement>())
            job->dark = fe.ActualTheme() == DevToolsX::ElementTheme::Dark;

        if (element.Visibility() != DevToolsX::Visibility::Visible) { *error = L"no-visible-box"; return nullptr; }
        job->element = BoundsIn(element, root);
        if (job->element.Empty()) { *error = L"no-visible-box"; return nullptr; }
        std::vector<DevToolsSnapshot::Ancestor> ancestors;
        for (const auto& node : chain) {
            if (node == root) break;
            const auto ui = node.try_as<DevToolsX::UIElement>();
            if (!ui) continue;
            if (ui.Visibility() != DevToolsX::Visibility::Visible) { *error = L"not-visible"; return nullptr; }
            ancestors.push_back({ BoundsIn(ui, root), Clips(node) });
        }
        Rect visible;
        const auto shown = DevToolsSnapshot::OnScreen(job->element, ancestors, job->root, &visible);
        if (visible.Empty()) { *error = L"not-visible"; return nullptr; }
        job->visible = visible;
        job->context = DevToolsSnapshot::ContextRect(visible, shown, job->root);

        // Secrets never reach the image: every PasswordBox in the frame is painted over, revealed or not, whether
        // it is the commented element or only near it.
        std::vector<DevToolsX::DependencyObject> stack{ root };
        int walked = 0;
        while (!stack.empty() && walked++ < kMaxWalkedNodes) {
            const auto node = stack.back();
            stack.pop_back();
            const bool password = (bool)node.try_as<DevToolsXC::PasswordBox>();
            const wchar_t* uncaptured = password ? nullptr : UncapturedKind(node);
            if (password || uncaptured) {
                auto ui = node.try_as<DevToolsX::UIElement>();
                // Mask the input itself, keeping the header a reviewer may be commenting on.
                if (password && DevToolsXM::VisualTreeHelper::GetChildrenCount(node) > 0)
                    if (const auto part = DevToolsXM::VisualTreeHelper::GetChild(node, 0).try_as<DevToolsX::FrameworkElement>())
                        if (const auto border = part.FindName(L"BorderElement").try_as<DevToolsX::UIElement>()) ui = border;
                const Rect bounds = ui ? DevToolsSnapshot::Intersect(BoundsIn(ui, root), job->context) : Rect{};
                if (!bounds.Empty()) {
                    if (password) job->masks.push_back(bounds);
                    else if (std::find(job->uncaptured.begin(), job->uncaptured.end(), uncaptured) == job->uncaptured.end())
                        job->uncaptured.push_back(uncaptured);
                }
                if (password) continue;
            }
            const int count = DevToolsXM::VisualTreeHelper::GetChildrenCount(node);
            for (int i = count - 1; i >= 0; --i) stack.push_back(DevToolsXM::VisualTreeHelper::GetChild(node, i));
        }
        // A capped walk could miss a password box; an unmasked secret is worse than no image.
        if (walked > kMaxWalkedNodes) { *error = L"tree-too-large"; return nullptr; }
        job->uiMs = Milliseconds(job->started, Now());
        DevToolsOverlayLog(L"snapshot: begin tid=%lu root=%.0fx%.0f context=%.0f,%.0f,%.0f,%.0f masks=%zu", GetCurrentThreadId(),
            job->root.w, job->root.h, job->context.x, job->context.y, job->context.w, job->context.h, job->masks.size());

        DevToolsXM::Imaging::RenderTargetBitmap bitmap;
        const auto dispatcher = root.DispatcherQueue();
        bitmap.RenderAsync(root).Completed([job, bitmap, dispatcher](auto const& render, winrt::Windows::Foundation::AsyncStatus status) {
            try {
                if (status != winrt::Windows::Foundation::AsyncStatus::Completed) { job->Complete(L"render-failed"); return; }
                render.GetResults();
                job->pixelWidth = bitmap.PixelWidth();
                job->pixelHeight = bitmap.PixelHeight();
                if (job->pixelWidth <= 0 || job->pixelHeight <= 0) { job->Complete(L"render-empty"); return; }
                ReadPixels(job, bitmap, dispatcher, 0);
            } catch (...) { job->Complete(L"render-failed"); }
        });
        return job;
    } catch (...) {
        *error = L"render-failed";
        return nullptr;
    }
}

bool DevToolsSnapshot_Finish(const std::shared_ptr<DevToolsSnapshotJob>& job, DWORD timeoutMs,
                             std::wstring* json, const wchar_t** error)
{
    *error = nullptr;
    if (!job) { *error = L"internal"; return false; }
    if (WaitForSingleObject(job->done, timeoutMs) != WAIT_OBJECT_0) { *error = L"timeout"; return false; }
    if (job->error) { *error = job->error; return false; }
    if (job->pixelWidth <= 0 || job->pixelHeight <= 0 || job->root.w <= 0 ||
        job->pixels.size() != (size_t)job->pixelWidth * job->pixelHeight * 4) {
        *error = L"render-failed";
        return false;
    }
    const LONGLONG composeStart = Now();
    const double scale = job->pixelWidth / job->root.w;
    const PixelRect crop = DevToolsSnapshot::ToPixels(job->context, scale, job->pixelWidth, job->pixelHeight);
    std::vector<PixelRect> masks;
    for (const auto& mask : job->masks) masks.push_back(DevToolsSnapshot::ToPixels(mask, scale, job->pixelWidth, job->pixelHeight));
    const auto frame = DevToolsSnapshot::Compose(job->pixels.data(), job->pixelWidth, job->pixelHeight, crop,
        DevToolsSnapshot::BackdropColor(job->dark), masks,
        DevToolsSnapshot::ToPixels(job->visible, scale, job->pixelWidth, job->pixelHeight));
    if (frame.pixels.empty()) { *error = L"not-visible"; return false; }
    int width = 0, height = 0;
    DevToolsSnapshot::FitSize(frame.width, frame.height, &width, &height);
    const LONGLONG encodeStart = Now();
    std::vector<uint8_t> png;
    const HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool encoded = EncodePng(frame, width, height, &png);
    if (SUCCEEDED(apartment)) CoUninitialize();
    if (!encoded) { *error = png.empty() ? L"encode-failed" : L"too-large"; return false; }
    const LONGLONG end = Now();

    const bool partial = job->visible.w < job->element.w - 0.5 || job->visible.h < job->element.h - 0.5;
    std::wstring uncaptured;
    for (const auto& kind : job->uncaptured) uncaptured += (uncaptured.empty() ? L"\"" : L",\"") + kind + L"\"";
    *json = L"{\"png\":\"" + DevToolsSnapshot::Base64(png) + L"\",\"width\":" + std::to_wstring(width) +
        L",\"height\":" + std::to_wstring(height) + L",\"visibility\":\"" + (partial ? L"partial" : L"full") +
        L"\",\"theme\":\"" + (job->dark ? L"dark" : L"light") + L"\",\"masked\":" + std::to_wstring(job->masks.size()) +
        L",\"notCaptured\":[" + uncaptured + L"],\"timing\":{\"uiMs\":" + Number(job->uiMs) +
        L",\"renderMs\":" + Number(job->renderMs) + L",\"composeMs\":" + Number(Milliseconds(composeStart, encodeStart)) +
        L",\"encodeMs\":" + Number(Milliseconds(encodeStart, end)) +
        L",\"totalMs\":" + Number(Milliseconds(job->started, end)) + L"}}";
    return true;
}
