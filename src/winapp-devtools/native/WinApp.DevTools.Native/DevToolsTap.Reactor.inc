// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsTap.cpp; included into that translation unit, not compiled on its own.

// Microsoft.UI.Reactor support. Only the in-process managed agent can read Reactor's element tags, so the tap hands
// it each census root and maps the COM identities it reports back to wire handles. A reported identity is only ever
// COMPARED with census elements' own identities; it is never dereferenced.
//
// Every function here may ask the managed agent, which marshals to the UI thread, so none may run ON the UI thread.

// Whether this app ships Reactor even though no agent can confirm it is loaded (late attach, Native AOT, trimmed
// builds that drop startup hooks). Reactor deploys Reactor.dll and its resource index Reactor.pri next to the app.
static bool TapReactorDeployed()
{
    static std::atomic<int> cached{-1};
    const int known = cached.load(std::memory_order_acquire);
    if (known >= 0) return known == 1;
    std::wstring path(32768, L'\0');
    const DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    bool deployed = false;
    if (length && length < path.size()) {
        path.resize(length);
        path.resize(path.find_last_of(L'\\') + 1);
        auto exists = [&path](const wchar_t* name) { return GetFileAttributesW((path + name).c_str()) != INVALID_FILE_ATTRIBUTES; };
        // A packaged layout merges Reactor.pri into resources.pri, so the pair of Reactor assemblies counts too.
        deployed = exists(L"Reactor.pri") || (exists(L"Reactor.dll") && exists(L"Reactor.Wrappers.Abstractions.dll"));
    }
    cached.store(deployed ? 1 : 0, std::memory_order_release);
    return deployed;
}

static std::mutex g_reactorStatusLock;
static TapReactorStatus g_reactorStatus;
static ULONGLONG g_reactorStatusAt = 0;

// What DevTools.negotiate advertises. A probe is one in-process pipe exchange that does not touch the UI thread;
// the answer is reused briefly so a burst of requests asks once.
static TapReactorStatus TapReactorStatusNow()
{
    static const ULONGLONG kReuseMs = 2000;
    std::lock_guard<std::mutex> lock(g_reactorStatusLock);
    const ULONGLONG now = GetTickCount64();
    // An agent loaded into the running app (`devtools attach`) is picked up at once, not after the reuse window.
    const bool agentArrived = g_reactorStatusAt && g_reactorStatus.agent == L"absent" && DevToolsBindingRelay_AgentPresent();
    if (g_reactorStatusAt && now - g_reactorStatusAt < kReuseMs && !agentArrived) return g_reactorStatus;

    TapReactorStatus status;
    std::wstring json;
    DevToolsJson answer;
    if (!DevToolsBindingRelay_AgentPresent()) {
        status.present = TapReactorDeployed();
        status.reason = DevToolsBindingRelay_AppRuntime() == DevToolsAppRuntime::Clr
            ? L"no managed DevTools agent is loaded in this app"
            : L"the app has no .NET runtime to load the managed DevTools agent into (Native AOT)";
        // Reactor in diagnostics mode publishes each control's metadata on the control itself; a describe reads it.
        if (status.present) status.annotations = L"published";
    } else if (DevToolsBindingRelay_Probe(&json) && DevToolsJsonParse(json, answer) && answer.IsObject()) {
        const std::wstring state = answer.GetString(L"state");
        status.version = answer.GetString(L"version");
        status.api = answer.GetString(L"api");
        status.reason = answer.GetString(L"reason");
        if (const DevToolsJson* sourceMap = answer.Find(L"sourceMap"); sourceMap && sourceMap->type == DevToolsJsonType::Bool)
            status.sourceMap = sourceMap->b ? 1 : 0;
        status.present = state == L"ok" || state == L"unavailable";
        status.agent = state == L"unavailable" ? L"unavailable" : L"ok";
        if (status.agent == L"ok") status.annotations = L"agent";
        if (state == L"absent") status.reason.clear();
    } else {
        status.present = TapReactorDeployed();
        status.agent = L"unavailable";
        DevToolsJson failure;
        status.reason = DevToolsJsonParse(json, failure) && failure.IsObject() ? failure.GetString(L"reason")
                                                                            : L"the managed DevTools agent did not answer";
    }
    g_reactorStatus = status;
    g_reactorStatusAt = now;
    return status;
}

// Whether the app may use Reactor: true until the first probe answers, then what it said. Lets UI-thread callers
// skip refreshing the Reactor cache in apps known not to use it.
static bool TapReactorMaybePresent()
{
    std::lock_guard<std::mutex> lock(g_reactorStatusLock);
    return !g_reactorStatusAt || g_reactorStatus.present;
}

// The `reactor` object of DevTools.negotiate and VisualTree.enumerate, without its closing brace so a caller can
// append the description.
static std::wstring TapReactorStatusJson(const TapReactorStatus& status)
{
    std::wstring out = L"{\"present\":";
    out += status.present ? L"true" : L"false";
    out += L",\"agent\":\"" + status.agent + L"\"";
    if (!status.version.empty()) out += L",\"version\":\"" + DevToolsJsonEscape(status.version) + L"\"";
    if (!status.api.empty()) out += L",\"api\":\"" + DevToolsJsonEscape(status.api) + L"\"";
    if (status.present && !status.annotations.empty()) out += L",\"annotations\":\"" + status.annotations + L"\"";
    if (status.sourceMap >= 0) out += status.sourceMap ? L",\"sourceMap\":true" : L",\"sourceMap\":false";
    if (status.present && status.agent != L"ok" && !status.reason.empty())
        out += L",\"reason\":\"" + DevToolsJsonEscape(status.reason) + L"\"";
    return out;
}

// What the last description said about each census element a Reactor element produced, with the wire generation it
// was seen at so a reused slot is never mistaken for a Reactor element. Keyed by the element's diagnostics handle,
// which is its COM identity for as long as it lives. The app-authored classifier, the overlay, the inspector window,
// Source.get and VisualTree.find all read this one cache, so they cannot disagree about where an element came from.
struct ReactorAnnotation
{
    unsigned long long wire = 0;
    std::wstring element;           // the Reactor element kind: Button, VStack, Component ...
    std::wstring key;
    std::wstring file;              // the compile-time C# path Reactor recorded
    unsigned int line = 0;
    unsigned int column = 0;
    std::wstring component;         // the component this element mounts, if any
    std::wstring componentKind;
    std::wstring owner;             // the component whose render created it (nearest component above, or host root)
    std::wstring declaredOwner;     // the owner Reactor published on the control, which wins over the derived one
    InstanceHandle parent = 0;      // the nearest Reactor element above it
    std::wstring name;              // the C# variable or member the element was assigned to
    std::wstring nameSource;        // csharp-variable (Reactor published it) or csharp-source-text (read from the line)
    std::vector<DevToolsReactorPublished::Hook> hooks; // a component element's hooks, when Reactor published them
    bool host = false;              // a host's root control
    std::wstring hostKind, windowId, windowKey, title, rootName, rootKind;
    HWND window = nullptr;
    std::wstring mountFile;         // where the host was mounted (ReactorApp.Run / OpenWindow / Mount)
    unsigned int mountLine = 0, mountColumn = 0;
};

static SRWLOCK g_reactorAuthoredLock = SRWLOCK_INIT;
static std::map<InstanceHandle, ReactorAnnotation> g_reactorAnnotations;
static std::map<unsigned long long, InstanceHandle> g_reactorByWire;
// Bumped whenever the set changes, so the inspector reclassifies only when there is something new.
static std::atomic<unsigned long long> g_reactorAuthoredVersion{0};
// Bumped on every description stored, so per-element answers derived from the cache (marker anchors) are recomputed.
static std::atomic<unsigned long long> g_reactorAnnotationGen{0};
// The census epoch (bumped on every tree mutation) the cache was last described at, and whether that description
// covered every window. A request at the same epoch reuses the cache instead of asking the agent again.
static unsigned long long g_reactorCacheEpoch = ~0ull;
static bool g_reactorCacheWhole = false;

// The inspector window's "Just my code" question for one row.
static bool TapReactorAuthoredWire(InstanceHandle wire)
{
    AcquireSRWLockShared(&g_reactorAuthoredLock);
    const bool authored = g_reactorByWire.count(static_cast<unsigned long long>(wire)) != 0;
    ReleaseSRWLockShared(&g_reactorAuthoredLock);
    return authored;
}

static void TapReactorAddAuthored(const CensusSnapshot& cs, std::set<InstanceHandle>& authored)
{
    AcquireSRWLockShared(&g_reactorAuthoredLock);
    for (const auto& [ih, annotation] : g_reactorAnnotations) {
        const auto current = cs.wire.find(ih);
        if (current != cs.wire.end() && current->second == annotation.wire && cs.type.count(ih)) authored.insert(ih);
    }
    ReleaseSRWLockShared(&g_reactorAuthoredLock);
}

// The annotation for one element (`nearest` false), or for the nearest element at or above it that a Reactor element
// produced. `exact` says whether it is the element's own. Reads the cache only, so it is safe on the UI thread.
static bool TapReactorAnnotationFor(InstanceHandle ih, bool nearest, ReactorAnnotation* out,
                                    bool* exact = nullptr, InstanceHandle* found = nullptr, bool requireFile = false)
{
    if (!ih) return false;
    std::vector<std::pair<InstanceHandle, unsigned long long>> chain;
    {
        CensusSharedLock lock;
        for (InstanceHandle current = ih; current && chain.size() < (nearest ? 512u : 1u);) {
            chain.emplace_back(current, PackWire_nolock(current));
            const auto parent = g_parent.find(current);
            current = parent == g_parent.end() ? 0 : parent->second;
        }
    }
    AcquireSRWLockShared(&g_reactorAuthoredLock);
    bool hit = false;
    for (size_t i = 0; i < chain.size(); ++i) {
        const auto entry = g_reactorAnnotations.find(chain[i].first);
        if (entry == g_reactorAnnotations.end() || !chain[i].second || entry->second.wire != chain[i].second) continue;
        if (requireFile && entry->second.file.empty()) continue;
        if (out) *out = entry->second;
        if (exact) *exact = i == 0;
        if (found) *found = chain[i].first;
        hit = true;
        break;
    }
    ReleaseSRWLockShared(&g_reactorAuthoredLock);
    return hit;
}

// The C# name of the element's own Reactor element, or empty. Reads the cache only, so it is safe on the UI thread.
static std::wstring TapReactorNameOf(InstanceHandle h)
{
    ReactorAnnotation a;
    return TapReactorAnnotationFor(h, false, &a) ? a.name : std::wstring();
}

// The same lookup by wire handle, for callers that hold one.
static bool TapReactorAnnotationForWire(unsigned long long wire, bool nearest, ReactorAnnotation* out,
                                        bool* exact = nullptr, InstanceHandle* found = nullptr)
{
    InstanceHandle ih = 0;
    {
        CensusSharedLock lock;
        const wchar_t* error = nullptr;
        if (!ResolveWire_nolock(wire, &ih, &error)) return false;
    }
    return TapReactorAnnotationFor(ih, nearest, out, exact, found);
}

// The short form a tree line or a panel header shows: `App.cs:35`.
static std::wstring TapReactorShortSource(const ReactorAnnotation& a)
{
    if (a.file.empty()) return {};
    const size_t slash = a.file.find_last_of(L"\\/");
    std::wstring leaf = slash == std::wstring::npos ? a.file : a.file.substr(slash + 1);
    return a.line ? leaf + L":" + std::to_wstring(a.line) : leaf;
}

// The label a row or a header shows for a Reactor element: `<Counter>` for a component, its element kind otherwise.
static std::wstring TapReactorLabel(const ReactorAnnotation& a)
{
    if (!a.component.empty()) return L"<" + a.component + L">";
    if (a.host && !a.rootName.empty()) return L"<" + a.rootName + L">";
    return a.element;
}

// Defined below; reads an environment variable the launch set.
static std::wstring ReactorEnvironment(const wchar_t* name);
// Defined below; where a compile-time C# path is on this machine.
static bool TapReactorMapFile(const std::wstring& file, std::wstring* onDisk);

// One Reactor root a surface shows, for Surface.list: the host, its root component and where it was mounted.
static std::wstring TapReactorSurfaceJson(const ReactorAnnotation& host)
{
    std::wstring out = L"{\"kind\":\"" + DevToolsJsonEscape(host.hostKind.empty() ? std::wstring(L"host") : host.hostKind) + L"\"";
    out += L",\"rootHandle\":\"" + std::to_wstring(host.wire) + L"\"";
    if (!host.windowId.empty()) out += L",\"windowId\":\"" + DevToolsJsonEscape(host.windowId) + L"\"";
    if (!host.windowKey.empty()) out += L",\"windowKey\":\"" + DevToolsJsonEscape(host.windowKey) + L"\"";
    if (!host.rootName.empty()) {
        const std::wstring kind = host.rootKind.empty() ? std::wstring(L"class") : host.rootKind;
        out += L",\"root\":{\"kind\":\"" + DevToolsJsonEscape(kind) + L"\",\"name\":\"" + DevToolsJsonEscape(host.rootName) + L"\"}";
    }
    if (!host.mountFile.empty()) {
        std::wstring path;
        TapReactorMapFile(host.mountFile, &path);
        out += L",\"mountFile\":\"" + DevToolsJsonEscape(path) + L"\"";
        if (host.mountLine) out += L",\"mountLine\":" + std::to_wstring(host.mountLine);
        if (host.mountColumn) out += L",\"mountColumn\":" + std::to_wstring(host.mountColumn);
    }
    return out + L"}";
}

// A copy of the cache keyed by wire, for one request that looks up many elements.
static std::map<unsigned long long, ReactorAnnotation> TapReactorSnapshotByWire();

