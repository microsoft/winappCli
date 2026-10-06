// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// In-process inspector: it uses the target WinUI runtime and UI thread.
// Generated wrappers keep HRESULT/ref-lifetime explicit; local COM delegates publish their IIDs.
#include "DevToolsWindow.h"
#include "DevToolsInspectorAcceptance.h"
#include "DevToolsSourcePath.h"
#include "DevToolsThreadGuard.h"
#include "DevToolsProjected.h"
#include <windows.h>
#include <dwmapi.h>
#include <shellapi.h>
#include <shlwapi.h>
#pragma comment(lib, "shlwapi.lib")
#include <commctrl.h>
#pragma comment(lib, "comctl32.lib")
#include <objidl.h>
#include <roapi.h>
#include <winstring.h>
#include <cstdio>
#include <map>
#include <cwctype>
#include <climits>
#include "DevToolsWindowXaml.g.h"
#include "DevToolsAppXaml.h"
#include "DevToolsTreeLayout.h"
#include "DevToolsTreeWatch.h"
#include "DevToolsRead.h"
#include "DevToolsPickRoute.h"
#include "DevToolsPerf.h"
#include "DevToolsResourceInline.h"
#include <algorithm>
#include "DevToolsSink.h"
#include "DevToolsProtocol.h"
#include "DevToolsBindingAnswer.h"
#include "DevToolsBindingRelay.h"
#include "DevToolsPathWalk.h"
#include "DevToolsPathSyntax.h"
#include "DevToolsBindingRow.h"
#include "DevToolsCommentText.h"
#include "DevToolsEditText.h"
#include "DevToolsSettings.h"
#include "DevToolsText.h"
#include "DevToolsShellOpen.h"
#include "DevToolsStyleEdit.h"
#include "DevToolsStyleSource.h"
#include "DevToolsLiveRows.h"
#include <winrt/Microsoft.UI.Xaml.Shapes.h>
static const size_t kMaxRenderNodes = 1000;

// Collapse framework chrome subtrees so template internals do not bury app-authored content.
static bool IsChromeCollapseType(const std::wstring& shortType)
{
    return DevToolsTreeLayout::IsChromeCollapseType(shortType);
}

// DevToolsTreeLayout supplies view-relative expansion and indentation. Hidden framework ancestry
// must not consume the app-content expansion budget; a committed pick expands its ancestors.
static constexpr int kDefaultExpandDepth = DevToolsTreeLayout::kDefaultExpandDepth;
static constexpr int kMaxAutoExpandDepth = DevToolsTreeLayout::kMaxAutoExpandDepth;

static int IndentPxForLevel(int level)
{
    return DevToolsTreeLayout::IndentPxForLevel(level);
}

// Hand-rolled COM event sinks must answer delegate IIDs in QI; interface IIDs come from DevToolsIid<T>().
static const GUID IID_RoutedEventHandler_Del =
    { 0xDAE23D85, 0x69CA, 0x5BDF, { 0x80, 0x5B, 0x61, 0x61, 0xA3, 0xA2, 0x15, 0xCC } };
// Microsoft.UI.Xaml.Input.PointerEventHandler (delegate) = A48A71E1-8BB4-5597-9E31-903A3F6A04FB.
// Harvested from the WindowsAppSDK Microsoft.UI.Xaml winmd IDL. Backs UIElement.PointerEntered so hovering
// a tree row highlights the matching app element. Same ABI shape as RoutedEventHandler (Invoke(sender,args)
// returning HRESULT), so ONE sink object can serve as both delegate types by answering both IIDs in QI.
static const GUID IID_PointerEventHandler_Del =
    { 0xA48A71E1, 0x8BB4, 0x5597, { 0x9E, 0x31, 0x90, 0x3A, 0x3F, 0x6A, 0x04, 0xFB } };
