// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

static constexpr wchar_t kSourceRootMissing[] =
    L"No source folder is set. Attach again with --source-root <project folder>.";

static bool IsCSharpPath(const std::wstring& path)
{
    return path.size() > 3 && CompareStringOrdinal(path.c_str() + path.size() - 3, 3, L".cs", 3, TRUE) == CSTR_EQUAL;
}

static std::wstring ResolveSourcePath(const std::wstring& uri, bool* sourceRootMissing = nullptr)
{
    // A Reactor call site is already a file path on this machine (mapped by the tap), not an ms-appx URI.
    if (IsCSharpPath(uri)) {
        if (sourceRootMissing) *sourceRootMissing = false;
        const DWORD attributes = GetFileAttributesW(uri.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY) ? uri : L"";
    }
    DWORD required = GetEnvironmentVariableW(L"WINAPP_DEVTOOLS_SOURCE_ROOT", nullptr, 0);
    if (sourceRootMissing) *sourceRootMissing = required == 0;
    if (required == 0) return L"";
    std::vector<wchar_t> root(required);
    DWORD written = GetEnvironmentVariableW(L"WINAPP_DEVTOOLS_SOURCE_ROOT", root.data(), required);
    if (written == 0 || written >= required) return L"";
    std::wstring resolved;
    return DevToolsSourcePath_ResolveExisting(std::wstring(root.data(), written), uri, &resolved)
        ? resolved
        : L"";
}

// AssocQueryString can succeed with an empty value; check the value, not just the HRESULT.
static std::wstring ShellAssocFor(const std::wstring& ext)
{
    DWORD cch = 0;
    if (FAILED(AssocQueryStringW(ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_FRIENDLYAPPNAME,
                                 ext.c_str(), L"open", nullptr, &cch)) || cch == 0) {
        return L"";
    }
    std::vector<wchar_t> buf(cch);
    if (FAILED(AssocQueryStringW(ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_FRIENDLYAPPNAME,
                                 ext.c_str(), L"open", buf.data(), &cch))) {
        return L"";
    }
    return std::wstring(buf.data(), wcsnlen(buf.data(), buf.size()));
}

// Recovery actions do not need a file association.
static bool RevealInExplorer(const std::wstring& path)
{
    const std::wstring args = L"/select,\"" + path + L"\"";
    HINSTANCE rc = ShellExecuteW(nullptr, L"open", L"explorer.exe", args.c_str(), nullptr, SW_SHOWNORMAL);
    return reinterpret_cast<INT_PTR>(rc) > 32;
}

// SetClipboardData takes ownership only on success; on failure the HGLOBAL remains ours to free.
static bool CopyTextToClipboard(const std::wstring& text)
{
    if (!OpenClipboard(nullptr)) return false;
    bool ok = false;
    if (EmptyClipboard()) {
        const size_t bytes = (text.size() + 1) * sizeof(wchar_t);
        if (HGLOBAL mem = GlobalAlloc(GMEM_MOVEABLE, bytes)) {
            if (void* dst = GlobalLock(mem)) {
                memcpy(dst, text.c_str(), bytes);
                GlobalUnlock(mem);
                if (SetClipboardData(CF_UNICODETEXT, mem)) ok = true;
                else GlobalFree(mem);
            } else {
                GlobalFree(mem);
            }
        }
    }
    CloseClipboard();
    return ok;
}