// Labels the inspector window's rows from the cache: a component row reads `<Counter>`, an element whose kind
// differs from its WinUI type reads the kind, and each carries its call site.
static void TapReactorLabelNodes(std::vector<DevToolsWindowNode>& nodes)
{
    const auto reactor = TapReactorSnapshotByWire();
    if (reactor.empty()) return;
    for (auto& node : nodes) {
        const auto annotation = reactor.find((unsigned long long)node.handle);
        if (annotation == reactor.end()) continue;
        const ReactorAnnotation& a = annotation->second;
        const std::wstring label = TapReactorLabel(a);
        const size_t dot = node.type.find_last_of(L'.');
        const std::wstring shortType = dot == std::wstring::npos ? node.type : node.type.substr(dot + 1);
        if (label != shortType) node.reactorLabel = label;
        node.reactorSource = TapReactorShortSource(a);
        if (!a.key.empty()) node.reactorSource = L"key=" + a.key + (node.reactorSource.empty() ? L"" : L"  " + node.reactorSource);
        // The C# variable reads like an x:Name in the row, its filter (`#name`) and the breadcrumb.
        if (node.name.empty() && !a.name.empty()) node.name = a.name;
    }
}

static std::map<unsigned long long, ReactorAnnotation> TapReactorSnapshotByWire()
{
    std::map<unsigned long long, ReactorAnnotation> out;
    AcquireSRWLockShared(&g_reactorAuthoredLock);
    for (const auto& entry : g_reactorAnnotations) out.emplace(entry.second.wire, entry.second);
    ReleaseSRWLockShared(&g_reactorAuthoredLock);
    return out;
}

// What a search matches a Reactor element on: `<Counter>`, the element kind, `#name`, `key=<k>` and `App.cs:35`.
static std::wstring TapReactorSearchText(const ReactorAnnotation& a)
{
    std::wstring text = a.element;
    if (!a.name.empty()) text += L" #" + a.name;
    if (!a.component.empty()) text += L" <" + a.component + L">";
    if (a.host && !a.rootName.empty()) text += L" <" + a.rootName + L">";
    if (!a.key.empty()) text += L" key=" + a.key;
    const std::wstring site = TapReactorShortSource(a);
    if (!site.empty()) text += L" " + site;
    return text;
}

// The `reactor` member of a VisualTree.find match.
static std::wstring TapReactorMatchJson(const ReactorAnnotation& a)
{
    std::wstring out = L"{\"element\":\"" + DevToolsJsonEscape(a.element) + L"\"";
    const std::wstring component = !a.component.empty() ? a.component : a.host ? a.rootName : std::wstring();
    if (!component.empty()) out += L",\"component\":\"" + DevToolsJsonEscape(component) + L"\"";
    if (!a.owner.empty()) out += L",\"owner\":\"" + DevToolsJsonEscape(a.owner) + L"\"";
    if (!a.key.empty()) out += L",\"key\":\"" + DevToolsJsonEscape(a.key) + L"\"";
    if (!a.name.empty())
        out += L",\"name\":\"" + DevToolsJsonEscape(a.name) + L"\",\"nameSource\":\"" + a.nameSource + L"\"";
    if (!a.file.empty()) {
        out += L",\"file\":\"" + DevToolsJsonEscape(a.file) + L"\"";
        if (a.line) out += L",\"line\":" + std::to_wstring(a.line);
    }
    return out + L"}";
}

static bool ReactorParseIdentity(const std::wstring& text, uintptr_t* out)
{
    if (text.size() < 3 || text.size() > 2 + sizeof(uintptr_t) * 2 || text[0] != L'0' || text[1] != L'x') return false;
    uintptr_t value = 0;
    for (size_t i = 2; i < text.size(); i++) {
        const wchar_t c = text[i];
        unsigned digit;
        if (c >= L'0' && c <= L'9') digit = c - L'0';
        else if (c >= L'a' && c <= L'f') digit = c - L'a' + 10;
        else return false;
        value = (value << 4) | digit;
    }
    *out = value;
    return value != 0;
}

struct ReactorMapped
{
    InstanceHandle ih = 0;
    unsigned long long wire = 0;
    HWND window = nullptr;
    bool published = false;                     // the agent described it and Reactor also published on it
    DevToolsReactorPublished::Record record;
};

// UI thread only: each census element's COM identity, remembered with the wire generation it was read at, so a
// describe asks QueryInterface only of elements it has not seen before rather than of the whole census every time.
static std::map<InstanceHandle, std::pair<unsigned long long, uintptr_t>> g_reactorIdentity_ui;

static uintptr_t ReactorIdentity_ui(InstanceHandle ih, unsigned long long wire)
{
    const auto known = g_reactorIdentity_ui.find(ih);
    if (known != g_reactorIdentity_ui.end() && known->second.first == wire) return known->second.second;
    winrt::com_ptr<IInspectable> insp;
    if (!wire || !g_diag || FAILED(g_diag->GetIInspectableFromHandle(ih, insp.put())) || !insp) return 0;
    ::IUnknown* identity = nullptr;
    if (FAILED(insp->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&identity))) || !identity) return 0;
    const auto key = reinterpret_cast<uintptr_t>(identity);
    identity->Release();
    g_reactorIdentity_ui[ih] = { wire, key };
    return key;
}

// Every census element in the subtree under `root` (inclusive). Caller holds the census lock.
static void ReactorSubtree_nolock(InstanceHandle root, std::set<InstanceHandle>& out)
{
    std::vector<InstanceHandle> stack{ root };
    while (!stack.empty() && out.size() < 200000) {
        const InstanceHandle h = stack.back();
        stack.pop_back();
        if (!out.insert(h).second) continue;
        const auto kids = g_children.find(h);
        if (kids != g_children.end()) stack.insert(stack.end(), kids->second.begin(), kids->second.end());
    }
}

// ---- Published Reactor diagnostics (DevToolsReactorPublished) ----
// With no managed agent (Native AOT, trimmed builds, late attach), Reactor in diagnostics mode publishes each
// control's element kind, key, call site and owner as an attached string property. The tap reads it through
// XamlDiagnostics like any other property and describes the elements exactly as the agent would.

// The published property's diagnostics index. A custom property keeps its index for the life of the process, so it
// is found once from a property chain. Not found means this Reactor publishes nothing, or has not registered the
// property yet (no Reactor control realized); looking costs a few property chains, so it is retried only now and then.
static std::atomic<unsigned int> g_reactorPublishedIndex_ui{0xFFFFFFFF}; // written on the UI thread only
static ULONGLONG g_reactorPublishedLookedAt_ui = 0;

static unsigned int TapReactorPublishedIndex_ui(const std::vector<std::pair<InstanceHandle, unsigned long long>>& census)
{
    if (g_reactorPublishedIndex_ui != 0xFFFFFFFF || !g_vts3) return g_reactorPublishedIndex_ui;
    static const ULONGLONG kRetryMs = 5000;
    const ULONGLONG now = GetTickCount64();
    if (g_reactorPublishedLookedAt_ui && now - g_reactorPublishedLookedAt_ui < kRetryMs) return g_reactorPublishedIndex_ui;
    g_reactorPublishedLookedAt_ui = now;
    // A registered attached property is in every element's chain, so a few elements settle it.
    size_t tried = 0;
    for (const auto& entry : census) {
        if (tried++ == 8) break;
        unsigned int sc = 0, pc = 0;
        PropertyChainSource* s = nullptr; PropertyChainValue* v = nullptr;
        if (SUCCEEDED(ReadPropChain(entry.first, &sc, &s, &pc, &v)) && v) {
            for (unsigned int i = 0; i < pc; i++) {
                if (v[i].PropertyName && DevToolsReactorPublished::IsPropertyName(v[i].PropertyName)) {
                    g_reactorPublishedIndex_ui = v[i].Index;
                    break;
                }
            }
        }
        FreePropChain(sc, s, pc, v);
        if (g_reactorPublishedIndex_ui != 0xFFFFFFFF) break;
    }
    return g_reactorPublishedIndex_ui;
}

// What was last read from each element: the value's diagnostics handle and the record it parsed to. XamlDiagnostics
// hands out one handle per value object and keeps it for the object's life, so an unchanged value reads back the same
// handle and is not converted or parsed again. Entries for elements that left the census are dropped.
struct ReactorPublishedRead
{
    unsigned long long wire = 0;
    InstanceHandle value = 0;
    bool parsed = false;
    DevToolsReactorPublished::Record record;
};
static std::map<InstanceHandle, ReactorPublishedRead> g_reactorPublished_ui;
static size_t g_reactorPublishedReads_ui = 0, g_reactorPublishedParsed_ui = 0; // since the last describe, for the log

// UI thread: the record Reactor published on one element. `malformed` counts values that did not parse.
static bool TapReactorReadPublished_ui(InstanceHandle ih, unsigned long long wire, unsigned int index,
                                       DevToolsReactorPublished::Record* out, size_t* malformed)
{
    if (index == 0xFFFFFFFF || !g_vts3 || !g_diag) return false;
    InstanceHandle valueHandle = 0;
    if (FAILED(g_vts3->GetProperty(ih, index, &valueHandle)) || !valueHandle) {
        g_reactorPublished_ui.erase(ih);
        return false;
    }
    auto& known = g_reactorPublished_ui[ih];
    ++g_reactorPublishedReads_ui;
    if (known.wire != wire || known.value != valueHandle) {
        ++g_reactorPublishedParsed_ui;
        known = ReactorPublishedRead{ wire, valueHandle };
        winrt::com_ptr<IInspectable> value;
        std::wstring text;
        if (SUCCEEDED(g_diag->GetIInspectableFromHandle(valueHandle, value.put())) && value &&
            ReadInspectableValue(value.get(), DevToolsReactorPublished::kPropertyLeaf, L"Windows.Foundation.String",
                                 &text, nullptr) == S_OK && !text.empty()) {
            known.parsed = DevToolsReactorPublished::Parse(text, &known.record);
            if (!known.parsed && malformed) ++*malformed;
        }
    }
    if (!known.parsed) return false;
    *out = known.record;
    return true;
}

// Forget what was read from elements no longer in the census. UI thread; `live` is the whole census.
static void TapReactorForgetPublished_ui(const std::vector<std::pair<InstanceHandle, unsigned long long>>& live)
{
    std::set<InstanceHandle> alive;
    for (const auto& entry : live) alive.insert(entry.first);
    for (auto it = g_reactorPublished_ui.begin(); it != g_reactorPublished_ui.end();) {
        if (alive.count(it->first)) ++it;
        else it = g_reactorPublished_ui.erase(it);
    }
}

// `"hooks":[{"name":"count","index":0,"line":68},...]`, or nothing when there are none. A hook whose index is not
// known statically has no `index`.
static std::wstring TapReactorHooksJson(const std::vector<DevToolsReactorPublished::Hook>& hooks)
{
    if (hooks.empty()) return {};
    std::wstring out = L",\"hooks\":[";
    for (size_t i = 0; i < hooks.size(); ++i) {
        if (i) out += L",";
        out += L"{\"name\":\"" + DevToolsJsonEscape(hooks[i].name) + L"\"";
        if (hooks[i].indexed) out += L",\"index\":" + std::to_wstring(hooks[i].index);
        if (hooks[i].line) out += L",\"line\":" + std::to_wstring(hooks[i].line);
        out += L"}";
    }
    return out + L"]";
}

static std::vector<DevToolsReactorPublished::Hook> TapReactorHooksFrom(const DevToolsJson* hooks)
{
    std::vector<DevToolsReactorPublished::Hook> out;
    if (!hooks || hooks->type != DevToolsJsonType::Array) return out;
    for (const auto& h : hooks->arr) {
        if (!h.IsObject()) continue;
        DevToolsReactorPublished::Hook hook;
        const DevToolsJson* index = h.Find(L"index");
        hook.indexed = index && index->type == DevToolsJsonType::Number && index->num >= 0;
        hook.index = hook.indexed ? static_cast<unsigned int>(index->num) : 0;
        hook.name = h.GetString(L"name");
        hook.line = static_cast<unsigned int>(h.GetInt(L"line", 0));
        if (!hook.name.empty()) out.push_back(std::move(hook));
    }
    return out;
}

