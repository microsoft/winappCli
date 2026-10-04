// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsBindingRow.h"
#include "DevToolsProtocol.h"   // the tap's JSON reader; the agent's answer arrives as one compact JSON object

namespace {

void SplitAroundSegment(const std::wstring& path, const std::wstring& segment,
                        std::wstring* head, std::wstring* emph, std::wstring* tail)
{
    *head = *emph = *tail = L"";
    if (segment.empty()) { *emph = path; return; }

    for (size_t start = 0; start <= path.size();) {
        size_t dot = path.find(L'.', start);
        size_t len = (dot == std::wstring::npos ? path.size() : dot) - start;
        if (path.compare(start, len, segment) == 0) {
            *head = path.substr(0, start);
            *emph = segment;
            *tail = path.substr(start + segment.size());
            return;
        }
        if (dot == std::wstring::npos) break;
        start = dot + 1;
    }
    *emph = segment;
}

} // namespace

const wchar_t* DevToolsBindingRow_PendingText() { return L"Checking this binding\u2026"; }

DevToolsBindingRowView DevToolsBindingRow_FromJson(const std::wstring& json)
{
    DevToolsBindingRowView v;

    DevToolsJson j;
    if (json.empty() || !DevToolsJsonParse(json, j) || !j.IsObject()) {
        v.show = true;
        v.unavailable = true;
        v.headEmphasis = L"Diagnosis unavailable";
        v.subtitle = L"the app did not answer";
        return v;
    }

    const std::wstring state  = j.GetString(L"state");
    const std::wstring path   = j.GetString(L"path");
    const std::wstring seg    = j.GetString(L"segment");
    const std::wstring reason = j.GetString(L"reason");

    if (state == L"none") return v;

    v.show = true;

    if (state == L"evaluated" || state == L"ok") {
        v.headEmphasis = L"Path/type evaluation only";
        v.subtitle = reason.empty() ? L"Target freshness and change notifications were not checked." : reason;
        for (const auto* field : { L"sourceValue", L"resolvedValue", L"targetValue" }) {
            if (j.HasKey(field)) v.subtitle += L"  \u00b7  " + std::wstring(field) + L": " + j.GetString(field);
        }
        if (j.HasKey(L"targetUnavailable")) v.subtitle += L"  \u00b7  target unavailable: " + j.GetString(L"targetUnavailable");
        return v;
    }

    if (state == L"confirmation-required") {
        v.headEmphasis = L"Confirm owner-wide restore";
        v.subtitle = j.GetString(L"restoreOwner") + L": " + j.GetString(L"warning");
        if (j.HasKey(L"restoreDisclosure")) v.subtitle += L"  " + j.GetString(L"restoreDisclosure");
        return v;
    }

    if (state == L"bad-segment" || state == L"null-link") {
        SplitAroundSegment(path, seg, &v.head, &v.headEmphasis, &v.headTail);
        v.subtitle = reason;
        v.isFault = true;
        return v;
    }

    if (state == L"no-datacontext") {
        v.headEmphasis = L"No DataContext";
        v.subtitle = reason;
        v.isFault = true;
        return v;
    }

    if (state == L"threw") {
        v.headEmphasis = seg.empty() ? path : seg;
        v.subtitle = reason;
        v.isFault = true;
        return v;
    }

    if (state == L"silent") {
        v.headEmphasis = path.empty() ? std::wstring(L"the bound value") : path;
        std::wstring resolved = j.GetString(L"resolvedValue");
        std::wstring rtype    = j.GetString(L"resolvedType");
        std::wstring tprop    = j.GetString(L"targetProperty");
        std::wstring ttype    = j.GetString(L"targetType");
        v.subtitle = L"resolves to " + resolved;
        if (!rtype.empty()) v.subtitle += L" (" + rtype + L")";
        if (!tprop.empty() && !ttype.empty()) v.subtitle += L"  \u00b7  " + tprop + L" expects " + ttype;
        if (j.HasKey(L"sourceValue")) v.subtitle += L"  \u00b7  source: " + j.GetString(L"sourceValue");
        if (j.HasKey(L"targetValue")) v.subtitle += L"  \u00b7  target: " + j.GetString(L"targetValue");
        if (j.HasKey(L"targetUnavailable")) v.subtitle += L"  \u00b7  target unavailable: " + j.GetString(L"targetUnavailable");
        if (!reason.empty()) v.subtitle += L"  \u00b7  " + reason;
        v.isFault = false;
        return v;
    }

    v.suppressNativeWalk = j.GetString(L"nativeFallback") == L"False";
    v.unavailable = !v.suppressNativeWalk;
    v.headEmphasis = L"Diagnosis unavailable";
    v.subtitle = reason.empty() ? L"this app could not answer" : reason;
    return v;
}

