// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include <windows.h>
#include <mmsystem.h>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <string>

#include "DevToolsPerf.h"

#pragma comment(lib, "winmm.lib")

// Site ids as they appear on the wire. Must stay index-aligned with DevToolsPerfSite.
static const wchar_t* const kDevToolsPerfSiteNames[] = {
    L"tap.treeCallback",
    L"tap.treeWatchTurn",
    L"tap.removedTurn",
    L"overlay.trackTick",
    L"overlay.adorner",
    L"overlay.pins",
    L"overlay.layoutAdorner",
    L"overlay.pick",
    L"overlay.highlight",
    L"overlay.setComments",
    L"overlay.bindDiag",
    L"win.treeRefresh",
    L"win.snapshot",
    L"win.preview",
    L"win.treeRows",
    L"win.classify",
    L"win.propPane",
    L"win.propRead",
    L"win.propWrite",
    L"win.propCommit",
    L"win.xamlParse",
    L"win.treeFilter",
    L"win.rowRealize",
    L"win.treeVisible",
    L"win.treeHover",
    L"win.hoverRevert",
    L"win.focus",
    L"win.comments",
    L"win.commentLive",
    L"tap.previewBatch",
};
static_assert(_countof(kDevToolsPerfSiteNames) == (size_t)DevToolsPerfSite::Count,
              "kDevToolsPerfSiteNames must stay index-aligned with DevToolsPerfSite");

static const double kBucketEdgeUs[] = { 100.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16700.0, 33400.0 };
static const int    kBucketCount    = (int)_countof(kBucketEdgeUs) + 1;   // + the overflow bucket

struct SiteStats {
    unsigned long long calls = 0;
    double totalUs = 0.0;    // inclusive
    double selfUs  = 0.0;    // inclusive minus nested scopes
    double maxUs   = 0.0;    // inclusive, worst single sample -- the stutter, not the mean
    unsigned long long bucket[kBucketCount] = {};
    // Report dropped off-UI-thread samples so zero calls is not mistaken for no work.
    std::atomic<unsigned long long> offThread{0};
};

static SiteStats        g_stats[(int)DevToolsPerfSite::Count];
static std::atomic<bool> g_enabled{false};
static std::atomic<DWORD> g_uiTid{0};

static double* g_childSink = nullptr;

static double QpcUs()
{
    static LARGE_INTEGER freq{};
    if (!freq.QuadPart) QueryPerformanceFrequency(&freq);
    LARGE_INTEGER c{}; QueryPerformanceCounter(&c);
    return freq.QuadPart ? (double)c.QuadPart * 1000000.0 / (double)freq.QuadPart : 0.0;
}

void DevToolsPerf_LatchUiThread() { g_uiTid.store(GetCurrentThreadId(), std::memory_order_release); }
bool DevToolsPerf_UiThreadLatched() { return g_uiTid.load(std::memory_order_acquire) != 0; }
bool DevToolsPerf_Enabled() { return g_enabled.load(std::memory_order_relaxed); }
bool DevToolsPerf_SetEnabled(bool on) { return g_enabled.exchange(on, std::memory_order_relaxed); }


static UINT_PTR           g_jankTimer = 0;
static double             g_jankLastUs = 0.0;
static double             g_jankStartUs = 0.0;
static double             g_jankStopUs = 0.0;      // 0 while running
static unsigned long long g_jankTicks = 0;
static double             g_jankMaxGapUs = 0.0;
static double             g_jankMinGapUs = 0.0;
static double             g_jankSumGapUs = 0.0;
static unsigned long long g_jankGapBucket[kBucketCount] = {};
static double             g_jankLostUs = 0.0;      // excess over one frame budget, in gaps that overran two
static unsigned long long g_jankFramesMissed = 0;
static unsigned long long g_jankGapsOverBudget = 0; // raw count of gaps > one frame -- mostly the floor
static bool               g_jankPeriodHeld = false;

static const double kFrameBudgetUs = 16700.0;

