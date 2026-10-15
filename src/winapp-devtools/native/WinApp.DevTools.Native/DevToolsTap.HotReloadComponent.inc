// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.
//
// HotReload.reloadComponent: 'winapp run --watch' for apps whose generated code can't change while they run (C++/WinRT).
// winapp compiles a saved page with the XAML compiler and sends its .xbf only when the generated code is byte-identical
// to the running build's. The tap hands that .xbf to WinUI (IXamlHotReloadService::SetComponentMarkup) and loads it into
// every live instance of the page's class the way the generated InitializeComponent does
// (Application::LoadComponent(*this, uri, location)), so the page's compiled Connect wires the new tree.

// The part of IXamlHotReloadService (xamlom.winui.h) this file uses; slots 3 and 4 of its vtable.
struct __declspec(uuid("73A07052-1BFE-40CD-8DC4-FB3D7A5BCECF")) IDevToolsXamlHotReloadMarkup : IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE SetComponentMarkup(LPCWSTR uri, BOOL binary, UINT32 size, const BYTE* content) = 0;
    virtual HRESULT STDMETHODCALLTYPE RemoveComponentMarkup(LPCWSTR uri) = 0;
};

static constexpr HRESULT kXamlHotReloadNotEnabled = static_cast<HRESULT>(0x802B0029);
static constexpr size_t kHotReloadMaxXbfBytes = 64u * 1024u * 1024u;

static bool HotReloadReadFile(const std::wstring& path, std::vector<BYTE>* bytes, std::wstring* error)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        *error = L"could not open " + path + L" (" + HrStr(HRESULT_FROM_WIN32(GetLastError())) + L")";
        return false;
    }
    LARGE_INTEGER size{};
    bool ok = GetFileSizeEx(file, &size) && size.QuadPart > 0 && static_cast<unsigned long long>(size.QuadPart) <= kHotReloadMaxXbfBytes;
    if (ok) {
        bytes->resize(static_cast<size_t>(size.QuadPart));
        DWORD read = 0;
        ok = ReadFile(file, bytes->data(), static_cast<DWORD>(bytes->size()), &read, nullptr) && read == bytes->size();
    }
    CloseHandle(file);
    if (!ok) *error = L"could not read " + path;
    return ok;
}

// Every live object whose runtime class is `className`: elements the census tracks, the windows that host them, and
// the Application. Compared by the class the object reports, so a page derived from another page isn't mistaken for it.
static std::vector<winrt::Windows::Foundation::IInspectable> HotReloadLiveComponents_ui(const std::wstring& className)
{
    std::vector<winrt::Windows::Foundation::IInspectable> found;
    std::set<void*> seen;
    auto consider = [&](winrt::Windows::Foundation::IInspectable object) {
        if (!object) return;
        // The diagnostics backing object of a Window or the Application can be the framework's inner object, which
        // names its framework class. Its interfaces delegate to the app's outer object, which names the app's class.
        if (auto window = object.try_as<DevToolsX::Window>()) object = window.as<winrt::Windows::Foundation::IInspectable>();
        else if (auto element = object.try_as<DevToolsX::DependencyObject>()) object = element.as<winrt::Windows::Foundation::IInspectable>();
        else if (auto application = object.try_as<DevToolsX::Application>()) object = application.as<winrt::Windows::Foundation::IInspectable>();
        void* identity = winrt::get_abi(object.as<winrt::Windows::Foundation::IUnknown>());
        if (!seen.insert(identity).second) return;
        const std::wstring name(winrt::get_class_name(object));
        Log(L"hot reload: candidate %s", name.c_str());
        if (name == className) found.push_back(object);
    };
    std::vector<InstanceHandle> handles;
    AcquireSRWLockShared(&g_censusLock);
    for (const auto& entry : g_type) {
        if (entry.second == className || entry.second.find(L"Window") != std::wstring::npos) handles.push_back(entry.first);
    }
    ReleaseSRWLockShared(&g_censusLock);
    for (InstanceHandle handle : handles) {
        winrt::com_ptr<IInspectable> raw;
        if (!g_diag || FAILED(g_diag->GetIInspectableFromHandle(handle, raw.put())) || !raw) continue;
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, raw.get());
        try { consider(object); } catch (const winrt::hresult_error&) {}
    }
    try { consider(DevToolsX::Application::Current()); } catch (const winrt::hresult_error&) {}
    return found;
}

static std::wstring HotReloadElementName(DevToolsX::DependencyObject const& element)
{
    auto framework = element.try_as<DevToolsX::FrameworkElement>();
    return framework ? std::wstring(framework.Name()) : std::wstring();
}