DevToolsBindingSummary DevToolsBindingRow_Summary(const std::wstring& json)
{
    DevToolsBindingSummary s;
    DevToolsJson j;
    if (json.empty() || !DevToolsJsonParse(json, j) || !j.IsObject()) {
        s.status = L"Status unknown";
        s.reason = L"the app did not answer";
        return s;
    }
    const std::wstring state = j.GetString(L"state");
    const std::wstring path  = j.GetString(L"path");
    const std::wstring seg   = j.GetString(L"segment");
    const std::wstring mode  = j.GetString(L"mode");
    s.kind = j.GetString(L"kind");
    s.source = j.GetString(L"source");
    if (s.source.empty()) s.source = j.GetString(L"dataContextType");
    if (s.source.rfind(L"DataContext=", 0) == 0) s.source = s.source.substr(12) + L" (DataContext)";
    else if (!s.source.empty() && s.kind == L"{x:Bind}") s.source += L" (x:Bind owner)";
    s.pathMode = path.empty() ? std::wstring(L"(no path)") : path;
    if (!mode.empty()) s.pathMode += L" \u00b7 " + mode;
    s.value = j.HasKey(L"resolvedValue") ? j.GetString(L"resolvedValue") : j.GetString(L"sourceValue");

    if (state == L"none") { s.tone = DevToolsBindingTone::NotBound; s.status = L"Not bound"; return s; }
    if (state == L"evaluated" || state == L"ok") { s.tone = DevToolsBindingTone::Works; s.status = L"\u2713 Works"; return s; }
    if (state == L"silent") {
        const std::wstring rtype = j.GetString(L"resolvedType"), ttype = j.GetString(L"targetType");
        if (!rtype.empty() && !ttype.empty() && rtype != ttype) {
            s.tone = DevToolsBindingTone::Warning;
            s.status = L"\u26A0 Wrong type";
            s.reason = L"resolves to " + rtype + L", " + j.GetString(L"targetProperty") + L" expects " + ttype;
        } else {
            s.tone = DevToolsBindingTone::Works;
            s.status = L"\u2713 Works";
        }
        return s;
    }
    if (state == L"bad-segment" || state == L"null-link") {
        s.tone = DevToolsBindingTone::Broken;
        s.status = L"\u2715 Broken at " + (seg.empty() ? path : seg);
        const std::wstring why = j.GetString(L"reason");
        const size_t on = why.rfind(L" on ");
        s.reason = state == L"null-link" ? std::wstring(L"the value before it is null")
                 : on != std::wstring::npos ? L"not found on " + why.substr(on + 4)
                 : std::wstring(L"not found on the source");
        s.value.clear();
        return s;
    }
    if (state == L"no-datacontext") {
        s.tone = DevToolsBindingTone::Broken;
        s.status = L"\u2715 No DataContext";
        s.reason = L"nothing to bind to";
        s.value.clear();
        return s;
    }
    if (state == L"threw") {
        s.tone = DevToolsBindingTone::Broken;
        s.status = L"\u2715 " + (seg.empty() ? path : seg) + L" threw";
        s.reason = L"its getter threw an exception";
        s.value.clear();
        return s;
    }
    s.status = L"Status unknown";
    s.reason = j.GetString(L"reason");
    return s;
}
