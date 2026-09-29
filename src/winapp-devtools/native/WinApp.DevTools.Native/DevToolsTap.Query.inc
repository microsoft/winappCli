// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Included by DevToolsTap.cpp: query reads and guarded writes share its census and property implementation.
#include "DevToolsQuery.h"

struct TapQueryRequest
{
    std::wstring type, text, filter, property, value, writeType;
    std::vector<DevToolsQuery::Predicate> predicates;
    std::vector<std::wstring> fields;
    unsigned long long root = 0;
    int depth = -1;
    bool authored = false, exact = false, write = false;
};

struct TapQueryRow
{
    InstanceHandle raw = 0;
    DevToolsWindowNode node;
    std::vector<DevToolsReadProp> properties;
    std::wstring readError;
    std::vector<DevToolsBatch::PreviewValue> preview;
};

static const DevToolsReadProp* QueryProperty(const TapQueryRow& row, const std::wstring& name)
{
    for (const auto& property : row.properties) if (property.name == name) return &property;
    return nullptr;
}

static std::wstring QueryValueJson(const DevToolsReadProp* property, const std::wstring& name,
                                   const std::wstring& error, bool truncated = false)
{
    std::wstring state = property ? property->valueState : error.empty() ? L"unavailable" : L"unreadable";
    std::wstring type = property ? property->type : L"";
    std::wstring value = L"null";
    if (property && (state.empty() || state == L"binding")) {
        const auto shortType = DevToolsQuery::ShortType(type);
        long double number = 0;
        if (DevToolsQuery::Numeric(type) && DevToolsQuery::Number(property->value, number)) {
            if (shortType == L"Double" || shortType == L"Single") {
                char buffer[64];
                const auto converted = std::to_chars(buffer, buffer + sizeof(buffer), static_cast<double>(number));
                if (converted.ec == std::errc{}) value.assign(buffer, converted.ptr);
                else state = L"unresolved";
            } else {
                unsigned long long magnitude = 0; bool negative = false;
                if (DevToolsQuery::Integer(property->value, shortType.front() == L'U' || shortType == L"Byte", magnitude, negative))
                    value = (negative ? L"-" : L"") + std::to_wstring(magnitude);
                else state = L"unresolved";
            }
        } else if (shortType == L"Boolean") {
            if (property->value == L"True" || property->value == L"true" || property->value == L"1") value = L"true";
            else if (property->value == L"False" || property->value == L"false" || property->value == L"0") value = L"false";
            else state = L"unresolved";
        } else if (shortType == L"String" || !property->enumValues.empty())
            value = L"\"" + DevToolsJsonEscape(property->value) + L"\"";
        else state = DevToolsQuery::Numeric(type) ? L"unset" : L"object";
    }
    if (state.empty()) state = L"value";
    std::wstring out = L"{\"name\":\"" + DevToolsJsonEscape(name) + L"\",\"value\":" + value +
        L",\"valueType\":\"" + DevToolsJsonEscape(type) + L"\",\"valueState\":\"" + DevToolsJsonEscape(state) +
        L"\",\"bindingState\":\"" + (property && (!property->binding.empty() || property->valueState == L"binding") ? L"known" : L"unknown") + L"\"";
    if (property && !property->source.empty()) out += L",\"valueSource\":\"" + DevToolsJsonEscape(property->source) + L"\"";
    if (!error.empty()) out += L",\"reason\":\"" + DevToolsJsonEscape(error) + L"\"";
    if (truncated) out += L",\"truncated\":true";
    return out + L"}";
}