// UI thread: one answer in the managed agent's `reactor` shape, built from the published property of every census
// element in scope (the -w window, or `scope` when it is set).
static std::wstring TapReactorPublishedAnswer_ui(const std::set<InstanceHandle>* scope)
{
    std::vector<std::pair<InstanceHandle, unsigned long long>> census;
    std::map<InstanceHandle, InstanceHandle> parents;
    AcquireSRWLockShared(&g_censusLock);
    for (const auto& kv : g_ihToSlot) {
        if (g_windowScope && !g_windowScope->members.count(kv.first)) continue;
        if (scope && !scope->count(kv.first)) continue;
        census.emplace_back(kv.first, PackWire_nolock(kv.first));
        const auto parent = g_parent.find(kv.first);
        if (parent != g_parent.end()) parents[kv.first] = parent->second;
    }
    ReleaseSRWLockShared(&g_censusLock);
    // The census is a hash map; walk it in handle order so answers do not depend on its layout.
    std::sort(census.begin(), census.end());

    const unsigned int index = TapReactorPublishedIndex_ui(census);
    if (index == 0xFFFFFFFF || !g_diag) {
        static const bool asked = ReactorEnvironment(L"REACTOR_DIAGNOSTICS") == L"1";
        return asked
            ? L"{\"state\":\"unavailable\",\"reason\":\"no managed DevTools agent is loaded, and this Reactor version does not "
              L"publish its diagnostics on the controls (Native AOT and trimmed builds need a Reactor that does)\"}"
            : L"{\"state\":\"unavailable\",\"reason\":\"no managed DevTools agent is loaded, and Reactor publishes its diagnostics "
              L"on the controls only when the app is launched with `winapp run --devtools on` (REACTOR_DIAGNOSTICS=1)\"}";
    }
    if (!scope && !g_windowScope) TapReactorForgetPublished_ui(census);
    std::map<InstanceHandle, DevToolsReactorPublished::Record> records;
    size_t malformed = 0;
    for (const auto& [ih, wire] : census) {
        DevToolsReactorPublished::Record record;
        if (TapReactorReadPublished_ui(ih, wire, index, &record, &malformed)) records.emplace(ih, std::move(record));
    }
    if (malformed) Log(L"reactor.published: %zu value(s) did not parse", malformed);
    Log(L"reactor.published: read %zu, parsed %zu new value(s), %zu remembered", g_reactorPublishedReads_ui,
        g_reactorPublishedParsed_ui, g_reactorPublished_ui.size());
    g_reactorPublishedReads_ui = g_reactorPublishedParsed_ui = 0;

    std::map<InstanceHandle, uintptr_t> identities;
    for (const auto& [ih, wire] : census) {
        if (!records.count(ih)) continue;
        if (const uintptr_t id = ReactorIdentity_ui(ih, wire)) identities[ih] = id;
    }
    // A host's root control (`root=`) is an island when a ReactorHostControl in XAML holds it, a window otherwise.
    std::set<InstanceHandle> islands;
    {
        CensusSharedLock lock;
        for (const auto& [ih, record] : records) {
            if (record.root.empty()) continue;
            InstanceHandle up = ih;
            for (int guard = 0; up && guard < 64; ++guard) {
                const auto type = g_type.find(up);
                if (type != g_type.end() && type->second.find(L"ReactorHostControl") != std::wstring::npos) { islands.insert(ih); break; }
                const auto parent = g_parent.find(up);
                up = parent == g_parent.end() ? 0 : parent->second;
            }
        }
    }

    std::map<std::wstring, size_t> fileIndex;
    std::vector<std::wstring> files;
    std::wstring nodes;
    bool sourceMap = false;
    for (const auto& [ih, wire] : census) {
        const auto record = records.find(ih);
        const auto identity = identities.find(ih);
        if (record == records.end() || identity == identities.end()) continue;
        wchar_t hex[2 + sizeof(uintptr_t) * 2 + 1];
        swprintf_s(hex, L"0x%llx", static_cast<unsigned long long>(identity->second));
        if (!nodes.empty()) nodes += L",";
        nodes += L"{\"id\":\"" + std::wstring(hex) + L"\"";
        // The Reactor parent is the nearest published element above, as the agent's tag walk reports it.
        InstanceHandle up = 0;
        if (const auto p = parents.find(ih); p != parents.end()) up = p->second;
        for (int guard = 0; up && !identities.count(up) && guard < 4096; ++guard) {
            const auto p = parents.find(up);
            up = p == parents.end() ? 0 : p->second;
        }
        if (const auto parent = identities.find(up); up && parent != identities.end()) {
            swprintf_s(hex, L"0x%llx", static_cast<unsigned long long>(parent->second));
            nodes += L",\"parent\":\"" + std::wstring(hex) + L"\"";
        }
        const auto& r = record->second;
        nodes += L",\"element\":\"" + DevToolsJsonEscape(r.element) + L"\"";
        if (!r.key.empty()) nodes += L",\"key\":\"" + DevToolsJsonEscape(r.key) + L"\"";
        if (!r.file.empty()) {
            auto known = fileIndex.find(r.file);
            if (known == fileIndex.end()) { known = fileIndex.emplace(r.file, files.size()).first; files.push_back(r.file); }
            nodes += L",\"file\":" + std::to_wstring(known->second) + L",\"line\":" + std::to_wstring(r.line);
            if (r.column) nodes += L",\"column\":" + std::to_wstring(r.column);
            sourceMap = true;
        }
        if (!r.mounts.empty()) nodes += L",\"component\":{\"kind\":\"class\",\"name\":\"" + DevToolsJsonEscape(r.mounts) + L"\"}";
        if (!r.root.empty())
            nodes += L",\"host\":{\"kind\":\"" + std::wstring(islands.count(ih) ? L"island" : L"window") +
                     L"\",\"root\":{\"kind\":\"class\",\"name\":\"" + DevToolsJsonEscape(r.root) + L"\"}}";
        if (!r.owner.empty()) nodes += L",\"owner\":\"" + DevToolsJsonEscape(r.owner) + L"\"";
        if (!r.name.empty()) nodes += L",\"name\":\"" + DevToolsJsonEscape(r.name) + L"\"";
        nodes += TapReactorHooksJson(r.hooks);
        nodes += L"}";
    }
    std::wstring out = L"{\"state\":\"ok\",\"sourceMap\":";
    out += sourceMap ? L"true" : L"false";
    out += L",\"nodes\":[" + nodes + L"],\"files\":[";
    for (size_t i = 0; i < files.size(); i++) {
        if (i) out += L",";
        out += L"\"" + DevToolsJsonEscape(files[i]) + L"\"";
    }
    return out + L"],\"truncated\":false}";
}