static const GUID IID_DragDeltaEventHandler_Del = DevToolsIid<DevToolsXCP::DragDeltaEventHandler>();
// Microsoft.UI.Xaml.Controls.SelectionChangedEventHandler (delegate). Same Invoke(sender,args)->HRESULT ABI
// shape as RoutedEventHandler; an EnumSelectSink answers this IID so a ComboBox selection commits the edit.
static const GUID IID_SelectionChanged_Del =
    { 0xA232390D, 0x0E34, 0x595E, { 0x89, 0x31, 0xFA, 0x92, 0x8A, 0x99, 0x09, 0xF4 } };
// Microsoft.UI.Xaml.Controls.TextChangedEventHandler (delegate). Same Invoke(sender,args)->HRESULT ABI shape
// as RoutedEventHandler; a FilterSink answers this IID so each keystroke re-applies the tree filter.
static const GUID IID_TextChanged_Del =
    { 0x5D8DDCFF, 0x45D8, 0x5E7C, { 0x9B, 0x8B, 0xC4, 0x1D, 0x28, 0x93, 0xC6, 0xA1 } };
static const GUID IID_KeyEventHandler_Del = DevToolsIid<DevToolsXI::KeyEventHandler>();




static HSTRING MakeStr(const std::wstring& s)
{
    HSTRING h = nullptr;
    WindowsCreateString(s.c_str(), (UINT32)s.size(), &h);
    return h;
}

static HRESULT BoxBool(bool value, IInspectable** out)
{
    *out = nullptr;
    HSTRING clsId = MakeStr(L"Windows.Foundation.PropertyValue");
    IInspectable* statics = nullptr;
    HRESULT hr = RoGetActivationFactory(clsId, DevToolsIid<winrt::Windows::Foundation::IPropertyValueStatics>(),
                                        reinterpret_cast<void**>(&statics));
    WindowsDeleteString(clsId);
    if (FAILED(hr) || !statics) return FAILED(hr) ? hr : E_NOINTERFACE;
    hr = DevToolsPropertyValueCreateBoolean(statics, value, out);
    statics->Release();
    return hr;
}

static HRESULT BoxString(const std::wstring& value, IInspectable** out)
{
    *out = nullptr;
    HSTRING clsId = MakeStr(L"Windows.Foundation.PropertyValue");
    IInspectable* statics = nullptr;
    HRESULT hr = RoGetActivationFactory(clsId, DevToolsIid<winrt::Windows::Foundation::IPropertyValueStatics>(),
                                        reinterpret_cast<void**>(&statics));
    WindowsDeleteString(clsId);
    if (FAILED(hr) || !statics) return FAILED(hr) ? hr : E_NOINTERFACE;
    HSTRING v = MakeStr(value);
    hr = DevToolsPropertyValueCreateString(statics, v, out);
    WindowsDeleteString(v);
    statics->Release();
    return hr;
}

// Cache the XamlReader statics for the process lifetime.
static IInspectable* g_xamlReaderStatics = nullptr;

static HRESULT XamlReaderStatics(IInspectable** out)
{
    *out = nullptr;
    if (!g_xamlReaderStatics) {
        HSTRING clsId = MakeStr(L"Microsoft.UI.Xaml.Markup.XamlReader");
        IInspectable* reader = nullptr;
        HRESULT hr = RoGetActivationFactory(clsId, DevToolsIid<DevToolsXMk::IXamlReaderStatics>(),
                                            reinterpret_cast<void**>(&reader));
        WindowsDeleteString(clsId);
        if (FAILED(hr) || !reader) return FAILED(hr) ? hr : E_NOINTERFACE;
        g_xamlReaderStatics = reader;
    }
    *out = g_xamlReaderStatics;
    return S_OK;
}

static HRESULT LoadMarkupRaw(const std::wstring& xaml, IInspectable** out, std::wstring* errorMessage = nullptr)
{
    if (errorMessage) errorMessage->clear();
    *out = nullptr;
    IInspectable* reader = nullptr;
    HRESULT hr = XamlReaderStatics(&reader);
    if (FAILED(hr) || !reader) return FAILED(hr) ? hr : E_NOINTERFACE;
    HSTRING markup = MakeStr(xaml);
    hr = DevToolsXamlReaderLoad(reader, markup, out);
    if (FAILED(hr) && errorMessage) *errorMessage = DevToolsXamlErrorMessage(hr);
    WindowsDeleteString(markup);
    return hr;
}

