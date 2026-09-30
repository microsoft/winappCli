// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsOverlay.cpp; included into that translation unit, not compiled on its own.

// Where the overlay chrome lives. Preferred: the XAML diagnostics UI layer of each XamlRoot, which draws above app
// content, in-window popups and dialogs, and is outside the app's visual tree, layout and hit tests. When that layer
// is unavailable for a XamlRoot, the chrome's own Popups are inserted into the app's outermost panel instead.
// The toolbar, highlight and selection hosts each keep their Popup; on the layer path its content moves into the
// layer and "open" becomes the content's Visibility.

#include <winrt/Microsoft.UI.Xaml.Automation.Peers.h>
#include <winrt/Windows.UI.h>

namespace DevToolsXAP = winrt::Microsoft::UI::Xaml::Automation::Peers;

enum class DevToolsHostPath { None, UiLayer, Popup };
static DevToolsHostPath g_hostPath = DevToolsHostPath::None;
static bool g_forcePopupHost = false;

// UI Automation does not walk into the layer. An unparented, zero-size Popup on the same XamlRoot holds this
// proxy, whose peer reports the layer content's peers as its children, so screen readers and UIA tree walks
// reach the chrome as they did when it was hosted in the app's tree.
struct DevToolsLayerBridgePeer : DevToolsXAP::FrameworkElementAutomationPeerT<DevToolsLayerBridgePeer>
{
    DevToolsLayerBridgePeer(DevToolsX::FrameworkElement const& owner, DevToolsX::UIElement const& content)
        : FrameworkElementAutomationPeerT<DevToolsLayerBridgePeer>(owner), m_content(content) {}

    winrt::Windows::Foundation::Collections::IVector<DevToolsXAP::AutomationPeer> GetChildrenCore()
    {
        auto peers = winrt::single_threaded_vector<DevToolsXAP::AutomationPeer>();
        try { Collect(m_content, peers); }
        catch (const winrt::hresult_error& e) { DevToolsOverlayLog(L"overlay.bridge children failed hr=0x%08X", e.code().value); }
        return peers;
    }
    DevToolsXAP::AutomationControlType GetAutomationControlTypeCore() { return DevToolsXAP::AutomationControlType::Pane; }
    winrt::hstring GetClassNameCore() { return L"DevToolsOverlay"; }
    winrt::hstring GetNameCore() { return L"DevTools"; }
    winrt::hstring GetAutomationIdCore() { return L"DevToolsOverlay"; }

private:
    // Like the framework's own walk: the nearest descendants that have a peer, skipping collapsed content.
    static void Collect(DevToolsX::DependencyObject const& node,
                        winrt::Windows::Foundation::Collections::IVector<DevToolsXAP::AutomationPeer> const& peers)
    {
        const int32_t count = DevToolsXM::VisualTreeHelper::GetChildrenCount(node);
        for (int32_t i = 0; i < count; ++i) {
            auto child = DevToolsXM::VisualTreeHelper::GetChild(node, i);
            auto ui = child.try_as<DevToolsX::UIElement>();
            if (!ui || ui.Visibility() != DevToolsX::Visibility::Visible) continue;
            if (auto peer = DevToolsXAP::FrameworkElementAutomationPeer::CreatePeerForElement(ui)) peers.Append(peer);
            else Collect(child, peers);
        }
    }
    DevToolsX::UIElement m_content{ nullptr };
};

struct DevToolsLayerBridge : DevToolsXC::GridT<DevToolsLayerBridge>
{
    explicit DevToolsLayerBridge(DevToolsX::UIElement const& content) : m_content(content) {}
    DevToolsXAP::AutomationPeer OnCreateAutomationPeer() { return winrt::make<DevToolsLayerBridgePeer>(*this, m_content); }
    DevToolsX::UIElement m_content{ nullptr };
};

