// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Native binding-answer tests. Successful invocation with a failed read-back must remain failure.
// Run through scripts/test-native-units.ps1.

#include "DevToolsBindingAnswer.h"

#include <cstdio>
#include <string>

static int g_bindingAnswerFailures = 0;

static void BCheck(bool cond, const char* what)
{
    if (!cond) { ++g_bindingAnswerFailures; std::printf("  FAIL  %s\n", what); }
    else       {                            std::printf("  ok    %s\n", what); }
}

static void BCheckEqW(const std::wstring& got, const std::wstring& want, const char* what)
{
    if (got != want) { ++g_bindingAnswerFailures; std::printf("  FAIL  %s (want \"%ls\", got \"%ls\")\n", what, want.c_str(), got.c_str()); }
    else             {                            std::printf("  ok    %s (\"%ls\")\n", what, got.c_str()); }
}

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
    BCheck(BContains(ok, L"\"bound\":\"True\""), "a read-back that reports a binding answers bound=True");
    BCheck(BContains(ok, L"\"before\":\"plain local value\""), "and names what the property held before");
    BCheck(BContains(ok, L"\"after\":\"bound title\""), "and what it evaluates to now");
    BCheck(BContains(ok, L"\"path\":\"Title\"") && BContains(ok, L"\"mode\":\"OneWay\""),
           "and echoes the path and mode that were applied");

    // THE ONE THAT MATTERS. The property is still there, the call did not fail, and NO BINDING IS INSTALLED.
    // An implementation that answered from the HRESULT would report this as a success.
    DevToolsBindingReadBack inert; inert.found = true; inert.isBinding = false; inert.value = L"plain local value";
    const std::wstring silent = DevToolsBindingInstall_Answer(L"Title", L"OneWay", before, inert, L"");
    BCheck(BContains(silent, L"\"bound\":\"False\""),
           "CONTROL: a call that changed nothing answers bound=False, which an HRESULT cannot distinguish");
    BCheck(!BContains(silent, L"\"bound\":\"True\""), "and never both");

    // A binding that installs and resolves to NOTHING is still installed. Reporting it as a failure would
    // send the developer looking for a broken graft when what is broken is the path -- which is the next
    // question, not this one.
    DevToolsBindingReadBack empty; empty.found = true; empty.isBinding = true; empty.value = L"";
    BCheck(BContains(DevToolsBindingInstall_Answer(L"NoSuchPropertyAtAll", L"OneWay", before, empty, L""),
                     L"\"bound\":\"True\""),
           "a binding that resolves to nothing is still a binding, and says so");
}

static void Test_APreconditionFailureIsUnavailableAndCarriesItsReason()
{
    std::printf("nothing to read back at all answers unavailable, with a reason\n");
    DevToolsBindingReadBack none;
    const std::wstring j = DevToolsBindingInstall_Answer(L"Title", L"OneWay", none, none,
                                                    L"this property cannot be set on this element");
    BCheckEqW(j, L"{\"state\":\"unavailable\",\"reason\":\"this property cannot be set on this element\"}",
              "the shape every other binding op already answers with");
    // A precondition failure must NOT carry bound=False as well: the window reads `bound` first and would
    // then report "the binding did not install" over the top of the reason that actually explains it.
    BCheck(!BContains(j, L"bound"), "and carries no bound field to be read instead of the reason");

    // Quotes in a path reach this string, and a reason is built from user input. Unescaped, the answer stops
    // being parseable JSON and the window renders no reason at all.
    BCheck(BContains(DevToolsBindingInstall_Answer(L"A\"B", L"OneWay", none, none, L"say \"no\""), L"\\\"no\\\""),
           "and its reason is JSON-escaped");
}

int RunBindingAnswerTests()
{
    std::printf("DevToolsBindingAnswer tests -- the native {Binding} install's answer\n");
    Test_TheAnswerIsTheReadBackAndNotTheCall();
    Test_APreconditionFailureIsUnavailableAndCarriesItsReason();
    return g_bindingAnswerFailures;
}
