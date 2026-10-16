// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for DevToolsReactorPublished -- the Reactor diagnostics string published on each control for apps the managed
// agent cannot load into: field parsing, versioning, escaping, call-site locations and the property-name match.
//
// Build/run: scripts/test-native-units.ps1.

#include "DevToolsReactorNames.h"
#include "DevToolsReactorPublished.h"

#include <cstdio>
#include <string>

namespace {

int g_failures = 0;

void Check(bool cond, const char* what)
{
    if (!cond) { ++g_failures; std::printf("  FAIL  %s\n", what); }
    else       {              std::printf("  ok    %s\n", what); }
}

using DevToolsReactorPublished::Parse;
using DevToolsReactorPublished::Record;

void Test_FullRecord()
{
    Record r;
    Check(Parse(L"v=1|at=C:\\src\\App.cs:35:13|owner=Counter|element=Button|key=t1|name=incrementButton", &r),
          "a full record parses");
    Check(r.file == L"C:\\src\\App.cs" && r.line == 35 && r.column == 13, "path, line and column");
    Check(r.owner == L"Counter" && r.element == L"Button" && r.key == L"t1" && r.name == L"incrementButton",
          "owner, element, key and name");
    Check(r.mounts.empty(), "no mounted component unless named");
}

void Test_LocationForms()
{
    Record r;
    Check(Parse(L"v=1|at=C:\\src\\App.cs:80|element=Button", &r) && r.line == 80 && r.column == 0 &&
          r.file == L"C:\\src\\App.cs", "a line without a column");
    Check(Parse(L"v=1|at=/_/src/App.cs:7:2|element=Stack", &r) && r.file == L"/_/src/App.cs" && r.line == 7 &&
          r.column == 2, "a deterministic mapped path");
    Check(Parse(L"v=1|element=Stack", &r) && r.file.empty() && r.line == 0, "a record without a call site");
    Check(!Parse(L"v=1|at=C:\\src\\App.cs|element=Stack", &r), "a location without a line is refused");
    Check(!Parse(L"v=1|at=C:\\src\\App.cs:0|element=Stack", &r), "line 0 is refused");
    Check(!Parse(L"v=1|at=:12|element=Stack", &r), "a location without a path is refused");
}

void Test_ComponentElement()
{
    Record r;
    Check(Parse(L"v=1|at=C:\\src\\App.cs:46|owner=App|element=Component|mounts=Counter", &r) &&
          r.element == L"Component" && r.mounts == L"Counter" && r.owner == L"App", "a component element names what it mounts");
}

void Test_Escaping()
{
    Record r;
    Check(Parse(L"v=1|element=TextBlock|key=a%7Cb%25c", &r) && r.key == L"a|b%c", "percent-escaped bar and percent");
    Check(Parse(L"v=1|element=TextBlock|key=a%7cb", &r) && r.key == L"a|b", "lowercase hex is accepted");
    Check(!Parse(L"v=1|element=TextBlock|key=50%", &r), "a dangling percent is refused");
    Check(!Parse(L"v=1|element=TextBlock|key=%41", &r), "an escape other than %25 and %7C is refused");
}

void Test_VersionAndShape()
{
    Record r;
    Check(!Parse(L"v=2|element=Button", &r), "an unknown version is refused");
    Check(!Parse(L"element=Button|v=1", &r), "the version must come first");
    Check(!Parse(L"v=1|at=C:\\a.cs:1", &r), "a record without an element is refused");
    Check(!Parse(L"v=1|element=Button|element=TextBlock", &r), "a repeated field is refused");
    Check(!Parse(L"v=1|element=Button|v=1", &r), "a repeated version is refused");
    Check(!Parse(L"v=1|element=Button|bare", &r), "a field without '=' is refused");
    Check(!Parse(L"", &r), "an empty value is refused");
    Check(Parse(L"v=1|element=Button|hooks=3|future=x", &r) && r.element == L"Button", "unknown fields are ignored");
    Check(Parse(L"v=1|element=Button|future=a=b", &r), "an unknown field whose value holds '=' is ignored too");
}

void Test_Hooks()
{
    Record r;
    Check(Parse(L"v=1|at=C:\\src\\App.cs:66|owner=App|element=Component|mounts=Counter|hooks=0:count@68;1:items@69", &r),
          "a component element with hooks parses");
    Check(r.hooks.size() == 2 && r.hooks[0].index == 0 && r.hooks[0].name == L"count" && r.hooks[0].line == 68 &&
          r.hooks[1].index == 1 && r.hooks[1].name == L"items" && r.hooks[1].line == 69, "index, name and line of each hook");
    unsigned int line = 0;
    Check(DevToolsReactorPublished::HookName(r.hooks, 1, &line) == L"items" && line == 69, "a hook's name by index");
    Check(DevToolsReactorPublished::HookName(r.hooks, 7).empty(), "an index Reactor did not name has no name");

    Check(Parse(L"v=1|element=Component|hooks=0:count;2:done@x", &r) && r.hooks.size() == 2 && r.hooks[0].line == 0 &&
          r.hooks[1].name == L"done" && r.hooks[1].line == 0, "a hook without a readable line keeps its name");
    Check(Parse(L"v=1|element=Component|hooks=x:count;1:;:a;3:ok@4;3:again@5", &r) && r.hooks.size() == 1 &&
          r.hooks[0].index == 3 && r.hooks[0].name == L"ok", "unreadable and repeated entries are skipped, not fatal");
    Check(Parse(L"v=1|element=Component|hooks=", &r) && r.hooks.empty(), "an empty hook list");
    Check(Parse(L"v=1|element=Component|hooks=0:a%7Cb@3", &r) && r.hooks.size() == 1 && r.hooks[0].name == L"a|b",
          "hook entries are unescaped like any value");
}

void Test_SourceNames()
{
    using DevToolsReactorNames::FromSource;
    // Exact column of the call (1-based), as Reactor's interceptors record it.
    const std::wstring local = L"        var incrementButton = Button(\"+1\", inc).Margin(4);";
    Check(FromSource(local, (unsigned)local.find(L"= Button") + 3) == L"incrementButton", "var name = Call(...)");
    Check(FromSource(local, 0) == L"incrementButton", "without a column, the first call on the line");
    const std::wstring typed = L"    Element header = Text(\"Tasks\");";
    Check(FromSource(typed, (unsigned)typed.find(L"Text") + 1) == L"header", "Type name = Call(...)");
    const std::wstring generic = L"    List<Element> rows = Ui.VStack(a, b);";
    Check(FromSource(generic, (unsigned)generic.find(L"VStack") + 1) == L"rows", "a generic type and a qualifier");
    Check(FromSource(L"        _header = Text(\"Tasks\");", 0) == L"_header", "assignment to an existing field");
    Check(FromSource(L"        this.header = Text(\"Tasks\");", 0) == L"header", "assignment through this.");
    Check(FromSource(L"    private static readonly Element Logo = Image(\"logo.png\");", 0) == L"Logo", "a field with modifiers");
    Check(FromSource(L"    Element IncrementButton => Button(\"+1\", inc);", 0) == L"IncrementButton", "an expression-bodied property");
    Check(FromSource(L"    Element IncrementButton() => Button(\"+1\", inc);", 0) == L"IncrementButton", "a method or local function");
    Check(FromSource(L"    Element Row(TaskItem t) => HStack(Text(t.Title));", 0) == L"Row", "a method with parameters");
    Check(FromSource(L"        Button(\"+1\", inc),", 0).empty(), "an element inline in a tree has no name");
    Check(FromSource(L"        return Button(\"+1\", inc);", 0).empty(), "a returned element has no name");
    Check(FromSource(L"        Content = Button(\"+1\", inc),", 0).empty(), "an object-initializer member is not a variable");
    Check(FromSource(L"        t => Text(t.Title),", 0).empty(), "a lambda parameter is not a name");
    Check(FromSource(L"        if (count == Max(a)) {", 0).empty(), "a comparison is not an assignment");

    // The call that is assigned, not one nested inside it.
    const std::wstring nested = L"    var row = HStack(Text(\"a\"), Button(\"b\"));";
    Check(FromSource(nested, (unsigned)nested.find(L"HStack") + 1) == L"row", "the outer call gets the name");
    Check(FromSource(nested, (unsigned)nested.find(L"Text") + 1).empty(), "a call nested inside it does not");
    Check(FromSource(L"    var rows = items.Select(i => TextBlock(i.Name));", 0, L"", L"TextBlock").empty(),
          "without a column, a call inside a lambda is not the one assigned");
    Check(FromSource(L"    var row = HStack(TextBlock(\"a\"), Button(\"b\"));", 0, L"", L"Stack") == L"row",
          "without a column, the element kind picks its own call (HStack makes a Stack)");
    Check(FromSource(L"    var counter = Component<Counter, CounterProps>(new(\"Done\"));", 0, L"", L"Component") == L"counter",
          "a generic factory call");
    Check(FromSource(L"    var countText = TextBlock($\"{n}\").FontSize(Size(2));", 0, L"", L"TextBlock") == L"countText",
          "a modifier chain after the call");
    const std::wstring selected = L"    var items = list.Select(t => Text(t));";
    Check(FromSource(selected, (unsigned)selected.find(L"Text") + 1).empty(), "a call inside a lambda does not");

    // A statement split after its operator.
    Check(FromSource(L"            Text(\"Tasks\")", 0, L"        var header =") == L"header", "the name on the line above");
    Check(FromSource(L"            Text(\"Tasks\")", 0, L"        Element Header =>") == L"Header", "an expression body above");
    Check(FromSource(L"            Text(\"Tasks\")", 0, L"        Children(a,").empty(), "a line above that does not assign");
    Check(FromSource(L"            .Margin(4)", 0, L"        var header =").empty(), "a modifier line is not the call");
    Check(FromSource(L"x", 50).empty() && FromSource(L"", 0).empty(), "a column past the line, or no call");
}

void Test_PropertyName()
{
    using DevToolsReactorPublished::IsPropertyName;
    Check(IsPropertyName(L".ReactorSource"), "the name XamlDiagnostics reports for an unresolved owner");
    Check(IsPropertyName(L"ReactorDiagnostics.ReactorSource"), "a resolved owner");
    Check(!IsPropertyName(L"ReactorSource"), "an unqualified name is not an attached property");
    Check(!IsPropertyName(L".ReactorSourceX"), "a longer leaf does not match");
    Check(!IsPropertyName(L".ReactorState"), "Reactor's other attached properties do not match");
}

} // namespace

int RunReactorPublishedTests()
{
    std::printf("DevToolsReactorPublished:\n");
    Test_FullRecord();
    Test_LocationForms();
    Test_ComponentElement();
    Test_Escaping();
    Test_VersionAndShape();
    Test_Hooks();
    Test_SourceNames();
    Test_PropertyName();
    return g_failures;
}