static std::wstring HandOffSource(const std::wstring& uri, unsigned int line, const std::wstring& offScreenPrefix,
                                  const std::function<void(const std::wstring&)>& feedback = {})
{
    bool sourceRootMissing = false;
    const std::wstring path = ResolveSourcePath(uri, &sourceRootMissing);
    const std::wstring display = SourceFileName(uri);
    const std::wstring extension = IsCSharpPath(uri) ? L".cs" : L".xaml";
    const DevToolsShellOpen::Result r = DevToolsShellOpen::Decide(
        path, extension,
        [](const std::wstring& ext) { return ShellAssocFor(ext); },
        [](const std::wstring& p)   { return GetFileAttributesW(p.c_str()) != INVALID_FILE_ATTRIBUTES; },
        [](const std::wstring& p)   {
            HINSTANCE rc = ShellExecuteW(nullptr, L"open", p.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
            return static_cast<int>(reinterpret_cast<INT_PTR>(rc));
        });
    const std::wstring message = sourceRootMissing ? std::wstring(kSourceRootMissing)
        : DevToolsShellOpen::Describe(r, display, line, path, offScreenPrefix, extension);
    if (feedback) feedback(message);
    else SetStatusText(message);
    DevToolsOverlayLog(L"route1.opensource outcome=%d handler='%s' path='%s'",
                  (int)r.outcome, r.handler.c_str(), path.c_str());
    return (r.outcome == DevToolsShellOpen::Outcome::NoPath) ? std::wstring() : path;
}

enum class SourceAction { Open, Reveal, Copy };

// XAML's visual ownership does not keep this WinRT wrapper's weak references alive.
// Retain it for the pane's lifetime; the sinks still hold only weak references.
static DevToolsXC::TextBlock g_sourceStatus{ nullptr };
// The same for the comment box's elements: its handlers hold them weakly, so the pane holds them here.
static std::vector<winrt::Windows::Foundation::IInspectable> g_commentBoxParts;

// Keep feedback with the source header that owns these actions, not the current selection's status line.
struct SourceActionSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        auto status = feedback.get();
        if (!status) return S_OK;
        auto report = [&](const std::wstring& text) {
            try {
                status.Text(text);
                status.Visibility(DevToolsX::Visibility::Visible);
                using namespace winrt::Microsoft::UI::Xaml::Automation::Peers;
                if (auto peer = FrameworkElementAutomationPeer::FromElement(status))
                    peer.RaiseAutomationEvent(AutomationEvents::LiveRegionChanged);
            } catch (const winrt::hresult_error& e) {
                DevToolsOverlayLog(L"route1.source feedback failed hr=0x%08X", e.code().value);
            }
        };
        if (action == SourceAction::Open) {
            HandOffSource(uri, line, L"", report);
        } else {
            bool sourceRootMissing = false;
            const std::wstring path = ResolveSourcePath(uri, &sourceRootMissing);
            if (path.empty()) {
                report(sourceRootMissing ? kSourceRootMissing
                    : L"Source file unavailable. Check that it still exists under the project's source folder.");
            } else if (action == SourceAction::Reveal) {
                report(RevealInExplorer(path) ? L"Showed " + path + L" in File Explorer."
                                               : L"Couldn't open File Explorer for " + path);
            } else {
                report(CopyTextToClipboard(path) ? L"Copied " + path : L"Couldn't copy " + path);
            }
        }
        return S_OK;
    }
    void Init(SourceAction a, const DevToolsXC::TextBlock& status)
    {
        action = a; uri = g_selSourceFile; line = g_selSourceLine; feedback = status;
        InitSink(IID_RoutedEventHandler_Del, g_route1Gen);
    }
    SourceAction action = SourceAction::Open;
    std::wstring uri;
    unsigned int line = 0;
    winrt::weak_ref<DevToolsXC::TextBlock> feedback;
};

static std::wstring CurrentEditSeed(const std::wstring& path, const std::wstring& fallback);

// Commit an enum ComboBox editor on selection change: read the ComboBox's SelectedIndex and write the matching
// member name to the live app element via the in-process write bridge. Self-contained (captures the node handle
// + property + write type + the ordered member-value list + a NON-owning ISelector pointer). Non-owning is safe
// for the same reason LostFocusSink's box is: SelectionChanged only fires while the ComboBox is live in the
// tree, and a torn-down window's sinks go inert (gen guard). Leaked for the process lifetime. UI thread only.
struct EnumSelectSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!g_ctx.writeFn || !selector) return S_OK;
        int idx = -1;
        if (FAILED(DevToolsGetSelectedIndex(selector, &idx))) return S_OK;
        if (idx < 0 || idx >= (int)members.size()) return S_OK;
        const std::wstring& val = members[(size_t)idx];
        std::wstring before = textEditor && selWire == g_selWire ? CurrentEditSeed(prop, original) : original;
        bool haveCurrentValue = true;
        if (textEditor && selWire == g_selWire && val == before) {
            // A previous write may have landed without refreshing this cached row.
            haveCurrentValue = RefreshCommittedValue(prop);
            if (haveCurrentValue) before = CurrentEditSeed(prop, original);
        }
        if (haveCurrentValue && val == before) {
            if (textEditor) textEditor->SyncCommittedValue(before);
            return S_OK;
        }
        bool refreshed = false;
        if (ApplyCommitOutcome(CommitWrite(selWire, prop, type, val, /*confirmBindingReplace*/ false),
                               selWire, prop, type, before, val, nullptr, &refreshed)) {
            original = val;
            if (textEditor && refreshed && gen == g_route1Gen && selWire == g_selWire) {
                textEditor->SyncCommittedValue(CurrentEditSeed(prop, val));
            }
        }
        return S_OK;
    }
    void Init(InstanceHandle w, const std::wstring& p, const std::wstring& t, const std::wstring& orig,
              std::vector<std::wstring> mem, void* sel)
    {
        selWire = w; prop = p; type = t; original = orig; members = std::move(mem); selector = sel;
        InitSink(IID_SelectionChanged_Del, g_route1Gen);
    }
    InstanceHandle            selWire = 0;
    std::wstring              prop, type, original;
    std::vector<std::wstring> members;
    void*                     selector = nullptr;
    LostFocusSink*            textEditor = nullptr; // Same expansion lifetime as the preset selector.
};

