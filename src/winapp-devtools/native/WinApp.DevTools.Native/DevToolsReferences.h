// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Element references: the WinUI properties that point one element at another. Accessibility relations
// (AutomationProperties.LabeledBy, DescribedBy, FlowsTo, FlowsFrom) and keyboard XY focus targets (XYFocusUp,
// XYFocusDown, XYFocusLeft, XYFocusRight). XAML sets them with {x:Bind}/ElementName; Reactor sets them with
// .LabeledBy()/.XYFocusDown() modifiers. Both end up as these public dependency properties, so one reader serves both.
//
// The model and the cycle walk are pure (no WinRT) so the unit tests exercise them; the tap reads the live values.
#pragma once

#include <cstdint>
#include <functional>
#include <set>
#include <string>
#include <vector>
#include "DevToolsProtocol.h"
#include "DevToolsText.h"

// The properties, in the order the inspector lists them.
inline const std::vector<std::wstring>& DevToolsReferenceKinds()
{
    static const std::vector<std::wstring> kinds = {
        L"LabeledBy", L"DescribedBy", L"FlowsTo", L"FlowsFrom",
        L"XYFocusUp", L"XYFocusDown", L"XYFocusLeft", L"XYFocusRight",
    };
    return kinds;
}

inline bool DevToolsReferenceIsAutomation(const std::wstring& kind)
{
    return kind == L"LabeledBy" || kind == L"DescribedBy" || kind == L"FlowsTo" || kind == L"FlowsFrom";
}

struct DevToolsReferenceEdge
{
    std::wstring kind;              // LabeledBy, XYFocusDown, ...
    bool incoming = false;          // another element points here (its `kind` names this element)
    unsigned long long wire = 0;    // the other element's wire handle; 0 when it is outside the inspected tree
    std::wstring label;             // the other element, as the tree names it (`TextBlock #NameLabel`)
    bool cycle = false;             // following `kind` from the other element comes back here
    bool pending = false;           // the target isn't shown: Reactor hasn't created it yet, or it exists outside the live
                                    // tree (an unselected tab, a closed popup), so there is nothing to select
    std::wstring targetType;        // pending: the type Reactor expects the target to be, or the detached target's type
    std::wstring automationId;      // pending: the AutomationId the reference names
};

// What a pending target is called: `Button`, `AutomationId "Title"`, or both.
inline std::wstring DevToolsPendingReferenceLabel(const std::wstring& targetType, const std::wstring& automationId)
{
    std::wstring label = targetType;
    if (!automationId.empty()) label += (label.empty() ? L"" : L" ") + std::wstring(L"AutomationId \"") + automationId + L"\"";
    return label.empty() ? L"an element" : label;
}

// A target that exists but isn't in the live tree (an unselected tab's content, a closed popup) reads like a pending
// one: `LabeledBy → TextBlock (not shown)`, with no handle to select. `runtimeClass` is its full WinRT class name.
inline void DevToolsMarkReferenceNotShown(DevToolsReferenceEdge& edge, const std::wstring& runtimeClass)
{
    const auto dot = runtimeClass.find_last_of(L'.');
    edge.wire = 0;
    edge.pending = true;
    edge.targetType = dot == std::wstring::npos ? runtimeClass : runtimeClass.substr(dot + 1);
    edge.label = DevToolsPendingReferenceLabel(edge.targetType, {});
}

// The managed agent's `reactorreferences` answer, `{"state":"ok","edges":[{"property":"XYFocusRight","targetType":"Button",
// "automationId":"Save"}]}`, as pending out-edges. Anything else (no Reactor, a Reactor without the API) is none.
inline std::vector<DevToolsReferenceEdge> DevToolsPendingReferences(const std::wstring& agentJson)
{
    std::vector<DevToolsReferenceEdge> edges;
    DevToolsJson answer;
    if (!DevToolsJsonParse(agentJson, answer) || !answer.IsObject() || answer.GetString(L"state") != L"ok") return edges;
    const auto list = answer.obj.find(L"edges");
    if (list == answer.obj.end() || list->second.type != DevToolsJsonType::Array) return edges;
    for (const auto& item : list->second.arr) {
        if (!item.IsObject()) continue;
        DevToolsReferenceEdge edge;
        edge.kind = item.GetString(L"property");
        if (edge.kind.empty()) continue;
        edge.pending = true;
        edge.targetType = item.GetString(L"targetType");
        edge.automationId = item.GetString(L"automationId");
        edge.label = DevToolsPendingReferenceLabel(edge.targetType, edge.automationId);
        edges.push_back(std::move(edge));
    }
    return edges;
}

// Whether following `kind` from `from` leads back to `start` within `maxSteps` hops. `next` returns the targets of
// `kind` on an element (one for single-valued properties, any number for the lists). Ids are opaque (COM identities).
inline bool DevToolsReferenceLeadsBack(std::uintptr_t start, std::uintptr_t from,
                                       const std::function<std::vector<std::uintptr_t>(std::uintptr_t)>& next,
                                       size_t maxSteps = 64)
{
    std::set<std::uintptr_t> seen;
    std::vector<std::uintptr_t> frontier{ from };
    for (size_t step = 0; step < maxSteps && !frontier.empty(); ++step) {
        std::vector<std::uintptr_t> following;
        for (auto id : frontier) {
            if (id == start) return true;
            if (!id || !seen.insert(id).second) continue;
            for (auto target : next(id)) following.push_back(target);
        }
        frontier = std::move(following);
    }
    return false;
}

