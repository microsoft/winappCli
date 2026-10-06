// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.
//
// Live application resources: Resource.list reads them, Resource.set replaces one through
// IVisualTreeService2::ReplaceResource, and Resource.reset puts the app's own value back. ReplaceResource
// re-resolves every {StaticResource} and {ThemeResource} that already resolved to the key, but only for XAML
// parsed with ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO, which `winapp run --devtools` sets.
//
// Scope is Application.Resources and everything it merges. Page and element Resources are not covered.

using DevToolsInspectable = winrt::Windows::Foundation::IInspectable;

struct DevToolsResourceOriginal
{
    DevToolsX::ResourceDictionary dictionary{ nullptr };
    DevToolsInspectable value{ nullptr };
};
static DevToolsResourceOverrides<DevToolsResourceOriginal> g_resourceOverrides;

constexpr size_t kResourceListCap = 500;
constexpr int kResourceMaxDepth = 16;

struct DevToolsResourceHit
{
    std::wstring key;
    DevToolsX::ResourceDictionary dictionary{ nullptr };
    std::wstring dictionaryPath;
    std::wstring themeBranch;  // the ThemeDictionaries entry that holds the key; empty when not themed
    bool framework = false;    // inside a WinUI-supplied dictionary such as XamlControlsResources
    DevToolsInspectable value{ nullptr };
};

static std::wstring ResourceScopeOf(const DevToolsX::ResourceDictionary& dictionary)
{
    wchar_t text[32]{};
    swprintf_s(text, L"%p", winrt::get_abi(dictionary));
    return text;
}

static std::wstring ResourceNumber(double d)
{
    std::wstring s = std::to_wstring(d);
    const size_t dot = s.find(L'.');
    if (dot != std::wstring::npos) {
        const size_t last = s.find_last_not_of(L'0');
        s.erase(last == dot ? dot : last + 1);
    }
    return s;
}

static std::wstring ResourceColorHex(const winrt::Windows::UI::Color& c)
{
    wchar_t text[10]{};
    swprintf_s(text, L"#%02X%02X%02X%02X", c.A, c.R, c.G, c.B);
    return text;
}

static std::wstring ResourceFourSides(double a, double b, double c, double d)
{
    if (a == b && b == c && c == d) return ResourceNumber(a);
    return ResourceNumber(a) + L"," + ResourceNumber(b) + L"," + ResourceNumber(c) + L"," + ResourceNumber(d);
}

static std::wstring ResourceShortClassName(const DevToolsInspectable& value)
{
    std::wstring name = winrt::get_class_name(value).c_str();
    const size_t generic = name.find(L'`');
    if (generic != std::wstring::npos) name.erase(generic);
    const size_t dot = name.find_last_of(L'.');
    return dot == std::wstring::npos ? name : name.substr(dot + 1);
}

// A resource value as text a user can read and paste back into `resources set`.
static std::wstring ResourceDisplayValue(const DevToolsInspectable& value, std::wstring* type)
{
    using namespace winrt::Windows::Foundation;
    if (!value) { *type = L"null"; return L""; }
    if (auto brush = value.try_as<DevToolsXM::SolidColorBrush>()) {
        *type = L"SolidColorBrush";
        return ResourceColorHex(brush.Color());
    }
    if (auto color = value.try_as<IReference<winrt::Windows::UI::Color>>()) {
        *type = L"Color";
        return ResourceColorHex(color.Value());
    }
    if (auto thickness = value.try_as<IReference<DevToolsX::Thickness>>()) {
        const auto t = thickness.Value();
        *type = L"Thickness";
        return ResourceFourSides(t.Left, t.Top, t.Right, t.Bottom);
    }
    if (auto radius = value.try_as<IReference<DevToolsX::CornerRadius>>()) {
        const auto r = radius.Value();
        *type = L"CornerRadius";
        return ResourceFourSides(r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft);
    }
    if (auto weight = value.try_as<IReference<winrt::Windows::UI::Text::FontWeight>>()) {
        *type = L"FontWeight";
        return std::to_wstring(weight.Value().Weight);
    }
    if (auto family = value.try_as<DevToolsXM::FontFamily>()) {
        *type = L"FontFamily";
        return family.Source().c_str();
    }
    std::wstring text, effective;
    if (ReadInspectableValue(static_cast<::IInspectable*>(winrt::get_abi(value)), L"", L"", &text, &effective) == S_OK) {
        const size_t dot = effective.find_last_of(L'.');
        *type = dot == std::wstring::npos ? effective : effective.substr(dot + 1);
        return text;
    }
    *type = ResourceShortClassName(value);
    return L"{" + *type + L"}";
}