// Describe the Reactor elements in the current scope (the whole process, the window -w named, or the subtree under
// `subtree`) and refresh the annotation cache. On success `members` holds the
// `"nodes":[...],"files":[...],"unmapped":n,"truncated":b` members and `status` the version and source-map state the
// agent reported; on failure `status` says why. `complete` is false when a root did not answer or the agent reported
// elements the census does not hold yet (a window that is still opening): the description then misses elements.
static bool TapReactorDescribeOnce(TapReactorStatus& status, std::wstring* members, InstanceHandle subtree, bool* complete)
{
    *complete = true;
    DevToolsBindingApartment apartment;
    if (FAILED(apartment.result)) { status.agent = L"unavailable"; status.reason = L"internal: " + HrStr(apartment.result); return false; }
    const unsigned long long epoch = g_treeEpoch.load(std::memory_order_acquire);
    const bool whole = !g_windowScope && !subtree;

    auto roots = std::make_shared<std::vector<winrt::com_ptr<IAgileReference>>>();
    auto scope = std::make_shared<std::set<InstanceHandle>>();
    HRESULT hr = RunOnUiThread([roots, scope, subtree]() -> HRESULT {
        DevToolsBindingRelay_NoteUiThread();
        std::vector<InstanceHandle> top;
        AcquireSRWLockShared(&g_censusLock);
        if (subtree) {
            if (g_ihToSlot.count(subtree) && (!g_windowScope || g_windowScope->members.count(subtree))) {
                top.push_back(subtree);
                ReactorSubtree_nolock(subtree, *scope);
            }
        } else {
            for (const auto& kv : g_ihToSlot) {
                const auto parent = g_parent.find(kv.first);
                if (parent != g_parent.end() && parent->second && g_ihToSlot.count(parent->second)) continue;
                // A XAML island's source (or the island itself) is not a DependencyObject; the trees the agent walks
                // are its children.
                const auto type = g_type.find(kv.first);
                if (type != g_type.end() && (type->second.find(L"DesktopWindowXamlSource") != std::wstring::npos ||
                                             type->second == L"Microsoft.UI.Xaml.XamlIsland")) {
                    const auto kids = g_children.find(kv.first);
                    if (kids != g_children.end()) top.insert(top.end(), kids->second.begin(), kids->second.end());
                    continue;
                }
                top.push_back(kv.first);
            }
            std::sort(top.begin(), top.end());
        }
        ReleaseSRWLockShared(&g_censusLock);
        for (InstanceHandle ih : top) {
            if (OverlayContains(ih)) continue;
            // -w: only that window's roots (its content and the popups open over it).
            if (g_windowScope && !g_windowScope->members.count(ih)) continue;
            winrt::com_ptr<IInspectable> insp;
            if (!g_diag || FAILED(g_diag->GetIInspectableFromHandle(ih, insp.put())) || !insp) continue;
            winrt::com_ptr<IAgileReference> agile;
            if (SUCCEEDED(RoGetAgileReference(AGILEREFERENCE_DEFAULT, __uuidof(IInspectable), insp.get(), agile.put())))
                roots->push_back(std::move(agile));
        }
        return S_OK;
    });
    if (FAILED(hr)) { status.agent = L"unavailable"; status.reason = L"the app's UI thread did not answer (" + HrStr(hr) + L")"; return false; }
    if (subtree && roots->empty()) { status.agent = L"unavailable"; status.reason = L"the element is not in the current scope"; return false; }

    std::vector<DevToolsJson> answers;
    std::wstring failure;
    bool absent = !roots->empty();
    size_t failedRoots = 0;
    const bool published = status.agent != L"ok" && status.annotations == L"published";
    if (published) {
        auto answer = std::make_shared<std::wstring>();
        const ULONGLONG started = GetTickCount64();
        hr = RunOnUiThread([answer, scope, subtree]() -> HRESULT {
            *answer = TapReactorPublishedAnswer_ui(subtree ? scope.get() : nullptr);
            return S_OK;
        });
        if (FAILED(hr)) { status.reason = L"the app's UI thread did not answer (" + HrStr(hr) + L")"; return false; }
        DevToolsJson parsed;
        if (!DevToolsJsonParse(*answer, parsed) || !parsed.IsObject() || parsed.GetString(L"state") != L"ok") {
            if (parsed.IsObject()) status.reason = parsed.GetString(L"reason");
            return false;
        }
        Log(L"reactor.published: described in %llu ms", GetTickCount64() - started);
        absent = false;
        answers.push_back(std::move(parsed));
    }
    for (const auto& root : *roots) {
        if (published) break;
        std::wstring json;
        DevToolsBindingRelay_Binding(L"reactor", root.get(), L"", L"", &json);
        DevToolsJson parsed;
        if (!DevToolsJsonParse(json, parsed) || !parsed.IsObject()) { failedRoots++; continue; }
        const std::wstring answerState = parsed.GetString(L"state");
        if (answerState != L"absent") absent = false;
        if (answerState == L"ok") answers.push_back(std::move(parsed));
        else if (answerState != L"absent") {
            failedRoots++;
            if (answerState == L"unavailable" && failure.empty()) failure = parsed.GetString(L"reason");
        }
    }
    if (answers.empty() && (absent || !failure.empty())) {
        if (absent) { status.present = false; status.agent = L"ok"; status.reason.clear(); }
        else { status.agent = L"unavailable"; status.reason = failure; }
        return false;
    }
    if (!answers.empty()) {
        if (const std::wstring version = answers.front().GetString(L"version"); !version.empty()) status.version = version;
        if (const DevToolsJson* sourceMap = answers.front().Find(L"sourceMap"); sourceMap && sourceMap->type == DevToolsJsonType::Bool)
            status.sourceMap = sourceMap->b ? 1 : 0;
    }

    auto wanted = std::make_shared<std::set<uintptr_t>>();
    auto hosts = std::make_shared<std::set<uintptr_t>>();
    for (const auto& answer : answers) {
        if (const DevToolsJson* nodes = answer.Find(L"nodes"); nodes && nodes->type == DevToolsJsonType::Array) {
            for (const auto& node : nodes->arr) {
                uintptr_t id = 0;
                if (!ReactorParseIdentity(node.GetString(L"id"), &id)) continue;
                wanted->insert(id);
                if (const DevToolsJson* host = node.Find(L"host"); host && host->IsObject()) hosts->insert(id);
            }
        }
    }

    auto mapped = std::make_shared<std::map<uintptr_t, ReactorMapped>>();
    if (!wanted->empty()) {
        hr = RunOnUiThread([wanted, hosts, mapped, scope, subtree, published]() -> HRESULT {
            std::vector<std::pair<InstanceHandle, unsigned long long>> census;
            AcquireSRWLockShared(&g_censusLock);
            census.reserve(subtree ? scope->size() : g_ihToSlot.size());
            for (const auto& kv : g_ihToSlot) {
                if (g_windowScope && !g_windowScope->members.count(kv.first)) continue;
                if (subtree && !scope->count(kv.first)) continue;
                census.emplace_back(kv.first, PackWire_nolock(kv.first));
            }
            ReleaseSRWLockShared(&g_censusLock);
            std::sort(census.begin(), census.end());
            // Forget identities of elements that left the census, so the map tracks the live tree.
            if (!subtree && !g_windowScope) {
                std::set<InstanceHandle> live;
                for (const auto& entry : census) live.insert(entry.first);
                for (auto it = g_reactorIdentity_ui.begin(); it != g_reactorIdentity_ui.end();) {
                    if (live.count(it->first)) ++it;
                    else it = g_reactorIdentity_ui.erase(it);
                }
            }
            for (const auto& [ih, wire] : census) {
                const uintptr_t key = ReactorIdentity_ui(ih, wire);
                if (!key || !wanted->count(key)) continue;
                ReactorMapped entry{ ih, wire, nullptr };
                DevToolsSurface surface;
                if (hosts->count(key) && WindowSurfaceOf_ui(ih, surface)) entry.window = surface.hostHwnd;
                (*mapped)[key] = entry;
            }
            // The agent's description is live; what Reactor published on the controls fills what it lacks (the C#
            // names of elements and hooks).
            if (!published && !mapped->empty()) {
                const unsigned int index = TapReactorPublishedIndex_ui(census);
                if (!subtree && !g_windowScope) TapReactorForgetPublished_ui(census);
                for (auto& [key, entry] : *mapped)
                    entry.published = TapReactorReadPublished_ui(entry.ih, entry.wire, index, &entry.record, nullptr);
                if (index != 0xFFFFFFFF)
                    Log(L"reactor.published: read %zu next to the agent, parsed %zu new value(s), %zu remembered",
                        g_reactorPublishedReads_ui, g_reactorPublishedParsed_ui, g_reactorPublished_ui.size());
                g_reactorPublishedReads_ui = g_reactorPublishedParsed_ui = 0;
            }
            return S_OK;
        });
        if (FAILED(hr)) { status.agent = L"unavailable"; status.reason = L"the app's UI thread did not answer (" + HrStr(hr) + L")"; return false; }
    }

    std::map<std::wstring, size_t> fileIndex;
    std::vector<std::wstring> files;
    std::set<unsigned long long> emitted;
    std::map<InstanceHandle, ReactorAnnotation> annotations;
    size_t unmapped = 0;
    bool truncated = false;
    std::wstring out = L"\"nodes\":[";
    bool first = true;
    auto fileOf = [](const DevToolsJson* answerFiles, const DevToolsJson* index) -> std::wstring {
        if (!index || index->type != DevToolsJsonType::Number || !answerFiles || answerFiles->type != DevToolsJsonType::Array ||
            index->num < 0 || (size_t)index->num >= answerFiles->arr.size() ||
            answerFiles->arr[(size_t)index->num].type != DevToolsJsonType::String) return {};
        return answerFiles->arr[(size_t)index->num].str;
    };
    auto number = [](const DevToolsJson& object, const wchar_t* name) -> unsigned int {
        const DevToolsJson* value = object.Find(name);
        return value && value->type == DevToolsJsonType::Number && value->num > 0 ? (unsigned int)value->num : 0u;
    };
    auto indexOf = [&](const std::wstring& path) {
        auto known = fileIndex.find(path);
        if (known == fileIndex.end()) {
            known = fileIndex.emplace(path, files.size()).first;
            files.push_back(path);
        }
        return known->second;
    };
    // Each element's call site by identity, so a name read from source text can skip an element nested in a call
    // on the same line (`var row = HStack(Text("a"))` names the HStack only).
    std::map<uintptr_t, std::tuple<std::wstring, unsigned int, unsigned int>> sites;
    std::map<uintptr_t, uintptr_t> parentOf;
    for (const auto& answer : answers) {
        const DevToolsJson* nodes = answer.Find(L"nodes");
        if (!nodes || nodes->type != DevToolsJsonType::Array) continue;
        for (const auto& node : nodes->arr) {
            uintptr_t id = 0, parent = 0;
            if (!ReactorParseIdentity(node.GetString(L"id"), &id)) continue;
            sites[id] = { fileOf(answer.Find(L"files"), node.Find(L"file")), number(node, L"line"), number(node, L"column") };
            if (ReactorParseIdentity(node.GetString(L"parent"), &parent)) parentOf[id] = parent;
        }
    }
    std::map<std::wstring, std::pair<bool, std::wstring>> mappedFiles;
    auto nameFromSource = [&](uintptr_t id, const ReactorAnnotation& a) -> std::wstring {
        if (a.file.empty() || !a.line) return {};
        if (!a.column) {
            const auto parent = parentOf.find(id);
            const auto above = parent == parentOf.end() ? sites.end() : sites.find(parent->second);
            if (above != sites.end() && std::get<0>(above->second) == a.file && std::get<1>(above->second) == a.line) return {};
        }
        auto path = mappedFiles.find(a.file);
        if (path == mappedFiles.end()) {
            std::wstring onDisk;
            const bool found = TapReactorMapFile(a.file, &onDisk);
            path = mappedFiles.emplace(a.file, std::make_pair(found, onDisk)).first;
        }
        return path->second.first ? DevToolsAuthored_CSharpName(path->second.second, a.line, a.column, a.element) : std::wstring();
    };
    for (const auto& answer : answers) {
        if (answer.GetBool(L"truncated", false)) truncated = true;
        const DevToolsJson* answerFiles = answer.Find(L"files");
        const DevToolsJson* nodes = answer.Find(L"nodes");
        if (!nodes || nodes->type != DevToolsJsonType::Array) continue;
        for (const auto& node : nodes->arr) {
            uintptr_t id = 0;
            const auto entry = ReactorParseIdentity(node.GetString(L"id"), &id) ? mapped->find(id) : mapped->end();
            if (entry == mapped->end() || !emitted.insert(entry->second.wire).second) { unmapped++; continue; }
            ReactorAnnotation& annotation = annotations[entry->second.ih];
            annotation.wire = entry->second.wire;
            out += first ? L"{" : L",{";
            first = false;
            out += L"\"handle\":\"" + std::to_wstring(entry->second.wire) + L"\"";
            uintptr_t parentId = 0;
            if (ReactorParseIdentity(node.GetString(L"parent"), &parentId)) {
                const auto parent = mapped->find(parentId);
                if (parent != mapped->end()) {
                    out += L",\"parent\":\"" + std::to_wstring(parent->second.wire) + L"\"";
                    annotation.parent = parent->second.ih;
                }
            }
            annotation.element = node.GetString(L"element");
            out += L",\"element\":\"" + DevToolsJsonEscape(annotation.element) + L"\"";
            annotation.key = node.GetString(L"key");
            if (!annotation.key.empty()) out += L",\"key\":\"" + DevToolsJsonEscape(annotation.key) + L"\"";
            annotation.file = fileOf(answerFiles, node.Find(L"file"));
            if (!annotation.file.empty()) {
                out += L",\"file\":" + std::to_wstring(indexOf(annotation.file));
                annotation.line = number(node, L"line");
                annotation.column = number(node, L"column");
                // Reactor versions whose source map records no column can still publish one.
                if (!annotation.column && entry->second.published && entry->second.record.line == annotation.line)
                    annotation.column = entry->second.record.column;
                if (annotation.line) out += L",\"line\":" + std::to_wstring(annotation.line);
                if (annotation.column) out += L",\"column\":" + std::to_wstring(annotation.column);
            }
            annotation.name = node.GetString(L"name");
            annotation.hooks = TapReactorHooksFrom(node.Find(L"hooks"));
            if (entry->second.published) {
                if (annotation.name.empty()) annotation.name = entry->second.record.name;
                if (annotation.hooks.empty()) annotation.hooks = entry->second.record.hooks;
            }
            if (!annotation.name.empty()) annotation.nameSource = L"csharp-variable";
            else if (!(annotation.name = nameFromSource(id, annotation)).empty()) annotation.nameSource = L"csharp-source-text";
            if (!annotation.name.empty()) {
                out += L",\"name\":\"" + DevToolsJsonEscape(annotation.name) + L"\",\"nameSource\":\"" + annotation.nameSource + L"\"";
            }
            if (node.GetBool(L"defaultAutomationName", false)) out += L",\"defaultAutomationName\":true";
            auto component = [&out](const wchar_t* name, const DevToolsJson* c, std::wstring* nameOut, std::wstring* kindOut) {
                if (!c || !c->IsObject()) return;
                *nameOut = c->GetString(L"name");
                *kindOut = c->GetString(L"kind");
                out += L",\"" + std::wstring(name) + L"\":{\"kind\":\"" + DevToolsJsonEscape(*kindOut) +
                    L"\",\"name\":\"" + DevToolsJsonEscape(*nameOut) + L"\"";
                const std::wstring type = c->GetString(L"type");
                if (!type.empty()) out += L",\"type\":\"" + DevToolsJsonEscape(type) + L"\"";
                out += L"}";
            };
            component(L"component", node.Find(L"component"), &annotation.component, &annotation.componentKind);
            annotation.declaredOwner = node.GetString(L"owner");
            if (const DevToolsJson* host = node.Find(L"host"); host && host->IsObject()) {
                annotation.host = true;
                annotation.hostKind = host->GetString(L"kind");
                if (annotation.hostKind.empty()) annotation.hostKind = L"host";
                annotation.window = entry->second.window;
                out += L",\"host\":{\"kind\":\"" + DevToolsJsonEscape(annotation.hostKind) + L"\"";
                if (annotation.window)
                    out += L",\"window\":\"" + std::to_wstring(reinterpret_cast<uintptr_t>(annotation.window)) + L"\"";
                annotation.windowId = host->GetString(L"windowId");
                annotation.windowKey = host->GetString(L"windowKey");
                annotation.title = host->GetString(L"title");
                for (const auto& [member, value] : { std::pair<const wchar_t*, const std::wstring*>{ L"windowId", &annotation.windowId },
                                                     { L"windowKey", &annotation.windowKey }, { L"title", &annotation.title } }) {
                    if (!value->empty()) out += L",\"" + std::wstring(member) + L"\":\"" + DevToolsJsonEscape(*value) + L"\"";
                }
                component(L"root", host->Find(L"root"), &annotation.rootName, &annotation.rootKind);
                if (const DevToolsJson* mount = host->Find(L"mount"); mount && mount->IsObject()) {
                    annotation.mountFile = fileOf(answerFiles, mount->Find(L"file"));
                    if (!annotation.mountFile.empty()) {
                        annotation.mountLine = number(*mount, L"line");
                        annotation.mountColumn = number(*mount, L"column");
                        out += L",\"mount\":{\"file\":" + std::to_wstring(indexOf(annotation.mountFile)) +
                               L",\"line\":" + std::to_wstring(annotation.mountLine);
                        if (annotation.mountColumn) out += L",\"column\":" + std::to_wstring(annotation.mountColumn);
                        out += L"}";
                    }
                }
                out += L"}";
            }
            out += L"}";
        }
    }
    out += L"],\"files\":[";
    for (size_t i = 0; i < files.size(); i++) {
        if (i) out += L",";
        out += L"\"" + DevToolsJsonEscape(files[i]) + L"\"";
    }
    out += L"],\"unmapped\":" + std::to_wstring(unmapped);
    out += truncated ? L",\"truncated\":true" : L",\"truncated\":false";
    *complete = failedRoots == 0 && unmapped == 0;
    out += *complete ? L",\"complete\":true" : L",\"complete\":false";
    if (!*complete) {
        const std::wstring why = failedRoots
            ? std::to_wstring(failedRoots) + L" root(s) did not answer" + (failure.empty() ? std::wstring() : L": " + failure)
            : std::to_wstring(unmapped) + L" Reactor element(s) are not in the element census yet";
        out += L",\"incompleteReason\":\"" + DevToolsJsonEscape(why) + L"\"";
    }

    AcquireSRWLockExclusive(&g_reactorAuthoredLock);
    if (whole && *complete) {
        g_reactorAnnotations.swap(annotations);
    } else if (whole) {
        // A partial description must not erase what the last whole one knew about the windows it missed.
        for (auto& entry : annotations) g_reactorAnnotations[entry.first] = std::move(entry.second);
    } else {
        // A scoped describe replaces only what it looked at, and keeps every other window's elements. Its top elements
        // keep the Reactor parent the last whole description gave them, which lies outside the scope it walked.
        std::map<InstanceHandle, InstanceHandle> outerParents;
        for (auto it = g_reactorAnnotations.begin(); it != g_reactorAnnotations.end();) {
            const bool inScope = subtree ? scope->count(it->first) != 0 : g_windowScope->members.count(it->first) != 0;
            if (inScope) {
                if (it->second.parent) outerParents[it->first] = it->second.parent;
                it = g_reactorAnnotations.erase(it);
            }
            else ++it;
        }
        for (auto& entry : annotations) {
            if (!entry.second.parent) {
                const auto outer = outerParents.find(entry.first);
                if (outer != outerParents.end()) entry.second.parent = outer->second;
            }
            g_reactorAnnotations[entry.first] = std::move(entry.second);
        }
    }
    // The component whose render created each element: the nearest component (or host root) above it.
    for (auto& [ih, annotation] : g_reactorAnnotations) {
        annotation.owner.clear();
        InstanceHandle current = annotation.parent;
        for (int guard = 0; current && guard < 4096; ++guard) {
            const auto parent = g_reactorAnnotations.find(current);
            if (parent == g_reactorAnnotations.end()) break;
            if (!parent->second.component.empty()) { annotation.owner = parent->second.component; break; }
            if (parent->second.host && !parent->second.rootName.empty()) { annotation.owner = parent->second.rootName; break; }
            current = parent->second.parent;
        }
        if (annotation.owner.empty() && annotation.host) annotation.owner = annotation.rootName;
        if (!annotation.declaredOwner.empty()) annotation.owner = annotation.declaredOwner;
    }
    std::map<unsigned long long, InstanceHandle> wires;
    for (const auto& entry : g_reactorAnnotations) wires[entry.second.wire] = entry.first;
    bool changed = wires.size() != g_reactorByWire.size();
    for (auto a = wires.begin(), b = g_reactorByWire.begin(); !changed && a != wires.end(); ++a, ++b) changed = a->first != b->first;
    g_reactorByWire.swap(wires);
    if (whole && *complete) { g_reactorCacheEpoch = epoch; g_reactorCacheWhole = true; }
    else if (g_reactorCacheEpoch != epoch || !*complete) g_reactorCacheWhole = false;
    if (changed) g_reactorAuthoredVersion.fetch_add(1, std::memory_order_acq_rel);
    g_reactorAnnotationGen.fetch_add(1, std::memory_order_acq_rel);
    ReleaseSRWLockExclusive(&g_reactorAuthoredLock);

    status.present = true;
    // Published annotations describe the elements; state, handlers and logs still need the agent.
    if (!published) {
        status.agent = L"ok";
        status.reason.clear();
    }
    if (members) *members = std::move(out);
    return true;
}

