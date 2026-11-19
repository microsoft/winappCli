// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Native binding-answer tests. Successful invocation with a failed read-back must remain failure.
// Run through scripts/test-native-units.ps1.

#include "DevToolsBindingAnswer.h"
#include "DevToolsEditUndo.h"

#include <cstdio>
#include <string>
#include "DevToolsTestCheck.h"

static bool BContains(const std::wstring& hay, const wchar_t* needle)
{
    return hay.find(needle) != std::wstring::npos;
}

static void Test_TheAnswerIsTheReadBackAndNotTheCall()
{
    std::printf("the answer is what the ELEMENT reports afterwards, not that the call was made\n");

    DevToolsBindingReadBack before; before.found = true; before.value = L"plain local value";
    DevToolsBindingReadBack landed; landed.found = true; landed.isBinding = true; landed.value = L"bound title";

    const std::wstring ok = DevToolsBindingInstall_Answer(L"Title", L"OneWay", before, landed, L"");
    Check(BContains(ok, L"\"bound\":\"True\""), "a read-back that reports a binding answers bound=True");
    Check(BContains(ok, L"\"before\":\"plain local value\""), "and names what the property held before");
    Check(BContains(ok, L"\"after\":\"bound title\""), "and what it evaluates to now");
    Check(BContains(ok, L"\"path\":\"Title\"") && BContains(ok, L"\"mode\":\"OneWay\""),
           "and echoes the path and mode that were applied");

    // THE ONE THAT MATTERS. The property is still there, the call did not fail, and NO BINDING IS INSTALLED.
    // An implementation that answered from the HRESULT would report this as a success.
    DevToolsBindingReadBack inert; inert.found = true; inert.isBinding = false; inert.value = L"plain local value";
    const std::wstring silent = DevToolsBindingInstall_Answer(L"Title", L"OneWay", before, inert, L"");
    Check(BContains(silent, L"\"bound\":\"False\""),
           "CONTROL: a call that changed nothing answers bound=False, which an HRESULT cannot distinguish");
    Check(!BContains(silent, L"\"bound\":\"True\""), "and never both");

    // A binding that installs and resolves to NOTHING is still installed. Reporting it as a failure would
    // send the developer looking for a broken graft when what is broken is the path -- which is the next
    // question, not this one.
    DevToolsBindingReadBack empty; empty.found = true; empty.isBinding = true; empty.value = L"";
    Check(BContains(DevToolsBindingInstall_Answer(L"NoSuchPropertyAtAll", L"OneWay", before, empty, L""),
                     L"\"bound\":\"True\""),
           "a binding that resolves to nothing is still a binding, and says so");
}

static void Test_APreconditionFailureIsUnavailableAndCarriesItsReason()
{
    std::printf("nothing to read back at all answers unavailable, with a reason\n");
    DevToolsBindingReadBack none;
    const std::wstring j = DevToolsBindingInstall_Answer(L"Title", L"OneWay", none, none,
                                                    L"this property cannot be set on this element");
    CheckEqW(j, L"{\"state\":\"unavailable\",\"reason\":\"this property cannot be set on this element\"}",
              "the shape every other binding op already answers with");
    // A precondition failure must NOT carry bound=False as well: the window reads `bound` first and would
    // then report "the binding did not install" over the top of the reason that actually explains it.
    Check(!BContains(j, L"bound"), "and carries no bound field to be read instead of the reason");

    // Quotes in a path reach this string, and a reason is built from user input. Unescaped, the answer stops
    // being parseable JSON and the window renders no reason at all.
    Check(BContains(DevToolsBindingInstall_Answer(L"A\"B", L"OneWay", none, none, L"say \"no\""), L"\\\"no\\\""),
           "and its reason is JSON-escaped");
}

static DevToolsReadProp Row(const wchar_t* source, const wchar_t* value, const wchar_t* valueState = L"",
                            const wchar_t* authoredKind = L"", const wchar_t* authoredKey = L"")
{
    DevToolsReadProp r;
    r.name = L"Foreground"; r.source = source; r.value = value; r.valueState = valueState;
    r.writeType = L"Microsoft.UI.Xaml.Media.Brush"; r.authoredKind = authoredKind; r.authoredKey = authoredKey;
    return r;
}

static std::wstring Undo(const DevToolsEditPrior& prior) { return DevToolsEditUndoMembers(L"42", L"Foreground", prior); }