// The XAML type whose type converter turns `resources set` text into a value of the resource's own kind. Empty when
// the kind has no literal form (a Style, a template, a converter), which `set` refuses.
static std::wstring ResourceWriteType(const DevToolsInspectable& value)
{
    using namespace winrt::Windows::Foundation;
    if (!value) return L"";
    if (value.try_as<DevToolsXM::Brush>()) return L"Microsoft.UI.Xaml.Media.Brush";
    if (value.try_as<IReference<winrt::Windows::UI::Color>>()) return L"Windows.UI.Color";
    if (value.try_as<IReference<DevToolsX::Thickness>>()) return L"Microsoft.UI.Xaml.Thickness";
    if (value.try_as<IReference<DevToolsX::CornerRadius>>()) return L"Microsoft.UI.Xaml.CornerRadius";
    if (value.try_as<IReference<winrt::Windows::UI::Text::FontWeight>>()) return L"Windows.UI.Text.FontWeight";
    if (value.try_as<DevToolsXM::FontFamily>()) return L"Microsoft.UI.Xaml.Media.FontFamily";
    if (auto primitive = value.try_as<IPropertyValue>()) {
        switch (primitive.Type()) {
            case PropertyType::Double:  return L"Windows.Foundation.Double";
            case PropertyType::Single:  return L"Windows.Foundation.Single";
            case PropertyType::Int32:   return L"Windows.Foundation.Int32";
            case PropertyType::Boolean: return L"Windows.Foundation.Boolean";
            case PropertyType::String:  return L"Windows.Foundation.String";
            default: break;
        }
    }
    return L"";
}

static std::wstring ResourceDictionaryLabel(const DevToolsX::ResourceDictionary& dictionary)
{
    const std::wstring className = winrt::get_class_name(dictionary).c_str();
    if (className != L"Microsoft.UI.Xaml.ResourceDictionary") {
        const size_t dot = className.find_last_of(L'.');
        return dot == std::wstring::npos ? className : className.substr(dot + 1);
    }
    if (auto source = dictionary.Source()) return source.RawUri().c_str();
    return L"";
}

// The theme whose ThemeDictionaries branch the app's visible elements resolve through. Apps commonly set
// RequestedTheme on their window content rather than on the Application, so read it from the content.
static std::wstring ActiveResourceTheme_ui()
{
    HIGHCONTRASTW contrast{};
    contrast.cbSize = sizeof(contrast);
    if (SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(contrast), &contrast, 0) &&
        (contrast.dwFlags & HCF_HIGHCONTRASTON) != 0) {
        return L"HighContrast";
    }
    auto themeName = [](DevToolsX::ElementTheme theme) {
        return theme == DevToolsX::ElementTheme::Dark ? std::wstring(L"Dark") : std::wstring(L"Light");
    };
    try {
        winrt::com_ptr<::IInspectable> raw;
        const InstanceHandle root = RootVisual();
        if (root && g_diag && SUCCEEDED(g_diag->GetIInspectableFromHandle(root, raw.put())) && raw) {
            DevToolsInspectable object{ nullptr };
            winrt::copy_from_abi(object, raw.get());
            if (auto rootElement = object.try_as<DevToolsX::FrameworkElement>()) {
                std::vector<DevToolsX::DependencyObject> level{ rootElement };
                for (int depth = 0; depth < 6 && !level.empty(); ++depth) {
                    std::vector<DevToolsX::DependencyObject> next;
                    for (const auto& node : level) {
                        auto element = node.try_as<DevToolsX::FrameworkElement>();
                        if (element && element.RequestedTheme() != DevToolsX::ElementTheme::Default)
                            return themeName(element.ActualTheme());
                        const int count = DevToolsXM::VisualTreeHelper::GetChildrenCount(node);
                        for (int i = 0; i < count; ++i) next.push_back(DevToolsXM::VisualTreeHelper::GetChild(node, i));
                    }
                    level = std::move(next);
                }
                return themeName(rootElement.ActualTheme());
            }
        }
        return DevToolsX::Application::Current().RequestedTheme() == DevToolsX::ApplicationTheme::Dark ? L"Dark" : L"Light";
    } catch (const winrt::hresult_error&) {
        return L"Light";
    }
}