static HRESULT LoadMarkupWin(const std::wstring& xaml, IInspectable** out, std::wstring* errorMessage = nullptr)
{
    WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinXamlParse);
    return LoadMarkupRaw(xaml, out, errorMessage);
}

static size_t CountOf(const std::wstring& hay, const wchar_t* needle) { return DevToolsResInline::CountOf(hay, needle); }

// Resource literals are theme-keyed: resolving a theme brush to a value loses the live resource reference.
static std::map<std::wstring, std::wstring> g_resLiteral;
static std::map<std::wstring, std::wstring> g_resStyleAttr;
static int g_resLiteralTheme = -1;
static int InspectorActualTheme();

static std::wstring HexColor(unsigned char a, unsigned char r, unsigned char g, unsigned char b)
{
    wchar_t buf[16]{};
    swprintf_s(buf, L"#%02X%02X%02X%02X", a, r, g, b);
    return buf;
}

// If a brush cannot resolve, leave the markup reference in place rather than emit unparsable XAML.
static std::wstring ResolveBrushLiteral(const std::wstring& ext, const std::wstring& key)
{
    auto it = g_resLiteral.find(key);
    if (it != g_resLiteral.end()) return it->second;
    std::wstring lit;
    IInspectable* probe = nullptr;
    const std::wstring markup =
        L"<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Foreground=\"{"
        + ext + L" " + key + L"}\"/>";
    if (SUCCEEDED(LoadMarkupRaw(markup, &probe)) && probe) {
        void* tb = nullptr;
        if (SUCCEEDED(probe->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) && tb) {
            IInspectable* brush = nullptr;
            if (SUCCEEDED(DevToolsGetTextForeground(tb, &brush)) && brush) {
                void* scb = nullptr;
                if (SUCCEEDED(brush->QueryInterface(DevToolsIid<DevToolsXM::ISolidColorBrush>(), &scb)) && scb) {
                    unsigned char a = 0, r = 0, g = 0, b = 0;
                    if (SUCCEEDED(DevToolsGetSolidColorBrushColor(scb, &a, &r, &g, &b))) lit = HexColor(a, r, g, b);
                    reinterpret_cast<IUnknown*>(scb)->Release();
                }
                brush->Release();
            }
            reinterpret_cast<IUnknown*>(tb)->Release();
        }
        probe->Release();
    }
    if (lit.empty()) DevToolsOverlayLog(L"route1.res '%s' did not resolve to a solid colour; leaving the reference", key.c_str());
    g_resLiteral[key] = lit;
    return lit;
}

// Resolve a `Style="{<ext> K}"` attribute to the literal attributes it sets on a TextBlock. Only the two
// typography keys the pane uses are text styles, and their whole effect is size and weight -- read off a
// probe that the style has actually been applied to, rather than hard-coded from the theme dictionary.
static std::wstring ResolveTextStyleAttrs(const std::wstring& ext, const std::wstring& key)
{
    auto it = g_resStyleAttr.find(key);
    if (it != g_resStyleAttr.end()) return it->second;
    std::wstring attrs;
    IInspectable* probe = nullptr;
    const std::wstring markup =
        L"<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Style=\"{"
        + ext + L" " + key + L"}\"/>";
    if (SUCCEEDED(LoadMarkupRaw(markup, &probe)) && probe) {
        void* tb = nullptr;
        if (SUCCEEDED(probe->QueryInterface(DevToolsIid<DevToolsXC::ITextBlock>(), &tb)) && tb) {
            double size = 0.0; unsigned short weight = 0;
            const bool haveSize = SUCCEEDED(DevToolsGetFontSize(tb, &size)) && size > 0.0;
            const wchar_t* wname = SUCCEEDED(DevToolsGetTextFontWeight(tb, &weight)) ? DevToolsResInline::FontWeightName(weight) : nullptr;
            if (haveSize) {
                wchar_t buf[64]{};
                swprintf_s(buf, L"FontSize=\"%g\"", size);
                attrs = buf;
                if (wname) attrs += std::wstring(L" FontWeight=\"") + wname + L"\"";
                else DevToolsOverlayLog(L"route1.res style '%s' has off-ladder FontWeight %u; weight not inlined", key.c_str(), (unsigned)weight);
            }
            reinterpret_cast<IUnknown*>(tb)->Release();
        }
        probe->Release();
    }
    if (attrs.empty()) DevToolsOverlayLog(L"route1.res style '%s' did not resolve; leaving the reference", key.c_str());
    g_resStyleAttr[key] = attrs;
    return attrs;
}