static void Test_AnEditAnswersWithTheCallThatUndoesIt()
{
    std::printf("a protocol edit's undo puts back what the property held, whatever kind that was\n");

    const auto unset = DevToolsClassifyPrior(Row(L"Style", L"#FF000000"), nullptr);
    Check(BContains(Undo(unset), L"\"method\":\"HotReload.clearProperty\""), "a value from a style is undone by clearing");
    Check(std::wstring(DevToolsPriorKindToken(unset)) == L"unset", "and is reported as unset");

    const auto literal = DevToolsClassifyPrior(Row(L"Local", L"#FFFF0000"), nullptr);
    Check(BContains(Undo(literal), L"\"method\":\"HotReload.setProperty\"") &&
          BContains(Undo(literal), L"\"value\":\"#FFFF0000\"") &&
          BContains(Undo(literal), L"\"type\":\"Microsoft.UI.Xaml.Media.Brush\""), "a local literal is written back");
    Check(BContains(Undo(DevToolsClassifyPrior(Row(L"DevTools", L"Red"), nullptr)), L"HotReload.setProperty"),
          "including one an earlier DevTools write set");

    const auto authored = DevToolsClassifyPrior(Row(L"Local", L"#FF0078D4", L"", L"themeResource", L"AccentBrush"), nullptr);
    Check(BContains(Undo(authored), L"\"method\":\"Resource.assign\"") && BContains(Undo(authored), L"\"key\":\"AccentBrush\"") &&
          BContains(Undo(authored), L"\"theme\":true"), "an authored {ThemeResource} is re-assigned by key, not as a colour");

    DevToolsSessionEdit assigned; assigned.resource = true; assigned.key = L"Danger"; assigned.after = L"#FFC42B1C";
    const auto session = DevToolsClassifyPrior(Row(L"Local", L"#FFC42B1C"), &assigned);
    Check(BContains(Undo(session), L"\"key\":\"Danger\"") && BContains(Undo(session), L"\"theme\":false"),
          "a key this session assigned is remembered, because the runtime forgets it");
    Check(BContains(Undo(DevToolsClassifyPrior(Row(L"Local", L"#FF00FF00"), &assigned)), L"HotReload.setProperty"),
          "and is forgotten once the property no longer shows that resource");

    auto boundRow = Row(L"Local", L"Title", L"binding");
    boundRow.binding = L"{Binding Vm.Title, Mode=OneWay}";
    DevToolsSessionEdit installed; installed.path = L"Vm.Title"; installed.mode = L"OneWay";
    const auto reinstall = DevToolsClassifyPrior(boundRow, &installed);
    Check(BContains(Undo(reinstall), L"\"method\":\"Binding.install\"") && BContains(Undo(reinstall), L"\"path\":\"Vm.Title\""),
          "a binding DevTools installed is undone by installing the earlier one again");

    auto appBinding = DevToolsClassifyPrior(boundRow, nullptr);
    Check(BContains(Undo(appBinding), L"\"undo\":null") && BContains(Undo(appBinding), L"undoReason"),
          "an app binding with no capture has no undo, and says why");
    appBinding.captured = true;
    Check(BContains(Undo(appBinding), L"\"method\":\"Binding.restore\""), "a captured app binding is undone by Binding.restore");

    auto xbind = DevToolsClassifyPrior(Row(L"Local", L"Heading", L"", L"xBind"), nullptr);
    Check(std::wstring(DevToolsPriorKindToken(xbind)) == L"xBind", "a compiled {x:Bind} value is recognised from the markup");
    xbind.captured = true;
    Check(BContains(Undo(xbind), L"\"method\":\"Binding.restore\"") && BContains(Undo(xbind), L"\"confirmOwnerRebind\":true"),
          "and is undone by Binding.restore, confirming the owner-wide rebind it needs");

    Check(BContains(Undo(DevToolsClassifyPrior(Row(L"Local", L"", L"unresolved"), nullptr)), L"\"undo\":null"),
          "a local value with no text form has no undo rather than a wrong one");
    Check(DevToolsDescribedBindingPath(L"{Binding ElementName=Box, Mode=OneWay}").empty(),
          "a binding with no plain path never matches a session install");
}

int RunBindingAnswerTests()
{
    std::printf("DevToolsBindingAnswer tests -- the native {Binding} install's answer\n");
    Test_TheAnswerIsTheReadBackAndNotTheCall();
    Test_APreconditionFailureIsUnavailableAndCarriesItsReason();
    Test_AnEditAnswersWithTheCallThatUndoesIt();
    return g_testFailures;
}
