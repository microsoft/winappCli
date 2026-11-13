// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for DevToolsReferences -- the element references (LabeledBy, DescribedBy, FlowsTo, FlowsFrom, XYFocus*) the
// inspector shows and Property.references returns: cycle detection, the JSON shape and the section markup.
//
// Build/run: scripts/test-native-units.ps1 (also invoked by src/winapp-devtools/build-devtools.ps1).

#include "DevToolsReferences.h"

#include <cstdio>
#include <map>
#include <string>
#include <vector>

namespace {

int g_failures = 0;

void Check(bool cond, const char* what)
{
    if (!cond) { ++g_failures; std::printf("  FAIL  %s\n", what); }
    else       {              std::printf("  ok    %s\n", what); }
}

std::function<std::vector<std::uintptr_t>(std::uintptr_t)> Graph(std::map<std::uintptr_t, std::vector<std::uintptr_t>> edges)
{
    return [edges](std::uintptr_t id) {
        const auto it = edges.find(id);
        return it == edges.end() ? std::vector<std::uintptr_t>{} : it->second;
    };
}

void Test_Cycles()
{
    // 1 -XYFocusDown-> 2 -XYFocusDown-> 1: pressing Down twice comes back.
    Check(DevToolsReferenceLeadsBack(1, 2, Graph({ { 2, { 1 } } })), "a two-element loop is a cycle");
    Check(DevToolsReferenceLeadsBack(1, 2, Graph({ { 2, { 3 } }, { 3, { 4 } }, { 4, { 1 } } })), "a longer loop is a cycle");
    Check(!DevToolsReferenceLeadsBack(1, 2, Graph({ { 2, { 3 } } })), "a chain that ends is not a cycle");
    Check(!DevToolsReferenceLeadsBack(1, 2, Graph({ { 2, { 3 } }, { 3, { 2 } } })),
          "a loop that never returns to the element is not its cycle, and the walk ends");
    Check(DevToolsReferenceLeadsBack(1, 2, Graph({ { 2, { 5, 1 } } })), "any target of a list property can close the loop");
    Check(DevToolsReferenceLeadsBack(1, 1, Graph({})), "an element that names itself is a cycle");
    Check(!DevToolsReferenceLeadsBack(1, 2, Graph({ { 2, { 3 } }, { 3, { 4 } }, { 4, { 1 } } }), 2),
          "the walk is bounded");
}

void Test_Json()
{
    std::vector<DevToolsReferenceEdge> edges(2);
    edges[0].kind = L"LabeledBy"; edges[0].wire = 42; edges[0].label = L"TextBlock #Name\"Label";
    edges[1].kind = L"XYFocusDown"; edges[1].incoming = true; edges[1].label = L"Button"; edges[1].cycle = true;
    const auto json = DevToolsReferencesJson(edges, true);
    Check(json.find(L"{\"kind\":\"LabeledBy\",\"direction\":\"out\",\"handle\":\"42\",\"element\":\"TextBlock #Name\\\"Label\",\"cycle\":false}") != std::wstring::npos,
          "an outgoing edge carries its handle and an escaped label");
    Check(json.find(L"\"direction\":\"in\",\"element\":\"Button\",\"cycle\":true") != std::wstring::npos,
          "an edge outside the tree has no handle");
    Check(json.find(L"\"incomingTruncated\":true") != std::wstring::npos, "a partial search says so");
    Check(DevToolsReferencesJson({}) == L"{\"edges\":[]}", "no references is an empty list");
}

void Test_Markup()
{
    Check(DevToolsReferencesMarkup({}, false).empty(), "no section without references");
    DevToolsReferenceEdge out; out.kind = L"LabeledBy"; out.label = L"TextBlock #<Name>";
    DevToolsReferenceEdge in; in.kind = L"XYFocusDown"; in.incoming = true; in.label = L"Button"; in.cycle = true;
    const auto markup = DevToolsReferencesMarkup({ out, in }, false);
    Check(markup.find(L"LabeledBy \u2192 TextBlock #&lt;Name&gt;") != std::wstring::npos, "an outgoing edge reads kind -> target, escaped");
    Check(markup.find(L"Button \u2192 this (XYFocusDown)  \u21BA cycle") != std::wstring::npos, "an incoming cycle reads source -> this");
    Check(markup.find(L"SystemFillColorCautionBrush") != std::wstring::npos, "a cycle stands out");
    Check(markup.find(L"Only part of the tree") == std::wstring::npos, "no truncation note for a complete search");
    Check(DevToolsReferencesMarkup({ out }, true).find(L"Only part of the tree") != std::wstring::npos, "a truncation note otherwise");
}

void Test_PendingFromReactor()
{
    const auto pending = DevToolsPendingReferences(
        L"{\"state\":\"ok\",\"edges\":[{\"property\":\"XYFocusRight\",\"targetType\":\"Button\"},"
        L"{\"property\":\"LabeledBy\",\"automationId\":\"Title\"},"
        L"{\"property\":\"reference#2\",\"targetType\":\"TextBlock\",\"automationId\":\"Status\"},{\"targetType\":\"Nameless\"}]}");
    Check(pending.size() == 3, "every named pending edge is kept, a nameless one is dropped");
    if (pending.size() == 3) {
        Check(pending[0].pending && !pending[0].incoming && pending[0].wire == 0, "a pending edge is outgoing and has no element");
        Check(DevToolsReferenceLine(pending[0]) == L"XYFocusRight \u2192 Button (not shown)", "named by the type Reactor expects");
        Check(DevToolsReferenceLine(pending[1]) == L"LabeledBy \u2192 AutomationId \"Title\" (not shown)", "or by the AutomationId it names");
        Check(DevToolsReferenceLine(pending[2]) == L"reference#2 \u2192 TextBlock AutomationId \"Status\" (not shown)", "or both");
        const auto json = DevToolsReferencesJson(pending);
        Check(json.find(L"\"kind\":\"XYFocusRight\",\"direction\":\"out\",\"element\":\"Button\",\"pending\":true,\"targetType\":\"Button\",\"cycle\":false")
              != std::wstring::npos, "the JSON marks a pending edge and its expected type");
        Check(json.find(L"\"automationId\":\"Title\"") != std::wstring::npos, "and the AutomationId it names");
        const auto markup = DevToolsReferencesMarkup(pending, false);
        Check(markup.find(L"(not shown)") != std::wstring::npos && markup.find(L"TextFillColorSecondaryBrush}\" Text=\"XYFocusRight") != std::wstring::npos,
              "the section shows a pending edge, muted");
    }
    // CONTROL: anything but an ok answer with edges adds nothing, so apps without the API look exactly as before.
    Check(DevToolsPendingReferences(L"{\"state\":\"absent\"}").empty(), "no Reactor, no pending edges");
    Check(DevToolsPendingReferences(L"{\"state\":\"ok\",\"supported\":false,\"edges\":[]}").empty(), "a Reactor without the API, none");
    Check(DevToolsPendingReferences(L"{\"state\":\"unavailable\",\"reason\":\"x\"}").empty(), "an agent that can't answer, none");
    Check(DevToolsPendingReferences(L"not json").empty(), "a malformed answer, none");
    Check(DevToolsPendingReferencesMarkup({}, true).empty(), "nothing to append without pending edges");
    const auto appended = DevToolsPendingReferencesMarkup(pending, true);
    Check(appended.find(L"Text=\"References\"") != std::wstring::npos, "appended lines bring the header when the section was empty");
    Check(DevToolsPendingReferencesMarkup(pending, false).find(L"Text=\"References\"") == std::wstring::npos, "and not when it already has one");
}

void Test_TargetOutsideTheLiveTree()
{
    // LabeledBy names a TextBlock in an unselected tab: it exists, but isn't in the live tree, so there is nothing to select.
    DevToolsReferenceEdge edge;
    edge.kind = L"LabeledBy";
    edge.wire = 77;
    DevToolsMarkReferenceNotShown(edge, L"Microsoft.UI.Xaml.Controls.TextBlock");
    Check(DevToolsReferenceLine(edge) == L"LabeledBy \u2192 TextBlock (not shown)", "reads (not shown), like a pending target");
    Check(DevToolsReferenceLine(edge).find(L"outside the inspected tree") == std::wstring::npos, "not 'outside the inspected tree'");
    const auto json = DevToolsReferencesJson({ edge });
    Check(json.find(L"\"element\":\"TextBlock\",\"pending\":true,\"targetType\":\"TextBlock\"") != std::wstring::npos,
          "the JSON marks it pending with its type");
    Check(json.find(L"\"handle\"") == std::wstring::npos, "and has no handle to select");
    DevToolsReferenceEdge bare;
    DevToolsMarkReferenceNotShown(bare, L"CustomLabel");
    Check(bare.label == L"CustomLabel", "a class name without a namespace is kept whole");
}

} // namespace

int RunReferencesTests()
{
    std::printf("DevToolsReferences:\n");
    Test_Cycles();
    Test_Json();
    Test_Markup();
    Test_PendingFromReactor();
    Test_TargetOutsideTheLiveTree();
    return g_failures;
}