static bool NormalizeResourceTheme(const std::wstring& requested, std::wstring* theme)
{
    if (requested.empty()) { *theme = ActiveResourceTheme_ui(); return true; }
    for (const wchar_t* known : { L"Light", L"Dark", L"HighContrast" }) {
        if (_wcsicmp(requested.c_str(), known) == 0) { *theme = known; return true; }
    }
    return false;
}

// WinUI's own dictionaries (XamlControlsResources and anything sourced from the framework package). Their entries
// are referenced from framework styles whose dependents WinUI can't update, and a failed ReplaceResource leaves the
// dictionary unreadable, so `set` refuses them.
static bool IsFrameworkResourceDictionary(const DevToolsX::ResourceDictionary& dictionary)
{
    const std::wstring className = winrt::get_class_name(dictionary).c_str();
    if (className != L"Microsoft.UI.Xaml.ResourceDictionary" && className.rfind(L"Microsoft.UI.Xaml.", 0) == 0) return true;
    if (auto source = dictionary.Source()) return _wcsnicmp(source.RawUri().c_str(), L"ms-appx:///Microsoft.UI.Xaml/", 29) == 0;
    return false;
}

// Visits application resources in WinUI's lookup order: a dictionary's own entries, then its active theme branch,
// then its merged dictionaries from last to first. `visit` returns false to stop the walk.
static bool WalkResources(const DevToolsX::ResourceDictionary& dictionary, const std::wstring& path,
                          const std::wstring& themeBranch, const std::wstring& theme, bool framework, int depth,
                          const std::function<bool(const DevToolsResourceHit&)>& visit)
{
    if (!dictionary || depth > kResourceMaxDepth) return true;
    framework = framework || IsFrameworkResourceDictionary(dictionary);
    for (const auto& item : dictionary) {
        DevToolsResourceHit hit;
        try {
            hit.key = winrt::unbox_value_or<winrt::hstring>(item.Key(), winrt::hstring{}).c_str();
            if (hit.key.empty()) continue;  // implicit styles and templates are keyed by type, not by name
            hit.value = item.Value();
        } catch (const winrt::hresult_error& error) {
            Log(L"resource.walk %s: skipped an entry (0x%08X)", path.c_str(), static_cast<HRESULT>(error.code()));
            continue;
        }
        hit.dictionary = dictionary;
        hit.dictionaryPath = path;
        hit.themeBranch = themeBranch;
        hit.framework = framework;
        if (!visit(hit)) return false;
    }
    if (auto themes = dictionary.ThemeDictionaries()) {
        for (const std::wstring& branch : { theme, std::wstring(L"Default") }) {
            const auto key = winrt::box_value(winrt::hstring(branch));
            if (!themes.HasKey(key)) continue;
            if (auto branchDictionary = themes.Lookup(key).try_as<DevToolsX::ResourceDictionary>()) {
                if (!WalkResources(branchDictionary, path + L"/ThemeDictionaries[" + branch + L"]", branch, theme,
                                   framework, depth + 1, visit)) return false;
            }
            break;
        }
    }
    const auto merged = dictionary.MergedDictionaries();
    for (int i = static_cast<int>(merged.Size()) - 1; i >= 0; --i) {
        const auto child = merged.GetAt(static_cast<uint32_t>(i));
        const std::wstring label = ResourceDictionaryLabel(child);
        std::wstring childPath = path + L"/MergedDictionaries[" + std::to_wstring(i) + L"]";
        if (!label.empty()) childPath += L" (" + label + L")";
        if (!WalkResources(child, childPath, themeBranch, theme, framework, depth + 1, visit)) return false;
    }
    return true;
}

