// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// DevToolsPerf's two load-bearing invariants. Both are the sort that survive a refactor silently and
// then quietly produce a table nobody can add up:
//
// 1. A sample is only taken on the app UI thread, and one taken anywhere else is COUNTED as dropped
// rather than discarded. `calls: 0` must never be able to mean "it ran, on another thread".
// 2. Self time excludes nested scopes. Without it a nesting pair double-counts, and the self column --
// the only column that sums to a budget -- overstates by exactly the child's cost.

#include "DevToolsPerf.h"

#include <windows.h>
#include <cstdio>
#include <string>

static int g_perfFailures = 0;

static void CheckPerf(bool cond, const char* what)
{
    if (!cond) { ++g_perfFailures; std::printf("  FAIL  %s\n", what); }
    else       {                   std::printf("  ok    %s\n", what); }
}

// Pull one numeric field out of a site's object in the report JSON. Deliberately a string scan rather
// than a parser: the point is to assert on the bytes a client actually receives.
static double FieldOf(const std::wstring& json, const wchar_t* site, const wchar_t* field)
{
    const std::wstring key = std::wstring(L"\"site\":\"") + site + L"\"";
    size_t at = json.find(key);
    if (at == std::wstring::npos) return -1.0;
    size_t f = json.find(std::wstring(L"\"") + field + L"\":", at);
    if (f == std::wstring::npos) return -1.0;
    f += wcslen(field) + 3;
    return _wtof(json.c_str() + f);
}

static void BurnUs(double us)
{
    LARGE_INTEGER freq{}, a{}, b{};
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&a);
    for (;;) {
        QueryPerformanceCounter(&b);
        if ((double)(b.QuadPart - a.QuadPart) * 1000000.0 / (double)freq.QuadPart >= us) return;
    }
}

static void TestOffThreadIsCountedNotSilentlyDropped()
{
    std::printf("a scope on the wrong thread is reported, not silently absent\n");
    DevToolsPerf_LatchUiThread();
    DevToolsPerf_SetEnabled(true);
    DevToolsPerf_Reset();

    // CONTROL: with no UI thread latched at all, every site would read zero -- which is exactly what a
    // healthy-but-free DevTools would look like. The report says so via `uiThread`, and that is the only
    // thing distinguishing the two.
    { WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinPropPane); BurnUs(300.0); }

    HANDLE t = CreateThread(nullptr, 0, [](LPVOID) -> DWORD {
        WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinPropPane);
        BurnUs(2000.0);
        return 0;
    }, nullptr, 0, nullptr);
    CheckPerf(t != nullptr, "the off-thread sampler thread started");
    if (t) { WaitForSingleObject(t, 10000); CloseHandle(t); }

    const std::wstring json = DevToolsPerf_ReportJson();
    CheckPerf(FieldOf(json, L"win.propPane", L"calls") == 1.0,
              "only the UI-thread scope was counted (the other thread's 2 ms is not charged to the app)");
    CheckPerf(FieldOf(json, L"win.propPane", L"offThread") == 1.0,
              "the off-thread scope is reported as offThread rather than vanishing");
    CheckPerf(json.find(L"\"uiThread\":0,") == std::wstring::npos,
              "the report names the latched UI thread, so an all-zero table can be told from an unlatched one");
    DevToolsPerf_SetEnabled(false);
}

static void TestSelfTimeExcludesNestedScopes()
{
    std::printf("self time subtracts nested scopes, so the self column sums to a budget\n");
    DevToolsPerf_LatchUiThread();
    DevToolsPerf_SetEnabled(true);
    DevToolsPerf_Reset();

    {
        WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinTreeRefresh);   // outer: 2 ms of its own around a 10 ms child
        BurnUs(1000.0);
        { WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinPreview); BurnUs(10000.0); }
        BurnUs(1000.0);
    }

    const std::wstring json = DevToolsPerf_ReportJson();
    const double outerTotal = FieldOf(json, L"win.treeRefresh", L"totalUs");
    const double outerSelf  = FieldOf(json, L"win.treeRefresh", L"selfUs");
    const double childTotal = FieldOf(json, L"win.preview", L"totalUs");

    CheckPerf(outerTotal >= 11000.0, "the outer scope's INCLUSIVE time contains the child");
    CheckPerf(childTotal >= 9500.0, "the child scope recorded its own work");
    // Negative control: inclusive time must not be reported as self time for nested scopes.
    CheckPerf(outerSelf < outerTotal - 8000.0,
              "CONTROL: self is NOT inclusive -- the child's 10 ms is not charged twice");
    CheckPerf(outerSelf < 6000.0, "the outer scope's self time is its own work only");
    DevToolsPerf_SetEnabled(false);
}