struct DevToolsLayerRoot
{
    unsigned long long key = 0;               // the XamlRoot's IUnknown identity, as DevToolsSurface keys it
    winrt::com_ptr<IUnknown> xamlRoot;
    DevToolsXC::Grid root{ nullptr };          // ours, inside the layer
    DevToolsXCP::Popup bridge{ nullptr };
    winrt::com_ptr<IUnknown> themeSource;      // the app content whose theme the chrome follows
};
static std::vector<DevToolsLayerRoot> g_layerRoots;
static std::vector<unsigned long long> g_layerUnavailable;

struct DevToolsHostSlot
{
    winrt::com_ptr<IUnknown> popup, popupUi;
    DevToolsXCP::Popup popupObject{ nullptr };
    DevToolsX::UIElement content{ nullptr };
    unsigned long long layerKey = 0;           // 0: hosted by its Popup
    bool open = false;
    bool topmost = false;                      // the toolbar stays above the other chrome
};
static std::vector<DevToolsHostSlot> g_hostSlots;

static winrt::com_ptr<IUnknown> HostIdentity(IInspectable* object)
{
    winrt::com_ptr<IUnknown> identity;
    if (object) object->QueryInterface(IID_IUnknown, identity.put_void());
    return identity;
}

static void HostRegister(IInspectable* popup, IInspectable* popupUi, IInspectable* content, bool topmost)
{
    if (!popup || !content) return;
    DevToolsHostSlot slot;
    slot.topmost = topmost;
    slot.popup = HostIdentity(popup);
    slot.popupUi = HostIdentity(popupUi ? popupUi : popup);
    winrt::Windows::Foundation::IInspectable popupObject{ nullptr }, object{ nullptr };
    winrt::copy_from_abi(popupObject, popup);
    slot.popupObject = popupObject.as<DevToolsXCP::Popup>();
    winrt::copy_from_abi(object, content);
    slot.content = object.as<DevToolsX::UIElement>();
    g_hostSlots.push_back(std::move(slot));
}

static DevToolsHostSlot* HostSlotFor(IInspectable* popupOrUi)
{
    const auto identity = HostIdentity(popupOrUi);
    for (auto& slot : g_hostSlots)
        if (identity && (slot.popup.get() == identity.get() || slot.popupUi.get() == identity.get())) return &slot;
    return nullptr;
}

static DevToolsLayerRoot* LayerRootByKey(unsigned long long key)
{
    for (auto& root : g_layerRoots) if (root.key == key) return &root;
    return nullptr;
}

// Key events in the layer do not route through the app's content, where Esc is otherwise handled.
static void OnAppEscape(void*, void* args);

// Every step must answer for the layer to be used; anything else selects the Popup host for that XamlRoot.
static bool HostUseLayer(bool forced, bool hasDiag2, HRESULT handle, HRESULT layer, bool isPanel)
{
    return !forced && hasDiag2 && SUCCEEDED(handle) && SUCCEEDED(layer) && isPanel;
}