static std::wstring QueryNodeJson(const TapQueryRow& row, const CensusSnapshot& cs, bool context,
                                 const std::vector<std::wstring>& fields)
{
    const auto& node = row.node;
    std::wstring out = L"{\"handle\":\"" + std::to_wstring(node.handle) +
        L"\",\"name\":\"" + DevToolsJsonEscape(node.name) + L"\",\"type\":\"" + DevToolsJsonEscape(node.type) +
        L"\",\"depth\":" + std::to_wstring(node.depth) + L",\"context\":" + (context ? L"true" : L"false");
    const auto identity = cs.id.find(row.raw);
    const auto parent = cs.parent.find(row.raw);
    if (identity != cs.id.end()) out += L",\"id\":\"" + DevToolsJsonEscape(identity->second) + L"\"";
    out += L",\"uniqueName\":" + std::wstring(cs.uniqueName.count(row.raw) ? L"true" : L"false");
    if (parent != cs.parent.end()) {
        const auto wire = cs.wire.find(parent->second);
        if (wire != cs.wire.end()) out += L",\"parentHandle\":\"" + std::to_wstring(wire->second) + L"\"";
    }
    out += L",\"fields\":[";
    if (!context) for (size_t i = 0; i < fields.size(); ++i) {
        if (i) out += L',';
        out += QueryValueJson(QueryProperty(row, fields[i]), fields[i], row.readError);
    }
    if (!context && fields.empty()) for (size_t i = 0; i < row.preview.size(); ++i) {
        if (i) out += L',';
        const auto& value = row.preview[i];
        DevToolsReadProp property;
        property.name = value.name; property.type = value.valueType;
        property.valueState = value.valueState == L"value" ? L"" : value.valueState;
        const bool cut = value.value.size() > 120;
        property.value = value.value.substr(0, 120);
        out += QueryValueJson(&property, property.name, L"", cut);
    }
    return out + L"]}";
}

struct TapQueryOutcome
{
    std::wstring json;
    std::atomic<bool> started{false};
};

struct TapQueryDiagnostic
{
    TapQueryRow row;
    std::wstring phase, reason;
};

static std::wstring QueryDiagnosticsJson(const std::vector<TapQueryDiagnostic>& candidates, size_t omitted,
    const CensusSnapshot& cs, const std::vector<DevToolsQuery::Predicate>& predicates)
{
    std::wstring entries;
    size_t bytes = 128;
    for (const auto& candidate : candidates) {
        const auto& row = candidate.row;
        std::wstring entry = L"{\"selector\":\"" + std::to_wstring(row.node.handle) +
            L"\",\"census\":" + QueryNodeJson(row, cs, false, {}) +
            L",\"phase\":\"" + candidate.phase + L"\",\"reason\":\"" + DevToolsJsonEscape(candidate.reason) +
            L"\",\"predicates\":[";
        for (size_t i = 0; i < predicates.size(); ++i) {
            const auto& predicate = predicates[i];
            const DevToolsReadProp* selected = nullptr;
            size_t count = 0;
            for (const auto& property : row.properties) if (property.name == predicate.property) {
                selected = &property;
                ++count;
            }
            if (count != 1) selected = nullptr;
            auto evaluation = DevToolsQuery::Compare(selected, predicate);
            if (count > 1) evaluation = {DevToolsQuery::Verdict::Unevaluated, L"ambiguous-property"};
            if (!row.readError.empty()) evaluation = {DevToolsQuery::Verdict::Unevaluated, L"unreadable"};
            const auto truth = evaluation.verdict == DevToolsQuery::Verdict::Match ? L"true" :
                evaluation.verdict == DevToolsQuery::Verdict::NoMatch ? L"false" : L"unknown";
            if (i) entry += L',';
            entry += L"{\"property\":\"" + DevToolsJsonEscape(predicate.property) +
                L"\",\"operator\":\"" + DevToolsJsonEscape(predicate.op) + L"\",\"truth\":\"" + truth +
                L"\",\"reason\":\"" + DevToolsJsonEscape(evaluation.reason) +
                L"\",\"valueType\":\"" + DevToolsJsonEscape(selected ? selected->type : L"") +
                L"\",\"valueState\":\"" + DevToolsJsonEscape(!row.readError.empty() ? L"unreadable" :
                    count > 1 ? L"ambiguous" : !selected ? L"unavailable" :
                    selected->valueState.empty() ? L"value" : selected->valueState) + L"\"}";
        }
        entry += L"]}";
        const size_t cost = winrt::to_string(entry).size() + 1;
        if (cost > 64 * 1024 - bytes) { ++omitted; continue; }
        bytes += cost;
        if (!entries.empty()) entries += L',';
        entries += entry;
    }
    return L"{\"candidates\":[" + entries + L"],\"omitted\":" + std::to_wstring(omitted) +
        L",\"truncated\":" + (omitted ? L"true" : L"false") + L"}";
}