static void TestDisabledAccountingRecordsNothing()
{
    std::printf("accounting is opt-in: a normal session records nothing at all\n");
    DevToolsPerf_LatchUiThread();
    DevToolsPerf_SetEnabled(true);
    DevToolsPerf_Reset();
    DevToolsPerf_SetEnabled(false);

    { WINAPP_DEVTOOLS_PERF(DevToolsPerfSite::WinXamlParse); BurnUs(500.0); }

    const std::wstring json = DevToolsPerf_ReportJson();
    CheckPerf(FieldOf(json, L"win.xamlParse", L"calls") == 0.0, "a scope taken while disabled is not recorded");
    CheckPerf(FieldOf(json, L"win.xamlParse", L"offThread") == 0.0,
              "and it is not misreported as an off-thread drop either");
}

static void TestNewSitesAreReportedUnderTheirOwnNames()
{
    // Assert site-name identity using exact call counts, not scheduler-sensitive duration bands.
// Include the enum tail so appending a site without its matching name is detected.
    std::printf("each accounted site is reported under its own name, not its neighbour's\n");
    DevToolsPerf_LatchUiThread();
    DevToolsPerf_SetEnabled(true);
    DevToolsPerf_Reset();

    struct Expect { DevToolsPerfSite site; const wchar_t* name; int calls; };
    static const Expect kExpect[] = {
        { DevToolsPerfSite::OverlayHighlight,   L"overlay.highlight",    1 },
        { DevToolsPerfSite::WinTreeFilter,      L"win.treeFilter",       2 },
        { DevToolsPerfSite::WinRowRealize,      L"win.rowRealize",       3 },
        { DevToolsPerfSite::WinTreeVisible,     L"win.treeVisible",      4 },
        { DevToolsPerfSite::WinTreeHover,       L"win.treeHover",        5 },
        { DevToolsPerfSite::WinHoverRevert,     L"win.hoverRevert",      6 },
        { DevToolsPerfSite::OverlaySetComments, L"overlay.setComments",  7 },
        { DevToolsPerfSite::OverlayBindDiag,    L"overlay.bindDiag",     8 },
        { DevToolsPerfSite::WinPropWrite,       L"win.propWrite",        9 },
        { DevToolsPerfSite::WinPropCommit,      L"win.propCommit",      10 },
        { DevToolsPerfSite::WinFocus,           L"win.focus",           11 },
        { DevToolsPerfSite::WinComments,        L"win.comments",        12 },
        { DevToolsPerfSite::WinCommentLive,     L"win.commentLive",     13 },
    };

    for (const auto& e : kExpect) {
        for (int i = 0; i < e.calls; ++i) { WINAPP_DEVTOOLS_PERF(e.site); BurnUs(100.0); }
    }

    const std::wstring json = DevToolsPerf_ReportJson();
    for (const auto& e : kExpect) {
        // The observed value is in the message. The old assertion printed only what it expected, so a red
        // gate could not be read without re-running it -- and the guess it invited ("a site was inserted
        // ahead of this one") was wrong.
        const double calls  = FieldOf(json, e.name, L"calls");
        const double selfUs = FieldOf(json, e.name, L"selfUs");
        char what[200];
        std::snprintf(what, sizeof(what), "%ls is reported under its own name (expected %d call(s), got %.0f)",
                      e.name, e.calls, calls);
        CheckPerf(calls == (double)e.calls, what);
        // Nonzero calls must also contain recorded work, not only correctly named zero-duration rows.
        std::snprintf(what, sizeof(what), "%ls recorded actual time, not just a count (selfUs %.0f)",
                      e.name, selfUs);
        CheckPerf(selfUs > 0.0, what);
    }
    DevToolsPerf_SetEnabled(false);
}

int RunPerfTests()
{
    std::printf("DevToolsPerf tests\n");
    TestOffThreadIsCountedNotSilentlyDropped();
    TestSelfTimeExcludesNestedScopes();
    TestDisabledAccountingRecordsNothing();
    TestNewSitesAreReportedUnderTheirOwnNames();
    return g_perfFailures;
}