// `{"edges":[{"kind":"LabeledBy","direction":"out","handle":"123","element":"TextBlock","cycle":false}]}`.
inline std::wstring DevToolsReferencesJson(const std::vector<DevToolsReferenceEdge>& edges, bool truncated = false)
{
    std::wstring out = L"{\"edges\":[";
    bool first = true;
    for (const auto& edge : edges) {
        if (!first) out += L",";
        first = false;
        out += L"{\"kind\":\"" + DevToolsJsonEscape(edge.kind) + L"\",\"direction\":\"" +
               (edge.incoming ? L"in" : L"out") + L"\"";
        if (edge.wire) out += L",\"handle\":\"" + std::to_wstring(edge.wire) + L"\"";
        out += L",\"element\":\"" + DevToolsJsonEscape(edge.label) + L"\"";
        if (edge.pending) {
            out += L",\"pending\":true";
            if (!edge.targetType.empty()) out += L",\"targetType\":\"" + DevToolsJsonEscape(edge.targetType) + L"\"";
            if (!edge.automationId.empty()) out += L",\"automationId\":\"" + DevToolsJsonEscape(edge.automationId) + L"\"";
        }
        out += edge.cycle ? L",\"cycle\":true}" : L",\"cycle\":false}";
    }
    out += L"]";
    if (truncated) out += L",\"incomingTruncated\":true";
    return out + L"}";
}

// One line per edge: `LabeledBy → TextBlock #NameLabel` (or `← ... XYFocusDown` for an incoming edge), and
// `XYFocusRight → Button (not shown)` when the target isn't in the live tree (or Reactor hasn't created it).
inline std::wstring DevToolsReferenceLine(const DevToolsReferenceEdge& edge)
{
    std::wstring line = edge.incoming ? (edge.label + L" \u2192 this (" + edge.kind + L")")
                                      : (edge.kind + L" \u2192 " + edge.label);
    if (edge.pending) line += L" (not shown)";
    if (edge.cycle) line += L"  \u21BA cycle";
    return line;
}

inline std::wstring DevToolsReferenceLineMarkup(const DevToolsReferenceEdge& edge)
{
    const wchar_t* brush = edge.cycle ? L"SystemFillColorCautionBrush" : edge.pending ? L"TextFillColorSecondaryBrush" : L"TextFillColorPrimaryBrush";
    return L"<TextBlock FontSize=\"12\" TextWrapping=\"NoWrap\" TextTrimming=\"CharacterEllipsis\" IsTextSelectionEnabled=\"True\""
           L" Foreground=\"{ThemeResource " + std::wstring(brush) + L"}\" Text=\"" + DevToolsXmlEscape(DevToolsReferenceLine(edge)) + L"\"/>";
}

inline std::wstring DevToolsReferencesHeaderMarkup()
{
    return L"<TextBlock Text=\"References\" Style=\"{StaticResource CaptionTextBlockStyle}\""
           L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\""
           L" ToolTipService.ToolTip=\"Accessibility relations and XY keyboard focus targets that point at or from this element. "
           L"A cycle means following the same property comes back here; (not shown) means the target isn't in the live tree "
           L"(an unselected tab, a closed popup, or a Reactor element not created yet).\"/>";
}

// The inspector's References section, or empty when the element has none.
inline std::wstring DevToolsReferencesMarkup(const std::vector<DevToolsReferenceEdge>& edges, bool truncated)
{
    if (edges.empty()) return {};
    std::wstring m = L"<StackPanel Spacing=\"2\" Margin=\"0,8,0,0\" AutomationProperties.AutomationId=\"WinAppDevToolsReferences\">";
    m += DevToolsReferencesHeaderMarkup();
    for (const auto& edge : edges) m += DevToolsReferenceLineMarkup(edge);
    if (truncated) {
        m += L"<TextBlock FontSize=\"11\" Foreground=\"{ThemeResource TextFillColorTertiaryBrush}\""
             L" Text=\"Only part of the tree was searched for elements pointing here.\"/>";
    }
    return m + L"</StackPanel>";
}

// Pending edges arrive after the pane is built (the agent can't be asked on the UI thread): lines to append to the
// References section, with its header when the element had no other reference. Empty when there are none.
inline std::wstring DevToolsPendingReferencesMarkup(const std::vector<DevToolsReferenceEdge>& pending, bool withHeader)
{
    if (pending.empty()) return {};
    std::wstring m = L"<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Spacing=\"2\"";
    m += withHeader ? L" Margin=\"0,8,0,0\">" + DevToolsReferencesHeaderMarkup() : std::wstring(L">");
    for (const auto& edge : pending) m += DevToolsReferenceLineMarkup(edge);
    return m + L"</StackPanel>";
}