static bool QuerySnapshotCurrent(const CensusSnapshot& cs)
{
    CensusSharedLock lock;
    if (cs.type != g_type || cs.name != g_nameOf || cs.parent != g_parent || cs.children != g_children) return false;
    for (const auto& entry : cs.wire) if (PackWire_nolock(entry.first) != entry.second) return false;
    return true;
}

static std::wstring ExecuteQuery_ui(const TapQueryRequest& request)
{
    const auto started = std::chrono::steady_clock::now();
    size_t total = 0;
    CensusSnapshot cs;
    auto flat = SnapshotFlatCensus(&total, &cs);
    CensusSnapshot windowView = cs;
    const bool windowCurrent = FilterWindowCensus(windowView);
    std::wstring failure = windowCurrent ? L"" : L"window-scope-changed";
    bool truncated = flat.size() < total || g_adviseFailed.load() || cs.type.empty();
    InstanceHandle root = 0;
    if (request.root) {
        CensusSharedLock lock;
        const wchar_t* error = nullptr;
        if (!ResolveWire_nolock(request.root, &root, &error)) failure = error ? error : L"stale-root";
        else if (!cs.type.count(root)) failure = L"missing-root";
    }
    std::wstring exactType = request.type;
    if (!request.type.empty() && request.type.find(L'.') == std::wstring::npos) {
        std::set<std::wstring> types;
        for (const auto& pair : windowView.type) if (DevToolsQuery::ShortType(pair.second) == request.type) types.insert(pair.second);
        if (types.size() > 1) failure = L"ambiguous-type";
        else if (types.size() == 1) exactType = *types.begin();
    }
    AuthoredVerdicts authored;
    bool authoredFilter = request.authored;
    if (authoredFilter && failure.empty()) {
        authored = ClassifyAuthored(windowView, true);
        truncated |= authored.truncated || authored.err != nullptr;
        // The same documented raw-tree fallback as inspect/search; never use it for exact targeting.
        if (authored.authored.empty()) authoredFilter = false;
    }
    const auto initialRoots = root ? std::vector<InstanceHandle>{root} : CollectRoots(windowView);
    const CensusSnapshot view = authoredFilter ? ProjectAuthored(windowView, authored.authored, initialRoots) : windowView;
    const auto roots = authoredFilter ? CollectRoots(view) : initialRoots;
    std::set<InstanceHandle> scope;
    int budget = kMaxTreeWalk;
    SelectBoundedBreadthFirst(view, roots, request.depth, budget, scope);
    if (!root && request.depth < 0) for (const auto& pair : view.type) {
        if (scope.size() == static_cast<size_t>(kMaxTreeWalk)) break;
        scope.insert(pair.first);
    }
    for (const auto raw : scope) {
        const auto children = view.children.find(raw);
        if (children != view.children.end())
            for (const auto child : children->second)
                if (!view.type.count(child) || (request.depth < 0 && !scope.count(child))) truncated = true;
    }
    flat.clear();
    std::set<InstanceHandle> emitted;
    for (const auto& item : view.type) {
        if (!scope.count(item.first)) continue;
        const auto parent = view.parent.find(item.first);
        if (parent != view.parent.end() && scope.count(parent->second)) continue;
        if (emitted.insert(item.first).second) FlattenCensusNode(view, item.first, 0, scope, emitted, flat);
    }
    if (flat.size() != scope.size()) truncated = true;
    std::set<std::wstring> projection(request.fields.begin(), request.fields.end());
    for (const auto& predicate : request.predicates) projection.insert(predicate.property);
    if (request.write) projection.insert(request.property);
    const auto normalizedText = DevToolsTreeLayout::NormalizeTreeFilterQuery(request.text);
    size_t evaluated = 0, unevaluated = 0, propertyReads = 0, propertyRows = 0, valueUnits = 0;
    std::map<std::wstring, size_t> reasons;
    std::vector<TapQueryRow> matches, context;
    std::vector<TapQueryDiagnostic> diagnostics;
    size_t diagnosticsOmitted = 0;
    const auto recordDiagnostic = [&](TapQueryRow&& row, const wchar_t* phase, const std::wstring& reason) {
        if (!request.exact) return;
        if (diagnostics.size() == 32) { ++diagnosticsOmitted; return; }
        diagnostics.push_back({std::move(row), phase, reason});
    };
    for (const auto& node : flat) {
        InstanceHandle raw = 0;
        {
            CensusSharedLock lock;
            const wchar_t* error = nullptr;
            if (!ResolveWire_nolock(node.handle, &raw, &error)) { truncated = true; continue; }
        }
        if (!scope.count(raw)) continue;
        context.push_back({raw, node, {}, {}});
        if (!failure.empty() || (authoredFilter && !authored.authored.count(raw)) ||
            (!exactType.empty() && node.type != exactType)) continue;
        if (std::chrono::steady_clock::now() - started > std::chrono::milliseconds(500)) {
            truncated = true; continue;
        }
        if (!request.text.empty()) {
            bool matchesText = DevToolsTreeLayout::TreeFilterMatches(node.type, node.name, node.sourceUri, normalizedText);
            if (!matchesText) {
                std::wstring caption, text;
                Route1ReadPreview(node.handle, caption, &text);
                matchesText = !text.empty() && DevToolsTreeLayout::TreeFilterMatches(L"", L"", text, normalizedText);
            }
            if (std::chrono::steady_clock::now() - started > std::chrono::milliseconds(500)) {
                truncated = true; break;
            }
            if (!matchesText) { ++evaluated; continue; }
        }
        TapQueryRow row{raw, node, {}, {}};
        if (!projection.empty()) {
            ++propertyReads;
            const HRESULT hr = ReadCuratedProps(raw, &row.properties, nullptr, nullptr,
                projection.empty() ? nullptr : &projection, &propertyRows);
            if (FAILED(hr)) row.readError = HrStr(hr);
        }
        for (const auto& property : row.properties) valueUnits += property.value.size();
        if (valueUnits > 4 * 1024 * 1024 || std::chrono::steady_clock::now() - started > std::chrono::milliseconds(500)) {
            truncated = true; break;
        }
        auto verdict = DevToolsQuery::Match(row.properties, request.predicates);
        if (!row.readError.empty() && !request.predicates.empty())
            verdict = {DevToolsQuery::Verdict::Unevaluated, L"unreadable"};
        if (!request.filter.empty() && verdict.verdict != DevToolsQuery::Verdict::Unevaluated) {
            std::wstring label = DevToolsQuery::ShortType(node.type);
            if (!node.name.empty()) label += L" #" + node.name;
            const auto source = cs.sourceUri.find(raw);
            if (source != cs.sourceUri.end() && !source->second.empty()) {
                auto file = source->second;
                if (file.size() >= 11 && CompareStringOrdinal(file.data(), 11, L"ms-appx:///", 11, TRUE) == CSTR_EQUAL)
                    file.erase(0, 11);
                label += L" " + file;
            }
            if (FindStringOrdinal(FIND_FROMSTART, label.data(), static_cast<int>(label.size()),
                request.filter.data(), static_cast<int>(request.filter.size()), TRUE) < 0)
                verdict.verdict = DevToolsQuery::Verdict::NoMatch;
        }
        if (verdict.verdict == DevToolsQuery::Verdict::Unevaluated) {
            ++unevaluated; ++reasons[verdict.reason];
            recordDiagnostic(std::move(row), L"initial", verdict.reason);
        }
        else {
            ++evaluated;
            if (verdict.verdict == DevToolsQuery::Verdict::Match) {
                if (!request.exact && request.fields.empty()) {
                    std::wstring caption;
                    Route1ReadPreview(node.handle, caption, nullptr, &row.preview);
                }
                matches.push_back(std::move(row));
            }
        }
    }
    if (!QuerySnapshotCurrent(cs)) truncated = true;
    bool complete = !truncated && !unevaluated && failure.empty();
    if (request.exact && failure.empty()) {
        if (!complete) failure = L"incomplete";
        else if (matches.empty()) failure = L"no-match";
        else if (matches.size() != 1) failure = L"ambiguous";
    }
    std::wstring exactProperties;
    if (request.exact && !request.write && failure.empty()) {
        ++propertyReads;
        matches.front().properties.clear();
        std::wstring authoredState;
        const HRESULT read = ReadCuratedProps(matches.front().raw, &matches.front().properties, &authoredState, nullptr, nullptr, &propertyRows);
        if (FAILED(read)) failure = L"unreadable";
        if (!QuerySnapshotCurrent(cs)) failure = L"target-changed";
        if (failure.empty()) exactProperties = DevToolsRead_SerializeProps(matches.front().node.handle, matches.front().properties, authoredState);
    }
    std::wstring before, after, status = request.write ? L"not-applied" : L"read";
    if (request.write && failure.empty()) {
        auto& match = matches.front();
        const auto* selected = QueryProperty(match, request.property);
        before = QueryValueJson(selected, request.property, match.readError);
        if (!selected || selected->writeType.empty()) failure = L"property-not-writable";
        else {
            std::vector<DevToolsReadProp> current;
            size_t currentMatches = 0;
            for (const auto& candidate : context) {
                if (!exactType.empty() && candidate.node.type != exactType) continue;
                if (std::chrono::steady_clock::now() - started > std::chrono::milliseconds(500)) {
                    failure = L"incomplete"; break;
                }
                std::vector<DevToolsReadProp> observed;
                ++propertyReads;
                const HRESULT read = ReadCuratedProps(candidate.raw, &observed, nullptr, nullptr, &projection, &propertyRows);
                const auto verdict = DevToolsQuery::Match(observed, request.predicates);
                if (FAILED(read) || verdict.verdict == DevToolsQuery::Verdict::Unevaluated) {
                    recordDiagnostic({candidate.raw, candidate.node, std::move(observed), FAILED(read) ? HrStr(read) : L""},
                        L"final", FAILED(read) ? L"unreadable" : verdict.reason);
                    failure = L"target-changed"; break;
                }
                if (verdict.verdict == DevToolsQuery::Verdict::Match) {
                    ++currentMatches;
                    if (candidate.raw != match.raw) { failure = L"target-changed"; break; }
                    current = std::move(observed);
                }
            }
            TapQueryRow currentRow{match.raw, match.node, std::move(current), {}};
            const auto* currentProperty = QueryProperty(currentRow, request.property);
            if (currentMatches != 1 || !QuerySnapshotCurrent(cs) || !currentProperty || currentProperty->writeType.empty())
                failure = L"target-changed";
            if (std::chrono::steady_clock::now() - started > std::chrono::milliseconds(500))
                failure = L"incomplete";
            if (failure.empty()) {
                before = QueryValueJson(currentProperty, request.property, L"");
                const auto type = request.writeType.empty() ? currentProperty->writeType : request.writeType;
                std::wstring detail;
                const HRESULT written = SetLiteral(match.raw, request.property, type, request.value, &detail);
                status = SUCCEEDED(written) ? L"applied" : L"indeterminate";
                if (FAILED(written)) failure = detail.empty() ? HrStr(written) : detail;
                TapQueryRow observed{match.raw, match.node, {}, {}};
                ++propertyReads;
                InstanceHandle live = 0;
                bool valid = false;
                {
                    CensusSharedLock lock;
                    const wchar_t* error = nullptr;
                    valid = ResolveWire_nolock(match.node.handle, &live, &error) && live == match.raw;
                }
                const HRESULT readback = valid ? ReadCuratedProps(live, &observed.properties, nullptr, nullptr, &projection, &propertyRows) : E_INVALIDARG;
                if (FAILED(readback)) observed.readError = HrStr(readback);
                const auto* afterProperty = QueryProperty(observed, request.property);
                after = QueryValueJson(afterProperty, request.property, observed.readError);
                if (SUCCEEDED(written) && (FAILED(readback) || !afterProperty ||
                    (afterProperty->value == currentProperty->value &&
                     afterProperty->value != request.value &&
                     DevToolsQuery::Compare(afterProperty, {request.property, L"==", request.value}).verdict != DevToolsQuery::Verdict::Match)))
                    failure = L"write-unconfirmed";
            }
        }
    }
    if (failure == L"incomplete" || failure == L"target-changed") complete = false;
    if (failure == L"incomplete" && request.write) truncated = true;
    std::vector<std::wstring> fields = request.fields;
    if (request.exact && !request.write) {
        if (!request.property.empty()) fields = {request.property};
        else if (matches.size() == 1) for (const auto& property : matches.front().properties) fields.push_back(property.name);
    }
    const auto elapsed = std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - started).count();
    std::wstring out = L"{\"ok\":" + std::wstring(failure.empty() && !matches.empty() ? L"true" : L"false") +
        L",\"status\":\"" + status + L"\",\"complete\":" + (complete ? L"true" : L"false") +
        L",\"truncated\":" + (truncated ? L"true" : L"false") + L",\"scopeNodes\":" + std::to_wstring(scope.size()) +
        L",\"censusNodes\":" + std::to_wstring(total) + L",\"evaluated\":" + std::to_wstring(evaluated) +
        L",\"unevaluated\":" + std::to_wstring(unevaluated) + L",\"propertyReads\":" + std::to_wstring(propertyReads) +
        L",\"propertyRows\":" + std::to_wstring(propertyRows) + L",\"uiThreadMicroseconds\":" + std::to_wstring(elapsed);
    out += L",\"scope\":{\"rootHandle\":\"" + std::to_wstring(request.root) + L"\",\"depth\":" + std::to_wstring(request.depth) +
        L",\"authored\":" + (authoredFilter ? L"true" : L"false") + L",\"ofType\":\"" + DevToolsJsonEscape(exactType) + L"\"}";
    if (failure.empty() && matches.empty()) failure = complete ? L"no-match" : L"incomplete";
    if (!failure.empty()) out += L",\"error\":\"" + DevToolsJsonEscape(failure) + L"\"";
    if (request.authored && !authoredFilter) out += L",\"fallback\":\"No app-authored elements classified; queried the raw tree.\"";
    if (!before.empty()) out += L",\"before\":" + before;
    if (!after.empty()) out += L",\"after\":" + after;
    if (!exactProperties.empty()) out += L",\"properties\":" + exactProperties;
    if (request.exact) out += L",\"diagnostics\":" + QueryDiagnosticsJson(diagnostics, diagnosticsOmitted, cs, request.predicates);
    out += L",\"reasons\":{";
    bool first = true;
    for (const auto& reason : reasons) {
        if (!first) out += L',';
        first = false;
        out += L"\"" + DevToolsJsonEscape(reason.first) + L"\":" + std::to_wstring(reason.second);
    }
    out += L"},\"matches\":[";
    for (size_t i = 0; i < matches.size(); ++i) {
        if (i) out += L',';
        out += QueryNodeJson(matches[i], view, false, fields);
    }
    out += L"],\"context\":[";
    std::set<InstanceHandle> ancestors, matched;
    for (const auto& match : matches) matched.insert(match.raw);
    for (const auto& match : matches) {
        auto raw = match.raw;
        std::set<InstanceHandle> walked;
        while (view.parent.count(raw) && walked.insert(raw).second) {
            raw = view.parent.at(raw);
            if (!scope.count(raw)) break;
            ancestors.insert(raw);
        }
    }
    first = true;
    for (const auto& row : context) if (ancestors.count(row.raw) && !matched.count(row.raw)) {
        if (!first) out += L',';
        first = false;
        out += QueryNodeJson(row, view, true, {});
    }
    return out + L"]}";
}