// Named Frames under `root` that show a page, by x:Name: navigation is code, so markup alone never fills them again.
static void HotReloadNamedFrames_ui(DevToolsX::DependencyObject const& root, std::vector<DevToolsXC::Frame>* frames)
{
    if (!root) return;
    if (auto frame = root.try_as<DevToolsXC::Frame>()) {
        if (frame.Content() && !frame.Name().empty()) frames->push_back(frame);
        return;
    }
    const int count = DevToolsXM::VisualTreeHelper::GetChildrenCount(root);
    for (int i = 0; i < count; ++i) HotReloadNamedFrames_ui(DevToolsXM::VisualTreeHelper::GetChild(root, i), frames);
}

static DevToolsXC::Frame HotReloadFindFrame_ui(DevToolsX::DependencyObject const& root, const std::wstring& name)
{
    if (!root) return nullptr;
    if (auto frame = root.try_as<DevToolsXC::Frame>()) {
        if (frame.Name() == name) return frame;
    }
    const int count = DevToolsXM::VisualTreeHelper::GetChildrenCount(root);
    for (int i = 0; i < count; ++i) {
        if (auto found = HotReloadFindFrame_ui(DevToolsXM::VisualTreeHelper::GetChild(root, i), name)) return found;
    }
    // Not in the visual tree yet (the new content hasn't been laid out): walk the logical content.
    if (auto control = root.try_as<DevToolsXC::ContentControl>()) {
        if (auto child = control.Content().try_as<DevToolsX::DependencyObject>()) return HotReloadFindFrame_ui(child, name);
    }
    if (auto panel = root.try_as<DevToolsXC::Panel>()) {
        for (auto const& child : panel.Children()) {
            if (auto found = HotReloadFindFrame_ui(child, name)) return found;
        }
    }
    if (auto border = root.try_as<DevToolsXC::Border>()) {
        if (auto child = border.Child()) return HotReloadFindFrame_ui(child, name);
    }
    return nullptr;
}

static DevToolsX::UIElement HotReloadContent(winrt::Windows::Foundation::IInspectable const& component)
{
    if (auto window = component.try_as<DevToolsX::Window>()) return window.Content();
    if (auto control = component.try_as<DevToolsXC::UserControl>()) return control.Content();
    if (auto control = component.try_as<DevToolsXC::ContentControl>()) return control.Content().try_as<DevToolsX::UIElement>();
    return nullptr;
}

static void HotReloadSetContent(winrt::Windows::Foundation::IInspectable const& component, DevToolsX::UIElement const& content)
{
    if (auto window = component.try_as<DevToolsX::Window>()) window.Content(content);
    else if (auto control = component.try_as<DevToolsXC::UserControl>()) control.Content(content);
    else if (auto control = component.try_as<DevToolsXC::ContentControl>()) control.Content(content);
}

// Loads `uri` into one live component as its generated InitializeComponent does. A failure puts the old tree back.
// Pages shown in the old tree's named Frames move to the rebuilt Frames of the same name; `notes` says so.
static HRESULT HotReloadReloadOne_ui(winrt::Windows::Foundation::IInspectable const& component, const std::wstring& uri,
    DevToolsXCP::ComponentResourceLocation location, std::vector<std::wstring>* notes, std::wstring* error)
{
    auto framework = component.try_as<DevToolsX::FrameworkElement>();
    auto application = component.try_as<DevToolsX::Application>();
    DevToolsX::ResourceDictionary resources{ nullptr };
    if (framework) resources = framework.Resources();
    else if (application) resources = application.Resources();
    auto content = HotReloadContent(component);
    std::vector<DevToolsXC::Frame> frames;
    if (content) HotReloadNamedFrames_ui(content, &frames);
    try {
        // Markup adds its own resources to the component's dictionary: start from an empty one, as a new instance does.
        if (framework) framework.Resources(DevToolsX::ResourceDictionary());
        DevToolsX::Application::LoadComponent(component, winrt::Windows::Foundation::Uri(uri), location);
    } catch (const winrt::hresult_error& e) {
        try {
            if (framework && resources) framework.Resources(resources);
            else if (application && resources) application.Resources(resources);
            if (content) HotReloadSetContent(component, content);
        } catch (const winrt::hresult_error&) {}
        *error = std::wstring(e.message()) + L" (" + HrStr(e.code()) + L")";
        return e.code();
    }
    auto rebuilt = HotReloadContent(component);
    for (auto const& old : frames) {
        const std::wstring name(old.Name());
        auto replacement = rebuilt ? HotReloadFindFrame_ui(rebuilt, name) : nullptr;
        if (!replacement || replacement == old || replacement.Content()) continue;
        auto page = old.Content();
        try {
            old.Content(nullptr);
            replacement.Content(page);
            notes->push_back(L"kept the page shown in Frame '" + name + L"' (its back stack starts over)");
        } catch (const winrt::hresult_error& e) {
            notes->push_back(L"Frame '" + name + L"' is empty after the reload: " + std::wstring(e.message()));
        }
    }
    return S_OK;
}