// Theme brush order matches the ComboBox item order; SelectedIndex maps directly to a resource name.
static const std::vector<std::wstring>& BrushResourceNames()
{
    static const std::vector<std::wstring> names = {
        L"TextFillColorPrimaryBrush",
        L"TextFillColorSecondaryBrush",
        L"TextFillColorTertiaryBrush",
        L"TextFillColorDisabledBrush",
        L"AccentTextFillColorPrimaryBrush",
        L"TextOnAccentFillColorPrimaryBrush",
        L"AccentFillColorDefaultBrush",
        L"AccentFillColorSecondaryBrush",
        L"ControlFillColorDefaultBrush",
        L"CardBackgroundFillColorDefaultBrush",
        L"LayerFillColorDefaultBrush",
        L"SolidBackgroundFillColorBaseBrush",
        L"SystemFillColorSuccessBrush",
        L"SystemFillColorCautionBrush",
        L"SystemFillColorCriticalBrush",
    };
    return names;
}

// WinUI type-ramp font sizes offered as one-click presets on a FontSize row (in addition to the free-text box,
// so arbitrary sizes are never lost). These are literal Double values applied via the normal write path -- NOT
// named resources (WinUI 3 ships essentially one named Double font-size resource, ControlContentThemeFontSize,
// so a named-resource picker here would be near-empty; the useful affordance is a size ramp). Index 0 is a
// clearly-small size so a gate/driver pressing Down once produces an unambiguous change.
static const std::vector<std::wstring>& FontSizePresets()
{
    static const std::vector<std::wstring> sizes = {
        L"8", L"10", L"11", L"12", L"13", L"14", L"16", L"18", L"20", L"24", L"28", L"32", L"40", L"48", L"68",
    };
    return sizes;
}

static const std::vector<std::pair<std::wstring, std::wstring>>& ColorPresets()
{
    static const std::vector<std::pair<std::wstring, std::wstring>> colors = {
        { L"Black",       L"#FF000000" },
        { L"White",       L"#FFFFFFFF" },
        { L"Gray",        L"#FF808080" },
        { L"Red",         L"#FFE81123" },
        { L"Orange",      L"#FFF7630C" },
        { L"Yellow",      L"#FFFFB900" },
        { L"Green",       L"#FF107C10" },
        { L"Teal",        L"#FF038387" },
        { L"Blue",        L"#FF0078D4" },
        { L"Purple",      L"#FF8764B8" },
        { L"Magenta",     L"#FFC239B3" },
        { L"Transparent", L"#00FFFFFF" },
    };
    return colors;
}