// The layer for `element`'s XamlRoot, with our root and UIA bridge in it; null selects the Popup path.
static DevToolsLayerRoot* LayerRootFor(IXamlDiagnostics* diag, IInspectable* element)
{
    if (!diag || !element || g_forcePopupHost) return nullptr;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, element);
        auto ui = object.as<DevToolsX::UIElement>();
        auto xamlRoot = ui.XamlRoot();
        if (!xamlRoot) return nullptr;
        auto identity = HostIdentity(reinterpret_cast<IInspectable*>(winrt::get_abi(xamlRoot)));
        const auto key = reinterpret_cast<unsigned long long>(identity.get());
        if (auto* known = LayerRootByKey(key)) return known;
        if (std::find(g_layerUnavailable.begin(), g_layerUnavailable.end(), key) != g_layerUnavailable.end()) return nullptr;

        IXamlDiagnostics2_Abi* diag2 = CachedDiag2(diag);
        InstanceHandle rootHandle = 0;
        winrt::com_ptr<IInspectable> layer;
        HRESULT hr = diag2 ? diag->GetHandleFromIInspectable(reinterpret_cast<IInspectable*>(winrt::get_abi(xamlRoot)), &rootHandle)
                           : E_NOINTERFACE;
        if (SUCCEEDED(hr)) hr = diag2->GetUiLayerForXamlRoot(rootHandle, layer.put());
        winrt::Windows::Foundation::IInspectable layerObject{ nullptr };
        if (SUCCEEDED(hr) && layer) winrt::copy_from_abi(layerObject, layer.get());
        auto panel = layerObject ? layerObject.try_as<DevToolsXC::Panel>() : nullptr;
        if (!HostUseLayer(g_forcePopupHost, diag2 != nullptr, diag2 ? S_OK : E_NOINTERFACE, hr, panel != nullptr)) {
            DevToolsOverlayLog(L"overlay.host uiLayer unavailable hr=0x%08X layer=%p -> popup path", hr, layer.get());
            g_layerUnavailable.push_back(key);
            return nullptr;
        }

        DevToolsLayerRoot entry;
        entry.key = key;
        entry.xamlRoot = identity;
        entry.root = DevToolsXC::Grid();
        entry.root.PreviewKeyDown([](auto&&, DevToolsXI::KeyRoutedEventArgs const& e) {
            OnAppEscape(nullptr, winrt::get_abi(e));
        });
        panel.Children().Append(entry.root);
        entry.bridge = DevToolsXCP::Popup();
        entry.bridge.Child(winrt::make<DevToolsLayerBridge>(entry.root));
        for (const winrt::Windows::Foundation::IInspectable& part : { winrt::Windows::Foundation::IInspectable(entry.bridge),
                 entry.bridge.Child().as<winrt::Windows::Foundation::IInspectable>() }) {
            InstanceHandle handle = 0;
            if (g_pickSeed && SUCCEEDED(diag->GetHandleFromIInspectable(reinterpret_cast<IInspectable*>(winrt::get_abi(part)), &handle)) && handle)
                g_pickSeed(handle);
        }
        entry.bridge.XamlRoot(xamlRoot);
        entry.bridge.IsOpen(true);
        DevToolsOverlayLog(L"overlay.host uiLayer root=%llu layer=%p", key, layer.get());
        g_layerRoots.push_back(std::move(entry));
        return &g_layerRoots.back();
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.host uiLayer failed hr=0x%08X -> popup path", e.code().value);
        return nullptr;
    }
}

static void HostDetach(DevToolsX::UIElement const& content)
{
    if (auto parent = DevToolsXM::VisualTreeHelper::GetParent(content).try_as<DevToolsXC::Panel>()) {
        uint32_t index = 0;
        if (parent.Children().IndexOf(content, index)) parent.Children().RemoveAt(index);
    }
}

static void HostApplyVisibility(DevToolsHostSlot& slot)
{
    slot.content.Visibility(slot.open ? DevToolsX::Visibility::Visible : DevToolsX::Visibility::Collapsed);
}

// The layer follows the Application theme; the chrome follows the app content it is attached to, as it did when
// it lived in the app's tree, including when the app changes its own theme.
static void HostFollowTheme(DevToolsLayerRoot& layer, IInspectable* panel)
{
    const auto identity = HostIdentity(panel);
    if (!identity || identity.get() == layer.themeSource.get()) return;
    winrt::Windows::Foundation::IInspectable object{ nullptr };
    winrt::copy_from_abi(object, panel);
    auto fe = object.try_as<DevToolsX::FrameworkElement>();
    if (!fe) return;
    layer.themeSource = identity;
    layer.root.RequestedTheme(fe.ActualTheme());
    const auto key = layer.key;
    fe.ActualThemeChanged([key, source = identity](DevToolsX::FrameworkElement const& sender, auto&&) {
        auto* current = LayerRootByKey(key);
        if (current && current->themeSource.get() == source.get()) current->root.RequestedTheme(sender.ActualTheme());
    });
}