static const double kGapFloorUs = 2.0 * kFrameBudgetUs;

static int BucketOf(double us)
{
    for (int i = 0; i < (int)_countof(kBucketEdgeUs); ++i) if (us < kBucketEdgeUs[i]) return i;
    return kBucketCount - 1;
}

static void ResetJankWindow(double nowUs)
{
    g_jankLastUs = nowUs;
    g_jankStartUs = nowUs;
    g_jankStopUs = 0.0;
    g_jankTicks = 0;
    g_jankMaxGapUs = 0.0;
    g_jankMinGapUs = 0.0;
    g_jankSumGapUs = 0.0;
    g_jankLostUs = 0.0;
    g_jankFramesMissed = 0;
    g_jankGapsOverBudget = 0;
    for (int i = 0; i < kBucketCount; ++i) g_jankGapBucket[i] = 0;
}

static void CALLBACK JankTimerProc(HWND, UINT, UINT_PTR, DWORD)
{
    const double now = QpcUs();
    if (g_jankLastUs > 0.0) {
        const double gap = now - g_jankLastUs;
        g_jankTicks++;
        g_jankSumGapUs += gap;
        if (gap > g_jankMaxGapUs) g_jankMaxGapUs = gap;
        if (g_jankMinGapUs == 0.0 || gap < g_jankMinGapUs) g_jankMinGapUs = gap;
        g_jankGapBucket[BucketOf(gap)]++;
        if (gap > kFrameBudgetUs) g_jankGapsOverBudget++;
        if (gap > kGapFloorUs) {
            g_jankLostUs += gap - kFrameBudgetUs;
            g_jankFramesMissed += (unsigned long long)((gap - kFrameBudgetUs) / kFrameBudgetUs);
        }
    }
    g_jankLastUs = now;
}

bool DevToolsPerf_SamplerStart()
{
    if (g_jankTimer) return true;
    if (!g_jankPeriodHeld && timeBeginPeriod(1) == TIMERR_NOERROR) g_jankPeriodHeld = true;
    ResetJankWindow(QpcUs());
    g_jankTimer = SetTimer(nullptr, 0, USER_TIMER_MINIMUM, &JankTimerProc);
    if (!g_jankTimer) {
        if (g_jankPeriodHeld) { timeEndPeriod(1); g_jankPeriodHeld = false; }
        return false;
    }
    return true;
}

void DevToolsPerf_SamplerStop()
{
    if (g_jankTimer) { KillTimer(nullptr, g_jankTimer); g_jankTimer = 0; }
    if (g_jankPeriodHeld) { timeEndPeriod(1); g_jankPeriodHeld = false; }
    if (g_jankStopUs == 0.0) g_jankStopUs = QpcUs();
}

bool DevToolsPerf_SamplerRunning() { return g_jankTimer != 0; }