// Describes run one at a time (the inspector's background refresh and protocol requests would otherwise compete for
// the agent), and a description taken while the tree was changing is retried before it is reported incomplete.
static std::mutex g_reactorDescribeLock;
static bool TapReactorDescribe(TapReactorStatus& status, std::wstring* members, InstanceHandle subtree)
{
    std::lock_guard<std::mutex> lock(g_reactorDescribeLock);
    constexpr int kAttempts = 3;
    for (int attempt = 1;; ++attempt) {
        bool complete = true;
        const bool ok = TapReactorDescribeOnce(status, members, subtree, &complete);
        if (!ok || complete || attempt == kAttempts) return ok;
        Sleep(150 * attempt);
    }
}

// Refresh the annotation cache before a read that needs it, so "just my code", sources and searches include Reactor
// elements. Apps without Reactor pay only the cached probe, and a tree that has not changed since the last whole
// description is not described again.
static void TapReactorRefreshAuthored()
{
    TapReactorStatus status = TapReactorStatusNow();
    if (!TapReactorCanDescribe(status)) return;
    AcquireSRWLockShared(&g_reactorAuthoredLock);
    const bool current = g_reactorCacheWhole && g_reactorCacheEpoch == g_treeEpoch.load(std::memory_order_acquire);
    ReleaseSRWLockShared(&g_reactorAuthoredLock);
    if (!current) TapReactorDescribe(status, nullptr);
}

// The overlay and the inspector window read the cache on the UI thread, where the agent cannot be asked. Describe on
// a worker and reclassify the window when the Reactor elements changed. At most one refresh runs at a time.
static std::atomic<bool> g_reactorInspectorRefreshing{false};
static bool TapReactorPumpEnsure(std::wstring* reason);
static void TapReactorRefreshInspectorAsync()
{
    if (g_reactorInspectorRefreshing.exchange(true)) return;
    std::thread([]() {
        const auto before = g_reactorAuthoredVersion.load(std::memory_order_acquire);
        TapReactorRefreshAuthored();
        // A Reactor app streams its trace events for the overlay's error badge and highlight updates.
        TapReactorPumpEnsure(nullptr);
        if (g_reactorAuthoredVersion.load(std::memory_order_acquire) != before)
            RunOnUiThread([]() { DevToolsWindow_Reclassify(); return S_OK; });
        g_reactorInspectorRefreshing.store(false, std::memory_order_release);
    }).detach();
}

// Reactor.describe: the same description VisualTree.enumerate embeds, as its own method. `subtree` limits it to
// the elements under one element.
static std::wstring CmdReactorDescribe(InstanceHandle subtree = 0)
{
    TapReactorStatus status = TapReactorStatusNow();
    static const wchar_t* kEmpty = L",\"nodes\":[],\"files\":[],\"unmapped\":0,\"truncated\":false}";
    if (!status.present) return std::wstring(L"{\"state\":\"absent\"") + kEmpty;
    std::wstring members;
    if (TapReactorCanDescribe(status) && TapReactorDescribe(status, &members, subtree)) {
        std::wstring out = L"{\"state\":\"ok\"";
        if (!status.version.empty()) out += L",\"version\":\"" + DevToolsJsonEscape(status.version) + L"\"";
    if (!status.api.empty()) out += L",\"api\":\"" + DevToolsJsonEscape(status.api) + L"\"";
        if (!status.annotations.empty()) out += L",\"annotations\":\"" + status.annotations + L"\"";
        if (status.sourceMap >= 0) out += status.sourceMap ? L",\"sourceMap\":true" : L",\"sourceMap\":false";
        return out + L"," + members + L"}";
    }
    if (!status.present) return std::wstring(L"{\"state\":\"absent\"") + kEmpty;
    std::wstring out = L"{\"state\":\"unavailable\"";
    if (!status.reason.empty()) out += L",\"reason\":\"" + DevToolsJsonEscape(status.reason) + L"\"";
    if (!status.version.empty()) out += L",\"version\":\"" + DevToolsJsonEscape(status.version) + L"\"";
    if (!status.api.empty()) out += L",\"api\":\"" + DevToolsJsonEscape(status.api) + L"\"";
    return out + kEmpty;
}

static std::wstring ReactorEnvironment(const wchar_t* name)
{
    const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (!required) return {};
    std::wstring value(required, L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), required);
    value.resize(written < required ? written : 0);
    return value;
}

// The C# source of an element: its own Reactor call site, or that of the nearest Reactor element containing it (a
// Button's template TextBlock was created by the Button's call site). Reads the cache only, so the overlay and the
// inspector can call it on the UI thread; workers refresh the cache first.
struct ReactorSource
{
    ReactorAnnotation node;     // the Reactor element whose call site this is
    InstanceHandle ih = 0;      // that element
    bool exact = false;         // the element's own call site, not an ancestor's
    std::wstring path;          // the file on this machine, or the recorded path when it is not here
    bool onDisk = false;
    std::vector<std::wstring> ambiguous; // when the recorded path names a file in more than one of the app's projects
    std::wstring code;          // the statement at the call site, secrets redacted
    std::wstring site;          // DevToolsReactorNames::SiteState: "" (holds), "stale" or "pastEnd"
    size_t lineCount = 0;       // lines in the file on disk
};

static bool TapReactorMapFile(const std::wstring& file, std::wstring* onDisk, std::vector<std::wstring>* ambiguous);

static bool TapReactorSourceFor(InstanceHandle ih, ReactorSource* out, bool readCode)
{
    if (!out) return false;
    *out = ReactorSource{};
    if (!TapReactorAnnotationFor(ih, true, &out->node, &out->exact, &out->ih, /*requireFile*/ true)) return false;
    out->onDisk = TapReactorMapFile(out->node.file, &out->path, &out->ambiguous);
    if (readCode && out->onDisk)
        DevToolsAuthored_ReadCSharp(out->path, out->node.line, out->node.column, &out->code, out->node.element, &out->site,
            &out->lineCount);
    return true;
}

// The `reactor` member of a C# Source.get answer (and of the in-app panel), without braces' surroundings.
static std::wstring TapReactorSourceJson(const ReactorSource& source, unsigned int ordinal = 0)
{
    std::wstring out = L"{\"element\":\"" + DevToolsJsonEscape(source.node.element) + L"\"";
    unsigned long long wire = source.node.wire;
    out += L",\"handle\":\"" + std::to_wstring(wire) + L"\"";
    out += source.exact ? L",\"exact\":true" : L",\"exact\":false";
    if (!source.node.key.empty()) out += L",\"key\":\"" + DevToolsJsonEscape(source.node.key) + L"\"";
    if (!source.node.owner.empty()) out += L",\"component\":\"" + DevToolsJsonEscape(source.node.owner) + L"\"";
    if (!source.node.component.empty()) out += L",\"mounts\":\"" + DevToolsJsonEscape(source.node.component) + L"\"";
    if (ordinal) out += L",\"ordinal\":" + std::to_wstring(ordinal);
    return out + L"}";
}

// Ask the managed agent one Reactor (or resource) question about one element: `op` with up to two string arguments.
// Answers the agent's JSON, `ERR <token>` for a handle that no longer resolves, or an absent/unavailable state.
// `needsReactor` false lets resource ops run in any app with the agent.
static std::wstring CmdReactorAgent(unsigned long long wire, const wchar_t* op, const std::wstring& arg = {},
                                    const std::wstring& value = {}, bool needsReactor = true)
{
    {
        CensusSharedLock lock;
        InstanceHandle ih = 0; const wchar_t* error = nullptr;
        if (!ResolveWire_nolock(wire, &ih, &error)) return std::wstring(L"ERR ") + (error ? error : L"stale-handle");
    }
    if (needsReactor) {
        TapReactorStatus status = TapReactorStatusNow();
        if (!status.present) return L"{\"state\":\"absent\"}";
        if (status.agent != L"ok") {
            std::wstring reason = status.reason.empty() ? std::wstring(L"the managed DevTools agent did not answer") : status.reason;
            // Without the agent the tree still comes from what Reactor published; say what is and is not available.
            if (status.annotations == L"published" && g_reactorPublishedIndex_ui != 0xFFFFFFFF) {
                reason = L"state, handlers, re-renders and logs need the managed DevTools agent, and " + reason +
                         L". The component tree, call sites, names and selectors still work";
            }
            return L"{\"state\":\"unavailable\",\"reason\":\"" + DevToolsJsonEscape(reason) + L"\"}";
        }
    } else if (!DevToolsBindingRelay_AgentPresent()) {
        return DevToolsBindingRelay_UnavailableJson(DevToolsBindingRelay_NoAgentReason(DevToolsBindingRelay_AppRuntime()));
    }

    DevToolsBindingApartment apartment;
    if (FAILED(apartment.result)) return L"{\"state\":\"unavailable\",\"reason\":\"internal: " + HrStr(apartment.result) + L"\"}";
    auto agile = std::make_shared<winrt::com_ptr<IAgileReference>>();
    auto rerr = std::make_shared<const wchar_t*>(nullptr);
    bool completed = false;
    const HRESULT hr = RunOnUiThread([wire, agile, rerr]() -> HRESULT {
        DevToolsBindingRelay_NoteUiThread();
        InstanceHandle ih = 0; const wchar_t* e = nullptr;
        { CensusSharedLock lock; if (!ResolveWire_nolock(wire, &ih, &e)) { *rerr = e ? e : L"stale-handle"; return E_INVALIDARG; } }
        winrt::com_ptr<IInspectable> insp;
        if (!g_diag || FAILED(g_diag->GetIInspectableFromHandle(ih, insp.put())) || !insp) { *rerr = L"stale-handle"; return E_INVALIDARG; }
        return RoGetAgileReference(AGILEREFERENCE_DEFAULT, __uuidof(IInspectable), insp.get(), agile->put());
    }, &completed);
    if (completed && *rerr) return std::wstring(L"ERR ") + *rerr;
    if (FAILED(hr) || !*agile) return L"{\"state\":\"unavailable\",\"reason\":\"the app's UI thread did not answer (" + HrStr(hr) + L")\"}";

    std::wstring json;
    DevToolsBindingRelay_Binding(op, agile->get(), arg, value, &json);
    return json;
}

// A compile-time C# path as it is on this machine, honouring the launch's source root, the projects compiled into
// the app and PathMap. `ambiguous` receives the candidates when more than one project has the file.
static bool TapReactorMapFile(const std::wstring& file, std::wstring* onDisk, std::vector<std::wstring>* ambiguous)
{
    static const std::wstring sourceRoot = ReactorEnvironment(L"WINAPP_DEVTOOLS_SOURCE_ROOT");
    static const std::wstring pathMap = ReactorEnvironment(L"WINAPP_DEVTOOLS_PATH_MAP");
    static const std::wstring projectDirs = ReactorEnvironment(L"WINAPP_DEVTOOLS_PROJECT_DIRS");
    return DevToolsSourcePath_MapCompiled(file, sourceRoot, pathMap, onDisk, projectDirs, ambiguous);
}

static bool TapReactorMapFile(const std::wstring& file, std::wstring* onDisk)
{
    return TapReactorMapFile(file, onDisk, nullptr);
}

// ---- Comment-marker anchors (DevToolsReactorAnchor) ----
// A comment left on a Reactor element is placed on the live element its call site created. The overlay asks for an
// anchor every frame for each marker, so answers are cached until the description or the tree changes.

// An annotation's call site, with its file relative to the project this app was launched for.
static DevToolsReactorAnchor::Site TapReactorSiteOf(const ReactorAnnotation& a)
{
    static std::mutex lock;
    static std::map<std::wstring, std::wstring> relative;
    static const std::wstring sourceRoot = ReactorEnvironment(L"WINAPP_DEVTOOLS_SOURCE_ROOT");
    DevToolsReactorAnchor::Site site;
    {
        std::lock_guard<std::mutex> guard(lock);
        auto known = relative.find(a.file);
        if (known == relative.end()) {
            std::wstring path;
            if (!TapReactorMapFile(a.file, &path) || path.empty()) path = a.file;
            known = relative.emplace(a.file, DevToolsReactorAnchor::ProjectRelative(path, sourceRoot)).first;
        }
        site.file = known->second;
    }
    site.line = a.line;
    site.column = a.column;
    site.element = a.element;
    site.key = a.key;
    site.owner = a.owner;
    site.name = a.name;
    return site;
}

// The child-index path from `top` (exclusive; 0 for the census root) down to `ih`, and optionally each step's short
// type. Caller holds the census lock.
static bool TapCensusPath_nolock(InstanceHandle ih, InstanceHandle top, std::vector<size_t>* indexes,
                                 std::vector<std::pair<unsigned int, std::wstring>>* typed)
{
    std::vector<size_t> up;
    std::vector<std::pair<unsigned int, std::wstring>> upTyped;
    InstanceHandle current = ih;
    for (int guard = 0; current && current != top && guard < 512; ++guard) {
        const auto parent = g_parent.find(current);
        const InstanceHandle owner = parent == g_parent.end() ? 0 : parent->second;
        size_t index = 0;
        if (owner) {
            const auto kids = g_children.find(owner);
            if (kids == g_children.end()) return false;
            const auto slot = std::find(kids->second.begin(), kids->second.end(), current);
            if (slot == kids->second.end()) return false;
            index = (size_t)(slot - kids->second.begin());
        }
        up.push_back(index);
        if (typed) {
            const auto type = g_type.find(current);
            const std::wstring full = type == g_type.end() ? std::wstring() : type->second;
            const size_t dot = full.find_last_of(L'.');
            upTyped.emplace_back((unsigned int)index, dot == std::wstring::npos ? full : full.substr(dot + 1));
        }
        current = owner;
    }
    if (current != top) return false;
    if (indexes) indexes->assign(up.rbegin(), up.rend());
    if (typed) typed->assign(upTyped.rbegin(), upTyped.rend());
    return true;
}