static bool FindAppResource(const std::wstring& key, const std::wstring& theme, DevToolsResourceHit* found)
{
    bool hit = false;
    WalkResources(DevToolsX::Application::Current().Resources(), L"Application.Resources", L"", theme, false, 0,
                  [&](const DevToolsResourceHit& candidate) {
                      if (candidate.key != key) return true;
                      *found = candidate;
                      hit = true;
                      return false;
                  });
    return hit;
}

static bool ResourceLiveUpdateEnabled()
{
    wchar_t value[8]{};
    const DWORD length = GetEnvironmentVariableW(L"ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO", value, ARRAYSIZE(value));
    return length > 0 && length < ARRAYSIZE(value) && wcscmp(value, L"1") == 0;
}

static std::wstring ResourceEntryJson(const DevToolsResourceHit& hit)
{
    std::wstring type;
    const std::wstring value = ResourceDisplayValue(hit.value, &type);
    std::wstring json = L"{\"key\":\"" + DevToolsJsonEscape(hit.key) + L"\",\"value\":\"" + DevToolsJsonEscape(value) +
        L"\",\"valueType\":\"" + DevToolsJsonEscape(type) + L"\",\"dictionary\":\"" + DevToolsJsonEscape(hit.dictionaryPath) + L"\"";
    if (!hit.themeBranch.empty()) json += L",\"themeDictionary\":\"" + DevToolsJsonEscape(hit.themeBranch) + L"\"";
    json += std::wstring(L",\"framework\":") + (hit.framework ? L"true" : L"false");
    json += std::wstring(L",\"overridden\":") + (g_resourceOverrides.Find(ResourceScopeOf(hit.dictionary), hit.key) ? L"true" : L"false") + L"}";
    return json;
}

static std::wstring ResourceList_ui(const std::wstring& id, const std::wstring& pattern, const std::wstring& requestedTheme)
{
    std::wstring theme;
    if (!NormalizeResourceTheme(requestedTheme, &theme))
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"theme must be Light, Dark or HighContrast", L"bad-args");
    std::set<std::wstring> seen;
    std::wstring entries;
    size_t total = 0, emitted = 0;
    WalkResources(DevToolsX::Application::Current().Resources(), L"Application.Resources", L"", theme, false, 0,
                  [&](const DevToolsResourceHit& hit) {
                      if (!DevToolsResourceKeyMatches(pattern, hit.key) || !seen.insert(hit.key).second) return true;
                      ++total;
                      if (emitted < kResourceListCap) {
                          if (emitted++) entries += L",";
                          entries += ResourceEntryJson(hit);
                      }
                      return true;
                  });
    return DevToolsRpcResult(id, L"{\"theme\":\"" + theme + L"\",\"resources\":[" + entries + L"],\"total\":" +
                                 std::to_wstring(total) + L",\"truncated\":" + (total > emitted ? L"true" : L"false") + L"}");
}

// Hands a WinRT object to the diagnostics service as an instance handle.
static HRESULT ResourceHandleOf(const DevToolsInspectable& object, InstanceHandle* handle)
{
    if (!g_diag) return E_NOINTERFACE;
    return g_diag->RegisterInstance(static_cast<::IInspectable*>(winrt::get_abi(object)), handle);
}