// Replaces AttachViaPanelChildren for a registered host Popup: its content goes into the layer of `panel`'s
// XamlRoot, or, when that is unavailable, the Popup goes into `panel` as before.
static HRESULT HostAttach(IXamlDiagnostics* diag, IInspectable* panel, IInspectable* popupUi, const wchar_t* tag)
{
    DevToolsHostSlot* slot = HostSlotFor(popupUi);
    if (slot) {
        try {
            if (auto* layer = LayerRootFor(diag, panel)) {
                if (slot->layerKey != layer->key) {
                    if (slot->popupObject.Child() == slot->content) {
                        slot->popupObject.IsOpen(false);
                        slot->popupObject.Child(nullptr);
                    }
                    HostDetach(slot->content);
                    if (auto fe = slot->content.try_as<DevToolsX::FrameworkElement>()) {
                        fe.HorizontalAlignment(DevToolsX::HorizontalAlignment::Left);
                        fe.VerticalAlignment(DevToolsX::VerticalAlignment::Top);
                    }
                    // A Popup keeps Tab inside its content; the layer does not, so Tab would leave the toolbar.
                    slot->content.TabFocusNavigation(DevToolsXI::KeyboardNavigationMode::Cycle);
                    layer->root.Children().Append(slot->content);
                    slot->layerKey = layer->key;
                    for (auto& other : g_hostSlots) {
                        uint32_t index = 0;
                        if (other.topmost && &other != slot && other.layerKey == layer->key &&
                            layer->root.Children().IndexOf(other.content, index)) layer->root.Children().Move(index, layer->root.Children().Size() - 1);
                    }
                }
                HostApplyVisibility(*slot);
                HostFollowTheme(*layer, panel);
                g_hostPath = DevToolsHostPath::UiLayer;
                DevToolsOverlayLog(L"overlay.%s host=uiLayer root=%llu", tag, layer->key);
                return S_OK;
            }
            if (slot->popupObject.Child() != slot->content) {
                HostDetach(slot->content);
                slot->content.Visibility(DevToolsX::Visibility::Visible);
                slot->popupObject.Child(slot->content);
            }
            slot->layerKey = 0;
        } catch (const winrt::hresult_error& e) {
            DevToolsOverlayLog(L"overlay.%s host move failed hr=0x%08X", tag, e.code().value);
            return e.code();
        }
    }
    const HRESULT hr = AttachViaPanelChildren(panel, popupUi, tag);
    if (SUCCEEDED(hr)) {
        g_hostPath = DevToolsHostPath::Popup;
        DevToolsOverlayLog(L"overlay.%s host=popup", tag);
    }
    return hr;
}

// A windowed Popup whose parent is in the layer does not get input: the layer's own chrome (the pick catcher)
// takes the click. On the layer path the quick peek's Popup therefore opens unparented on the XamlRoot instead;
// its offsets keep their meaning because the selection canvas it came from sits at the root origin.
static void HostLiftPopup(IInspectable* popup, IInspectable* hostPopup)
{
    DevToolsHostSlot* slot = HostSlotFor(hostPopup);
    if (!popup || !slot || !slot->layerKey) return;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, popup);
        auto lifted = object.as<DevToolsXCP::Popup>();
        auto xamlRoot = slot->content.XamlRoot();
        HostDetach(lifted);
        // Outside the layer the Popup's content reaches the census; seed it so it is excluded like all our chrome.
        if (g_pickDiag && g_pickSeed) {
            for (const winrt::Windows::Foundation::IInspectable& part : { winrt::Windows::Foundation::IInspectable(lifted),
                     winrt::Windows::Foundation::IInspectable(lifted.Child()) }) {
                InstanceHandle handle = 0;
                if (part && SUCCEEDED(g_pickDiag->GetHandleFromIInspectable(
                        reinterpret_cast<IInspectable*>(winrt::get_abi(part)), &handle)) && handle) g_pickSeed(handle);
            }
        }
        lifted.XamlRoot(xamlRoot);
    } catch (const winrt::hresult_error& e) {
        DevToolsOverlayLog(L"overlay.host popup lift failed hr=0x%08X", e.code().value);
    }
}

static HRESULT HostPutOpen(IInspectable* popup, bool open)
{
    DevToolsHostSlot* slot = HostSlotFor(popup);
    if (!slot || !slot->layerKey) {
        if (slot) slot->open = open;
        return DevToolsPopupPutIsOpen(popup, open ? 1 : 0);
    }
    slot->open = open;
    try { HostApplyVisibility(*slot); }
    catch (const winrt::hresult_error& e) { return e.code(); }
    return S_OK;
}