// to a curated brush name, and assign it live via the in-process resolve bridge (IVisualTreeService3::ResolveResource).
// This is an ASSIGN, not a rebind: it switches the property TO a named resource; the prior value's resource identity
// is unrecoverable, so the picker never claims to show "where the value came from". No initial seed / placeholder-only
// (SelectedIndex starts -1 => no spurious apply on realize). Same non-owning-selector + gen-guard lifetime contract as
// EnumSelectSink: the ComboBox is owned by the pane, so the selector stays live until the next rebuild.
struct ResourceSelectSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!g_ctx.resolveFn || !selector) return S_OK;
        int idx = -1;
        if (FAILED(DevToolsGetSelectedIndex(selector, &idx))) return S_OK;
        const std::vector<std::wstring>& names = BrushResourceNames();
        if (idx < 0 || idx >= (int)names.size()) return S_OK;
        const std::wstring& name = names[(size_t)idx];
        if (g_ctx.resolveFn(selWire, prop, name, /*asTheme*/ true)) SetStatusText(L"Set " + prop + L" -> " + name);
        else SetStatusText(L"Couldn't resolve " + name);
        return S_OK;
    }
    void Init(InstanceHandle w, const std::wstring& p, void* sel)
    {
        selWire = w; prop = p; selector = sel;
        InitSink(IID_SelectionChanged_Del, g_route1Gen);
    }
    InstanceHandle selWire = 0;
    std::wstring   prop;
    void*          selector = nullptr;
};

static std::wstring ElideBreadcrumb(const std::wstring& full, size_t& outSegments)
{
    return DevToolsTreeLayout::ElideBreadcrumb(full, outSegments);
}

// Reconstruct the ancestor chain of the node at flat pre-order index `i` from the depth column: walking
// backwards, the nearest earlier node at each strictly-decreasing depth is that level's ancestor. Returns a
// muted "Root \u203A \u2026 \u203A Parent" string (empty for a root). Pure data (0 ABI); feeds the props-pane
// breadcrumb so a selected deep node shows where it sits in the tree.
static std::wstring BuildBreadcrumb(const std::vector<DevToolsWindowNode>& nodes, size_t i)
{
    std::vector<std::wstring> chain;
    int want = nodes[i].depth - 1;
    for (size_t j = i; j-- > 0 && want >= 0; ) {
        if (nodes[j].depth == want) {
            std::wstring seg = ShortType(nodes[j].type);
            if (!nodes[j].name.empty()) { seg += L" #"; seg += nodes[j].name; }
            chain.push_back(seg);
            --want;
        }
    }
    std::wstring bc;
    for (size_t k = chain.size(); k-- > 0; ) {
        if (!bc.empty()) bc += L" \u203A ";
        bc += chain[k];
    }
    return bc;
}

static std::wstring PidText()
{
    wchar_t buf[32];
    swprintf_s(buf, L"PID %lu", GetCurrentProcessId());
    return buf;
}

static std::wstring TitleWithPid()
{
    return L"WinApp DevTools \u00B7 " + PidText();
}

extern "C" IMAGE_DOS_HEADER __ImageBase;

// Without an icon of its own, the window shows the generic application glyph in its caption and Alt+Tab.
static void SetInspectorIcon(HWND hwnd)
{
    const UINT dpi = GetDpiForWindow(hwnd);
    const HINSTANCE module = reinterpret_cast<HINSTANCE>(&__ImageBase);
    int set = 0;
    for (const auto& [which, metric] : { std::pair{ ICON_SMALL, SM_CXSMICON }, std::pair{ ICON_BIG, SM_CXICON } }) {
        const int size = GetSystemMetricsForDpi(metric, dpi);
        if (HANDLE icon = LoadImageW(module, L"WINAPP_DEVTOOLS", IMAGE_ICON, size, size, LR_SHARED)) {
            SendMessageW(hwnd, WM_SETICON, which, reinterpret_cast<LPARAM>(icon));
            ++set;
        }
    }
    DevToolsOverlayLog(L"route1.icon set=%d of 2", set);
}

