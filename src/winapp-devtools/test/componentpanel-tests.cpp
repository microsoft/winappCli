// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for DevToolsComponentPanel -- what the Reactor Component section of the inspector
// shows for Reactor.component / Reactor.element answers, and which editors it offers.
//
// Build/run: scripts/test-native-units.ps1 (also invoked by src/winapp-devtools/build-devtools.ps1).

#include "DevToolsComponentPanel.h"

#include <cstdio>
#include <string>

namespace {

int g_failures = 0;

void Check(bool cond, const char* what)
{
    if (!cond) { ++g_failures; std::printf("  FAIL  %s\n", what); }
    else       {              std::printf("  ok    %s\n", what); }
}

bool Has(const std::wstring& text, const std::wstring& part) { return text.find(part) != std::wstring::npos; }

const wchar_t* kCounter = LR"({"state":"ok","api":"reactor","exact":true,
  "component":{"kind":"class","name":"Counter","isRoot":false,"mounted":true},
  "definition":{"file":"/_/App.cs","fileOnDisk":"C:\\src\\App.cs","line":71,"method":"Counter.Render","stale":true},
  "declared":{"file":"C:\\src\\App.cs","line":44},
  "props":{"type":"CounterProps","values":[{"name":"Label","value":"Done today"}]},
  "hooks":[{"index":0,"kind":"state","type":"int","value":"3","canSet":true,"editable":true},
           {"index":1,"kind":"state","type":"string","value":"\"Ada \\\"L\\\"\"","canSet":true,"editable":true,"name":"owner","line":69},
           {"index":2,"kind":"context","type":"string","value":"\"Compact\"","context":"Density","canSet":false,"editable":false},
           {"index":3,"kind":"effect"}],
  "contexts":[{"hook":2,"name":"Density","value":"\"Compact\""}]})";

const wchar_t* kElement = LR"json({"state":"ok","tagged":true,"handlers":[
  {"event":"OnClick","method":"lambda in Counter.Render","signature":"()","invokable":true,"file":"C:\\src\\App.cs","line":76},
  {"event":"OnDrop","method":"App.Drop","signature":"(TaskItem item)","invokable":false}],
  "themeBindings":[{"property":"Foreground","resourceKey":"AccentTextFillColorPrimaryBrush"}]})json";

const DevToolsComponentColors kColors{ L"#FF111111", L"#FF666666", L"#FF0067C0", L"#FFC42B1C" };

void Test_ParseReadsIdentityPropsHooksContextsAndHandlers()
{
    const auto view = DevToolsComponentPanel_Parse(kCounter, kElement);
    Check(view.ok, "an ok answer parses");
    Check(view.name == L"Counter" && view.kind == L"class component", "the component's name and kind");
    Check(view.defined == L"App.cs:71 (Counter.Render)", "where its render method is defined, on this machine");
    Check(view.declared == L"App.cs:44", "where its parent declares it");
    Check(view.stale, "a definition file that changed since the build is flagged");
    Check(view.props.size() == 1 && view.propsType == L"CounterProps", "props with their type");
    Check(view.hooks.size() == 4 && view.hooks[0].editable && !view.hooks[2].editable, "hooks with which are editable");
    Check(view.contexts.size() == 1 && view.contexts[0].first == L"Density", "contexts");
    Check(view.handlers.size() == 2 && view.handlers[0].invokable && !view.handlers[1].invokable &&
          view.handlers[0].site == L"App.cs:76", "handlers with their site and whether DevTools can run them");
}

void Test_ParseExplainsWhyThereIsNoComponent()
{
    Check(!DevToolsComponentPanel_Parse(LR"({"state":"none"})", L"").ok, "no component is not ok");
    Check(Has(DevToolsComponentPanel_Parse(LR"({"state":"none"})", L"").problem, L"No Reactor component"), "and says so");
    Check(Has(DevToolsComponentPanel_Parse(LR"({"state":"unavailable","reason":"the agent is old"})", L"").problem, L"the agent is old"),
          "an unavailable answer carries its reason");
    Check(Has(DevToolsComponentPanel_Parse(L"not json", L"").problem, L"did not describe"), "garbage is not a component");
}