static HRESULT ReplaceAppResource_ui(const DevToolsX::ResourceDictionary& dictionary, const std::wstring& key,
                                     InstanceHandle valueHandle)
{
    if (!g_vts3) return E_NOINTERFACE;
    InstanceHandle dictionaryHandle = 0, keyHandle = 0;
    HRESULT hr = ResourceHandleOf(dictionary, &dictionaryHandle);
    if (FAILED(hr)) return hr;
    BSTR keyType = SysAllocString(L"Windows.Foundation.String");
    BSTR keyText = SysAllocString(key.c_str());
    hr = keyType && keyText ? g_vts3->CreateInstance(keyType, keyText, &keyHandle) : E_OUTOFMEMORY;
    SysFreeString(keyType);
    SysFreeString(keyText);
    if (FAILED(hr)) return hr;
    return g_vts3->ReplaceResource(dictionaryHandle, keyHandle, valueHandle);
}

static std::wstring ResourceSet_ui(const std::wstring& id, const std::wstring& key, const std::wstring& text,
                                   const std::wstring& requestedTheme, const std::wstring& requestedType,
                                   unsigned long long owner)
{
    std::wstring theme;
    if (!NormalizeResourceTheme(requestedTheme, &theme))
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"theme must be Light, Dark or HighContrast", L"bad-args");
    DevToolsResourceHit hit;
    if (!FindAppResource(key, theme, &hit)) {
        return DevToolsRpcError(id, DevToolsErr::NotFound,
            L"'" + key + L"' is not defined in Application.Resources, its merged dictionaries, or their " + theme +
            L" theme dictionaries. Framework defaults the app does not define can't be replaced; add the key to App.xaml to override it.",
            L"resource-not-found", L"\"theme\":\"" + theme + L"\"");
    }
    std::wstring previousType;
    const std::wstring previous = ResourceDisplayValue(hit.value, &previousType);
    if (hit.framework) {
        return DevToolsRpcError(id, DevToolsErr::InvalidParams,
            L"'" + key + L"' is a WinUI default resource (" + hit.dictionaryPath + L"), which can't be replaced while the app runs. "
            L"To override it, define the key in App.xaml and restart the app; after that `set` can change it live.",
            L"resource-framework-default", L"\"dictionary\":\"" + DevToolsJsonEscape(hit.dictionaryPath) + L"\"");
    }
    const std::wstring writeType = requestedType.empty() ? ResourceWriteType(hit.value) : requestedType;
    if (writeType.empty()) {
        return DevToolsRpcError(id, DevToolsErr::InvalidParams,
            L"'" + key + L"' holds a " + previousType + L", which has no text form to replace it with.",
            L"resource-unsupported-type", L"\"valueType\":\"" + DevToolsJsonEscape(previousType) + L"\"");
    }

    InstanceHandle valueHandle = 0;
    BSTR typeName = SysAllocString(writeType.c_str());
    BSTR valueText = SysAllocString(text.c_str());
    HRESULT hr = typeName && valueText ? g_vts3->CreateInstance(typeName, valueText, &valueHandle) : E_OUTOFMEMORY;
    SysFreeString(typeName);
    SysFreeString(valueText);
    if (FAILED(hr) || !valueHandle) {
        return DevToolsRpcError(id, DevToolsErr::InvalidParams,
            L"'" + text + L"' is not a valid " + previousType + L" value.", L"resource-bad-value",
            L"\"valueType\":\"" + DevToolsJsonEscape(previousType) + L"\",\"hresult\":\"" + HrStr(FAILED(hr) ? hr : E_INVALIDARG) + L"\"");
    }

    hr = ReplaceAppResource_ui(hit.dictionary, key, valueHandle);
    if (FAILED(hr)) {
        // WinUI removes the old entry before it inserts the new one and re-resolves references, so a failure part
        // way through can leave the key missing or half-applied. Put the app's own value back.
        InstanceHandle originalHandle = 0;
        HRESULT restoreHr = ResourceHandleOf(hit.value, &originalHandle);
        if (SUCCEEDED(restoreHr)) restoreHr = ReplaceAppResource_ui(hit.dictionary, key, originalHandle);
        Log(L"resource.set %s: ReplaceResource hr=0x%08X restore hr=0x%08X", key.c_str(), hr, restoreHr);
        return DevToolsRpcError(id, DevToolsErr::InternalError,
            L"WinUI could not replace '" + key + L"' (" + HrStr(hr) + L"). " +
                (SUCCEEDED(restoreHr) ? L"The app's value was put back."
                                      : L"Putting the app's value back also failed; restart the app."),
            L"resource-replace-failed",
            L"\"hresult\":\"" + HrStr(hr) + L"\",\"restored\":" + (SUCCEEDED(restoreHr) ? L"true" : L"false"));
    }
    g_resourceOverrides.RecordSet(ResourceScopeOf(hit.dictionary), key, owner,
                                  DevToolsResourceOriginal{ hit.dictionary, hit.value });

    std::wstring valueType, value = text;
    bool verified = false;
    try {
        winrt::com_ptr<::IInspectable> rawValue;
        DevToolsInspectable created{ nullptr };
        if (g_diag && SUCCEEDED(g_diag->GetIInspectableFromHandle(valueHandle, rawValue.put())) && rawValue)
            winrt::copy_from_abi(created, rawValue.get());
        const std::wstring wanted = ResourceDisplayValue(created, &valueType);
        const DevToolsInspectable current = hit.dictionary.Lookup(winrt::box_value(winrt::hstring(key)));
        std::wstring currentType;
        value = ResourceDisplayValue(current, &currentType);
        verified = current && value == wanted;
        valueType = currentType;
    } catch (const winrt::hresult_error& e) {
        Log(L"resource.set %s: read-back failed hr=0x%08X", key.c_str(), static_cast<HRESULT>(e.code()));
    }
    const DevToolsResourceOverrides<DevToolsResourceOriginal>::Entry* entry =
        g_resourceOverrides.Find(ResourceScopeOf(hit.dictionary), key);
    std::wstring originalType, original = entry ? ResourceDisplayValue(entry->original.value, &originalType) : previous;

    std::wstring json = L"{\"key\":\"" + DevToolsJsonEscape(key) + L"\",\"theme\":\"" + theme +
        L"\",\"dictionary\":\"" + DevToolsJsonEscape(hit.dictionaryPath) + L"\"";
    if (!hit.themeBranch.empty()) json += L",\"themeDictionary\":\"" + DevToolsJsonEscape(hit.themeBranch) + L"\"";
    json += L",\"previous\":\"" + DevToolsJsonEscape(previous) + L"\",\"original\":\"" + DevToolsJsonEscape(original) +
        L"\",\"value\":\"" + DevToolsJsonEscape(value) + L"\",\"valueType\":\"" + DevToolsJsonEscape(valueType) +
        L"\",\"verified\":" + (verified ? L"true" : L"false") +
        L",\"liveUpdate\":" + (ResourceLiveUpdateEnabled() ? L"true" : L"false") + L"}";
    return DevToolsRpcResult(id, json);
}