// Which of the elements one call site created under the same Reactor parent this one is, in document order. Caller
// holds the annotation lock (shared is enough); takes the census lock.
static unsigned int TapReactorOrdinal_locked(InstanceHandle ih, const ReactorAnnotation& a)
{
    std::vector<InstanceHandle> peers;
    for (const auto& [other, b] : g_reactorAnnotations) {
        if (b.parent == a.parent && b.line == a.line && b.column == a.column && b.key == a.key && b.element == a.element &&
            b.file == a.file)
            peers.push_back(other);
    }
    if (peers.size() <= 1) return 0;
    std::vector<std::pair<InstanceHandle, std::vector<size_t>>> paths;
    CensusSharedLock lock;
    for (InstanceHandle peer : peers) {
        std::vector<size_t> path;
        if (TapCensusPath_nolock(peer, a.parent, &path, nullptr)) paths.emplace_back(peer, std::move(path));
    }
    return DevToolsReactorAnchor::Ordinal(ih, std::move(paths));
}

// Describes in the background when the cache is not current for this tree, so a marker whose element is not in it
// yet appears once it is. Cheap when the cache is current; safe on the UI thread.
static void TapReactorRefreshIfStaleAsync()
{
    if (!TapReactorMaybePresent()) return;
    AcquireSRWLockShared(&g_reactorAuthoredLock);
    const bool current = g_reactorCacheWhole && g_reactorCacheEpoch == g_treeEpoch.load(std::memory_order_acquire);
    ReleaseSRWLockShared(&g_reactorAuthoredLock);
    if (!current) TapReactorRefreshInspectorAsync();
}

struct ReactorAnchorCacheEntry { std::wstring anchor; bool unique = false; const wchar_t* reason = L"unique"; };
static std::mutex g_reactorAnchorCacheLock;
static unsigned long long g_reactorAnchorCacheGen = ~0ull, g_reactorAnchorCacheEpoch = ~0ull;
static std::map<InstanceHandle, ReactorAnchorCacheEntry> g_reactorAnchorOf;
static std::map<std::wstring, InstanceHandle> g_reactorAnchorResolved;

// Caller holds g_reactorAnchorCacheLock.
static void TapReactorAnchorCacheSync_locked()
{
    const auto gen = g_reactorAnnotationGen.load(std::memory_order_acquire);
    const auto epoch = g_treeEpoch.load(std::memory_order_acquire);
    if (gen == g_reactorAnchorCacheGen && epoch == g_reactorAnchorCacheEpoch) return;
    g_reactorAnchorOf.clear();
    g_reactorAnchorResolved.clear();
    g_reactorAnchorCacheGen = gen;
    g_reactorAnchorCacheEpoch = epoch;
}

// The marker anchor of a Reactor element (its own call site, or the nearest Reactor element's plus the path down to
// it). Empty for an element no Reactor element produced. `unique` is false when the same anchor names another live
// element too (one component rendered in two windows).
static bool TapReactorAnchorOf(InstanceHandle h, std::wstring* out, bool* unique, const wchar_t** reason)
{
    if (!h || !out) return false;
    out->clear();
    {
        std::lock_guard<std::mutex> guard(g_reactorAnchorCacheLock);
        TapReactorAnchorCacheSync_locked();
        const auto known = g_reactorAnchorOf.find(h);
        if (known != g_reactorAnchorOf.end()) {
            *out = known->second.anchor;
            if (unique) *unique = known->second.unique;
            if (reason) *reason = known->second.reason;
            return !out->empty();
        }
    }
    ReactorAnchorCacheEntry entry;
    ReactorAnnotation a;
    bool exact = false;
    InstanceHandle found = 0;
    if (TapReactorAnnotationFor(h, true, &a, &exact, &found, /*requireFile*/ true)) {
        auto site = TapReactorSiteOf(a);
        unsigned int repeats = 0;
        AcquireSRWLockShared(&g_reactorAuthoredLock);
        site.ordinal = TapReactorOrdinal_locked(found, a);
        for (const auto& [other, b] : g_reactorAnnotations) {
            if (b.file == a.file && b.line == a.line && b.column == a.column && b.element == a.element && b.key == a.key &&
                b.owner == a.owner && TapReactorOrdinal_locked(other, b) == site.ordinal)
                ++repeats;
        }
        ReleaseSRWLockShared(&g_reactorAuthoredLock);
        bool located = true;
        if (!exact) {
            CensusSharedLock lock;
            located = TapCensusPath_nolock(h, found, nullptr, &site.sub) && site.sub.size() <= 64;
        }
        if (located) {
            entry.anchor = DevToolsReactorAnchor::Encode(site);
            entry.unique = repeats <= 1;
            entry.reason = entry.unique ? L"unique" : L"repeated-source-instance";
        }
    } else {
        entry.reason = L"missing-source-identity";
    }
    {
        std::lock_guard<std::mutex> guard(g_reactorAnchorCacheLock);
        TapReactorAnchorCacheSync_locked();
        g_reactorAnchorOf[h] = entry;
    }
    *out = entry.anchor;
    if (unique) *unique = entry.unique;
    if (reason) *reason = entry.reason;
    return !out->empty();
}

// The live element a marker anchor names, or 0 when it is not in the tree (yet).
static InstanceHandle TapReactorResolveAnchor(const std::wstring& anchor)
{
    DevToolsReactorAnchor::Site wanted;
    if (!DevToolsReactorAnchor::Decode(anchor, &wanted)) return 0;
    // Markers follow the active window, so the answer depends on it too.
    const InstanceHandle active = DevToolsOverlay_ActiveSurface().rootHandle;
    const std::wstring cacheKey = anchor + L"|" + std::to_wstring(active);
    {
        std::lock_guard<std::mutex> guard(g_reactorAnchorCacheLock);
        TapReactorAnchorCacheSync_locked();
        const auto known = g_reactorAnchorResolved.find(cacheKey);
        if (known != g_reactorAnchorResolved.end()) return known->second;
    }
    std::vector<DevToolsReactorAnchor::Site> sites;
    std::vector<std::pair<InstanceHandle, unsigned long long>> handles;
    AcquireSRWLockShared(&g_reactorAuthoredLock);
    for (const auto& [ih, a] : g_reactorAnnotations) {
        if (a.file.empty() || a.element != wanted.element || a.key != wanted.key || a.owner != wanted.owner) continue;
        auto site = TapReactorSiteOf(a);
        if (!DevToolsReactorAnchor::SameText(site.file, wanted.file)) continue;
        site.ordinal = TapReactorOrdinal_locked(ih, a);
        sites.push_back(std::move(site));
        handles.emplace_back(ih, a.wire);
    }
    ReleaseSRWLockShared(&g_reactorAuthoredLock);
    InstanceHandle resolved = 0;
    const auto matching = DevToolsReactorAnchor::Matching(sites, wanted);
    // One call site shown in several windows: the marker belongs on the active window's element.
    const InstanceHandle activeRoot = matching.size() > 1 ? active : 0;
    int index = matching.empty() ? -1 : (int)matching.front();
    for (size_t candidate : matching) {
        if (!activeRoot) break;
        CensusSharedLock lock;
        InstanceHandle current = handles[candidate].first;
        for (int guard = 0; current && current != activeRoot && guard < 512; ++guard) {
            const auto parent = g_parent.find(current);
            current = parent == g_parent.end() ? 0 : parent->second;
        }
        if (current == activeRoot) { index = (int)candidate; break; }
    }
    if (index >= 0) {
        CensusSharedLock lock;
        InstanceHandle current = handles[(size_t)index].first;
        bool ok = PackWire_nolock(current) == handles[(size_t)index].second;
        for (const auto& step : wanted.sub) {
            if (!ok) break;
            const auto kids = g_children.find(current);
            if (kids == g_children.end() || step.first >= kids->second.size()) { ok = false; break; }
            current = kids->second[step.first];
            const auto type = g_type.find(current);
            const std::wstring full = type == g_type.end() ? std::wstring() : type->second;
            const size_t dot = full.find_last_of(L'.');
            ok = (dot == std::wstring::npos ? full : full.substr(dot + 1)) == step.second;
        }
        if (ok) resolved = current;
    }
    if (!resolved) TapReactorRefreshIfStaleAsync();
    {
        std::lock_guard<std::mutex> guard(g_reactorAnchorCacheLock);
        TapReactorAnchorCacheSync_locked();
        g_reactorAnchorResolved[cacheKey] = resolved;
    }
    return resolved;
}

// Adds where `member.file` is on this machine (`fileOnDisk`) and, when the PDB recorded the file's checksum, whether
// the file on disk is still the one the app was built from (`stale`).
static void TapReactorLocateMember(DevToolsJson& owner, const wchar_t* member)
{
    const auto entry = owner.obj.find(member);
    if (entry == owner.obj.end() || !entry->second.IsObject()) return;
    DevToolsJson& at = entry->second;
    const std::wstring file = at.GetString(L"file");
    std::wstring path;
    if (file.empty() || !TapReactorMapFile(file, &path)) return;
    DevToolsJson text; text.type = DevToolsJsonType::String; text.str = path;
    if (path != file) at.obj[L"fileOnDisk"] = text;
    const std::wstring algorithm = at.GetString(L"checksumAlgorithm");
    const std::wstring expected = at.GetString(L"checksum");
    if (algorithm.empty() || expected.empty()) return;
    const std::wstring actual = DevToolsAuthored_FileHash(path, algorithm);
    if (actual.empty()) return;
    DevToolsJson stale; stale.type = DevToolsJsonType::Bool; stale.b = _wcsicmp(actual.c_str(), expected.c_str()) != 0;
    at.obj[L"stale"] = stale;
}

// Gives a hook of a Reactor.component answer its C# name. A hook named as a secret, by the rule XAML properties follow
// (DevToolsRead_IsSecretProperty), shows no value and is not editable.
static void TapReactorApplyHookName(DevToolsJson& hook, const std::wstring& name, unsigned int line)
{
    DevToolsJson text; text.type = DevToolsJsonType::String; text.str = name;
    hook.obj[L"name"] = text;
    if (DevToolsRead_IsSecretProperty(name)) {
        if (hook.Find(L"value")) {
            DevToolsJson redacted; redacted.type = DevToolsJsonType::String; redacted.str = kDevToolsRedacted;
            hook.obj[L"value"] = redacted;
        }
        DevToolsJson yes; yes.type = DevToolsJsonType::Bool; yes.b = true;
        hook.obj[L"redacted"] = yes;
        if (hook.Find(L"editable")) { DevToolsJson no; no.type = DevToolsJsonType::Bool; no.b = false; hook.obj[L"editable"] = no; }
    }
    if (line) {
        DevToolsJson number; number.type = DevToolsJsonType::Number; number.num = line;
        hook.obj[L"line"] = number;
    }
}

// Reactor.setState's refusal for the hook at `index` when a Reactor.component answer shows it redacted; empty when
// the write may go ahead.
static std::wstring TapReactorSecretRefusal(const DevToolsJson& component, const std::wstring& index)
{
    const DevToolsJson* hooks = component.Find(L"hooks");
    if (!hooks || hooks->type != DevToolsJsonType::Array) return L"";
    for (const auto& hook : hooks->arr) {
        const DevToolsJson* at = hook.Find(L"index");
        if (!at || at->type != DevToolsJsonType::Number || std::to_wstring(static_cast<long long>(at->num)) != index) continue;
        if (!hook.GetBool(L"redacted", false)) return L"";
        return L"{\"state\":\"refused\",\"code\":\"secret\",\"reason\":\"hook " + DevToolsJsonEscape(index) +
               L" holds a secret; DevTools does not write it\"}";
    }
    return L"";
}

// After a late attach Reactor published no hook names: read them from the component's render body, where the PDB says
// it is defined (DevToolsCSharpSource::HookNames), so a hook assigned to a password variable is redacted like a
// published one. Such names are marked as read from the source text.
static void TapReactorNameHooksFromSource(const DevToolsJson& answer, DevToolsJson& hooks)
{
    const DevToolsJson* definition = answer.Find(L"definition");
    if (!definition || !definition->IsObject()) return;
    std::wstring path = definition->GetString(L"fileOnDisk");
    if (path.empty()) path = definition->GetString(L"file");
    const bool rooted = path.size() > 2 && (path[1] == L':' || (path[0] == L'\\' && path[1] == L'\\'));
    if (!rooted || path.size() < 3 || _wcsicmp(path.c_str() + path.size() - 3, L".cs") != 0) return;
    std::vector<std::wstring> kinds;
    for (size_t i = 0; i < hooks.arr.size(); ++i) {
        const DevToolsJson& hook = hooks.arr[i];
        const DevToolsJson* index = hook.IsObject() ? hook.Find(L"index") : nullptr;
        if (!index || index->type != DevToolsJsonType::Number || index->num != static_cast<double>(i)) return;
        kinds.push_back(hook.GetString(L"kind"));
    }
    std::vector<std::wstring> names;
    if (!DevToolsAuthored_CSharpHookNames(path, static_cast<unsigned int>(definition->GetInt(L"line", 0)),
            static_cast<unsigned int>(definition->GetInt(L"column", 0)), kinds, &names)) return;
    for (size_t i = 0; i < names.size() && i < hooks.arr.size(); ++i) {
        if (names[i].empty()) continue;
        TapReactorApplyHookName(hooks.arr[i], names[i], 0);
        DevToolsJson source; source.type = DevToolsJsonType::String; source.str = L"csharp-source-text";
        hooks.arr[i].obj[L"nameSource"] = source;
    }
}