void Test_MarkupOffersEditorsOnlyForSettableStateWhenWritable()
{
    const auto view = DevToolsComponentPanel_Parse(kCounter, kElement);
    const std::wstring writable = DevToolsComponentPanel_Markup(view, false, kColors);
    Check(Has(writable, L"x:Name=\"DevToolsHook0\"") && Has(writable, L"x:Name=\"DevToolsHook1\""), "settable state gets an editor");
    Check(!Has(writable, L"DevToolsHook2") && !Has(writable, L"DevToolsHook3"), "contexts and effects do not");
    Check(Has(writable, L"Text=\"Ada &quot;L&quot;\""), "a text editor shows the text itself, unquoted and unescaped");
    Check(Has(writable, L"x:Name=\"DevToolsHandler0\"") && !Has(writable, L"DevToolsHandler1"), "only invokable handlers get Run");
    Check(Has(writable, L"&lt;Counter&gt; \u00B7 class component \u00B7 defined App.cs:71 (Counter.Render)"), "the header names the component and where it is defined");
    Check(Has(writable, L"Foreground \u2190 ThemeRef AccentTextFillColorPrimaryBrush"), "ThemeRef provenance is shown");
    std::wstring ancestor = kCounter;
    ancestor.replace(ancestor.find(L"\"exact\":true"), 12, L"\"exact\":false");
    Check(Has(DevToolsComponentPanel_Markup(DevToolsComponentPanel_Parse(ancestor, kElement), true, kColors), L"Rendered by &lt;Counter&gt;"),
          "a component found above the element says it rendered it");
    Check(Has(writable, L"changed since the app was built"), "and warns when the file changed");
    Check(Has(writable, L"Density"), "contexts are listed");

    const std::wstring readOnly = DevToolsComponentPanel_Markup(view, true, kColors);
    Check(!Has(readOnly, L"TextBox") && !Has(readOnly, L"<Button"), "a read-only session gets no editors and no Run buttons");
    Check(Has(readOnly, L"[0] state int = 3"), "it still shows the values");
    Check(view.hooks[1].name == L"owner" && view.hooks[0].name.empty(), "a hook Reactor named carries its C# variable");
    Check(Has(readOnly, L"[1] owner state string"), "a named hook shows its name next to its index");
    Check(Has(writable, L"AutomationProperties.Name=\"State [1] owner state string\""), "and its editor is named after it");
}

void Test_EditProblemSaysWhatHappened()
{
    Check(DevToolsComponentPanel_EditProblem(LR"({"state":"ok"})").empty(), "a write that worked has no problem");
    Check(DevToolsComponentPanel_EditProblem(LR"({"state":"refused","reason":"'x' is not a valid int"})") == L"'x' is not a valid int",
          "a refusal is shown as the agent said it");
    Check(Has(DevToolsComponentPanel_EditProblem(LR"({"state":"unavailable","indeterminate":true})"), L"may or may not"),
          "a timeout is indeterminate, not a failure or a success");
    Check(Has(DevToolsComponentPanel_EditProblem(LR"({"state":"threw","exception":"System.Exception","message":"boom"})"), L"boom"),
          "a handler that threw says why");
}

}   // namespace

int RunComponentPanelTests()
{
    std::printf("DevToolsComponentPanel tests -- the Reactor Component section\n");
    Test_ParseReadsIdentityPropsHooksContextsAndHandlers();
    Test_ParseExplainsWhyThereIsNoComponent();
    Test_MarkupOffersEditorsOnlyForSettableStateWhenWritable();
    Test_EditProblemSaysWhatHappened();
    return g_failures;
}
