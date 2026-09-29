// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#include <fstream>

void OverlayFixtureSeedHighlight(InstanceHandle handle);
bool OverlayFixtureDetachedDraftGuards();
std::vector<InstanceHandle> OverlayFixtureSurfaceCandidates();

static unsigned g_queryDispatches = 0;
static long long g_queryDispatchMicros = 0;
static bool g_insideQueryDispatch = false, g_queryWriteOnDispatcher = false;

static HRESULT QueryEnqueue(void*, IUnknown* handler, bool* enqueued)
{
    ++g_queryDispatches;
    IDispatcherQueueHandler* callback = nullptr;
    const HRESULT hr = handler->QueryInterface(IID_PPV_ARGS(&callback));
    if (FAILED(hr)) return hr;
    *enqueued = true;
    g_insideQueryDispatch = true;
    const auto started = std::chrono::steady_clock::now();
    callback->Invoke();
    g_queryDispatchMicros += std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - started).count();
    g_insideQueryDispatch = false;
    callback->Release();
    return S_OK;
}

struct QueryDiagnostics : TextDiagnostics
{
    std::map<InstanceHandle, InputTextBox> elements;
    std::function<void()> onInspect;
    HRESULT STDMETHODCALLTYPE GetIInspectableFromHandle(InstanceHandle handle, IInspectable** value) override
    {
        if (onInspect) onInspect();
        *value = nullptr;
        const auto found = elements.find(handle);
        if (found == elements.end()) return E_INVALIDARG;
        found->second.AddRef(); *value = &found->second;
        return S_OK;
    }
};

static std::vector<std::wstring> QueryRoundTrips(const std::vector<std::wstring>& requests)
{
    GUID id{}; CoCreateGuid(&id);
    wchar_t guid[40]{}; StringFromGUID2(id, guid, 40);
    const auto name = L"\\\\.\\pipe\\winapp-query-fixture-" + std::wstring(guid);
    HANDLE pipe = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 65536, 65536, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) return {};
    auto readFrame = [](HANDLE handle) {
        std::string bytes;
        char ch = 0; DWORD count = 0;
        while (bytes.size() < 16 * 1024 * 1024 && ReadFile(handle, &ch, 1, &count, nullptr) && count) {
            if (ch == '\n') return bytes;
            bytes += ch;
        }
        return std::string();
    };
    std::thread server([&] {
        for (size_t i = 0; i < requests.size(); ++i) {
            if (!(ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED)) break;
            const auto frame = readFrame(pipe);
            const auto response = HandleRpc(nullptr, DevToolsRpcParse(std::wstring(winrt::to_hstring(frame))));
            const auto bytes = winrt::to_string(response) + "\n";
            DWORD sent = 0;
            WriteFile(pipe, bytes.data(), static_cast<DWORD>(bytes.size()), &sent, nullptr);
            FlushFileBuffers(pipe);
            DisconnectNamedPipe(pipe);
        }
    });
    std::vector<std::wstring> responses;
    for (const auto& request : requests) {
        HANDLE client = INVALID_HANDLE_VALUE;
        for (unsigned attempt = 0; attempt < 300 && client == INVALID_HANDLE_VALUE; ++attempt) {
            client = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
            if (client == INVALID_HANDLE_VALUE) Sleep(1);
        }
        if (client == INVALID_HANDLE_VALUE) { CancelSynchronousIo(server.native_handle()); break; }
        const auto bytes = winrt::to_string(request) + "\n";
        DWORD sent = 0;
        if (WriteFile(client, bytes.data(), static_cast<DWORD>(bytes.size()), &sent, nullptr))
            responses.emplace_back(winrt::to_hstring(readFrame(client)));
        CloseHandle(client);
    }
    server.join();
    CloseHandle(pipe);
    return responses;
}

struct QueryTreeService : TextTreeService
{
    QueryDiagnostics& diagnostics;
    unsigned chainReads = 0, propertyRows = 0;
    bool failRead = false;
    std::map<InstanceHandle, std::wstring> automationIds;
    std::set<InstanceHandle> nullText;
    std::function<void()> onRead;
    explicit QueryTreeService(QueryDiagnostics& value) : TextTreeService(value.text), diagnostics(value) {}
    HRESULT STDMETHODCALLTYPE GetPropertyValuesChain(InstanceHandle handle, unsigned int* sc, PropertyChainSource** sources,
        unsigned int* pc, PropertyChainValue** values) override
    {
        ++chainReads;
        if (onRead) onRead();
        *sc = 0; *pc = 0; *sources = nullptr; *values = nullptr;
        if (failRead || !diagnostics.elements.count(handle)) return E_FAIL;
        auto& element = diagnostics.elements.at(handle);
        const wchar_t* names[] = {L"Text", L"PlaceholderText", L"AutomationProperties.Name", L"FontSize", L"IsEnabled"};
        const wchar_t* types[] = {L"String", L"String", L"String", L"Double", L"Boolean"};
        const std::wstring data[] = {element.text, element.placeholder, L"Search tasks", L"24", L"False"};
        const auto id = automationIds.find(handle);
        const unsigned count = id == automationIds.end() ? 5 : 6;
        auto* rows = static_cast<PropertyChainValue*>(CoTaskMemAlloc(count * sizeof(PropertyChainValue)));
        if (!rows) return E_OUTOFMEMORY;
        ZeroMemory(rows, count * sizeof(PropertyChainValue));
        for (unsigned i = 0; i < 5; ++i) {
            rows[i].PropertyName = SysAllocString(names[i]);
            rows[i].Type = SysAllocString(types[i]);
            rows[i].ValueType = SysAllocString(types[i]);
            rows[i].Value = SysAllocString(data[i].c_str());
            rows[i].Index = i;
        }
        if (nullText.count(handle)) rows[0].MetadataBits = static_cast<MetadataBit>(0x20);
        if (id != automationIds.end()) {
            rows[5].PropertyName = SysAllocString(L"AutomationProperties.AutomationId");
            rows[5].Type = SysAllocString(L"String");
            rows[5].ValueType = SysAllocString(L"String");
            rows[5].Value = SysAllocString(id->second.c_str());
            rows[5].Index = 5;
        }
        *pc = count; *values = rows; propertyRows += count;
        return S_OK;
    }
};