DevToolsPerfScope::DevToolsPerfScope(DevToolsPerfSite site)
    : m_site(site), m_live(false), m_t0(0.0), m_childUs(0.0), m_prevSink(nullptr)
{
    if (!g_enabled.load(std::memory_order_relaxed)) return;
    if (GetCurrentThreadId() != g_uiTid.load(std::memory_order_acquire)) {
        g_stats[(int)site].offThread.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    m_live = true;
    m_prevSink = g_childSink;
    g_childSink = &m_childUs;
    m_t0 = QpcUs();
}

DevToolsPerfScope::~DevToolsPerfScope()
{
    if (!m_live) return;
    const double total = QpcUs() - m_t0;
    g_childSink = m_prevSink;
    // Invariant 2: charge the enclosing scope our INCLUSIVE time so its self time excludes us.
    if (m_prevSink) *m_prevSink += total;

    SiteStats& s = g_stats[(int)m_site];
    s.calls++;
    s.totalUs += total;
    double self = total - m_childUs;
    if (self < 0.0) self = 0.0;   // clock jitter across a nested pair; never let self go negative
    s.selfUs += self;
    if (total > s.maxUs) s.maxUs = total;
    s.bucket[BucketOf(total)]++;
}


void DevToolsPerf_Reset()
{
    for (int i = 0; i < (int)DevToolsPerfSite::Count; ++i) {
        SiteStats& s = g_stats[i];
        s.calls = 0; s.totalUs = 0.0; s.selfUs = 0.0; s.maxUs = 0.0;
        for (int b = 0; b < kBucketCount; ++b) s.bucket[b] = 0;
        s.offThread.store(0, std::memory_order_relaxed);
    }
    ResetJankWindow(QpcUs());
}

static std::wstring Num(double v)
{
    wchar_t b[64];
    _snwprintf_s(b, _countof(b), _TRUNCATE, L"%.1f", v);
    return b;
}

static std::wstring BucketJson(const unsigned long long* b)
{
    std::wstring out = L"[";
    for (int i = 0; i < kBucketCount; ++i) {
        if (i) out += L",";
        out += std::to_wstring(b[i]);
    }
    out += L"]";
    return out;
}

std::wstring DevToolsPerf_ReportJson()
{
    std::wstring out = L"{\"enabled\":";
    out += DevToolsPerf_Enabled() ? L"true" : L"false";
    out += L",\"uiThread\":" + std::to_wstring((unsigned long)g_uiTid.load(std::memory_order_acquire));
    out += L",\"bucketEdgesUs\":[";
    for (int i = 0; i < (int)_countof(kBucketEdgeUs); ++i) { if (i) out += L","; out += Num(kBucketEdgeUs[i]); }
    out += L"]";

    out += L",\"sites\":[";
    bool first = true;
    for (int i = 0; i < (int)DevToolsPerfSite::Count; ++i) {
        const SiteStats& s = g_stats[i];
        if (!first) out += L",";
        first = false;
        out += L"{\"site\":\"";
        out += kDevToolsPerfSiteNames[i];
        out += L"\",\"calls\":" + std::to_wstring(s.calls);
        out += L",\"totalUs\":" + Num(s.totalUs);
        out += L",\"selfUs\":" + Num(s.selfUs);
        out += L",\"maxUs\":" + Num(s.maxUs);
        out += L",\"meanUs\":" + Num(s.calls ? s.totalUs / (double)s.calls : 0.0);
        out += L",\"offThread\":" + std::to_wstring(s.offThread.load(std::memory_order_relaxed));
        out += L",\"buckets\":" + BucketJson(s.bucket);
        out += L"}";
    }
    out += L"]";

    const double endUs = g_jankStopUs != 0.0 ? g_jankStopUs : QpcUs();
    const double windowUs = (g_jankStartUs > 0.0 && endUs > g_jankStartUs) ? endUs - g_jankStartUs : 0.0;
    out += L",\"jank\":{\"running\":";
    out += DevToolsPerf_SamplerRunning() ? L"true" : L"false";
    out += L",\"windowUs\":" + Num(windowUs);
    out += L",\"ticks\":" + std::to_wstring(g_jankTicks);
    out += L",\"minGapUs\":" + Num(g_jankMinGapUs);
    out += L",\"meanGapUs\":" + Num(g_jankTicks ? g_jankSumGapUs / (double)g_jankTicks : 0.0);
    out += L",\"maxGapUs\":" + Num(g_jankMaxGapUs);
    out += L",\"frameBudgetUs\":" + Num(kFrameBudgetUs);
    out += L",\"framesMissed\":" + std::to_wstring(g_jankFramesMissed);
    out += L",\"gapsOverBudget\":" + std::to_wstring(g_jankGapsOverBudget);
    out += L",\"gapFloorUs\":" + Num(kGapFloorUs);
    out += L",\"framesInWindow\":" + std::to_wstring((unsigned long long)(windowUs / kFrameBudgetUs));
    out += L",\"stalledUs\":" + Num(g_jankLostUs);
    out += L",\"gapBuckets\":" + BucketJson(g_jankGapBucket);
    out += L"}}";
    return out;
}