static HRESULT ConfigureInspectorCaptionTheme(IInspectable* window, IInspectable* content)
{
    winrt::com_ptr<IInspectable> appWindow, caption;
    winrt::com_ptr<IUnknown> rootFe, window2, appWindowAbi, captionTheme;
    auto prepare = [&]() -> HRESULT {
        HRESULT hr = content->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), rootFe.put_void());
        if (FAILED(hr)) return hr;
        hr = window->QueryInterface(DevToolsIid<DevToolsX::IWindow2>(), window2.put_void());
        if (FAILED(hr)) return hr;
        hr = DevToolsWinGetAppWindow(window2.get(), appWindow.put());
        if (FAILED(hr) || !appWindow) return FAILED(hr) ? hr : E_NOINTERFACE;
        hr = appWindow->QueryInterface(DevToolsIid<DevToolsW::IAppWindow>(), appWindowAbi.put_void());
        if (FAILED(hr)) return hr;
        hr = DevToolsAppWindowGetTitleBar(appWindowAbi.get(), caption.put());
        if (FAILED(hr) || !caption) return FAILED(hr) ? hr : E_NOINTERFACE;
        return caption->QueryInterface(DevToolsIid<DevToolsW::IAppWindowTitleBar3>(), captionTheme.put_void());
    };
    HRESULT hr = prepare();
    if (FAILED(hr)) {
        DevToolsOverlayLog(L"route1.caption theme API unavailable hr=0x%08X; keeping system theme", hr);
        return S_FALSE;
    }
    g_captionTheme = captionTheme.detach();
    g_captionThemeRoot = rootFe.detach();
    hr = SyncInspectorCaptionTheme();
    if (SUCCEEDED(hr)) {
        auto* sink = new InspectorCaptionThemeSink();
        sink->Init();
        hr = DevToolsAddActualThemeChanged(g_captionThemeRoot, sink, &g_captionThemeToken);
        sink->Release();
        g_captionThemeSubscribed = SUCCEEDED(hr);
    }
    if (FAILED(hr)) {
        DevToolsOverlayLog(L"route1.caption theme setup failed hr=0x%08X", hr);
        ReleaseInspectorCaptionTheme();
        return S_FALSE;
    }
    DevToolsOverlayLog(L"route1.caption system caption follows inspector theme");
    return S_OK;
}

static void ReplaceToken(std::wstring& s, const std::wstring& token, const std::wstring& value)
{
    size_t pos = s.find(token);
    if (pos != std::wstring::npos) s.replace(pos, token.size(), value);
}

// The TitleBar control draws its own icon; WM_SETICON only reaches the taskbar and Alt+Tab. Hand it the frame of
// the embedded winapp.ico that fits a 16-pixel icon at this DPI. BitmapImage decodes those frames because they are
// stored as PNG; a classic bitmap frame is rejected so the control shows no icon rather than an empty gap.
static HRESULT LoadInspectorTitleBarIcon(HWND hwnd, IInspectable** iconSource)
{
    *iconSource = nullptr;
    const HMODULE module = reinterpret_cast<HMODULE>(&__ImageBase);
    auto resourceBytes = [module](LPCWSTR name, LPCWSTR type, DWORD* size) -> const BYTE* {
        *size = 0;
        HRSRC res = FindResourceW(module, name, type);
        HGLOBAL data = res ? LoadResource(module, res) : nullptr;
        if (!data) return nullptr;
        *size = SizeofResource(module, res);
        return static_cast<const BYTE*>(LockResource(data));
    };
    DWORD size = 0;
    const BYTE* directory = resourceBytes(L"WINAPP_DEVTOOLS", RT_GROUP_ICON, &size);
    if (!directory) return HRESULT_FROM_WIN32(ERROR_RESOURCE_NAME_NOT_FOUND);
    const UINT dpi = hwnd ? GetDpiForWindow(hwnd) : 0;
    const int px = MulDiv(16, dpi ? static_cast<int>(dpi) : USER_DEFAULT_SCREEN_DPI, USER_DEFAULT_SCREEN_DPI);
    const int id = LookupIconIdFromDirectoryEx(const_cast<PBYTE>(directory), TRUE, px, px, LR_DEFAULTCOLOR);
    const BYTE* png = id ? resourceBytes(MAKEINTRESOURCEW(id), RT_ICON, &size) : nullptr;
    static constexpr BYTE pngSignature[] = { 0x89, 0x50, 0x4E, 0x47 };
    if (!png || size < sizeof(pngSignature) || memcmp(png, pngSignature, sizeof(pngSignature)) != 0) {
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }

    winrt::com_ptr<IStream> stream;
    stream.attach(SHCreateMemStream(png, size));
    if (!stream) return E_OUTOFMEMORY;
    winrt::com_ptr<IUnknown> randomAccess, imageIconSource, bitmapSource;
    winrt::com_ptr<IInspectable> markup, image;
    HRESULT hr = CreateRandomAccessStreamOverStream(stream.get(), BSOS_DEFAULT,
        DevToolsIid<winrt::Windows::Storage::Streams::IRandomAccessStream>(), randomAccess.put_void());
    if (SUCCEEDED(hr)) {
        hr = LoadMarkupWin(L"<ImageIconSource xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">"
                           L"<ImageIconSource.ImageSource><BitmapImage/></ImageIconSource.ImageSource></ImageIconSource>",
                           markup.put());
        if (SUCCEEDED(hr) && !markup) hr = E_UNEXPECTED;
    }
    if (SUCCEEDED(hr)) hr = markup->QueryInterface(DevToolsIid<DevToolsXC::IImageIconSource>(), imageIconSource.put_void());
    if (SUCCEEDED(hr)) {
        hr = DevToolsImageIconSourceGetImageSource(imageIconSource.get(), image.put());
        if (SUCCEEDED(hr) && !image) hr = E_UNEXPECTED;
    }
    if (SUCCEEDED(hr)) hr = image->QueryInterface(DevToolsIid<DevToolsXMI::IBitmapSource>(), bitmapSource.put_void());
    if (SUCCEEDED(hr)) hr = DevToolsBitmapSourceSetSource(bitmapSource.get(), randomAccess.get());
    DevToolsOverlayLog(L"route1.titlebar icon px=%d frame=%d bytes=%lu hr=0x%08X", px, id, size, hr);
    if (FAILED(hr)) return hr;
    *iconSource = markup.detach();
    return S_OK;
}