// The COM half's entry point: refresh the theme-keyed cache, then hand the substitution to the pure
// rule in DevToolsResourceInline.h with this file's two resolvers.
static std::wstring InlineResourceLiterals(const std::wstring& markup, size_t* asked, size_t* leftBehind)
{
    const int theme = InspectorActualTheme();
    if (theme != g_resLiteralTheme) {
        g_resLiteral.clear(); g_resStyleAttr.clear(); g_resLiteralTheme = theme;
        DevToolsOverlayLog(L"route1.res theme is now %d; re-resolving inlined resource literals", theme);
    }
    return DevToolsResInline::Apply(markup, ResolveBrushLiteral, ResolveTextStyleAttrs, asked, leftBehind);
}

static HRESULT ApplyMicaBackdrop(IInspectable* window)
{
    if (!window) return E_INVALIDARG;
    void* window2 = nullptr;
    HRESULT hr = window->QueryInterface(DevToolsIid<DevToolsX::IWindow2>(), &window2);
    if (FAILED(hr) || !window2) return FAILED(hr) ? hr : E_NOINTERFACE;

    IInspectable* mica = nullptr;
    hr = LoadMarkupWin(
        L"<MicaBackdrop xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"/>",
        &mica);
    if (SUCCEEDED(hr) && mica) {
        void* backdrop = nullptr;
        hr = mica->QueryInterface(DevToolsIid<DevToolsXM::ISystemBackdrop>(), &backdrop);
        if (SUCCEEDED(hr) && backdrop) {
            hr = DevToolsWinPutSystemBackdrop(window2, backdrop);
            reinterpret_cast<IUnknown*>(backdrop)->Release();
        }
        mica->Release();
    }
    reinterpret_cast<IUnknown*>(window2)->Release();
    return hr;
}

static bool IsHexColor(const std::wstring& s)
{
    return DevToolsRead_IsHexColor(s);
}

static bool IsBrushRow(const DevToolsCardRow& r)
{
    auto endsWithBrush = [](const std::wstring& t) {
        return t.size() >= 5 && t.compare(t.size() - 5, 5, L"Brush") == 0;
    };
    return endsWithBrush(r.valueType) || endsWithBrush(r.type);
}


static std::wstring ShortType(const std::wstring& t)
{
    size_t dot = t.find_last_of(L'.');
    return dot == std::wstring::npos ? t : t.substr(dot + 1);
}

#include "DevToolsWindow.State.inc"
#include "DevToolsWindow.TreeView.inc"
#include "DevToolsWindow.PaneSinks.inc"
#include "DevToolsWindow.SourceAndResources.inc"
#include "DevToolsWindow.TreeRows.inc"
#include "DevToolsWindow.TreeRefresh.inc"
#include "DevToolsWindow.PropsLayout.inc"
#include "DevToolsWindow.StyleEdit.inc"
#include "DevToolsWindow.PaneRows.inc"
#include "DevToolsWindow.KindModel.inc"
#include "DevToolsWindow.Editors.inc"
#include "DevToolsWindow.Expansion.inc"
#include "DevToolsWindow.PropsPane.inc"
#include "DevToolsWindow.LiveProps.inc"
#include "DevToolsWindow.Placement.inc"
#include "DevToolsWindow.Comments.inc"
#include "DevToolsWindow.Lifecycle.inc"