static QueryDiagnostics* g_queryDiagnostics = nullptr;
static HANDLE g_queryMutationEntered = nullptr, g_queryMutationFinish = nullptr;
static HRESULT QueryMutation(InstanceHandle handle, const std::wstring& property, const std::wstring&,
    const std::wstring& value, bool)
{
    g_queryWriteOnDispatcher = g_insideQueryDispatch;
    g_mutatedHandle = handle;
    if (g_queryMutationEntered) {
        SetEvent(g_queryMutationEntered);
        if (WaitForSingleObject(g_queryMutationFinish, 15000) != WAIT_OBJECT_0) return E_ABORT;
    }
    if (g_queryDiagnostics && property == L"Text") g_queryDiagnostics->elements.at(handle).text = value;
    return S_OK;
}

static void QueryBenchmark(QueryDiagnostics& diagnostics, QueryTreeService& service,
                           const std::function<void(bool, const char*)>& check)
{
    for (InstanceHandle raw = 1000; raw < 1024; ++raw) {
        MintSlot_nolock(raw);
        g_type[raw] = L"Microsoft.UI.Xaml.Controls.TextBlock";
        g_nameOf[raw] = L"Heading" + std::to_wstring(raw);
        diagnostics.elements[raw].textBlock = true;
        diagnostics.elements[raw].text = L"Daylight-like heading " + std::to_wstring(raw);
    }
    const std::wstring enumerate = L"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"VisualTree.enumerate\",\"params\":{}}";
    std::vector<std::wstring> baselineRequests{enumerate};
    std::wstring handles = L"[";
    for (const auto& element : diagnostics.elements) {
        const auto handle = std::to_wstring(PackWire_nolock(element.first));
        baselineRequests.push_back(L"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"Property.get\",\"params\":{\"handle\":\"" + handle + L"\"}}");
        if (handles.size() > 1) handles += L',';
        handles += L"\"" + handle + L"\"";
    }
    handles += L"]";
    std::wstring benchmark = L"{\"fixture\":\"headless synthetic COM; shipping protocol, projection and dispatch body; real local pipes\",\"runs\":[";
    for (unsigned mode = 0; mode < 3; ++mode) {
        const auto requests = mode == 0 ? baselineRequests : mode == 1
            ? std::vector<std::wstring>{enumerate, L"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"VisualTree.getPreviews\",\"params\":{\"handles\":" + handles + L"}}"}
            : std::vector<std::wstring>{L"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"VisualTree.query\",\"params\":{\"fields\":[\"Text\",\"FontSize\"]}}"};
        service.chainReads = 0; service.propertyRows = 0; g_queryDispatches = 0; g_queryDispatchMicros = 0;
        const auto begin = std::chrono::steady_clock::now();
        const auto responses = QueryRoundTrips(requests);
        const auto elapsed = std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - begin).count();
        check(responses.size() == requests.size(), "benchmark executes every real local pipe round trip");
        size_t bytes = 0;
        for (const auto& response : responses) {
            DevToolsJson parsed;
            const bool valid = DevToolsJsonParse(response, parsed) && parsed.Find(L"result") && !parsed.Find(L"error");
            check(valid, "benchmark measures successful protocol results, not error packets");
            if (valid && mode == 2) check(parsed.Find(L"result")->GetBool(L"ok", false), "benchmark query completes successfully");
            bytes += winrt::to_string(response).size();
        }
        std::printf("Query benchmark mode=%u connections=%zu dispatches=%u chains=%u rows=%u dispatch-us=%lld end-to-end-us=%lld wire-bytes=%zu\n",
            mode, responses.size(), g_queryDispatches, service.chainReads, service.propertyRows, g_queryDispatchMicros, elapsed, bytes);
        if (mode == 1) check(service.chainReads == 0 && g_queryDispatches == 1, "batch previews avoid property chains");
        if (mode == 2) check(service.chainReads == diagnostics.elements.size() && g_queryDispatches == 1,
            "arbitrary projection batches dispatch but still retrieves one runtime chain per candidate");
        if (mode) benchmark += L',';
        benchmark += L"{\"mode\":" + std::to_wstring(mode) + L",\"connections\":" + std::to_wstring(responses.size()) +
            L",\"dispatches\":" + std::to_wstring(g_queryDispatches) + L",\"chains\":" + std::to_wstring(service.chainReads) +
            L",\"rows\":" + std::to_wstring(service.propertyRows) + L",\"dispatchMicroseconds\":" + std::to_wstring(g_queryDispatchMicros) +
            L",\"endToEndMicroseconds\":" + std::to_wstring(elapsed) + L",\"wireBytes\":" + std::to_wstring(bytes) + L",\"responses\":[";
        for (size_t i = 0; i < responses.size(); ++i) {
            if (i) benchmark += L',';
            benchmark += L"\"" + DevToolsJsonEscape(responses[i]) + L"\"";
        }
        benchmark += L"]}";
    }
    benchmark += L"]}";
    wchar_t benchmarkPath[32768]{};
    if (GetEnvironmentVariableW(L"WINAPP_QUERY_BENCHMARK_OUT", benchmarkPath, 32768)) {
        std::ofstream output(benchmarkPath, std::ios::binary);
        output << winrt::to_string(benchmark);
        check(output.good(), "benchmark evidence written");
    }
}

