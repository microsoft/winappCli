// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsPathWalk.h"
#include "DevToolsProtocol.h"   // the tap's JSON reader; the walk answer arrives as one compact JSON object

namespace {

std::wstring LastComponent(const std::wstring& prefix)
{
    const size_t dot = prefix.rfind(L'.');
    return dot == std::wstring::npos ? prefix : prefix.substr(dot + 1);
}

void SplitAroundSegment(const std::wstring& path, const std::wstring& segment,
                        std::wstring* head, std::wstring* emph, std::wstring* tail)
{
    *head = *emph = *tail = L"";
    if (segment.empty()) { *emph = path; return; }

    for (size_t start = 0; start <= path.size();) {
        const size_t dot = path.find(L'.', start);
        const size_t len = (dot == std::wstring::npos ? path.size() : dot) - start;
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

std::wstring TypeNote(const std::wstring& type)
{
    if (type.empty()) return L"";
    if (type.front() == L'(' && type.back() == L')') return type;
    return L"(" + type + L")";
}

// Classify one segment from the facts the wire carries.
DevToolsWalkOutcome Classify(const DevToolsJson& seg)
{
    if (!seg.GetBool(L"reached", true))  return DevToolsWalkOutcome::NotReached;
    if (!seg.GetBool(L"found", true))    return DevToolsWalkOutcome::Missing;

    const std::wstring hr = seg.GetString(L"hr");
    if (!hr.empty() && hr != L"0x0" && hr != L"0x00000000" && hr != L"0") return DevToolsWalkOutcome::Threw;

    if (seg.GetBool(L"isNull", false))   return DevToolsWalkOutcome::Null;
    return DevToolsWalkOutcome::Resolved;
}

} // namespace

const wchar_t* DevToolsPathWalk_StopMark() { return L"\u2716"; }

std::wstring DevToolsPathWalk_PlainText(const DevToolsPathWalkView& v)
{
    if (!v.show || v.steps.empty()) return L"";

    size_t widest = 0;
    for (const auto& s : v.steps) widest = s.path.size() > widest ? s.path.size() : widest;
    const size_t pad = widest > 28 ? 28 : widest;

    std::wstring out;
    for (const auto& s : v.steps) {
        if (!out.empty()) out += L"\n";
        out += s.stop ? (std::wstring(DevToolsPathWalk_StopMark()) + L" ") : std::wstring(L"  ");
        out += s.path;
        if (s.path.size() < pad) out.append(pad - s.path.size(), L' ');
        out += L"  ";
        out += s.value;
        if (!s.type.empty() && s.type != s.value) out += L" " + s.type;
    }
    return out;
}

const wchar_t* DevToolsPathWalk_OutcomeText(DevToolsWalkOutcome outcome)
{
    switch (outcome) {
        case DevToolsWalkOutcome::Missing:    return L"no such property";
        case DevToolsWalkOutcome::Null:       return L"null";
        case DevToolsWalkOutcome::Threw:      return L"the getter threw";
        case DevToolsWalkOutcome::NotReached: return L"not reached";
        case DevToolsWalkOutcome::Resolved:   break;
    }
    return L"";
}

DevToolsPathWalkView DevToolsPathWalk_FromJson(const std::wstring& json, bool agentPresent)
{
    DevToolsPathWalkView v;

    DevToolsJson j;
    if (json.empty() || !DevToolsJsonParse(json, j) || !j.IsObject()) {
        v.show = true;
        v.qEmphasis = L"Path walk unavailable";
        v.answer = L"the app did not answer";
        return v;
    }

    const std::wstring path        = j.GetString(L"path");
    const std::wstring state       = j.GetString(L"state");
    const std::wstring against     = j.GetString(L"against");
    const std::wstring againstKind = j.GetString(L"againstKind");
    auto reasonOr = [&j](const wchar_t* fallback) {
        const std::wstring r = j.GetString(L"reason");
        return r.empty() ? std::wstring(fallback) : r;
    };

    // An {x:Bind} resolves against the page's own generated code and a {Binding} against the DataContext.
    if (againstKind == L"xbind") {
        v.againstName = against.empty() ? std::wstring(L"the page") : against;
        v.againstNote = L"resolved against " + v.againstName +
                        L" \u2014 an {x:Bind} binds to the page, not the DataContext";
    } else if (againstKind == L"binding") {
        v.againstName = against.empty() ? std::wstring(L"the DataContext") : against;
        v.againstNote = L"resolved against the DataContext";
        if (!against.empty()) v.againstNote += L" (" + against + L")";
    }
    if (j.GetString(L"expressionSource") == L"proposed")
        v.againstNote = L"Proposed replacement: a new classic {Binding} using DataContext, not the installed binding.";
    if (state == L"unknown" || state == L"path-unavailable" || state == L"source-unavailable") {
        v.show = true;
        v.qEmphasis = L"Path walk unavailable";
        v.answer = reasonOr(L"The binding path or its actual source could not be established.");
        v.againstNote.clear();
        return v;
    }

    if (state == L"none") return v;

    if (state == L"bad-path") {
        v.show = true;
        v.qEmphasis = path.empty() ? std::wstring(L"Malformed path") : path;
        v.answer = reasonOr(L"this is not a well-formed property path");
        return v;
    }

    if (state == L"no-context") {
        v.show = true;
        v.qEmphasis = L"No DataContext";
        v.answer = L"this element and its ancestors have no DataContext, so no path can resolve against it";
        return v;
    }

    // A Window-rooted x:Bind source may not be a DependencyObject in the visual tree.
        // Do not substitute DataContext: it is a different source and would produce a false diagnosis.
    if (state == L"no-xbind-source") {
        v.show = true;
        v.againstNote.clear();
        v.qEmphasis = L"Cannot reach the {x:Bind} source";
        v.answer = reasonOr(L"an {x:Bind} resolves against the page's own generated code, which DevTools "
                            L"cannot reach from the visual tree here. It is NOT resolved against the "
                            L"DataContext, so nothing below is guessed from one.");
        return v;
    }

    const DevToolsJson* segs = j.Find(L"segments");
    if (!segs || segs->type != DevToolsJsonType::Array || segs->arr.empty()) {
        v.show = true;
        v.qEmphasis = L"Path walk unavailable";
        v.answer = j.GetString(L"reason", L"the app answered with no segments");
        return v;
    }

    size_t stopAt = SIZE_MAX;
    for (size_t i = 0; i < segs->arr.size(); ++i) {
        const DevToolsJson& s = segs->arr[i];
        DevToolsPathWalkStep step;
        step.path    = s.GetString(L"path");
        step.outcome = Classify(s);

        if (step.outcome == DevToolsWalkOutcome::Resolved) {
            const std::wstring val = s.GetString(L"value");
            const std::wstring ty  = s.GetString(L"type");
            if (val.empty()) { step.value = ty; }
            else             { step.value = val; step.type = TypeNote(ty); }
        } else {
            step.value = DevToolsPathWalk_OutcomeText(step.outcome);
            step.type  = TypeNote(s.GetString(L"type"));
        }

        if (stopAt == SIZE_MAX && step.outcome != DevToolsWalkOutcome::Resolved &&
            step.outcome != DevToolsWalkOutcome::NotReached) {
            stopAt = i;
            step.stop = true;
        } else if (stopAt != SIZE_MAX) {
            step.after = true;
        }
        v.steps.push_back(step);
    }

    if (state == L"not-probeable") {
        v.show = true;
        v.qEmphasis = L"Cannot read this object's properties";
        v.answer = reasonOr(L"an object on this path does not expose its properties to DevTools, so the "
                            L"walk could not continue past it");
        return v;
    }

    if (state == L"indexer-unsupported") {
        v.show = true;
        v.qEmphasis = L"Indexed path";
        v.answer = reasonOr(L"DevTools cannot follow an indexer in a binding path. The property before it "
                            L"may be perfectly fine; only the indexing step is unsupported here.");
        return v;
    }

    if (stopAt == SIZE_MAX) return v;

    v.show = true;

    const DevToolsPathWalkStep& stop = v.steps[stopAt];
    const std::wstring failing = LastComponent(stop.path);
    SplitAroundSegment(path.empty() ? stop.path : path, failing, &v.qHead, &v.qEmphasis, &v.qTail);

    std::wstring lookedOn = v.againstName;
    if (stopAt > 0) {
        const DevToolsPathWalkStep& prev = v.steps[stopAt - 1];
        std::wstring ty = prev.type;
        if (ty.size() >= 2 && ty.front() == L'(' && ty.back() == L')') ty = ty.substr(1, ty.size() - 2);
        if      (!ty.empty())         lookedOn = ty;
        else if (!prev.value.empty()) lookedOn = prev.value;
    }

    switch (stop.outcome) {
        case DevToolsWalkOutcome::Null:
            v.answer = L"stopped at " + failing + L" because it was null";
            break;

        case DevToolsWalkOutcome::Missing: {
            v.answer = L"no " + failing + L" on " + lookedOn;
            const std::wstring suggestion = j.GetString(L"suggestion");
            if (!suggestion.empty()) v.answer += L" \u2014 did you mean " + suggestion + L"?";
            break;
        }

        case DevToolsWalkOutcome::Threw:
            if (agentPresent) {
                // A managed answer can expose the getter's inner exception, which native ABI failure cannot.
                const std::wstring ex  = j.GetString(L"exception");
                const std::wstring msg = j.GetString(L"message");
                if (!ex.empty()) {
                    v.namesException = true;
                    v.answer = L"its getter threw " + ex;
                    if (!msg.empty()) v.answer += L" \u2014 \"" + msg + L"\"";
                    break;
                }
            }
            v.answer = L"its getter threw. DevTools cannot see which exception \u2014 "
                       L"the type and message are lost crossing the ABI.";
            v.revealException = true;
            break;

        case DevToolsWalkOutcome::Resolved:
        case DevToolsWalkOutcome::NotReached:
            break;   // cannot be the stop; the loop above excludes both
    }

    return v;
}