// A context hook redacted by its name shows the same value in `contexts`: redact it there too.
static void TapReactorRedactNamedContexts(DevToolsJson& answer, const DevToolsJson& hooks)
{
    auto contexts = answer.obj.find(L"contexts");
    if (contexts == answer.obj.end() || contexts->second.type != DevToolsJsonType::Array) return;
    for (const auto& hook : hooks.arr) {
        const DevToolsJson* index = hook.IsObject() ? hook.Find(L"index") : nullptr;
        if (!index || hook.GetString(L"kind") != L"context" || !hook.GetBool(L"redacted", false)) continue;
        for (auto& context : contexts->second.arr) {
            const DevToolsJson* at = context.IsObject() ? context.Find(L"hook") : nullptr;
            if (!at || at->type != DevToolsJsonType::Number || at->num != index->num) continue;
            DevToolsJson redacted; redacted.type = DevToolsJsonType::String; redacted.str = kDevToolsRedacted;
            context.obj[L"value"] = redacted;
            DevToolsJson yes; yes.type = DevToolsJsonType::Bool; yes.b = true;
            context.obj[L"redacted"] = yes;
        }
    }
}

// The hooks Reactor published for the root `name` of a host whose root control is inside `ih` (a few levels down, as a
// ReactorHostControl's content is), or none.
static std::vector<DevToolsReactorPublished::Hook> TapReactorHostedRootHooks(InstanceHandle ih, const std::wstring& name)
{
    std::vector<InstanceHandle> roots;
    AcquireSRWLockShared(&g_reactorAuthoredLock);
    for (const auto& [handle, annotation] : g_reactorAnnotations)
        if (annotation.host && annotation.rootName == name && !annotation.hooks.empty()) roots.push_back(handle);
    ReleaseSRWLockShared(&g_reactorAuthoredLock);
    InstanceHandle inside = 0;
    {
        CensusSharedLock lock;
        for (InstanceHandle root : roots) {
            InstanceHandle current = root;
            for (int depth = 0; current && depth < 8 && !inside; ++depth) {
                const auto parent = g_parent.find(current);
                current = parent == g_parent.end() ? 0 : parent->second;
                if (current == ih) inside = root;
            }
            if (inside) break;
        }
    }
    std::vector<DevToolsReactorPublished::Hook> hooks;
    if (!inside) return hooks;
    AcquireSRWLockShared(&g_reactorAuthoredLock);
    if (const auto a = g_reactorAnnotations.find(inside); a != g_reactorAnnotations.end()) hooks = a->second.hooks;
    ReleaseSRWLockShared(&g_reactorAuthoredLock);
    return hooks;
}

// Names each hook of a Reactor.component answer with the C# variable it was assigned to, from the hooks Reactor
// published on the component's element (`hooks=`). The component is the nearest element at or above `wire` that
// mounts it (or whose host renders it as the root). Hooks Reactor did not name keep only their index; when Reactor
// published no names at all, they are read from the component's source. The agent formats values before the name is
// known, so a hook whose name says it holds a secret is redacted here.
static void TapReactorNameHooks(unsigned long long wire, DevToolsJson& answer)
{
    auto hooks = answer.obj.find(L"hooks");
    const DevToolsJson* component = answer.Find(L"component");
    if (hooks == answer.obj.end() || hooks->second.type != DevToolsJsonType::Array || hooks->second.arr.empty() ||
        !component || !component->IsObject()) return;
    const std::wstring name = component->GetString(L"name");
    std::vector<DevToolsReactorPublished::Hook> named;
    InstanceHandle ih = 0;
    bool resolved = false;
    {
        CensusSharedLock lock;
        const wchar_t* error = nullptr;
        resolved = ResolveWire_nolock(wire, &ih, &error);
    }
    InstanceHandle start = 0;
    if (resolved && TapReactorAnnotationFor(ih, true, nullptr, nullptr, &start)) {
        AcquireSRWLockShared(&g_reactorAuthoredLock);
        InstanceHandle current = start;
        for (int guard = 0; current && guard < 4096; ++guard) {
            const auto a = g_reactorAnnotations.find(current);
            if (a == g_reactorAnnotations.end()) break;
            if (a->second.component == name || (a->second.host && a->second.rootName == name)) {
                named = a->second.hooks;
                break;
            }
            current = a->second.parent;
        }
        ReleaseSRWLockShared(&g_reactorAuthoredLock);
    }
    // A ReactorHostControl (selected by its XAML x:Name) is above the root it renders, not at or below it.
    if (named.empty() && resolved) named = TapReactorHostedRootHooks(ih, name);
    bool anyName = false;
    for (const auto& hook : hooks->second.arr) anyName = anyName || !hook.GetString(L"name").empty();
    if (named.empty()) {
        if (!anyName) TapReactorNameHooksFromSource(answer, hooks->second);
        TapReactorRedactNamedContexts(answer, hooks->second);
        return;
    }
    // Hooks after one whose position is not fixed (in a branch, a loop or a composite hook) have no static index:
    // they are listed by name only.
    DevToolsJson unindexed; unindexed.type = DevToolsJsonType::Array;
    for (const auto& hook : named) {
        if (hook.indexed) continue;
        DevToolsJson entry; entry.type = DevToolsJsonType::Object;
        DevToolsJson text; text.type = DevToolsJsonType::String; text.str = hook.name;
        entry.obj[L"name"] = text;
        if (hook.line) { DevToolsJson number; number.type = DevToolsJsonType::Number; number.num = hook.line; entry.obj[L"line"] = number; }
        unindexed.arr.push_back(std::move(entry));
    }
    if (!unindexed.arr.empty()) answer.obj[L"unindexedHooks"] = std::move(unindexed);
    for (auto& hook : hooks->second.arr) {
        if (!hook.IsObject()) continue;
        const DevToolsJson* index = hook.Find(L"index");
        if (!index || index->type != DevToolsJsonType::Number || index->num < 0) continue;
        unsigned int line = 0;
        const std::wstring hookName = DevToolsReactorPublished::HookName(named, static_cast<unsigned int>(index->num), &line);
        if (hookName.empty()) continue;
        TapReactorApplyHookName(hook, hookName, line);
    }
    TapReactorRedactNamedContexts(answer, hooks->second);
}

// Reactor.component: the component that renders an element, with its props, hooks, contexts and where it is declared
// and defined (the definition checked against the file on disk).
static std::wstring CmdReactorComponent(unsigned long long wire)
{
    TapReactorRefreshAuthored();
    std::wstring json = CmdReactorAgent(wire, L"reactorcomponent");
    DevToolsJson parsed;
    if (json.rfind(L"ERR ", 0) == 0 || !DevToolsJsonParse(json, parsed) || !parsed.IsObject() || parsed.GetString(L"state") != L"ok")
        return json;
    TapReactorLocateMember(parsed, L"declared");
    TapReactorLocateMember(parsed, L"definition");
    TapReactorNameHooks(wire, parsed);
    return DevToolsJsonSerialize(parsed);
}

static std::wstring CmdReactorElement(unsigned long long wire);
static std::wstring CmdReactorSetState(unsigned long long wire, const std::wstring& index, const std::wstring& value);
static bool TapReactorAskWire(unsigned long long wire, const std::wstring& operation, const std::wstring& arg,
                              const std::wstring& value, std::wstring* out);

// Out-references whose target Reactor knows but hasn't created (ReactorDiagnostics.GetReferenceEdges, in Reactor
// versions that have it); none otherwise. Worker only: the agent answers on the UI thread.
static std::vector<DevToolsReferenceEdge> TapPendingReferences(unsigned long long wire)
{
    return DevToolsPendingReferences(CmdReactorAgent(wire, L"reactorreferences"));
}

// The inspector's Component section. Worker only (the agent needs the UI thread). Writes follow the session's
// mutation posture, like the Mutation-access protocol methods.
static bool TapReactorAskWire(unsigned long long wire, const std::wstring& operation, const std::wstring& arg,
                              const std::wstring& value, std::wstring* out)
{
    const wchar_t* op = operation.c_str();
    if ((operation == L"reactorsetstate" || operation == L"reactorinvoke") && !DevToolsTrust_MutationEnabled()) {
        *out = L"{\"state\":\"refused\",\"code\":\"read-only\",\"reason\":\"this session does not allow DevTools to change the app\"}";
        return false;
    }
    if (operation == L"reactorcomponent") *out = CmdReactorComponent(wire);
    else if (operation == L"reactorelement") *out = CmdReactorElement(wire);
    else if (operation == L"reactorsetstate") *out = CmdReactorSetState(wire, arg, value);
    else if (operation == L"reactorinvoke") *out = CmdReactorAgent(wire, op, arg, value);
    else { *out = DevToolsBindingRelay_UnavailableJson(L"unsupported Reactor operation"); return false; }
    if (out->rfind(L"ERR ", 0) == 0) {
        *out = DevToolsBindingRelay_UnavailableJson(L"this element is no longer in the visual tree");
        return false;
    }
    return true;
}

// Reactor.element: what Reactor set on one element (its record properties, applied modifiers, component props), from
// the managed agent, plus the component that rendered it from the annotation cache.
// Reactor.setState: refused for a hook shown as a secret (by its name), otherwise the agent writes it. The agent knows
// the names Reactor reports; the published names need the component answer first.
static std::wstring CmdReactorSetState(unsigned long long wire, const std::wstring& index, const std::wstring& value)
{
    DevToolsJson component;
    if (DevToolsJsonParse(CmdReactorComponent(wire), component) && component.IsObject() && component.GetString(L"state") == L"ok") {
        std::wstring refusal = TapReactorSecretRefusal(component, index);
        if (!refusal.empty()) return refusal;
    }
    return CmdReactorAgent(wire, L"reactorsetstate", index, value);
}

static std::wstring CmdReactorElement(unsigned long long wire)
{
    TapReactorRefreshAuthored();
    std::wstring json = CmdReactorAgent(wire, L"reactorelement");
    DevToolsJson parsed;
    if (json.rfind(L"ERR ", 0) == 0 || !DevToolsJsonParse(json, parsed) || !parsed.IsObject() || parsed.GetString(L"state") != L"ok")
        return json;

    // The agent's answer is complete; append what only the cache knows: the rendering component and where the file
    // is on this machine.
    ReactorAnnotation annotation;
    std::wstring extra;
    if (TapReactorAnnotationForWire(wire, false, &annotation) && !annotation.owner.empty())
        extra += L",\"owner\":\"" + DevToolsJsonEscape(annotation.owner) + L"\"";
    const std::wstring file = parsed.GetString(L"file");
    if (!file.empty()) {
        std::wstring path;
        if (TapReactorMapFile(file, &path) && path != file)
            extra += L",\"fileOnDisk\":\"" + DevToolsJsonEscape(path) + L"\"";
    }
    const size_t close = json.find_last_of(L'}');
    return close == std::wstring::npos ? json : json.substr(0, close) + extra + L"}";
}

static std::wstring TapReactorSourceAnswer(unsigned long long wire)
{
    TapReactorRefreshAuthored();
    InstanceHandle ih = 0;
    {
        CensusSharedLock lock;
        const wchar_t* error = nullptr;
        if (!ResolveWire_nolock(wire, &ih, &error)) return {};
    }
    ReactorSource source;
    if (!TapReactorSourceFor(ih, &source, true)) return {};
    std::wstring out = L"{\"handle\":" + std::to_wstring(wire);
    out += L",\"kind\":\"csharp\"";
    out += L",\"fileName\":\"" + DevToolsJsonEscape(source.path) + L"\"";
    out += L",\"lineNumber\":" + std::to_wstring(source.node.line);
    out += L",\"columnNumber\":" + std::to_wstring(source.node.column);
    if (source.path != source.node.file) out += L",\"compiledFileName\":\"" + DevToolsJsonEscape(source.node.file) + L"\"";
    out += source.onDisk ? L",\"onDisk\":true" : L",\"onDisk\":false";
    // Same meaning as for XAML: whether the authored text could be read from here, and whether it is still the code the
    // app was built from ("stale": edited since; "pastEnd": a #line directive names a line this file does not have).
    out += !source.site.empty() ? L",\"authoredState\":\"" + source.site + L"\""
         : !source.code.empty() ? L",\"authoredState\":\"available\""
         : !source.ambiguous.empty() ? L",\"authoredState\":\"ambiguous\""
         : source.onDisk ? L",\"authoredState\":\"noSourceInfo\"" : L",\"authoredState\":\"noFile\"";
    if (!source.site.empty()) out += L",\"lineCount\":" + std::to_wstring(source.lineCount);
    if (!source.ambiguous.empty()) {
        out += L",\"candidates\":[";
        for (size_t i = 0; i < source.ambiguous.size(); ++i)
            out += (i ? L",\"" : L"\"") + DevToolsJsonEscape(source.ambiguous[i]) + L"\"";
        out += L"]";
    }
    AcquireSRWLockShared(&g_reactorAuthoredLock);
    const unsigned int ordinal = TapReactorOrdinal_locked(source.ih, source.node);
    ReleaseSRWLockShared(&g_reactorAuthoredLock);
    out += L",\"reactor\":" + TapReactorSourceJson(source, ordinal);
    if (!source.code.empty()) out += L",\"csharp\":\"" + DevToolsJsonEscape(source.code) + L"\"";
    return out + L"}";
}

// ---- Reactor trace events: Reactor.changed, Reactor.logs, the overlay's error badge and highlight updates ----
// One long-lived connection to the managed agent's event stream (EVENTS1). It replays the warnings and errors kept since
// launch, then streams every Microsoft-UI-Reactor event. Started when the overlay is built in a Reactor app, when a
// client enables the Reactor domain, or when Reactor.logs is asked.