struct DevToolsResourceRestore
{
    std::wstring key;
    std::wstring value;
    HRESULT hr = S_OK;
};

static std::vector<DevToolsResourceRestore> RestoreResources_ui(
    std::vector<DevToolsResourceOverrides<DevToolsResourceOriginal>::Entry> entries)
{
    std::vector<DevToolsResourceRestore> results;
    for (const auto& entry : entries) {
        DevToolsResourceRestore result;
        result.key = entry.key;
        std::wstring type;
        result.value = ResourceDisplayValue(entry.original.value, &type);
        InstanceHandle originalHandle = 0;
        result.hr = ResourceHandleOf(entry.original.value, &originalHandle);
        if (SUCCEEDED(result.hr)) result.hr = ReplaceAppResource_ui(entry.original.dictionary, entry.key, originalHandle);
        if (FAILED(result.hr)) Log(L"resource.reset %s: hr=0x%08X", entry.key.c_str(), result.hr);
        results.push_back(std::move(result));
    }
    return results;
}

static std::wstring ResourceReset_ui(const std::wstring& id, const std::wstring& key)
{
    const auto results = RestoreResources_ui(g_resourceOverrides.TakeKey(key));
    std::wstring restored, failed;
    for (const auto& result : results) {
        std::wstring& list = SUCCEEDED(result.hr) ? restored : failed;
        if (!list.empty()) list += L",";
        list += L"{\"key\":\"" + DevToolsJsonEscape(result.key) + L"\",\"value\":\"" + DevToolsJsonEscape(result.value) + L"\"";
        if (FAILED(result.hr)) list += L",\"hresult\":\"" + HrStr(result.hr) + L"\"";
        list += L"}";
    }
    return DevToolsRpcResult(id, L"{\"restored\":[" + restored + L"],\"failed\":[" + failed + L"]}");
}

