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

static std::wstring Undo(const DevToolsEditPrior& prior, bool editLeftBinding = false)
{
    return DevToolsEditUndoMembers(L"42", L"Foreground", prior, editLeftBinding);
}

static void Test_AnEditAnswersWithTheCallsThatUndoIt()
{
    std::printf("a protocol edit's undo puts back what the property held, whatever kind that was\n");

    const auto unset = DevToolsClassifyPrior(Row(L"Style", L"#FF000000"), nullptr);
    Check(BContains(Undo(unset), L"\"undo\":[{\"method\":\"HotReload.clearProperty\""), "a value from a style is undone by clearing");
    Check(std::wstring(DevToolsPriorKindToken(unset)) == L"unset", "and is reported as unset");

    const auto literal = DevToolsClassifyPrior(Row(L"Local", L"#FFFF0000"), nullptr);
    Check(BContains(Undo(literal), L"\"undo\":[{\"method\":\"HotReload.setProperty\"") &&
          BContains(Undo(literal), L"\"value\":\"#FFFF0000\"") &&
          BContains(Undo(literal), L"\"type\":\"Microsoft.UI.Xaml.Media.Brush\""), "a local literal is written back");
    Check(BContains(Undo(DevToolsClassifyPrior(Row(L"DevTools", L"Red"), nullptr)), L"HotReload.setProperty"),
          "including one an earlier DevTools write set");

    // Writing a literal over a TwoWay binding writes through to the source and keeps the binding.
    const std::wstring overBinding = Undo(literal, /*editLeftBinding*/ true);
    Check(BContains(overBinding, L"\"undo\":[{\"method\":\"HotReload.clearProperty\"") &&
          overBinding.find(L"HotReload.setProperty") > overBinding.find(L"HotReload.clearProperty"),
          "undoing an install over a literal clears the installed binding before writing the literal back");

    const auto authored = DevToolsClassifyPrior(Row(L"Local", L"#FF0078D4", L"", L"themeResource", L"AccentBrush"), nullptr);
    Check(BContains(Undo(authored), L"\"method\":\"Resource.assign\"") && BContains(Undo(authored), L"\"key\":\"AccentBrush\"") &&
          BContains(Undo(authored), L"\"theme\":true"), "an authored {ThemeResource} is re-assigned by key, not as a colour");
    DevToolsEditObserved changedInCode; changedInCode.authoredKeyMatch = 0;
    Check(BContains(Undo(DevToolsClassifyPrior(Row(L"Local", L"#FF0000FF", L"", L"staticResource", L"OkBrush"), nullptr, changedInCode)),
                    L"\"value\":\"#FF0000FF\""),
          "but once app code set another value, undo puts back that value, not the key's");
    DevToolsEditObserved unknownKey; unknownKey.authoredKeyMatch = -1;
    Check(BContains(Undo(DevToolsClassifyPrior(Row(L"Local", L"#FF0078D4", L"", L"themeResource", L"AccentBrush"), nullptr, unknownKey)),
                    L"\"key\":\"AccentBrush\""),
          "CONTROL: when DevTools cannot read the key's value to compare, it keeps the authored key");

    DevToolsSessionEdit assigned; assigned.resource = true; assigned.key = L"Danger"; assigned.after = L"#FFC42B1C";
    const auto session = DevToolsClassifyPrior(Row(L"Local", L"#FFC42B1C"), &assigned);
    Check(BContains(Undo(session), L"\"key\":\"Danger\"") && BContains(Undo(session), L"\"theme\":false"),
          "a key this session assigned is remembered, because the runtime forgets it");
    Check(BContains(Undo(DevToolsClassifyPrior(Row(L"Local", L"#FF00FF00"), &assigned)), L"HotReload.setProperty"),
          "and is forgotten once the property no longer shows that resource");

    DevToolsSessionEdit themed; themed.resource = true; themed.theme = true; themed.key = L"AccentText";
    themed.after = L"#FF99EBFF"; themed.elementTheme = 2;
    DevToolsEditObserved light; light.elementTheme = 1;
    Check(BContains(Undo(DevToolsClassifyPrior(Row(L"Local", L"#FF003E92", L"", L"themeResource", L"Secondary"), &themed, light)),
                    L"\"key\":\"AccentText\""),
          "a theme key this session assigned survives a theme change that changes its value");
    DevToolsEditObserved dark; dark.elementTheme = 2;
    Check(BContains(Undo(DevToolsClassifyPrior(Row(L"Local", L"#FF0000FF"), &themed, dark)), L"\"value\":\"#FF0000FF\""),
          "CONTROL: in the same theme, a value the key never produced is undone as that value");

    auto boundRow = Row(L"Binding", L"Title", L"binding");
    boundRow.binding = L"{Binding Vm.Title, Mode=OneWay}";
    DevToolsSessionEdit installed; installed.path = L"Vm.Title"; installed.mode = L"OneWay"; installed.bindingId = L"1302";
    DevToolsEditObserved same; same.bindingId = L"1302";
    const auto reinstall = DevToolsClassifyPrior(boundRow, &installed, same);
    Check(BContains(Undo(reinstall), L"\"method\":\"Binding.install\"") && BContains(Undo(reinstall), L"\"path\":\"Vm.Title\"") &&
          !BContains(Undo(reinstall, true), L"clearProperty"),
          "the binding DevTools installed is undone by installing the earlier one again");
    DevToolsEditObserved restored; restored.bindingId = L"1418";
    Check(DevToolsClassifyPrior(boundRow, &installed, restored).kind == DevToolsPriorKind::Binding,
          "once a restore replaces that expression, the same path no longer counts as DevTools' install");

    auto appBinding = DevToolsClassifyPrior(boundRow, nullptr);
    Check(BContains(Undo(appBinding), L"\"undo\":[]") && BContains(Undo(appBinding), L"undoReason"),
          "an app binding with no capture has no undo, and says why");
    appBinding.captured = true;
    Check(BContains(Undo(appBinding), L"\"method\":\"Binding.restore\""), "a captured app binding is undone by Binding.restore");

    auto nullBound = Row(L"Binding", L"", L"null");
    nullBound.binding = L"{Binding SelectedItem, Mode=OneWay}";
    Check(DevToolsClassifyPrior(nullBound, nullptr).kind == DevToolsPriorKind::Binding,
          "a binding whose current value is null is still a binding, not an unset property");
    Check(DevToolsClassifyPrior(Row(L"Binding", L"", L"unresolved"), nullptr).kind == DevToolsPriorKind::Binding,
          "and so is one whose object value cannot be shown as text");

    auto xbind = DevToolsClassifyPrior(Row(L"Local", L"Heading", L"", L"xBind"), nullptr);
    Check(std::wstring(DevToolsPriorKindToken(xbind)) == L"xBind", "a compiled {x:Bind} value is recognised from the markup");
    xbind.captured = true;
    Check(BContains(Undo(xbind), L"\"method\":\"Binding.restore\"") && BContains(Undo(xbind), L"\"confirmOwnerRebind\":true"),
          "and is undone by Binding.restore, confirming the owner-wide rebind it needs");
    const std::wstring xbindAfterInstall = Undo(xbind, /*editLeftBinding*/ true);
    Check(BContains(xbindAfterInstall, L"\"undo\":[{\"method\":\"HotReload.clearProperty\"") &&
          xbindAfterInstall.find(L"Binding.restore") > xbindAfterInstall.find(L"HotReload.clearProperty"),
          "undoing a {Binding} installed over an {x:Bind} clears it first, so the rebind cannot write through it");

    Check(DevToolsPathNamesSecret(L"Password") && DevToolsPathNamesSecret(L"Vm.Login.UserPassword") &&
          DevToolsPathNamesSecret(L"Accounts[0].Password") && DevToolsPathNamesSecret(L"Vm.Password.Length") &&
          DevToolsPathNamesSecret(L"Creds.Password.Value"),
          "a binding path that reads a secret anywhere along it is recognised");
    Check(DevToolsPathNamesSecret(L"Credentials[Password]") && DevToolsPathNamesSecret(L"Vm.Settings['DbPassword']") &&
          DevToolsPathNamesSecret(L"Items[Password].Name"),
          "and so is an indexer key that names a secret, which reads that entry");
    Check(!DevToolsPathNamesSecret(L"Vm.Title") && !DevToolsPathNamesSecret(L"Items[0].Name") &&
          !DevToolsPathNamesSecret(L"Items[PasswordHint]"),
          "CONTROL: ordinary paths and keys are not");
    Check(DevToolsTextPrintsSecret(L"Login { User = ada, Password = hunter2 }") &&
          DevToolsTextPrintsSecret(L"[Login { User = ada, DbPassword = x }]") &&
          DevToolsTextPrintsSecret(L"State { Auth = SignedIn {  password  = x } }"),
          "a value whose text prints a member named like a password is recognised, as a record prints it");
    Check(!DevToolsTextPrintsSecret(L"Enter your password = here") && !DevToolsTextPrintsSecret(L"Login { User = ada }") &&
          !DevToolsTextPrintsSecret(L"Login { PasswordHint = x }") && !DevToolsTextPrintsSecret(L""),
          "CONTROL: ordinary text, a record without one and a member that only starts with the word are not");

    auto halfRed = Row(L"Local", L"#FFFF0000");
    DevToolsReadProp color; color.name = L"Color"; color.value = L"Red";
    DevToolsReadProp opacity; opacity.name = L"Opacity"; opacity.value = L"0.5";
    halfRed.children = { color, opacity };
    Check(BContains(Undo(DevToolsClassifyPrior(halfRed, nullptr)), L"\"undo\":[]") &&
          BContains(Undo(DevToolsClassifyPrior(halfRed, nullptr)), L"Opacity=0.5"),
          "a brush whose opacity its text cannot keep gets no text undo, and says why");
    halfRed.children[1].value = L"1";
    Check(BContains(Undo(DevToolsClassifyPrior(halfRed, nullptr)), L"HotReload.setProperty"),
          "an opaque brush still round-trips as text");

    Check(BContains(Undo(DevToolsClassifyPrior(Row(L"Local", L"", L"unresolved"), nullptr)), L"\"undo\":[]"),
          "a local value with no text form has no undo rather than a wrong one");
}

int RunBindingAnswerTests()
{
    std::printf("DevToolsBindingAnswer tests -- the native {Binding} install's answer\n");
    Test_TheAnswerIsTheReadBackAndNotTheCall();
    Test_APreconditionFailureIsUnavailableAndCarriesItsReason();
    Test_AnEditAnswersWithTheCallsThatUndoIt();
    return g_testFailures;
}
