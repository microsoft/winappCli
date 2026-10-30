// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

// Microsoft.UI.Reactor trace in the overlay: a badge on the pill (render and swallowed errors and keyed-list warnings since launch), and
// the "Highlight updates" toggle that flashes each component as it renders. The toggle appears only when the app's
// Reactor reports component renders. Everything here runs on the app's UI thread.

static DevToolsHighlightUpdatesFn g_highlightUpdatesSet = nullptr;
static bool g_highlightUpdatesAvailable = false;
static bool g_highlightUpdates = false;
static unsigned int g_reactorErrorCount = 0;

struct RenderFlash
{
    InstanceHandle handle = 0;
    IInspectable* ui = nullptr;
    ULONGLONG until = 0;
};
static std::vector<RenderFlash> g_renderFlashes;
static const ULONGLONG kRenderFlashMs = 450;
static const size_t kMaxRenderFlashes = 64;

static void UpdateReactorErrorBadge()
{
    const unsigned int n = g_reactorErrorCount;
    ProtoSetPartText(L"DevToolsChipErrorsText", n > 99 ? std::wstring(L"99+") : std::to_wstring(n));
    ProtoSetPartVisible(L"DevToolsChipErrors", n > 0);
    // The open (pinned) bar hides the pill, so it carries the count too.
    ProtoSetPartText(L"DevToolsBarErrorsText", n > 99 ? std::wstring(L"99+") : std::to_wstring(n));
    ProtoSetPartVisible(L"DevToolsBarErrors", n > 0);
}

static void RemoveRenderFlash(RenderFlash& flash)
{
    if (!flash.ui) return;
    unsigned int idx = 0; unsigned char found = 0;
    if (g_canvasChildren && SUCCEEDED(DevToolsVecIndexOf(g_canvasChildren, flash.ui, &idx, &found)) && found)
        DevToolsVecRemoveAt(g_canvasChildren, idx);
    flash.ui->Release();
    flash.ui = nullptr;
}

// Called every tracking tick: drop the flashes whose time is up.
static void ExpireRenderFlashes()
{
    if (g_renderFlashes.empty()) return;
    const ULONGLONG now = GetTickCount64();
    for (auto it = g_renderFlashes.begin(); it != g_renderFlashes.end();) {
        if (now < it->until) { ++it; continue; }
        RemoveRenderFlash(*it);
        it = g_renderFlashes.erase(it);
    }
}

static void ClearRenderFlashes()
{
    for (auto& flash : g_renderFlashes) RemoveRenderFlash(flash);
    g_renderFlashes.clear();
}

static void OnHighlightUpdatesClick(void*, void*)
{
    g_highlightUpdates = !g_highlightUpdates;
    ProtoSetToggle(L"DevToolsProtoHighlight", L"DevToolsHighlightOn", L"DevToolsHighlightOff", g_highlightUpdates);
    if (!g_highlightUpdates) ClearRenderFlashes();
    DevToolsOverlayLog(L"overlay.reactor highlight updates %s", g_highlightUpdates ? L"on" : L"off");
    if (g_highlightUpdatesSet) g_highlightUpdatesSet(g_highlightUpdates);
}

void DevToolsOverlay_SetHighlightUpdatesBridge(DevToolsHighlightUpdatesFn set) { g_highlightUpdatesSet = set; }

void DevToolsOverlay_SetReactorProblems(unsigned int count)
{
    g_reactorErrorCount = count;
    UpdateReactorErrorBadge();
}

void DevToolsOverlay_SetHighlightUpdatesAvailable(bool available)
{
    if (available == g_highlightUpdatesAvailable) return;
    g_highlightUpdatesAvailable = available;
    ProtoSetPartVisible(L"DevToolsProtoHighlight", available);
    g_barExtraW = available ? kHighlightButtonW : 0;
    if (IInspectable* bar = g_protoRoot ? DevToolsSelFindName(g_protoRoot, L"DevToolsBar") : nullptr) {
        void* fe = nullptr;
        if (SUCCEEDED(bar->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), &fe)) && fe) {
            DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->put_Width((double)(kBarW + g_barExtraW));
            reinterpret_cast<IUnknown*>(fe)->Release();
        }
        bar->Release();
    }
    if (!available && g_highlightUpdates) OnHighlightUpdatesClick(nullptr, nullptr);
    RepositionToolbar();
}

bool DevToolsOverlay_HighlightUpdatesOn() { return g_highlightUpdates; }

// A rebuilt toolbar starts from its markup; put back the badge and the toggle.
static void ReapplyReactorToolbarState()
{
    UpdateReactorErrorBadge();
    if (!g_highlightUpdatesAvailable) return;
    g_highlightUpdatesAvailable = false;
    const bool on = g_highlightUpdates;
    g_highlightUpdates = false;
    DevToolsOverlay_SetHighlightUpdatesAvailable(true);
    if (on) {
        g_highlightUpdates = true;
        ProtoSetToggle(L"DevToolsProtoHighlight", L"DevToolsHighlightOn", L"DevToolsHighlightOff", true);
    }
}

// Outlines each element that just re-rendered, on the active surface, for a moment.
void DevToolsOverlay_FlashRendered(const std::vector<InstanceHandle>& handles)
{
    if (!g_highlightUpdates || !g_pickDiag || !g_pickRoot || !g_canvasChildren) return;
    const DevToolsPalette& pal = DevToolsBuildPalette();
    for (InstanceHandle handle : handles) {
        RECT bounds{};
        if (!handle || !TryElementRootBounds(g_pickDiag, g_pickRoot, handle, &bounds) ||
            bounds.right <= bounds.left || bounds.bottom <= bounds.top) continue;
        const ULONGLONG until = GetTickCount64() + kRenderFlashMs;
        auto existing = std::find_if(g_renderFlashes.begin(), g_renderFlashes.end(),
                                     [handle](const RenderFlash& flash) { return flash.handle == handle; });
        if (existing != g_renderFlashes.end()) {
            existing->until = until;
            MoveOverlayElement(existing->ui, (double)bounds.left, (double)bounds.top);
            continue;
        }
        if (g_renderFlashes.size() >= kMaxRenderFlashes) {
            RemoveRenderFlash(g_renderFlashes.front());
            g_renderFlashes.erase(g_renderFlashes.begin());
        }
        const std::wstring markup =
            L"<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" IsHitTestVisible=\"False\""
            L" AutomationProperties.AccessibilityView=\"Raw\" Canvas.ZIndex=\"900\" CornerRadius=\"3\" BorderThickness=\"2\""
            L" BorderBrush=\"" + pal.accent + L"\" Canvas.Left=\"" + std::to_wstring(bounds.left) + L"\" Canvas.Top=\"" +
            std::to_wstring(bounds.top) + L"\" Width=\"" + std::to_wstring(bounds.right - bounds.left) + L"\" Height=\"" +
            std::to_wstring(bounds.bottom - bounds.top) + L"\"/>";
        IInspectable* insp = nullptr;
        if (FAILED(LoadMarkup(markup, &insp)) || !insp) continue;
        IInspectable* ui = nullptr;
        if (SUCCEEDED(insp->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), reinterpret_cast<void**>(&ui))) && ui) {
            unsigned int n = 0;
            DevToolsVecGetSize(g_canvasChildren, &n);
            if (g_cardScope) g_cardScope(1);
            const HRESULT hr = DevToolsVecInsertAt(g_canvasChildren, n, ui);
            if (g_cardScope) g_cardScope(0);
            if (SUCCEEDED(hr)) g_renderFlashes.push_back({ handle, ui, until });
            else ui->Release();
        }
        insp->Release();
    }
}