static void CheckQueries(const std::function<void(bool, const char*)>& check)
{
    QueryDiagnostics diagnostics;
    QueryTreeService service(diagnostics);
    auto* previousDiagnostics = g_diag; auto* previousService = g_vts3;
    g_diag = &diagnostics; g_vts3 = &service;
    g_queryDiagnostics = &diagnostics;
    g_slots.clear(); g_freeSlots.clear(); g_ihToSlot.clear();
    g_type.clear(); g_children.clear(); g_parent.clear(); g_nameOf.clear(); g_byName.clear();
    g_sessionNonce = 17;
    for (InstanceHandle raw : {901ull, 902ull}) {
        MintSlot_nolock(raw);
        g_type[raw] = raw == 901 ? L"Microsoft.UI.Xaml.Controls.TextBlock" : L"Microsoft.UI.Xaml.Controls.TextBox";
        g_nameOf[raw] = raw == 901 ? L"Heading" : L"SearchBox";
        diagnostics.elements[raw].textBlock = raw == 901;
    }
    diagnostics.elements[901].text = L"Today";
    diagnostics.elements[902].placeholder = L"Search tasks";
    g_dispatcher = reinterpret_cast<IInspectable*>(1);
    g_testEnqueueUiOperation = QueryEnqueue;
    g_testPropertyMutation = QueryMutation;
    DevToolsTrust_InitializePosture(DevToolsAccess::Mutation);
    DevToolsOverlay_SetSurfaceRootsReader(nullptr);
    const auto surfaces = HandleRpc(nullptr, DevToolsRpcParse(
        L"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"Surface.list\",\"params\":{}}"));
    check(surfaces.find(L"\"surfaces\"") != std::wstring::npos &&
        OverlayFixtureSurfaceCandidates() == TapCensusRoots() && !OverlayFixtureSurfaceCandidates().empty(),
        "headless Surface.list supplies live census candidates before overlay construction");
    check(!g_overlayBuilt.load() && !DevToolsOverlay_IsToolbarVisible() && !DevToolsWindow_IsOpen(),
        "headless surface enumeration does not build or display the overlay or inspector");
    auto query = [&](const std::wstring& parameters, bool write = false) {
        const auto request = DevToolsRpcParse(L"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" +
            std::wstring(write ? L"Property.setQuery" : L"VisualTree.query") + L"\",\"params\":" + parameters + L"}");
        return HandleRpc(nullptr, request);
    };
    {
        const auto first = PackWire_nolock(901), second = PackWire_nolock(902);
        auto scope = std::make_shared<TapWindowScope>();
        scope->members.emplace(901, std::make_pair(first, 1ull));
        {
            TapWindowScopeGuard guard(scope);
            InstanceHandle raw = 0;
            const wchar_t* error = nullptr;
            check(ResolveWire_nolock(first, &raw, &error) && raw == 901,
                "window membership preserves the process-wide handle");
            check(!ResolveWire_nolock(second, &raw, &error) && std::wstring(error) == L"outside-window",
                "window membership refuses a live foreign handle");
            CensusSnapshot census;
            census.type = g_type;
            census.wire = {{901, first}, {902, second}};
            const auto identities = census.wire;
            check(FilterWindowCensus(census) && census.type.size() == 1 && census.type.count(901) && census.wire == identities,
                "window census filters traversal without rewriting handle identities");
            scope->members.at(901).first = first + 1;
            check(!ResolveWire_nolock(first, &raw, &error) && std::wstring(error) == L"outside-window",
                "window membership refuses a changed captured generation");
            check(!FilterWindowCensus(census) && census.type.empty(),
                "window snapshot refuses raw handle reuse instead of emitting the replacement node");
        }
        InstanceHandle raw = 0;
        const wchar_t* error = nullptr;
        check(!g_windowScope && ResolveWire_nolock(second, &raw, &error),
            "window guard restores process-wide behavior after request");
        {
            auto picking = std::make_shared<TapWindowScope>();
            picking->members.emplace(901, std::make_pair(first, 1ull));
            picking->members.emplace(902, std::make_pair(second, 1ull));
            picking->pickRoot = 902;
            TapWindowScopeGuard guard(picking);
            check(RootVisual() == 902,
                "window picking uses the captured app panel instead of an arbitrary census root");
            picking->root = 901;
            check(RootVisual() == 901, "explicit pick subtree remains exact");
            picking->root = 0;
            picking->pickRoot = 0;
            picking->members.at(902).second = 2;
            check(RootVisual() == 0, "ambiguous window roots cannot choose a pick subtree");
        }
        const auto arm = HandleRpc(nullptr, DevToolsRpcParse(
            L"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"Selection.arm\",\"params\":{\"root\":\"" +
            std::to_wstring(first) + L"\"}}"));
        check(arm.find(L"root-scope-unsupported") != std::wstring::npos,
            "interactive arming refuses a subtree scope it cannot retain");
        OverlayFixtureSeedHighlight(901);
        const auto refusedHighlight = HandleCommand(L"highlight|" + std::to_wstring(first + 1));
        check(refusedHighlight.rfind(L"ERR ", 0) == 0 && DevToolsOverlay_GetHighlightHandle() == 901,
            "rejected stale highlight preserves the existing highlight");
        OverlayFixtureSeedHighlight(0);
        check(OverlayFixtureDetachedDraftGuards(),
            "detached draft refuses arming and implicit dismissal until explicit Close");
        for (bool write : {false, true}) {
            const auto response = query(write
                ? L"{\"window\":\"18446744073709551615\",\"property\":\"Text\",\"value\":\"No\"}"
                : L"{\"window\":\"18446744073709551615\"}", write);
            check(response.find(L"invalid-window") != std::wstring::npos,
                "invalid HWND refuses reads and writes rather than widening scope");
            const auto invalidRoot = query(write
                ? L"{\"root\":\"not-a-handle\",\"property\":\"Text\",\"value\":\"No\"}"
                : L"{\"root\":\"not-a-handle\"}", write);
            check(invalidRoot.find(L"invalid-root") != std::wstring::npos,
                "invalid root refuses reads and writes rather than widening scope");
        }
        const auto savedParents = g_parent;
        g_parent[902] = 901;
        check(WithinRoot_nolock(901, 901) && WithinRoot_nolock(902, 901) && !WithinRoot_nolock(901, 902),
            "root scope includes itself and descendants but not ancestors");
        g_parent[902] = 902;
        check(!WithinRoot_nolock(902, 901), "root scope refuses cyclic ancestry");
        g_parent = savedParents;
    }
    const auto diagnosticRows = [&](const std::wstring& wire) {
        DevToolsJson parsed;
        const bool valid = DevToolsJsonParse(wire, parsed) && parsed.Find(L"result") &&
            parsed.Find(L"result")->Find(L"diagnostics");
        check(valid, "exact query diagnostic payload parses");
        return valid ? *parsed.Find(L"result")->Find(L"diagnostics") : DevToolsJson{};
    };
    for (bool write : {false, true}) {
        g_queryDispatches = 0; service.chainReads = 0; g_mutatedHandle = 0;
        const auto invalid = query(write
            ? L"{\"with\":[\"FontSize>>20\"],\"property\":\"Text\",\"value\":\"No\"}"
            : L"{\"with\":[\"FontSize>>20\"]}", write);
        check(invalid.find(L"bad-args") != std::wstring::npos && g_queryDispatches == 0 &&
            service.chainReads == 0 && g_mutatedHandle == 0,
            "malformed ordered predicate refuses read and write before dispatch or property traversal");
    }
    const auto incompatible = query(L"{\"ofType\":\"TextBlock\",\"with\":[\"Text>20\"]}");
    check(incompatible.find(L"ordered-comparison-requires-number") != std::wstring::npos &&
        incompatible.find(L"\"error\":\"incomplete\"") != std::wstring::npos,
        "valid operator with incompatible runtime type remains explicitly unevaluated");
    g_queryDispatches = 0;
    const auto projected = query(L"{\"ofType\":\"TextBox\",\"fields\":[\"Text\",\"PlaceholderText\",\"AutomationProperties.Name\",\"Missing\"]}");
    check(projected.find(L"Search tasks") != std::wstring::npos &&
        projected.find(L"PlaceholderText") != std::wstring::npos &&
        projected.find(L"AutomationProperties.Name") != std::wstring::npos, "query returns placeholder separately from accessibility name");
    check(projected.find(L"\"value\":\"\"") != std::wstring::npos &&
        projected.find(L"\"valueState\":\"unavailable\"") != std::wstring::npos, "empty string differs from missing projection");
    check(g_queryDispatches == 1 && projected.find(L"\"propertyReads\":1") != std::wstring::npos &&
        projected.find(L"\"propertyRows\":5") != std::wstring::npos, "one dispatch and one chain only after cheap type filtering");
    const auto unknown = query(L"{\"with\":[\"Missing!=Save\"],\"mode\":\"one\"}");
    std::printf("Query initial diagnostic result: %s\n", winrt::to_string(unknown).c_str());
    const auto savedSourceInfo = g_sourceInfo;
    g_sourceInfo[901] = {L"ms-appx:///Views/MainPage.xaml", g_sourceEpoch.load()};
    check(query(L"{\"ofType\":\"TextBlock\",\"filter\":\"Views/MainPage.xaml\"}").find(L"\"ok\":true") != std::wstring::npos,
        "inspect filter still matches source paths with structured criteria");
    check(query(L"{\"ofType\":\"TextBlock\",\"filter\":\"Today\"}").find(L"no-match") != std::wstring::npos,
        "inspect filter is not silently changed to visible-text search");
    for (const auto* text : {L"Today", L"Heading", L"#Heading", L"TextBlock", L"Views/MainPage.xaml", L"Search tasks"}) {
        DevToolsJson found, projectedSearch;
        const bool decoded = DevToolsJsonParse(CmdFind(text, false), found) &&
            DevToolsJsonParse(query(L"{\"text\":\"" + std::wstring(text) +
                L"\",\"fields\":[\"FontSize\"]}"), projectedSearch);
        const auto* result = decoded ? projectedSearch.Find(L"result") : nullptr;
        const auto handles = [](const DevToolsJson* rows) {
            std::set<std::wstring> values;
            if (rows) for (const auto& row : rows->arr) values.insert(row.GetString(L"handle"));
            return values;
        };
        check(result && result->GetBool(L"complete", false) &&
            !handles(found.Find(L"matches")).empty() &&
            handles(found.Find(L"matches")) == handles(result->Find(L"matches")),
            "search fields preserve the complete plain-search identity set for text, name and source file");
    }
    const auto projectedText = query(L"{\"text\":\"Today\",\"fields\":[\"FontSize\"]}");
    check(projectedText.find(L"\"propertyReads\":1") != std::wstring::npos &&
        projectedText.find(L"\"propertyRows\":5") != std::wstring::npos,
        "text filtering precedes property projection on nonmatching elements");
    const auto unrelatedUnknown = query(L"{\"text\":\"not-present\",\"with\":[\"Missing==x\"],\"fields\":[\"FontSize\"]}");
    check(unrelatedUnknown.find(L"\"complete\":true") != std::wstring::npos &&
        unrelatedUnknown.find(L"\"propertyReads\":0") != std::wstring::npos &&
        unrelatedUnknown.find(L"\"unevaluated\":0") != std::wstring::npos,
        "text-excluded elements neither project fields nor make a definite nonmatch unevaluated");
    diagnostics.onInspect = [] { Sleep(550); };
    const auto slowPreview = query(L"{\"text\":\"Today\",\"fields\":[\"FontSize\"]}");
    diagnostics.onInspect = {};
    check(slowPreview.find(L"\"complete\":false") != std::wstring::npos &&
        slowPreview.find(L"\"truncated\":true") != std::wstring::npos &&
        slowPreview.find(L"\"propertyReads\":0") != std::wstring::npos,
        "a preview exceeding the 500ms budget reports incompleteness before projecting fields");
    g_sourceInfo = savedSourceInfo;
    check(unknown.find(L"\"unevaluated\":2") != std::wstring::npos &&
        unknown.find(L"\"error\":\"incomplete\"") != std::wstring::npos, "missing inequality blocks exact targeting");
    const auto noMatch = query(L"{\"ofType\":\"Button\"}");
    check(noMatch.find(L"\"error\":\"no-match\"") != std::wstring::npos &&
        noMatch.find(L"\"complete\":true") != std::wstring::npos, "complete zero-match is explicit");
    check(query(L"{\"fields\":[\"FontSize\",\"IsEnabled\"]}").find(L"\"value\":24") != std::wstring::npos,
        "query numeric JSON values are typed");
    check(query(L"{\"fields\":[\"FontSize\",\"IsEnabled\"]}").find(L"\"value\":false") != std::wstring::npos,
        "query boolean JSON values are typed");
    check(query(L"{\"with\":[\"FontSize>=20\",\"IsEnabled==false\"]}").find(L"\"evaluated\":2") != std::wstring::npos,
        "repeated typed predicates evaluate both candidates");
    g_mutatedHandle = 0; g_queryDispatches = 0; g_queryWriteOnDispatcher = false;
    const auto written = query(L"{\"ofType\":\"TextBlock\",\"with\":[\"Text==Today\"],\"property\":\"Text\",\"value\":\"Tomorrow\"}", true);
    check(written.find(L"\"status\":\"applied\"") != std::wstring::npos && g_mutatedHandle == 901 &&
        diagnostics.elements[901].text == L"Tomorrow" && g_queryWriteOnDispatcher && g_queryDispatches == 1,
        "query uniqueness, current predicate and write use one dispatched operation");
    check(written.find(L"\"before\":") != std::wstring::npos && written.find(L"\"after\":") != std::wstring::npos &&
        written.find(L"Tomorrow") != std::wstring::npos, "write observes before and after");
    diagnostics.elements[902].text = L"Tomorrow";
    g_mutatedHandle = 0;
    const auto duplicate = query(L"{\"with\":[\"Text==Tomorrow\"],\"property\":\"Text\",\"value\":\"No\"}", true);
    check(g_mutatedHandle == 0 && duplicate.find(L"ambiguous") != std::wstring::npos, "two matches refuse mutation");
    unsigned reads = 0;
    service.onRead = [&] { if (++reads == 2) diagnostics.elements[901].text = L"Upcoming"; };
    const auto changed = query(L"{\"ofType\":\"TextBlock\",\"with\":[\"Text==Tomorrow\"],\"property\":\"Text\",\"value\":\"No\"}", true);
    check(g_mutatedHandle == 0 && changed.find(L"target-changed") != std::wstring::npos, "current predicate change refuses mutation");
    service.onRead = {};
    diagnostics.elements[901].text = L"Unique";
    diagnostics.elements[902].text = L"Different";
    reads = 0;
    service.onRead = [&] { if (++reads == 3) diagnostics.elements[902].text = L"Unique"; };
    const auto newMatch = query(L"{\"with\":[\"Text==Unique\"],\"property\":\"Text\",\"value\":\"No\"}", true);
    check(g_mutatedHandle == 0 && newMatch.find(L"target-changed") != std::wstring::npos, "new scoped match before commit refuses mutation");
    service.onRead = {};
    reads = 0;
    service.onRead = [&] { if (++reads == 2) { FreeSlot_nolock(901); MintSlot_nolock(901); } };
    check(query(L"{\"ofType\":\"TextBlock\",\"property\":\"Text\",\"value\":\"No\"}", true).find(L"target-changed") != std::wstring::npos &&
        g_mutatedHandle == 0, "recycled raw target cannot authorize a write");
    service.onRead = {};
    service.failRead = true;
    check(query(L"{\"with\":[\"Text==Tomorrow\"],\"mode\":\"one\"}").find(L"unreadable") != std::wstring::npos,
        "getter failure cannot prove unique match");
    service.failRead = false;
    for (bool reversed : {false, true}) {
        const std::wstring predicates = reversed
            ? L"\"Text==Spaces\",\"AutomationProperties.AutomationId==SpacesHeading\""
            : L"\"AutomationProperties.AutomationId==SpacesHeading\",\"Text==Spaces\"";
        const auto parameters = L"{\"with\":[" + predicates + L"],\"property\":\"Text\",\"value\":\"Updated\"}";
        diagnostics.elements[901].text = L"Spaces";
        diagnostics.elements[902].canRead = false;
        service.automationIds = {{901, L"SpacesHeading"}, {902, L"Other"}};
        service.nullText = {902};
        for (const auto* mode : {L"many", L"one"}) {
            const auto read = query(L"{\"with\":[" + predicates + L"],\"mode\":\"" + mode + L"\"}");
            check(read.find(L"\"ok\":true") != std::wstring::npos &&
                read.find(L"\"unevaluated\":0") != std::wstring::npos,
                "read-many and exact read exclude false ID despite null Text in both orders");
        }
        service.failRead = true; g_mutatedHandle = 0;
        check(query(parameters, true).find(L"incomplete") != std::wstring::npos && g_mutatedHandle == 0,
            "whole-chain failure blocks initial enumeration");
        service.failRead = false;
        g_mutatedHandle = 0; g_queryDispatches = 0;
        const auto excluded = query(parameters, true);
        check(excluded.find(L"\"status\":\"applied\"") != std::wstring::npos && g_mutatedHandle == 901 &&
            g_queryDispatches == 1 && excluded.find(L"\"unevaluated\":0") != std::wstring::npos,
            "false ID excludes null Text in both orders using one guarded dispatch");
        const auto excludedDetails = diagnosticRows(excluded);
        check(excludedDetails.Find(L"candidates") && excludedDetails.Find(L"candidates")->arr.empty() &&
            excludedDetails.GetInt(L"omitted", -1) == 0, "false ID produces no unresolved diagnostic");
        diagnostics.elements[901].text = L"Spaces";
        diagnostics.elements[902].canRead = true;
        diagnostics.elements[902].readResult = E_FAIL;
        check(query(parameters, true).find(L"\"status\":\"applied\"") != std::wstring::npos &&
            g_mutatedHandle == 901, "false ID excludes failed individual Text getter");
        diagnostics.elements[902].readResult = S_OK;
        diagnostics.elements[902].canRead = false;
        const auto projectedNull = query(L"{\"with\":[\"AutomationProperties.AutomationId==Other\"],\"fields\":[\"Text\"]}");
        check(projectedNull.find(L"\"valueState\":\"null\"") != std::wstring::npos &&
            projectedNull.find(L"\"value\":null") != std::wstring::npos, "excluded null Text remains null when projected");
        diagnostics.elements[901].text = L"Spaces";
        service.automationIds[902] = L"SpacesHeading";
        g_mutatedHandle = 0; g_queryDispatches = 0; service.chainReads = 0;
        const auto refused = query(parameters, true);
        check(refused.find(L"incomplete") != std::wstring::npos && g_mutatedHandle == 0,
            "matching ID plus null Text blocks write in both orders");
        const auto details = diagnosticRows(refused);
        const auto* candidates = details.Find(L"candidates");
        check(candidates && candidates->arr.size() == 1 &&
            candidates->arr.front().GetString(L"selector") == std::to_wstring(PackWire_nolock(902)) &&
            candidates->arr.front().GetString(L"phase") == L"initial" &&
            candidates->arr.front().GetString(L"reason") == L"null" &&
            candidates->arr.front().Find(L"predicates") &&
            candidates->arr.front().Find(L"predicates")->arr[reversed ? 0 : 1].GetString(L"truth") == L"unknown" &&
            candidates->arr.front().Find(L"predicates")->arr[reversed ? 1 : 0].GetString(L"truth") == L"true",
            "null refusal identifies candidate and each predicate in either order");
        check(g_queryDispatches == 1 && service.chainReads == 2 && g_mutatedHandle == 0,
            "initial diagnostics add no dispatch chains or writes");
        service.automationIds.erase(902);
        const auto bothUnknown = query(parameters, true);
        check(bothUnknown.find(L"incomplete") != std::wstring::npos && g_mutatedHandle == 0,
            "missing ID plus null Text blocks write in both orders");
        const auto bothDetails = diagnosticRows(bothUnknown);
        const auto* bothCandidates = bothDetails.Find(L"candidates");
        check(bothCandidates && bothCandidates->arr.size() == 1 &&
            bothCandidates->arr.front().Find(L"predicates") &&
            bothCandidates->arr.front().Find(L"predicates")->arr[0].GetString(L"truth") == L"unknown" &&
            bothCandidates->arr.front().Find(L"predicates")->arr[1].GetString(L"truth") == L"unknown",
            "both unknown predicates remain independently identified");
        for (bool stillUnknown : {false, true}) {
            service.automationIds[902] = L"Other";
            reads = 0;
            service.onRead = [&] {
                if (++reads == 3) {
                    service.automationIds[902] = L"SpacesHeading";
                    if (!stillUnknown) {
                        service.nullText.clear();
                        diagnostics.elements[902].canRead = true;
                        diagnostics.elements[902].text = L"Spaces";
                    }
                }
            };
            g_queryDispatches = 0; service.chainReads = 0;
            const auto finalRefusal = query(parameters, true);
            if (reversed && stillUnknown)
                std::printf("Query final diagnostic result: %s\n", winrt::to_string(finalRefusal).c_str());
            check(finalRefusal.find(L"target-changed") != std::wstring::npos && g_mutatedHandle == 0,
                "excluded ID becoming extra or uncertain match at final guard blocks write");
            const auto finalDetails = diagnosticRows(finalRefusal);
            const auto* finalCandidates = finalDetails.Find(L"candidates");
            check(finalCandidates && finalCandidates->arr.size() == (stillUnknown ? 1u : 0u) &&
                (!stillUnknown || (finalCandidates->arr.front().GetString(L"phase") == L"final" &&
                    finalCandidates->arr.front().GetString(L"selector") == std::to_wstring(PackWire_nolock(902)))),
                "final uncertainty records only already observed unresolved candidate");
            check(g_queryDispatches == 1 && service.chainReads == 4 && g_mutatedHandle == 0,
                "final diagnostics add no dispatch chains or writes");
            service.onRead = {};
            service.nullText = {902};
            diagnostics.elements[902].canRead = false;
        }
        service.automationIds[902] = L"Other";
        reads = 0;
        service.onRead = [&] { if (++reads == 3) service.failRead = true; };
        check(query(parameters, true).find(L"target-changed") != std::wstring::npos && g_mutatedHandle == 0,
            "whole-chain acquisition failure still blocks final guard");
        service.onRead = {}; service.failRead = false;
    }
    service.automationIds.clear(); service.nullText.clear();
    diagnostics.elements[902].canRead = true;
    for (InstanceHandle raw = 903; raw < 936; ++raw) {
        MintSlot_nolock(raw);
        g_type[raw] = L"Microsoft.UI.Xaml.Controls.TextBox";
        diagnostics.elements[raw] = {};
    }
    service.chainReads = 0; g_queryDispatches = 0; g_mutatedHandle = 0;
    const auto capped = query(L"{\"with\":[\"Missing==x\"],\"property\":\"Text\",\"value\":\"No\"}", true);
    const auto cappedDetails = diagnosticRows(capped);
    check(cappedDetails.Find(L"candidates") && cappedDetails.Find(L"candidates")->arr.size() == 32 &&
        cappedDetails.GetInt(L"omitted", -1) == 3 && cappedDetails.GetBool(L"truncated", false),
        "candidate cap discloses omitted diagnostics without limiting evaluation");
    check(service.chainReads == 35 && g_queryDispatches == 1 && g_mutatedHandle == 0 &&
        capped.find(L"\"unevaluated\":35") != std::wstring::npos,
        "capped diagnostics preserve scope evaluation reads dispatch and refusal");
    for (InstanceHandle raw = 903; raw < 936; ++raw) {
        FreeSlot_nolock(raw); g_type.erase(raw); diagnostics.elements.erase(raw);
    }
    const auto savedName = g_nameOf[901];
    g_nameOf[901] = L"Quoted \"name\"\n\\\u0416";
    const auto escaped = diagnosticRows(query(L"{\"ofType\":\"TextBlock\",\"with\":[\"Missing==x\"],\"mode\":\"one\"}"));
    check(escaped.Find(L"candidates") && escaped.Find(L"candidates")->arr.size() == 1 &&
        escaped.Find(L"candidates")->arr.front().Find(L"census") &&
        escaped.Find(L"candidates")->arr.front().Find(L"census")->GetString(L"name") == g_nameOf[901],
        "diagnostic census identity escapes quotes newlines backslash and Unicode");
    g_nameOf[901] = std::wstring(40000, L'"');
    service.chainReads = 0; g_queryDispatches = 0;
    const auto byteCapped = diagnosticRows(query(L"{\"ofType\":\"TextBlock\",\"with\":[\"Missing==x\"],\"mode\":\"one\"}"));
    check(byteCapped.Find(L"candidates") && byteCapped.Find(L"candidates")->arr.empty() &&
        byteCapped.GetInt(L"omitted", -1) == 1 && byteCapped.GetBool(L"truncated", false) &&
        service.chainReads == 1 && g_queryDispatches == 1,
        "byte cap omits oversized identity without extra reads or silent clipping");
    g_nameOf[901] = savedName;
    service.chainReads = 0; g_queryDispatches = 0;
    const auto globalFailure = diagnosticRows(query(L"{\"rootHandle\":\"123\",\"with\":[\"Missing==x\"],\"mode\":\"one\"}"));
    check(globalFailure.Find(L"candidates") && globalFailure.Find(L"candidates")->arr.empty() &&
        globalFailure.GetInt(L"omitted", -1) == 0 && service.chainReads == 0 && g_queryDispatches == 1,
        "global stale-root failure does not invent candidate evidence");
    TapQueryRow ambiguousRow{901, { }, {}, {}};
    ambiguousRow.node.handle = PackWire_nolock(901);
    DevToolsReadProp diagnosticProperty;
    diagnosticProperty.name = L"Text"; diagnosticProperty.type = L"String"; diagnosticProperty.value = L"PRIVATE_RAW_VALUE";
    ambiguousRow.properties = {diagnosticProperty, diagnosticProperty};
    CensusSnapshot diagnosticCensus;
    DevToolsJson ambiguousDetails;
    const auto ambiguousJson = QueryDiagnosticsJson({{ambiguousRow, L"initial", L"ambiguous-property"}}, 0,
        diagnosticCensus, {{L"Text", L"==", L"x"}});
    check(DevToolsJsonParse(ambiguousJson, ambiguousDetails) &&
        ambiguousJson.find(L"\"truth\":\"unknown\"") != std::wstring::npos &&
        ambiguousJson.find(L"\"valueState\":\"ambiguous\"") != std::wstring::npos &&
        ambiguousJson.find(L"PRIVATE_RAW_VALUE") == std::wstring::npos,
        "duplicate property diagnostics never pick a value or expose raw values");
    ambiguousRow.readError = L"E_FAIL";
    const auto unreadableJson = QueryDiagnosticsJson({{ambiguousRow, L"final", L"unreadable"}}, 0,
        diagnosticCensus, {{L"Text", L"==", L"x"}});
    check(unreadableJson.find(L"\"truth\":\"unknown\",\"reason\":\"unreadable\"") != std::wstring::npos,
        "whole-chain failure overrides partial predicate observations");
    ambiguousRow.node.name = std::wstring(32000, L'"');
    const auto nearBudget = QueryDiagnosticsJson({{ambiguousRow, L"final", L"unreadable"}}, 0,
        diagnosticCensus, {{L"Text", L"==", L"x"}});
    check(winrt::to_string(nearBudget).size() <= 64 * 1024 && nearBudget.find(L"\"omitted\":0") != std::wstring::npos,
        "near-budget escaped diagnostic stays within the actual UTF-8 byte limit");
    service.onRead = [] { Sleep(550); };
    check(query(L"{\"ofType\":\"TextBlock\",\"property\":\"Text\",\"value\":\"No\"}", true).find(L"incomplete") != std::wstring::npos &&
        g_mutatedHandle == 0, "over-budget last candidate cannot authorize mutation");
    service.onRead = {};
    const auto exactRead = query(L"{\"ofType\":\"TextBlock\",\"mode\":\"one\"}");
    check(exactRead.find(L"\"properties\":{\"handle\":") != std::wstring::npos &&
        exactRead.find(L"\"authoredState\":") != std::wstring::npos, "exact read preserves existing full property envelope");
    g_children[901] = {902}; g_parent[902] = 901;
    const auto subtree = query(L"{\"rootHandle\":\"" + std::to_wstring(PackWire_nolock(901)) +
        L"\",\"ofType\":\"TextBox\",\"fields\":[\"Text\"],\"depth\":1}");
    check(subtree.find(L"\"context\":true") != std::wstring::npos &&
        subtree.find(L"\"scopeNodes\":2") != std::wstring::npos, "filtered subtree retains contextual ancestor");
    g_children.clear(); g_parent.clear();
    check(query(L"{\"authored\":true,\"ofType\":\"TextBlock\",\"mode\":\"one\"}").find(L"bad-args") != std::wstring::npos,
        "authored-only partial scope cannot authorize exact targeting");
    check(query(L"{\"depth\":1,\"ofType\":\"TextBlock\",\"mode\":\"one\"}").find(L"bad-args") != std::wstring::npos,
        "depth-limited scope cannot authorize exact targeting");
    InstanceHandle previous = 901;
    for (InstanceHandle raw = 10000; raw < 10000 + kMaxEnumDepth + 2; ++raw) {
        MintSlot_nolock(raw); g_type[raw] = L"Test.Container";
        g_children[previous] = {raw}; g_parent[raw] = previous; previous = raw;
    }
    check(query(L"{\"rootHandle\":\"" + std::to_wstring(PackWire_nolock(901)) +
        L"\",\"ofType\":\"TextBlock\",\"property\":\"Text\",\"value\":\"No\"}", true).find(L"incomplete") != std::wstring::npos &&
        g_mutatedHandle == 0, "internal depth cap cannot prove complete subtree uniqueness");
    for (InstanceHandle raw = 10000; raw < 10000 + kMaxEnumDepth + 2; ++raw) {
        FreeSlot_nolock(raw); g_type.erase(raw); g_children.erase(raw); g_parent.erase(raw);
    }
    g_children.clear();
    for (InstanceHandle raw = 10000; raw < 10000 + kMaxTreeWalk; ++raw) {
        MintSlot_nolock(raw); g_type[raw] = L"Test.Container";
    }
    check(query(L"{\"ofType\":\"TextBlock\",\"property\":\"Text\",\"value\":\"No\"}", true).find(L"\"truncated\":true") != std::wstring::npos &&
        g_mutatedHandle == 0, "node-capped census cannot authorize mutation");
    for (InstanceHandle raw = 10000; raw < 10000 + kMaxTreeWalk; ++raw) {
        FreeSlot_nolock(raw); g_type.erase(raw);
    }
    DevToolsReadProp field;
    field.name = L"Content"; field.type = L"String";
    for (const auto* state : {L"null", L"unset", L"unresolved", L"unreadable"}) {
        field.valueState = state;
        check(QueryValueJson(&field, field.name, L"").find(L"\"valueState\":\"" + std::wstring(state) + L"\"") != std::wstring::npos,
            "projection preserves distinct unavailable states");
    }
    field.valueState.clear(); field.type = L"Microsoft.UI.Xaml.Controls.StackPanel"; field.value = L"Not text";
    check(QueryValueJson(&field, field.name, L"").find(L"\"valueState\":\"object\"") != std::wstring::npos,
        "projected object is not an invented string");
    g_type[902] = L"Custom.TextBlock";
    check(query(L"{\"ofType\":\"TextBlock\"}").find(L"ambiguous-type") != std::wstring::npos, "short type collision refuses first-match choice");
    g_type[902] = L"Microsoft.UI.Xaml.Controls.TextBox";
    g_children[901] = {999};
    check(query(L"{\"ofType\":\"TextBlock\",\"property\":\"Text\",\"value\":\"No\"}", true).find(L"incomplete") != std::wstring::npos &&
        g_mutatedHandle == 0, "unknown child coverage refuses mutation");
    g_children.clear();
    check(query(L"{\"ofType\":3}").find(L"bad-args") != std::wstring::npos, "typed protocol admission rejects numeric type");
    check(query(L"{\"ofType\":\"TextBlock\",\"property\":\"Text\",\"value\":false}", true).find(L"bad-args") != std::wstring::npos,
        "typed protocol admission rejects non-string literal");
    const auto stale = PackWire_nolock(901);
    FreeSlot_nolock(901); MintSlot_nolock(901);
    check(query(L"{\"rootHandle\":\"" + std::to_wstring(stale) + L"\",\"ofType\":\"TextBlock\",\"property\":\"Text\",\"value\":\"No\"}", true)
        .find(L"stale-handle") != std::wstring::npos && g_mutatedHandle == 0, "stale root fails closed");
    for (bool running : {false, true}) {
        diagnostics.elements[901].text = L"Before";
        g_mutatedHandle = 0;
        g_testEnqueueUiOperation = HoldEnqueue;
        if (running) {
            g_queryMutationEntered = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            g_queryMutationFinish = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        }
        std::wstring response;
        std::thread caller([&] { response = query(L"{\"ofType\":\"TextBlock\",\"property\":\"Text\",\"value\":\"After timeout\"}", true); });
        IUnknown* handler = nullptr;
        {
            std::unique_lock<std::mutex> lock(g_heldMutex);
            if (g_heldReady.wait_for(lock, std::chrono::seconds(3), [] { return g_heldHandler != nullptr; })) {
                handler = g_heldHandler; g_heldHandler = nullptr;
            }
        }
        std::thread dispatcher;
        const auto invoke = [&] {
            if (!handler) return;
            IDispatcherQueueHandler* callback = nullptr;
            if (SUCCEEDED(handler->QueryInterface(IID_PPV_ARGS(&callback)))) { callback->Invoke(); callback->Release(); }
        };
        if (running && handler) {
            dispatcher = std::thread(invoke);
            check(WaitForSingleObject(g_queryMutationEntered, 3000) == WAIT_OBJECT_0, "delayed write reached actual dispatch");
        }
        caller.join();
        check(response.find(running ? L"indeterminate" : L"not-applied") != std::wstring::npos,
            "timeout distinguishes running write from cancelled pending operation");
        if (running) {
            SetEvent(g_queryMutationFinish);
            if (dispatcher.joinable()) dispatcher.join();
            CloseHandle(g_queryMutationEntered); CloseHandle(g_queryMutationFinish);
            g_queryMutationEntered = nullptr; g_queryMutationFinish = nullptr;
        } else invoke();
        if (handler) handler->Release();
        check(running ? diagnostics.elements[901].text == L"After timeout" : g_mutatedHandle == 0,
            "running write can finish later; pending operation never mutates");
        g_testEnqueueUiOperation = QueryEnqueue;
        if (running) check(query(L"{\"ofType\":\"TextBlock\",\"mode\":\"one\",\"property\":\"Text\"}").find(L"After timeout") != std::wstring::npos,
            "indeterminate write reconciles with a read, not a retry");
    }
    QueryBenchmark(diagnostics, service, check);
    std::wprintf(L"Query fixture projected result: %s\n", projected.c_str());
    g_diag = previousDiagnostics; g_vts3 = previousService;
    g_queryDiagnostics = nullptr; g_testPropertyMutation = nullptr; g_testEnqueueUiOperation = nullptr; g_dispatcher = nullptr;
    g_slots.clear(); g_freeSlots.clear(); g_ihToSlot.clear();
    g_type.clear(); g_children.clear(); g_parent.clear(); g_nameOf.clear(); g_byName.clear();
}