static std::wstring HotReloadReloadComponent_ui(const std::wstring& id, const std::wstring& uri, const std::wstring& className,
    const std::wstring& locationName, const std::wstring& xbfPath)
{
    const auto location = locationName == L"Nested" ? DevToolsXCP::ComponentResourceLocation::Nested
                                                    : DevToolsXCP::ComponentResourceLocation::Application;
    size_t bytes = 0;
    if (!xbfPath.empty()) {
        IUnknown* service = g_hotReloadService.load();
        winrt::com_ptr<IDevToolsXamlHotReloadMarkup> markup;
        if (!service || FAILED(service->QueryInterface(__uuidof(IDevToolsXamlHotReloadMarkup), markup.put_void())) || !markup) {
            return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                L"this WinUI has no XAML hot reload service (IXamlHotReloadService)", L"hot-reload-unavailable");
        }
        std::vector<BYTE> xbf;
        std::wstring readError;
        if (!HotReloadReadFile(xbfPath, &xbf, &readError)) {
            return DevToolsRpcError(id, DevToolsErr::InvalidParams, readError, L"bad-args");
        }
        // Markup set earlier for the URI is dropped first, as the managed agent does.
        (void)markup->RemoveComponentMarkup(uri.c_str());
        const HRESULT set = markup->SetComponentMarkup(uri.c_str(), TRUE, static_cast<UINT32>(xbf.size()), xbf.data());
        if (FAILED(set)) {
            return DevToolsRpcError(id, DevToolsErr::InvalidParams,
                set == kXamlHotReloadNotEnabled
                    ? L"XAML hot reload is not enabled for this app (build it in Debug, or with EnableXamlHotReload=true)"
                    : L"the XAML hot reload service refused the markup",
                set == kXamlHotReloadNotEnabled ? L"hot-reload-not-enabled" : L"markup-refused",
                L"\"hresult\":\"" + HrStr(set) + L"\"");
        }
        bytes = xbf.size();
    }
    std::vector<std::wstring> notes, errors;
    size_t reloaded = 0;
    const auto components = HotReloadLiveComponents_ui(className);
    for (auto const& component : components) {
        std::wstring error;
        if (SUCCEEDED(HotReloadReloadOne_ui(component, uri, location, &notes, &error))) ++reloaded;
        else errors.push_back(error);
    }
    Log(L"hot reload: %s (%s) %zu bytes, %zu of %zu instance(s) reloaded", uri.c_str(), className.c_str(), bytes, reloaded, components.size());
    auto list = [](const std::vector<std::wstring>& items) {
        std::wstring json = L"[";
        for (size_t i = 0; i < items.size(); ++i) json += (i ? L",\"" : L"\"") + DevToolsJsonEscape(items[i]) + L"\"";
        return json + L"]";
    };
    return DevToolsRpcResult(id, L"{\"bytes\":" + std::to_wstring(bytes) + L",\"instances\":" + std::to_wstring(components.size()) +
        L",\"reloaded\":" + std::to_wstring(reloaded) + L",\"notes\":" + list(notes) + L",\"errors\":" + list(errors) + L"}");
}

static std::wstring HandleHotReloadReloadComponent(DevToolsConn* conn, const std::wstring& id, const DevToolsJson& p)
{
    const std::wstring uri = p.GetString(L"uri");
    const std::wstring className = p.GetString(L"className");
    const std::wstring location = p.GetString(L"location", L"Application");
    const std::wstring xbf = p.GetString(L"xbf");
    if (uri.rfind(L"ms-appx:///", 0) != 0 || className.empty() || (location != L"Application" && location != L"Nested")) {
        return DevToolsRpcError(id, DevToolsErr::InvalidParams,
            L"uri must be an ms-appx:/// URI, className is required, and location is Application or Nested", L"bad-args");
    }
    HRESULT posted = PostRpcToUi(conn, id, [id, uri, className, location, xbf]() {
        try {
            return HotReloadReloadComponent_ui(id, uri, className, location, xbf);
        } catch (const winrt::hresult_error& e) {
            return DevToolsRpcError(id, DevToolsErr::InternalError, std::wstring(e.message()), L"internal",
                L"\"hresult\":\"" + HrStr(e.code()) + L"\"");
        }
    });
    if (SUCCEEDED(posted)) return {};
    return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher, L"could not reach the app's UI thread", L"not-on-dispatcher",
        L"\"hresult\":\"" + HrStr(posted) + L"\"");
}