static std::wstring HandleQuery(const std::wstring& id, const DevToolsJson& params, bool write)
{
    const auto bad = [&](const wchar_t* message) {
        return DevToolsRpcError(id, DevToolsErr::InvalidParams, message, L"bad-args");
    };
    if (!params.IsObject()) return bad(L"Query parameters must be an object.");
    for (const auto& item : params.obj) {
        if (item.first == L"ofType" || item.first == L"text" || item.first == L"filter" || item.first == L"property" ||
            item.first == L"value" || item.first == L"writeType" || item.first == L"rootHandle" || item.first == L"mode" ||
            item.first == kDevToolsReservedParamWindow || item.first == kDevToolsReservedParamRoot) {
            if (item.second.type != DevToolsJsonType::String) return bad(L"Query string parameters must be strings.");
            if (!write && (item.first == L"value" || item.first == L"writeType")) return bad(L"A read cannot carry a new value.");
            if (write && (item.first == L"text" || item.first == L"filter" || item.first == L"mode")) return bad(L"A write uses structured exact-one criteria.");
        } else if (item.first == L"with" || (!write && item.first == L"fields")) {
            if (item.second.type != DevToolsJsonType::Array) return bad(L"with and fields must be arrays.");
        } else if (!write && item.first == L"authored") {
            if (item.second.type != DevToolsJsonType::Bool) return bad(L"authored must be a boolean.");
        } else if (!write && item.first == L"depth") {
            if (item.second.type != DevToolsJsonType::Number || item.second.num < -1 || item.second.num > kMaxTreeWalk ||
                std::floor(item.second.num) != item.second.num) return bad(L"depth must be an integer from -1 to the tree budget.");
        } else return bad(L"Unknown query parameter.");
    }
    const auto mode = params.GetString(L"mode", L"many");
    if (mode != L"one" && mode != L"many") return bad(L"mode must be one or many.");
    if (params.HasKey(L"ofType") && params.GetString(L"ofType", L"").empty()) return bad(L"ofType cannot be empty.");
    TapQueryRequest request;
    request.write = write;
    request.exact = write || params.GetString(L"mode", L"many") == L"one";
    request.type = params.GetString(L"ofType", L"");
    request.text = params.GetString(L"text", L"");
    request.filter = params.GetString(L"filter", L"");
    request.property = params.GetString(L"property", L"");
    request.value = params.GetString(L"value", L"");
    request.writeType = params.GetString(L"writeType", L"");
    request.depth = static_cast<int>(params.GetInt(L"depth", -1));
    const auto* authored = params.Find(L"authored");
    request.authored = authored && authored->type == DevToolsJsonType::Bool && authored->b;
    std::wstring error;
    if (const auto* root = params.Find(L"rootHandle"))
        if (root->type != DevToolsJsonType::String || !DevToolsParseWireHandle(root->str, false, &request.root)) error = L"Invalid rootHandle.";
    if (const auto* predicates = params.Find(L"with")) {
        if (predicates->type != DevToolsJsonType::Array || predicates->arr.size() > 16) error = L"with must contain at most 16 predicates.";
        else for (const auto& value : predicates->arr) {
            DevToolsQuery::Predicate predicate;
            if (value.type != DevToolsJsonType::String || !DevToolsQuery::Parse(value.str, predicate, error)) {
                if (error.empty()) error = L"Predicates must be strings.";
                break;
            }
            request.predicates.push_back(std::move(predicate));
        }
    }
    if (const auto* fields = params.Find(L"fields")) {
        if (fields->type != DevToolsJsonType::Array || fields->arr.size() > 32) error = L"fields must contain at most 32 property names.";
        else for (const auto& field : fields->arr) {
            if (field.type != DevToolsJsonType::String || !DevToolsQuery::PropertyName(field.str)) { error = L"Invalid field name."; break; }
            request.fields.push_back(field.str);
        }
    }
    if (request.exact && (request.depth != -1 || request.authored)) error = L"Exact targeting requires an unlimited-depth raw scope.";
    if (request.exact && (!request.text.empty() || !request.filter.empty())) error = L"Exact targeting uses only structured criteria.";
    if (request.exact && request.type.empty() && request.predicates.empty()) error = L"Exact targeting requires ofType or with.";
    if (write && (!DevToolsQuery::PropertyName(request.property) || !params.HasKey(L"value"))) error = L"Writes require property and value.";
    if (!error.empty()) return DevToolsRpcError(id, DevToolsErr::InvalidParams, error, L"bad-args");
    auto outcome = std::make_shared<TapQueryOutcome>();
    bool completed = false;
    const HRESULT hr = RunOnUiThread([request, outcome]() {
        outcome->started.store(true);
        outcome->json = ExecuteQuery_ui(request);
        return S_OK;
    }, &completed);
    if (!completed || FAILED(hr)) {
        const bool indeterminate = write && outcome->started.load();
        return DevToolsRpcError(id, DevToolsErr::NotOnDispatcher,
            indeterminate ? L"The dispatched write may have applied. Read the target property to reconcile; do not retry automatically."
                          : L"The query did not complete. No mutation was attempted.",
            indeterminate ? L"indeterminate" : L"not-applied");
    }
    return DevToolsRpcResult(id, outcome->json);
}