// A disconnecting client's replacements go back to the app's values, unless another client changed the key since.
static void ReleaseOwnedResources_ui(unsigned long long owner)
{
    const auto results = RestoreResources_ui(g_resourceOverrides.TakeOwned(owner));
    if (!results.empty()) Log(L"resource.release owner=%llu restored=%zu", owner, results.size());
}

static std::wstring HandleResourceRpc(DevToolsConn* conn, const std::wstring& id, const std::wstring& m,
                                      const DevToolsJson& p, unsigned long long ownerConn, bool respond)
{
    for (const wchar_t* name : { L"key", L"value", L"pattern", L"theme", L"type" }) {
        const DevToolsJson* field = p.Find(name);
        if (field && field->type != DevToolsJsonType::String)
            return DevToolsRpcError(id, DevToolsErr::InvalidParams, std::wstring(name) + L" must be a string", L"bad-args");
    }
    const std::wstring key = p.GetString(L"key", L"");
    if (m == L"Resource.set" && (key.empty() || !p.HasKey(L"value")))
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"Resource.set needs key and value", L"bad-args");

    const std::wstring value = p.GetString(L"value", L""), pattern = p.GetString(L"pattern", L"");
    const std::wstring theme = p.GetString(L"theme", L""), type = p.GetString(L"type", L"");
    const HRESULT postHr = PostRpcToUi(conn, id, [=]() -> std::wstring {
        if (m != L"Resource.list" && ownerConn != 0 && !DevToolsEvents_IsOwnerLive(ownerConn)) {
            if (!respond) return std::wstring();
            return DevToolsRpcError(id, DevToolsErr::InvalidParams, L"owner does not name a live negotiated connection",
                                    L"unknown-owner");
        }
        std::wstring reply;
        try {
            if (m == L"Resource.list")      reply = ResourceList_ui(id, pattern, theme);
            else if (m == L"Resource.set")  reply = ResourceSet_ui(id, key, value, theme, type, ownerConn);
            else                            reply = ResourceReset_ui(id, key);
        } catch (const winrt::hresult_error& e) {
            reply = DevToolsRpcError(id, DevToolsErr::InternalError, L"reading application resources failed (" + HrStr(static_cast<HRESULT>(e.code())) + L")",
                                     L"resource-read-failed", L"\"hresult\":\"" + HrStr(static_cast<HRESULT>(e.code())) + L"\"");
        }
        return respond ? reply : std::wstring();
    });
    if (SUCCEEDED(postHr)) return {};
    return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher, L"could not enqueue the resource operation",
                            L"not-on-dispatcher", L"\"hresult\":\"" + HrStr(postHr) + L"\"");
}