// Content extends under the caption only once the control can take over dragging; if the window rejects the control,
// the system caption comes back, so the window never shows two caption bars or none.
static HRESULT AttachInspectorTitleBar(void* window, void* titleBarUi)
{
    HRESULT hr = DevToolsWinPutExtendsContentIntoTitleBar(window, true);
    if (FAILED(hr)) return hr;
    hr = DevToolsWinSetTitleBar(window, titleBarUi);
    if (FAILED(hr)) DevToolsWinPutExtendsContentIntoTitleBar(window, false);
    return hr;
}

// The taskbar, Alt+Tab and `winapp ui` read the TitleWithPid() window title. The TitleBar control copies its shorter
// Title there when its template is applied, which happens before it raises Loaded.
struct InspectorWindowTitleSink : DevToolsSinkBase
{
    HWND hwnd = nullptr;
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_route1Gen) return S_OK;
        if (!SetWindowTextW(hwnd, TitleWithPid().c_str())) {
            DevToolsOverlayLog(L"route1.titlebar title reset error=%lu", GetLastError());
        }
        return S_OK;
    }
    void Init(HWND window)
    {
        hwnd = window;
        InitSink(IID_RoutedEventHandler_Del, g_route1Gen);
    }
};

static HRESULT KeepInspectorWindowTitle(IInspectable* titleBar, HWND hwnd)
{
    if (!hwnd) return E_HANDLE;
    winrt::com_ptr<IUnknown> fe;
    HRESULT hr = titleBar->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), fe.put_void());
    if (FAILED(hr)) return hr;
    auto* sink = new InspectorWindowTitleSink();
    sink->Init(hwnd);
    __int64 token = 0;
    hr = DevToolsAddLoaded(fe.get(), sink, &token);
    sink->Release();
    return hr;
}