struct ReactorLogEntry
{
    unsigned long long seq = 0;
    int severity = 0;          // 3 error, 2 warning, 1 info
    std::wstring json;
};
static std::mutex g_reactorPumpLock;
static std::condition_variable g_reactorPumpCv;
static HANDLE g_reactorPumpPipe = INVALID_HANDLE_VALUE;
static bool g_reactorPumpRunning = false;
static bool g_reactorPumpReady = false;
static std::wstring g_reactorPumpError;
static std::deque<ReactorLogEntry> g_reactorLog;
static unsigned long long g_reactorLogDropped = 0;
static unsigned int g_reactorErrors = 0;
static unsigned int g_reactorWarnings = 0;   // keyed-list bailouts, counted once each ("first")
static int g_reactorPumpMaskSent = 0;
static std::set<uint64_t> g_reactorRenderConns;       // protocol connections that asked for renders
static std::atomic<bool> g_reactorHighlight{false};   // the overlay's "Highlight updates" toggle
static std::atomic<bool> g_reactorRereadPosted{false};
static const size_t kReactorLogCapacity = 500;

static int ReactorSeverity(const std::wstring& level)
{
    return level == L"error" ? 3 : level == L"warning" ? 2 : level == L"info" ? 1 : 0;
}

// Component renders are asked for only while someone shows or receives them: they are the one costly event.
// Caller holds g_reactorPumpLock.
static int TapReactorWantedMask_locked()
{
    for (auto it = g_reactorRenderConns.begin(); it != g_reactorRenderConns.end();) {
        if (DevToolsEvents_IsOwnerLive(*it)) ++it;
        else it = g_reactorRenderConns.erase(it);
    }
    return (g_reactorHighlight.load() || !g_reactorRenderConns.empty()) ? 1 : 0;
}

static void TapReactorPumpSyncMask()
{
    std::lock_guard<std::mutex> lock(g_reactorPumpLock);
    const int wanted = TapReactorWantedMask_locked();
    if (!g_reactorPumpRunning || g_reactorPumpPipe == INVALID_HANDLE_VALUE || wanted == g_reactorPumpMaskSent) return;
    if (DevToolsBindingRelay_WriteLine(g_reactorPumpPipe, "MASK " + std::to_string(wanted))) g_reactorPumpMaskSent = wanted;
}

// Re-reads the inspector's selected element once per burst of state changes.
static void TapReactorPostReread()
{
    if (g_reactorRereadPosted.exchange(true)) return;
    if (FAILED(PostToUi([]() {
            g_reactorRereadPosted.store(false);
            DevToolsWindow_RereadCurrentSelection();
        })))
        g_reactorRereadPosted.store(false);
}

// A batch of component renders: flash them in the overlay and tell subscribers which elements re-rendered.
static void TapReactorRendered(const DevToolsJson& event)
{
    const DevToolsJson* components = event.Find(L"components");
    if (!components || components->type != DevToolsJsonType::Array) return;
    auto wanted = std::make_shared<std::vector<std::tuple<uintptr_t, std::wstring, std::wstring>>>();
    for (const auto& item : components->arr) {
        uintptr_t id = 0;
        if (item.IsObject() && ReactorParseIdentity(item.GetString(L"id"), &id))
            wanted->emplace_back(id, item.GetString(L"component"), item.GetString(L"reason"));
    }
    if (wanted->empty()) return;
    const unsigned long long seq = (unsigned long long)event.GetInt(L"seq", 0);
    PostToUi([wanted, seq]() {
        std::map<uintptr_t, std::pair<InstanceHandle, unsigned long long>> byIdentity;
        std::set<uintptr_t> ids;
        for (const auto& item : *wanted) ids.insert(std::get<0>(item));
        for (const auto& [ih, known] : g_reactorIdentity_ui)
            if (ids.count(known.second)) byIdentity[known.second] = { ih, known.first };
        std::vector<InstanceHandle> flash;
        std::wstring list;
        for (const auto& [id, component, reason] : *wanted) {
            const auto found = byIdentity.find(id);
            if (found == byIdentity.end()) continue;
            flash.push_back(found->second.first);
            list += list.empty() ? L"{" : L",{";
            list += L"\"component\":\"" + DevToolsJsonEscape(component) + L"\"";
            if (!reason.empty()) list += L",\"reason\":\"" + DevToolsJsonEscape(reason) + L"\"";
            list += L",\"handle\":\"" + std::to_wstring(found->second.second) + L"\"}";
        }
        if (flash.empty()) return;
        if (DevToolsOverlay_HighlightUpdatesOn()) DevToolsOverlay_FlashRendered(flash);
        DevToolsEvents_Broadcast(DevToolsDomain_Reactor,
            DevToolsRpcEvent(L"Reactor.changed", L"{\"seq\":" + std::to_wstring(seq) +
                L",\"kind\":\"rendered\",\"level\":\"verbose\",\"event\":\"ComponentRendered\",\"components\":[" + list + L"]}", L"null"),
            DevToolsCoalesce_None, L"");
    });
}

static void TapReactorPumpLine(const std::string& raw)
{
    if (raw.rfind("EVENT ", 0) != 0) return;
    const std::wstring json = DevToolsFromUtf8(raw.data() + 6, raw.size() - 6);
    DevToolsJson event;
    if (!DevToolsJsonParse(json, event) || !event.IsObject()) return;
    const std::wstring kind = event.GetString(L"kind");
    if (kind == L"ready") {
        const bool renders = event.GetBool(L"renders", false);
        {
            std::lock_guard<std::mutex> lock(g_reactorPumpLock);
            g_reactorPumpReady = true;
            g_reactorLogDropped = (unsigned long long)event.GetInt(L"dropped", 0);
        }
        g_reactorPumpCv.notify_all();
        PostToUi([renders]() { DevToolsOverlay_SetHighlightUpdatesAvailable(renders); });
        return;
    }
    if (kind == L"rendered") { TapReactorRendered(event); return; }
    const bool replay = event.GetBool(L"replay", false);
    if (event.GetBool(L"log", false)) {
        unsigned int problems = 0;
        bool counted = false;
        {
            std::lock_guard<std::mutex> lock(g_reactorPumpLock);
            g_reactorLog.push_back({ (unsigned long long)event.GetInt(L"seq", 0), ReactorSeverity(event.GetString(L"level")), json });
            while (g_reactorLog.size() > kReactorLogCapacity) { g_reactorLog.pop_front(); ++g_reactorLogDropped; }
            if (kind == L"renderError" || kind == L"swallowedError") { ++g_reactorErrors; counted = true; }
            // A keyed list is counted when it first goes wrong, not again each time the same bailout fires.
            else if (kind == L"keyedList" && event.GetBool(L"first", false)) { ++g_reactorWarnings; counted = true; }
            problems = g_reactorErrors + g_reactorWarnings;
        }
        if (counted) PostToUi([problems]() { DevToolsOverlay_SetReactorProblems(problems); });
    }
    if (replay) return;
    if (kind == L"hotReload") {
        // An edit can move call sites without changing the tree: describe again, so sources, markers and the
        // inspector follow the new code.
        AcquireSRWLockExclusive(&g_reactorAuthoredLock);
        g_reactorCacheWhole = false;
        ReleaseSRWLockExclusive(&g_reactorAuthoredLock);
        TapReactorRefreshInspectorAsync();
        TapReactorPostReread();
    } else if (kind == L"state") {
        TapReactorPostReread();
    }
    DevToolsEvents_Broadcast(DevToolsDomain_Reactor, DevToolsRpcEvent(L"Reactor.changed", json, L"null"),
        DevToolsCoalesce_None, L"");
}

static void TapReactorPumpRun(HANDLE pipe)
{
    std::string line;
    char buffer[4096];
    OVERLAPPED overlapped{};
    overlapped.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    while (overlapped.hEvent) {
        ResetEvent(overlapped.hEvent);
        DWORD got = 0;
        if (!ReadFile(pipe, buffer, sizeof(buffer), nullptr, &overlapped) && GetLastError() != ERROR_IO_PENDING) break;
        if (!GetOverlappedResult(pipe, &overlapped, &got, TRUE) || got == 0) break;
        for (DWORD i = 0; i < got; ++i) {
            if (buffer[i] == '\n') { TapReactorPumpLine(line); line.clear(); }
            else if (buffer[i] != '\r' && line.size() < (1u << 20)) line.push_back(buffer[i]);
        }
    }
    if (overlapped.hEvent) CloseHandle(overlapped.hEvent);
    {
        std::lock_guard<std::mutex> lock(g_reactorPumpLock);
        CloseHandle(g_reactorPumpPipe);
        g_reactorPumpPipe = INVALID_HANDLE_VALUE;
        g_reactorPumpRunning = false;
        g_reactorPumpReady = false;
        g_reactorPumpError = L"the managed DevTools agent closed its event stream";
    }
    g_reactorPumpCv.notify_all();
}

// Starts the event stream when Reactor is present and it is not running. Worker threads only (it may probe the agent).
static bool TapReactorPumpEnsure(std::wstring* reason)
{
    TapReactorStatus status = TapReactorStatusNow();
    if (!status.present) { if (reason) reason->clear(); return false; }
    if (status.agent != L"ok") { if (reason) *reason = status.reason; return false; }
    std::lock_guard<std::mutex> lock(g_reactorPumpLock);
    if (g_reactorPumpRunning) return true;
    std::wstring error;
    const int mask = TapReactorWantedMask_locked();
    HANDLE pipe = DevToolsBindingRelay_OpenEvents(mask, &error);
    if (pipe == INVALID_HANDLE_VALUE) {
        g_reactorPumpError = error;
        if (reason) *reason = error;
        return false;
    }
    g_reactorPumpPipe = pipe;
    g_reactorPumpRunning = true;
    g_reactorPumpReady = false;
    g_reactorPumpMaskSent = mask;
    // The replay rebuilds the log and the error count.
    g_reactorLog.clear();
    g_reactorErrors = 0;
    g_reactorWarnings = 0;
    try {
        std::thread([pipe]() { TapReactorPumpRun(pipe); }).detach();
    } catch (...) {
        CloseHandle(pipe);
        g_reactorPumpPipe = INVALID_HANDLE_VALUE;
        g_reactorPumpRunning = false;
        return false;
    }
    return true;
}

static void TapReactorPumpEnsureAsync()
{
    {
        std::lock_guard<std::mutex> lock(g_reactorPumpLock);
        if (g_reactorPumpRunning) return;
    }
    try { std::thread([]() { TapReactorPumpEnsure(nullptr); }).detach(); } catch (...) {}
}

// The overlay's "Highlight updates" toggle (UI thread).
static void OverlayHighlightUpdatesBridge(bool on)
{
    g_reactorHighlight.store(on);
    try { std::thread([]() { if (TapReactorPumpEnsure(nullptr)) TapReactorPumpSyncMask(); }).detach(); } catch (...) {}
}

// Reactor.enable / Reactor.disable: delivery opt-in plus, with `renders`, component render events.
static void TapReactorSubscribe(DevToolsConn* conn, bool enable, bool renders)
{
    DevToolsEvents_SetDomain(conn, DevToolsDomain_Reactor, enable);
    {
        std::lock_guard<std::mutex> lock(g_reactorPumpLock);
        const uint64_t id = DevToolsEvents_ConnId(conn);
        if (enable && renders) g_reactorRenderConns.insert(id);
        else g_reactorRenderConns.erase(id);
    }
    if (enable) TapReactorPumpEnsure(nullptr);
    TapReactorPumpSyncMask();
}

// Reactor.logs: the warnings, errors, hot reloads and window events kept since launch, oldest first.
static std::wstring CmdReactorLogs(const DevToolsJson& params)
{
    static const wchar_t* kEmpty = L",\"entries\":[],\"next\":0,\"dropped\":0,\"errors\":0}";
    TapReactorStatus status = TapReactorStatusNow();
    if (!status.present) return std::wstring(L"{\"state\":\"absent\"") + kEmpty;
    std::wstring reason;
    if (!TapReactorPumpEnsure(&reason)) {
        return L"{\"state\":\"unavailable\",\"reason\":\"" + DevToolsJsonEscape(reason.empty()
            ? std::wstring(L"the managed DevTools agent did not answer") : reason) + L"\"" + kEmpty;
    }
    const unsigned long long since = (unsigned long long)(std::max)(0LL, (long long)params.GetInt(L"since", 0));
    const std::wstring level = params.GetString(L"level", L"info");
    const int least = (std::max)(1, ReactorSeverity(level));
    std::unique_lock<std::mutex> lock(g_reactorPumpLock);
    if (!g_reactorPumpCv.wait_for(lock, std::chrono::seconds(3), [] { return g_reactorPumpReady || !g_reactorPumpRunning; }) ||
        !g_reactorPumpReady) {
        const std::wstring why = g_reactorPumpRunning ? L"the managed DevTools agent did not send its log" : g_reactorPumpError;
        return L"{\"state\":\"unavailable\",\"reason\":\"" + DevToolsJsonEscape(why) + L"\"" + kEmpty;
    }
    std::wstring out = L"{\"state\":\"ok\",\"entries\":[";
    bool first = true;
    unsigned long long next = since;
    for (const auto& entry : g_reactorLog) {
        next = (std::max)(next, entry.seq + 1);
        if (entry.seq < since || entry.severity < least) continue;
        // Entries are reported as they happened, not as replays of them.
        std::wstring json = entry.json;
        const std::wstring flag = L",\"replay\":true";
        const size_t at = json.find(flag);
        if (at != std::wstring::npos) json.erase(at, flag.size());
        out += first ? L"" : L",";
        out += json;
        first = false;
    }
    out += L"],\"next\":" + std::to_wstring(next) + L",\"dropped\":" + std::to_wstring(g_reactorLogDropped) +
           L",\"errors\":" + std::to_wstring(g_reactorErrors) + L",\"warnings\":" + std::to_wstring(g_reactorWarnings) + L"}";
    return out;
}