static HRESULT HostGetOpen(IInspectable* popup, bool* open)
{
    DevToolsHostSlot* slot = HostSlotFor(popup);
    if (!slot || !slot->layerKey) return DevToolsPopupGetIsOpen(popup, open);
    *open = slot->open;
    return S_OK;
}

// On the layer path a host is attached to `panel` when its content is in the layer of the panel's XamlRoot.
// Returns false when the host is not layer-hosted, so the caller checks the panel's children instead.
static bool HostLayerAttached(IInspectable* panel, IInspectable* popupUi, bool* attached)
{
    DevToolsHostSlot* slot = HostSlotFor(popupUi);
    if (!slot || !slot->layerKey) return false;
    *attached = false;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, panel);
        auto fe = object.try_as<DevToolsX::FrameworkElement>();
        auto xamlRoot = fe && fe.IsLoaded() ? fe.XamlRoot() : nullptr;
        const auto key = xamlRoot ? reinterpret_cast<unsigned long long>(
            HostIdentity(reinterpret_cast<IInspectable*>(winrt::get_abi(xamlRoot))).get()) : 0ull;
        *attached = key && key == slot->layerKey && slot->open;
    } catch (const winrt::hresult_error&) {
    }
    return true;
}

// The chrome keeps the app content's theme, which the layer does not inherit.
static void HostSetLayerTheme(int theme)
{
    for (auto& layer : g_layerRoots) {
        try { layer.root.RequestedTheme(static_cast<DevToolsX::ElementTheme>(theme)); }
        catch (const winrt::hresult_error&) {}
    }
}

// A closed window's XamlRoot is gone: drop its layer root and UIA bridge.
static bool HostXamlRootClosed(const winrt::com_ptr<IUnknown>& identity)
{
    try {
        auto root = identity.as<winrt::Windows::Foundation::IInspectable>().as<winrt::Microsoft::UI::Xaml::XamlRoot>();
        return !root || !root.Content();
    } catch (const winrt::hresult_error&) {
        return true;
    }
}

static void HostPruneLayers(const std::vector<DevToolsSurface>& live)
{
    if (!g_overlayUiThreadId || GetCurrentThreadId() != g_overlayUiThreadId) return;
    for (size_t i = 0; i < g_layerRoots.size();) {
        const auto key = g_layerRoots[i].key;
        const bool alive = std::any_of(live.begin(), live.end(), [key](const DevToolsSurface& s) { return s.xamlRootKey == key; });
        if (alive || !HostXamlRootClosed(g_layerRoots[i].xamlRoot)) { ++i; continue; }
        try { g_layerRoots[i].bridge.IsOpen(false); g_layerRoots[i].bridge.Child(nullptr); }
        catch (const winrt::hresult_error&) {}
        for (auto& slot : g_hostSlots) if (slot.layerKey == key) slot.layerKey = 0;
        DevToolsOverlayLog(L"overlay.host uiLayer root=%llu closed", key);
        g_layerRoots.erase(g_layerRoots.begin() + static_cast<std::ptrdiff_t>(i));
    }
    g_layerUnavailable.erase(std::remove_if(g_layerUnavailable.begin(), g_layerUnavailable.end(), [&](unsigned long long key) {
        return std::none_of(live.begin(), live.end(), [key](const DevToolsSurface& s) { return s.xamlRootKey == key; });
    }), g_layerUnavailable.end());
}

const wchar_t* DevToolsOverlay_HostPath()
{
    switch (g_hostPath) {
        case DevToolsHostPath::UiLayer: return L"uiLayer";
        case DevToolsHostPath::Popup: return L"popup";
        default: return L"none";
    }
}

void DevToolsOverlay_ForcePopupHost(bool force) { g_forcePopupHost = force; }
bool DevToolsOverlay_IsPopupHostForced() { return g_forcePopupHost; }