// Row 0 of the shell is the WinUI TitleBar control. When the window cannot hand its caption to the control, the
// control is collapsed and Windows keeps drawing the standard caption. A collapsed control is never templated, so it
// also leaves the window title alone.
static HRESULT ConfigureInspectorTitleBar(void* window, IInspectable* content, HWND hwnd)
{
    winrt::com_ptr<IUnknown> rootFe, titleBarAbi, titleBarUi;
    winrt::com_ptr<IInspectable> titleBar;
    HRESULT hr = content->QueryInterface(DevToolsIid<DevToolsX::IFrameworkElement>(), rootFe.put_void());
    if (SUCCEEDED(hr)) {
        HSTRING name = MakeStr(L"InspectorTitleBar");
        hr = DevToolsFindName(rootFe.get(), name, titleBar.put());
        WindowsDeleteString(name);
        if (SUCCEEDED(hr) && !titleBar) hr = E_NOINTERFACE;
    }
    if (SUCCEEDED(hr)) hr = titleBar->QueryInterface(DevToolsIid<DevToolsX::IUIElement>(), titleBarUi.put_void());
    if (FAILED(hr)) {
        DevToolsOverlayLog(L"route1.titlebar not found hr=0x%08X; keeping system caption", hr);
        return S_FALSE;
    }

    winrt::com_ptr<IInspectable> icon;
    HRESULT iconHr = titleBar->QueryInterface(DevToolsIid<DevToolsXC::ITitleBar>(), titleBarAbi.put_void());
    if (SUCCEEDED(iconHr)) iconHr = LoadInspectorTitleBarIcon(hwnd, icon.put());
    if (SUCCEEDED(iconHr)) iconHr = DevToolsTitleBarControlPutIconSource(titleBarAbi.get(), icon.get());
    if (FAILED(iconHr)) DevToolsOverlayLog(L"route1.titlebar no icon hr=0x%08X", iconHr);

    hr = KeepInspectorWindowTitle(titleBar.get(), hwnd);
    if (SUCCEEDED(hr)) hr = AttachInspectorTitleBar(window, titleBarUi.get());
    if (FAILED(hr)) {
        DevToolsPutVisibility(titleBarUi.get(), 1);
        DevToolsOverlayLog(L"route1.titlebar attach failed hr=0x%08X; keeping system caption", hr);
        return S_FALSE;
    }
    DevToolsOverlayLog(L"route1.titlebar TitleBar control replaces the system caption");
    return S_OK;
}

// The row PALETTE: live donor elements declared in window-shell.xaml, held from the shell wiring.
// Constructed elements cannot carry a {ThemeResource} reference, so every brush, font family and style the
// rows need is read off these on each rebuild. Reading them LIVE rather than caching the resolved objects is
// what keeps the rows following the app's theme: after a theme change the donors have re-resolved, so the
// next rebuild picks up the new brushes.
static IInspectable* g_palTransparent = nullptr;
static IInspectable* g_palAccent      = nullptr;
static IInspectable* g_palPrimary     = nullptr;
static IInspectable* g_palSecondary   = nullptr;
static IInspectable* g_palTertiary    = nullptr;
static IInspectable* g_palMonoFont    = nullptr;
static IInspectable* g_palGlyphFont   = nullptr;
static IInspectable* g_palCaption     = nullptr;

// Statics the row builder calls (Grid.SetColumn, the AutomationProperties setters, the boxed IsChecked).
static void* g_gridStatics = nullptr;
static void* g_apStatics   = nullptr;

static void CaptureRowPalette(void* feRoot)
{
    struct Donor { const wchar_t* name; IInspectable** slot; };
    const Donor donors[] = {
        { L"PalTransparent", &g_palTransparent },
        { L"PalAccent",      &g_palAccent      },
        { L"PalPrimary",     &g_palPrimary     },
        { L"PalSecondary",   &g_palSecondary   },
        { L"PalTertiary",    &g_palTertiary    },
        { L"PalMonoFont",    &g_palMonoFont    },
        { L"PalGlyphFont",   &g_palGlyphFont   },
        { L"PalCaption",     &g_palCaption     },
    };
    for (const Donor& d : donors) {
        HSTRING nm = MakeStr(d.name);
        IInspectable* found = nullptr;
        DevToolsFindName(feRoot, nm, &found);
        WindowsDeleteString(nm);
        if (!found) {
            DevToolsOverlayLog(L"route1.palette MISS %s -- tree rows will not be themed", d.name);
            continue;
        }
        *d.slot = found;
    }
    if (!g_gridStatics) {
        HSTRING cls = MakeStr(L"Microsoft.UI.Xaml.Controls.Grid");
        RoGetActivationFactory(cls, DevToolsIid<DevToolsXC::IGridStatics>(), &g_gridStatics);
        WindowsDeleteString(cls);
        if (!g_gridStatics)
            DevToolsOverlayLog(L"route1.palette no IGridStatics -- every row would collapse into column 0");
    }
    if (!g_apStatics) {
        HSTRING cls = MakeStr(L"Microsoft.UI.Xaml.Automation.AutomationProperties");
        RoGetActivationFactory(cls, DevToolsIid<DevToolsXA::IAutomationPropertiesStatics>(), &g_apStatics);
        WindowsDeleteString(cls);
        if (!g_apStatics)
            DevToolsOverlayLog(L"route1.palette no IAutomationPropertiesStatics -- the tree would have no names, "
                          L"no tree semantics, and rows could not be addressed by automation id");
    }
}
