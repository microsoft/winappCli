// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Source handoff tests: no handler means no ShellExecute call, and successful shell open cannot
// claim navigation to a line the verb cannot pass.

#include "DevToolsShellOpen.h"

#include <cstdio>
#include <string>

static int g_shellOpenFailures = 0;

static void CheckShellOpen(bool condition, const char* what)
{
    if (!condition) { ++g_shellOpenFailures; std::printf("  FAIL  %s\n", what); }
    else            {                        std::printf("  ok    %s\n", what); }
}

namespace {

// A recording stand-in for the OS. `launched` is the whole point of the first test: it must stay false.
struct Shell {
    std::wstring handler;       // what the association lookup answers
    bool         fileExists = true;
    int          rc = 42;       // what the launcher returns (> 32 == "the shell accepted the verb")
    bool         launched = false;
    std::wstring askedExtension;

    DevToolsShellOpen::AssocLookup Assoc()
    {
        return [this](const std::wstring& ext) { askedExtension = ext; return handler; };
    }
    DevToolsShellOpen::Exists Exists()
    {
        return [this](const std::wstring&) { return fileExists; };
    }
    DevToolsShellOpen::Launcher Launch()
    {
        return [this](const std::wstring&) { launched = true; return rc; };
    }
};

DevToolsShellOpen::Result Run(Shell& s, const std::wstring& path)
{
    return DevToolsShellOpen::Decide(path, L".xaml", s.Assoc(), s.Exists(), s.Launch());
}

} // namespace

// RULE 1. Ask before you launch. `ShellExecuteW(L"open",...)` on an unassociated type does not fail -- it
// shows the shell's app picker and still returns > 32.
static void TestNoHandlerNeverLaunches()
{
    Shell s; s.handler = L"";
    const auto r = Run(s, L"C:\\app\\MainWindow.xaml");
    CheckShellOpen(r.outcome == DevToolsShellOpen::Outcome::NoHandler,
                   "no association reports NoHandler");
    CheckShellOpen(!s.launched,
                   "with no handler, the launcher is never reached (the picker cannot appear)");
    CheckShellOpen(s.askedExtension == L".xaml",
                   "the association is asked for the extension the caller named");
    const std::wstring msg = DevToolsShellOpen::Describe(r, L"MainWindow.xaml", 88, L"C:\\app\\MainWindow.xaml", L"");
    CheckShellOpen(msg.find(L"Reveal in File Explorer") != std::wstring::npos &&
                       msg.find(L"Copy path") != std::wstring::npos,
                   "and the status names the two actions that cannot fail instead");
    CheckShellOpen(msg.find(L"88") == std::wstring::npos,
                   "a no-handler status does not mention a line nothing was going to reach");
}

// RULE 3. Stop claiming the line. The `open` verb carries no line argument, so the message must not repeat
// one back; a launcher that DOES take a line sets lineHonoured and earns it.
static void TestSuccessDoesNotClaimTheLine()
{
    Shell s; s.handler = L"Visual Studio Code";
    const auto r = Run(s, L"C:\\app\\SettingsPage.xaml");
    CheckShellOpen(s.launched && r.outcome == DevToolsShellOpen::Outcome::Handed,
                   "with a handler the launcher runs and the verb is reported accepted");
    CheckShellOpen(!r.lineHonoured,
                   "the shell `open` verb is not line-capable, and says so");
    const std::wstring msg = DevToolsShellOpen::Describe(r, L"SettingsPage.xaml", 88, L"C:\\app\\SettingsPage.xaml", L"");
    CheckShellOpen(msg.find(L"88") == std::wstring::npos,
                   "the success message does not claim a line the launcher could not pass");
    CheckShellOpen(msg.find(L"Visual Studio Code") != std::wstring::npos,
                   "it names the handler it actually handed the file to");
    // RULE 2. rc > 32 means "the shell accepted the verb", not "an editor is open". The verb is what the
    // message reports.
    CheckShellOpen(msg.find(L"handed") != std::wstring::npos && msg.find(L"opened") == std::wstring::npos,
                   "and reports what happened (handed off), not what was hoped for (opened)");

    // The day a line-capable launcher is added, the line comes back with it -- carried by the launcher's
    // report and not by the caller's optimism.
    DevToolsShellOpen::Result honoured = r;
    honoured.lineHonoured = true;
    const std::wstring withLine =
        DevToolsShellOpen::Describe(honoured, L"SettingsPage.xaml", 88, L"C:\\app\\SettingsPage.xaml", L"");
    CheckShellOpen(withLine.find(L"88") != std::wstring::npos,
                   "a line-capable launcher DOES get to name the line");
}

static void TestLaunchRefused()
{
    Shell s; s.handler = L"Notepad"; s.rc = 31;   // <= 32 is the shell's refusal
    const auto r = Run(s, L"C:\\app\\MainWindow.xaml");
    CheckShellOpen(r.outcome == DevToolsShellOpen::Outcome::LaunchFailed,
                   "a refused verb is reported as a failure, not as success");
    const std::wstring msg = DevToolsShellOpen::Describe(r, L"MainWindow.xaml", 0, L"C:\\app\\MainWindow.xaml", L"");
    CheckShellOpen(msg.find(L"Notepad") != std::wstring::npos,
                   "and the failure names the handler that refused it");
}

static void TestUnresolvedAndMissing()
{
    {
        Shell s; s.handler = L"Notepad";
        const auto r = Run(s, L"");
        CheckShellOpen(r.outcome == DevToolsShellOpen::Outcome::NoPath, "an unresolved URI reports NoPath");
        CheckShellOpen(!s.launched, "and nothing is launched for it");
    }
    {
        Shell s; s.handler = L"Notepad"; s.fileExists = false;
        const auto r = Run(s, L"C:\\app\\Gone.xaml");
        CheckShellOpen(r.outcome == DevToolsShellOpen::Outcome::NotFound, "a missing file reports NotFound");
        CheckShellOpen(!s.launched, "and nothing is launched for it either");
        CheckShellOpen(s.askedExtension.empty(),
                       "the association is not even asked for a file that is not there");
    }
}

// The comments row leads with "that element isn't on this screen right now"; the properties pane does not,
// because there the file IS what the user asked for. One helper, two lead-ins, no duplicated message logic.
static void TestOffScreenPrefix()
{
    Shell s; s.handler = L"Notepad";
    const auto r = Run(s, L"C:\\app\\MainWindow.xaml");
    const std::wstring fromRow =
        DevToolsShellOpen::Describe(r, L"MainWindow.xaml", 0, L"C:\\app\\MainWindow.xaml", L"That element isn't on this screen right now");
    const std::wstring fromProps =
        DevToolsShellOpen::Describe(r, L"MainWindow.xaml", 0, L"C:\\app\\MainWindow.xaml", L"");
    CheckShellOpen(fromRow.find(L"isn't on this screen") != std::wstring::npos,
                   "the comments row's status leads with why it did not select anything");
    CheckShellOpen(fromProps.find(L"isn't on this screen") == std::wstring::npos,
                   "the properties pane's status does not, because nothing was off screen there");
    CheckShellOpen(fromRow.find(L"Notepad") != std::wstring::npos &&
                       fromProps.find(L"Notepad") != std::wstring::npos,
                   "and both report the same outcome from the same decision");
}

int RunShellOpenTests()
{
    std::printf("DevTools shell hand-off tests\n");
    TestNoHandlerNeverLaunches();
    TestSuccessDoesNotClaimTheLine();
    TestLaunchRefused();
    TestUnresolvedAndMissing();
    TestOffScreenPrefix();
    return g_shellOpenFailures;
}
