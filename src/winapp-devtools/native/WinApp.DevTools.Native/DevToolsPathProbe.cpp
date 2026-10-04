// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsPathProbe.h"
#include "DevToolsProtocol.h"   // DevToolsJsonEscape
#include "DevToolsRead.h"

namespace {

std::wstring HexHr(long hr)
{
    if (hr == 0) return L"0x0";
    wchar_t buf[16] = {};
    swprintf_s(buf, L"0x%08X", (unsigned long)hr);
    return buf;
}

void AppendString(std::wstring& j, const wchar_t* key, const std::wstring& v, bool& first)
{
    if (v.empty()) return;
    if (!first) j += L",";
    first = false;
    j += L"\""; j += key; j += L"\":\"" + DevToolsJsonEscape(v) + L"\"";
}

void AppendBool(std::wstring& j, const wchar_t* key, bool v, bool& first)
{
    if (!first) j += L",";
    first = false;
    j += L"\""; j += key; j += L"\":"; j += (v ? L"true" : L"false");
}

} // namespace

bool DevToolsPathProbe_Prepare(const DevToolsReadProp& prop, const std::wstring& authoredState,
                              const std::wstring& tryPath, DevToolsProbeResult* out)
{
    if (!out) return false;
    *out = {};
    out->authoredState = authoredState;
    out->againstKind = L"unknown";
    out->expressionSource = L"unknown";
    if (!tryPath.empty()) {
        out->path = tryPath;
        out->againstKind = L"binding";
        out->expressionSource = L"proposed";
        return true;
    }

    const bool runtime = prop.source == L"Binding" || prop.valueState == L"binding" || !prop.binding.empty();
    const std::wstring expression = runtime ? prop.binding : prop.authored;
    const std::wstring kind = DevToolsRead_ClassifyAuthored(expression);
    out->expressionSource = runtime ? L"runtime" : (expression.empty() ? L"unknown" : L"authored");
    if (kind != L"binding" && kind != L"xBind") {
        if (!runtime && kind == L"literal" && authoredState == L"available") {
            out->state = L"none";
        } else {
            out->state = runtime ? L"path-unavailable" : L"unknown";
            out->reason = runtime
                ? L"A runtime binding exists, but its path and source settings could not be read."
                : L"No analyzable authored binding is available. This does not prove the property is unbound; "
                  L"compiled bindings and programmatic SetBinding may have no authored expression.";
        }
        return false;
    }
    out->againstKind = kind == L"xBind" ? L"xbind" : L"binding";
    // Read top-level options only. A converter parameter containing "Source=" is not a source selector.
    const size_t opening = expression.find_first_not_of(L" \t\r\n");
    size_t start = expression.find_first_of(L" \t\r\n}", opening + 1);
    int depth = 0;
    wchar_t quote = 0;
    bool complete = false;
    if (start != std::wstring::npos) {
        ++start;
        for (size_t i = start; i < expression.size(); ++i) {
            const wchar_t c = expression[i];
            if (quote) { if (c == quote) quote = 0; continue; }
            if (c == L'\'' || c == L'"') { quote = c; continue; }
            if (c == L'{') { ++depth; continue; }
            if (c == L'}' && depth > 0) { --depth; continue; }
            if ((c != L',' && c != L'}') || depth != 0) continue;
            const auto part = expression.substr(start, i - start);
            const size_t eq = part.find(L'=');
            auto trim = [](std::wstring s) {
                const auto b = s.find_first_not_of(L" \t\r\n");
                if (b == std::wstring::npos) return std::wstring();
                return s.substr(b, s.find_last_not_of(L" \t\r\n") - b + 1);
            };
            if (eq == std::wstring::npos) {
                if (out->path.empty()) out->path = trim(part);
            } else {
                const auto name = trim(part.substr(0, eq));
                const auto value = trim(part.substr(eq + 1));
                if (name == L"Path") out->path = value;
                else if (name == L"ElementName") out->againstKind = L"elementName";
                else if (name == L"Source") out->againstKind = L"source";
                else if (name == L"RelativeSource") out->againstKind = L"relativeSource";
            }
            start = i + 1;
            if (c == L'}') {
                complete = expression.find_first_not_of(L" \t\r\n", start) == std::wstring::npos;
                break;
            }
        }
    }
    if (!complete) {
        out->state = L"path-unavailable";
        out->againstKind = L"unknown";
        out->reason = L"The binding expression could not be parsed completely; its source is not guessed.";
        return false;
    }
    if (out->againstKind != L"binding" && out->againstKind != L"xbind") {
        out->state = L"source-unavailable";
        out->reason = L"This binding's " + out->againstKind +
            L" source cannot be established by the native walker. It is not retried against DataContext.";
        return false;
    }
    if (out->path.empty()) {
        out->state = L"path-unavailable";
        out->reason = L"This binding has no named path to walk. A pathless binding can still be active.";
        return false;
    }
    return true;
}

bool DevToolsPathProbe_SplitPath(const std::wstring& path, std::vector<std::wstring>* out)
{
    if (!out || path.empty()) return false;
    for (wchar_t c : path) { if (c == L' ' || c == L'\t' || c == L'\n' || c == L'\r') return false; }

    std::vector<std::wstring> segs;
    size_t start = 0;
    for (;;) {
        const size_t dot = path.find(L'.', start);
        const size_t len = (dot == std::wstring::npos ? path.size() : dot) - start;
        if (len == 0) return false;                 // leading dot, trailing dot, or ".."
        segs.push_back(path.substr(start, len));
        if (dot == std::wstring::npos) break;
        start = dot + 1;
    }
    *out = segs;
    return true;
}

std::wstring DevToolsPathProbe_ToJson(const DevToolsProbeResult& r)
{
    std::wstring j = L"{";
    bool first = true;
    AppendString(j, L"path",        r.path,        first);
    AppendString(j, L"state",       r.state,       first);
    AppendString(j, L"against",     r.against,     first);
    AppendString(j, L"againstKind", r.againstKind, first);
    AppendString(j, L"reason",      r.reason,      first);
    AppendString(j, L"expressionSource", r.expressionSource, first);
    AppendString(j, L"authoredState", r.authoredState, first);

    if (!first) j += L",";
    j += L"\"segments\":[";
    for (size_t i = 0; i < r.segments.size(); ++i) {
        const DevToolsProbeSegment& s = r.segments[i];
        if (i) j += L",";
        j += L"{";
        bool sfirst = true;
        AppendString(j, L"path", s.path, sfirst);
        if (!s.reached) {
            AppendBool(j, L"reached", false, sfirst);
        } else {
            AppendBool(j, L"found", s.found, sfirst);
            if (s.found) {
                AppendString(j, L"hr", HexHr(s.hr), sfirst);
                if (s.hr == 0) AppendBool(j, L"isNull", s.isNull, sfirst);
            }
            AppendString(j, L"value", s.value, sfirst);
            AppendString(j, L"type",  s.type,  sfirst);
        }
        j += L"}";
    }
    j += L"]}";
    return j;
}
