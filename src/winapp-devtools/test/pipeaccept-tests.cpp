// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Drive the actual accept loop on unique real pipes without injection or a target app.
// The pipe name must survive bursts; PIPE_BUSY is valid backpressure, FILE_NOT_FOUND is not.
// Fault seams compile only into this test translation unit, never the shipping tap.
#define WINAPP_DEVTOOLS_PIPE_FAULT_INJECTION 1
#include "DevToolsPipeAcceptLoop.h"
#include "DevToolsThreadGuard.h"

#include <sddl.h>
#include <aclapi.h>
#include <cstdio>
#include <string>
#include <thread>
#include <vector>
#include <atomic>
#include <new>
#include <stdexcept>

namespace {

int g_pipeAcceptFailures = 0;

void Check(bool condition, const char* message)
{
    if (condition) std::printf("  ok    %s\n", message);
    else { ++g_pipeAcceptFailures; std::printf("  FAIL  %s\n", message); }
}

void CheckEq(unsigned long actual, unsigned long expected, const char* message)
{
    if (actual == expected) { std::printf("  ok    %s\n", message); }
    else { ++g_pipeAcceptFailures; std::printf("  FAIL  %s (expected %lu, got %lu)\n", message, expected, actual); }
}

std::atomic<unsigned long> g_served{ 0 };
std::atomic<unsigned long> g_handlerThreads{ 0 };
// Scoped to the saturation test alone. g_served is process-global and every earlier test's DETACHED handlers
// contribute to it, so a delayed one retiring mid-test inflates a before/after delta and can satisfy the
// assertion even though a saturating connection was never dispatched. A counter only this test's server can
// touch makes the count exact instead of a lower bound.
std::atomic<unsigned long> g_saturationServed{ 0 };

// Stands in for PipeConnection: reads until the client goes away, then closes. Short-lived on purpose --
// that is what makes the pool churn, which is the condition the defect needed.
//
// The read is OVERLAPPED because the loop creates its instances with FILE_FLAG_OVERLAPPED. Passing a null
// OVERLAPPED to ReadFile on such a handle is undefined: the read pends, ReadFile returns without it having
// completed, and the IRP later writes the status block into an already-returned frame and the payload into a
// local that may no longer exist. That is stack corruption inside the very binary that gates this loop, so it
// can manufacture false failures and mask real ones. The real handler uses the same shape.
DWORD WINAPI TestConnection(LPVOID param)
{
    HANDLE pipe = static_cast<HANDLE>(param);
    ++g_handlerThreads;
    char buffer[16];
    DWORD read = 0;
    OVERLAPPED ov{};
    ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (ov.hEvent) {
        if (ReadFile(pipe, buffer, sizeof(buffer), &read, &ov) || GetLastError() == ERROR_IO_PENDING) {
            GetOverlappedResult(pipe, &ov, &read, TRUE);
        }
        CloseHandle(ov.hEvent);
    }
    FlushFileBuffers(pipe);
    DisconnectNamedPipe(pipe);
    CloseHandle(pipe);
    ++g_served;
    --g_handlerThreads;
    return 0;
}

// The saturation test's handler. Identical work, plus a counter no other test's stragglers can reach. The
// increment lands after TestConnection has released g_handlerThreads, which is fine: the thunk counter covers
// the whole thread, and the test waits on that before reading.
DWORD WINAPI SaturationConnection(LPVOID param)
{
    const DWORD r = TestConnection(param);
    ++g_saturationServed;
    return r;
}

std::atomic<unsigned long> g_thrownHandlers{ 0 };

// Models a handler that fails with an exception -- the shape a std::bad_alloc inside the tap's framing code
// takes under memory pressure. It does everything a normal handler does first, so the throw is the only
// difference under test and no handle is leaked by the test itself.
DWORD WINAPI ThrowingConnection(LPVOID param)
{
    TestConnection(param);
    g_thrownHandlers.fetch_add(1, std::memory_order_acq_rel);
    throw std::runtime_error("handler fault");
}

struct Server {
    std::wstring name;
    HANDLE stop = nullptr;
    std::thread loop;
    DevToolsPipeAcceptStats stats;

    // `failEventsAtStart` arms the CreateEventW fault before the loop runs, which is the only way to observe a
    // slot that has never had an event. Doing it after construction is too late: the first turn posts them all.
    explicit Server(const wchar_t* suffix, SECURITY_ATTRIBUTES* sa = nullptr, long failEventsAtStart = 0,
                    LPTHREAD_START_ROUTINE handlerFn = TestConnection)
    {
        name = L"\\\\.\\pipe\\winapp-devtools-test-" + std::to_wstring(GetCurrentProcessId()) + L"-" + suffix;
        stats.failNextEvents.store(failEventsAtStart);
        stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        HANDLE started = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        std::wstring n = name;
        HANDLE s = stop;
        DevToolsPipeAcceptStats* st = &stats;
        loop = std::thread([n, s, started, st, sa, handlerFn] {
            SetEvent(started);
            DevToolsRunPipeAcceptLoop(n.c_str(), sa, handlerFn, s, nullptr, st);
        });
        WaitForSingleObject(started, 5000);
        CloseHandle(started);
        WaitForName();
    }

    // The loop posts asynchronously, so wait for the name to appear before measuring anything about it.
    // Without this the first client could legitimately race server startup and the test would be flaky in
    // exactly the direction it is trying to detect.
    void WaitForName() const
    {
        for (int i = 0; i < 500; ++i) {
            if (WaitNamedPipeW(name.c_str(), 10)) return;
            if (GetLastError() == ERROR_SEM_TIMEOUT) return;   // exists, just busy
            Sleep(10);
        }
    }

    // Signals the loop and joins it. Idempotent, so a test can complete teardown while the object is still
    // alive and read the counters the teardown produced.
    void Stop()
    {
        SetEvent(stop);
        if (loop.joinable()) loop.join();
    }

    ~Server()
    {
        Stop();
        CloseHandle(stop);
    }
};

struct BurstResult {
    unsigned long ok = 0;
    unsigned long busy = 0;
    unsigned long notFound = 0;
    unsigned long other = 0;
};

// One raw connect, NO retry -- this measures the server's instantaneous capacity, which is the only way the
// zero-instance hole is observable. A client with retry would paper over exactly what is under test.
void Burst(const std::wstring& name, int threads, int perThread, BurstResult* result)
{
    std::atomic<unsigned long> ok{ 0 }, busy{ 0 }, notFound{ 0 }, other{ 0 };
    std::vector<std::thread> workers;
    for (int t = 0; t < threads; ++t) {
        workers.emplace_back([&name, perThread, &ok, &busy, &notFound, &other] {
            for (int i = 0; i < perThread; ++i) {
                if (!WaitNamedPipeW(name.c_str(), 3000)) {
                    const DWORD e = GetLastError();
                    if (e == ERROR_FILE_NOT_FOUND) ++notFound;
                    else if (e == ERROR_SEM_TIMEOUT) ++busy;
                    else ++other;
                    continue;
                }
                HANDLE h = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                       OPEN_EXISTING, 0, nullptr);
                if (h == INVALID_HANDLE_VALUE) {
                    const DWORD e = GetLastError();
                    if (e == ERROR_PIPE_BUSY) ++busy;
                    else if (e == ERROR_FILE_NOT_FOUND) ++notFound;
                    else ++other;
                    continue;
                }
                DWORD written = 0;
                WriteFile(h, "x", 1, &written, nullptr);
                CloseHandle(h);
                ++ok;
            }
        });
    }
    for (auto& w : workers) w.join();
    result->ok = ok;
    result->busy = busy;
    result->notFound = notFound;
    result->other = other;
}

void TestServesASingleClient()
{
    Server server(L"single");
    BurstResult r;
    Burst(server.name, 1, 20, &r);

    CheckEq(r.notFound, 0, "single client never sees ERROR_FILE_NOT_FOUND");
    CheckEq(r.ok, 20, "single client connects every time");
}

void TestConcurrentBurstNeverLosesThePipeName()
{
    Server server(L"burst");

    // Bounded concurrent burst stresses replenishment without a long-running soak.
    for (int threads : { 4, 8, 16 }) {
        BurstResult r;
        Burst(server.name, threads, 60, &r);

        char label[240];
        std::snprintf(label, sizeof(label),
                      "%d concurrent clients never see ERROR_FILE_NOT_FOUND "
                      "(ok=%lu busy=%lu other=%lu | min=%ld zero=%ld inFlightMax=%ld postFail=%ld connFail=%ld err=%ld)",
                      threads, r.ok, r.busy, r.other,
                      server.stats.minListening.load(), server.stats.zeroWindows.load(),
                      server.stats.maxInFlight.load(), server.stats.postFailures.load(),
                      server.stats.connectFailures.load(), server.stats.lastConnectError.load());
        CheckEq(r.notFound, 0, label);

        // Non-vacuous: a server that accepted nothing would also report zero not-found.
        std::snprintf(label, sizeof(label), "%d concurrent clients: at least some connects succeeded", threads);
        Check(r.ok > 0, label);

        std::snprintf(label, sizeof(label), "%d concurrent clients: no unexpected Win32 error", threads);
        CheckEq(r.other, 0, label);

        // The invariant itself, stated directly rather than inferred from a client's luck: the pool never
        // holds zero instances while the server is up. A client's ERROR_FILE_NOT_FOUND is the symptom; this
        // is the cause, and it catches a drain even in a run where no client happened to be looking.
        std::snprintf(label, sizeof(label),
                      "%d concurrent clients: the listener pool never drains to zero (connFail=%ld err=%ld)",
                      threads, server.stats.connectFailures.load(), server.stats.lastConnectError.load());
        CheckEq(server.stats.zeroWindows.load(), 0, label);

        // Non-vacuous in the other direction: the client hang-up path this loop has to recover from must
        // actually be exercised. A burst reaches it only when a client happens to close inside the window
        // before the server re-posts, which is a race that resolves differently per machine, so this is
        // reported rather than asserted. TestHangUpRecycleIsRetried injects the same path deterministically.
        std::snprintf(label, sizeof(label),
                      "%d concurrent clients: ERROR_NO_DATA hang-up recycles observed = %ld",
                      threads, server.stats.connectFailures.load());
        Check(true, label);
    }
}

// The hang-up recycle path on demand: a burst reaches it by luck, this reaches it every time. The injected
// fault takes the exact branch the real ERROR_NO_DATA takes -- count, DisconnectNamedPipe, retry the same
// handle -- so what is asserted is that a refused connect recycles the instance instead of losing it.
void TestHangUpRecycleIsRetried()
{
    Server server(L"hangup-recycle");

    // One below the per-slot recycle budget, so the whole dose lands on the FIRST slot postListener touches
    // and that slot still ends up posted. A dose at or above the budget would exhaust the slot instead, which
    // is slot destruction rather than recycling -- a different path, and one that leaves a two-slot pool with
    // a single listener whose next harvest necessarily passes through zero. Tying the dose to the budget keeps
    // this gate meaningful at every pool depth instead of only at the shipping depth of 32.
    const long injected = DevToolsHangUpRecycleBudget - 1;
    server.stats.failNextConnects.store(injected);

    BurstResult r;
    Burst(server.name, 4, 30, &r);

    Check(server.stats.failNextConnects.load() == 0,
          "every injected hang-up was consumed, so the recycle path ran exactly as many times as asked");
    Check(server.stats.reuseDisconnects.load() >= injected,
          "the recycle disconnected each hung-up instance rather than merely retrying it");
    Check(server.stats.connectFailures.load() >= injected,
          "each refused connect was counted as a hang-up recycle (natural hang-ups add to the injected ones)");
    CheckEq(server.stats.lastConnectError.load(), static_cast<long>(ERROR_NO_DATA),
            "the recycle path recorded the hang-up error it recovers from");
    CheckEq(r.notFound, 0, "recycling a hung-up instance never costs the pipe name");
    Check(r.ok > 0, "clients still connected across the recycles (non-vacuous)");
    CheckEq(server.stats.postFailures.load(), 0,
            "the dose recycled instances rather than destroying the slot (no post shortfall)");

    char label[220];
    std::snprintf(label, sizeof(label),
                  "recycling never drains the pool to zero listeners "
                  "(pool=%lu injected=%ld recycles=%ld min=%ld zero=%ld)",
                  static_cast<unsigned long>(DevToolsListenerPoolSize), injected,
                  server.stats.connectFailures.load(), server.stats.minListening.load(),
                  server.stats.zeroWindows.load());
    Check(server.stats.zeroWindows.load() == 0, label);
}

void TestNameSurvivesRepeatedDrainAndRefill()
{
    Server server(L"drain");

    // Hammer in waves with no pause, then assert the name is still there afterwards. This is the shape of the
    // real failure: the inspector works, then a burst empties the pool, and every later request reports the
    // target as gone even though the app is fine.
    for (int wave = 0; wave < 4; ++wave) {
        BurstResult r;
        Burst(server.name, 8, 40, &r);
        char label[240];
        std::snprintf(label, sizeof(label),
                      "wave %d leaves the pipe name intact "
                      "(ok=%lu busy=%lu | min=%ld zero=%ld inFlightMax=%ld postFail=%ld connFail=%ld err=%ld)",
                      wave, r.ok, r.busy,
                      server.stats.minListening.load(), server.stats.zeroWindows.load(),
                      server.stats.maxInFlight.load(), server.stats.postFailures.load(),
                      server.stats.connectFailures.load(), server.stats.lastConnectError.load());
        CheckEq(r.notFound, 0, label);
    }

    Check(server.stats.zeroWindows.load() == 0, "four drain waves never empty the listener pool");
    const BOOL present = WaitNamedPipeW(server.name.c_str(), 1000);
    Check(present || GetLastError() == ERROR_SEM_TIMEOUT, "pipe name still present after four drain waves");
}

void TestStoppingClosesTheListeners()
{
    std::wstring name;
    {
        Server server(L"stop");
        name = server.name;
        BurstResult r;
        Burst(name, 4, 10, &r);
        CheckEq(r.notFound, 0, "connects before shutdown never see ERROR_FILE_NOT_FOUND");
    }   // ~Server signals stop and joins

    // Every pooled instance must be closed on the way out, so the name disappears. A leaked listener would
    // keep the name alive and, in the tap, keep a kernel object per instance for the life of the process.
    bool gone = false;
    for (int i = 0; i < 200; ++i) {
        if (!WaitNamedPipeW(name.c_str(), 10) && GetLastError() == ERROR_FILE_NOT_FOUND) { gone = true; break; }
        Sleep(10);
    }
    Check(gone, "stopping the loop closes every pooled listener (pipe name disappears)");
}

// A pool that cannot post every slot -- the tap's real exposure is instance-cap pressure or low resources --
// must degrade to fewer listeners, never to none, and must climb back afterwards. Injection is the only way
// to reach this deterministically; waiting for the real cap would make the gate a soak test.
void TestPartialPostFailuresStillKeepTheNamePresent()
{
    Server server(L"partialpost");

    // Phase A -- a PARTIAL shortfall: fewer injected failures than the pool is deep, so some slot always
    // posts. This is the real-world shape (transient resource pressure), and the name must not flicker.
    server.stats.failNextPosts.store(DevToolsListenerPoolSize / 2);

    BurstResult r;
    Burst(server.name, 8, 40, &r);

    char label[240];
    std::snprintf(label, sizeof(label),
                  "a partial post shortfall never takes the name away (ok=%lu busy=%lu postFail=%ld zero=%ld)",
                  r.ok, r.busy, server.stats.postFailures.load(), server.stats.zeroWindows.load());
    CheckEq(r.notFound, 0, label);
    Check(server.stats.postFailures.load() > 0, "the injected post failures actually fired (non-vacuous)");

    // Reported, not asserted: whether the shortfall is repaired by the pre-dispatch top-up or by the next
    // turn's top-of-turn pass depends on whether a harvest happens in between, which is a race that resolves
    // differently per depth and per machine. TestAFailedRepostIsMadeGoodBeforeDispatch drives the top-up
    // deterministically instead.
    std::snprintf(label, sizeof(label), "post shortfalls repaired by the pre-dispatch top-up = %ld (of %ld)",
                  server.stats.topUps.load(), server.stats.postFailures.load());
    Check(true, label);
    // Only meaningful where the shortfall leaves slack. A 2-deep pool with one slot failing has a single
    // instance left by construction, so eight consumers legitimately take it and the posted count touches
    // zero; that is arithmetic, not the defect. The shipping depth of 32 always has slack, so CI always
    // asserts it -- this only relaxes under an explicit reduced-depth sweep.
    if (DevToolsListenerPoolSize - DevToolsListenerPoolSize / 2 >= 2) {
        CheckEq(server.stats.zeroWindows.load(), 0, "the pool never drains to zero while some posts are failing");
    } else {
        std::printf("  n/a   zero-drain check needs a pool with slack; depth %lu leaves none by construction\n",
                    static_cast<unsigned long>(DevToolsListenerPoolSize));
    }
    Check(r.ok > 0, "clients are still served while the pool is short of listeners");

    // Phase B -- TOTAL starvation: every post fails while eight client threads consume the slots that are
    // still posted, which is what actually drives the posted count to zero (a gentle nudge loop never does:
    // slots get re-posted one at a time and at least one always survives). The name genuinely cannot exist
    // when the kernel will not hand out a single instance, so that is not asserted here. What must hold is
    // that the loop neither wedges nor spins -- it keeps retrying, then climbs back to full depth and serves.
    //
    // This is where the second defect lived. The backoff was keyed off `waitCount`, which INCLUDES the stop
    // event and so is never zero, so a fully starved pool skipped the backoff and blocked forever on
    // WaitForMultipleObjects. The tap would have been unreachable for the rest of the process's life.
    server.stats.failNextPosts.store(DevToolsListenerPoolSize * 8);
    BurstResult starved;
    Burst(server.name, 8, 40, &starved);

    for (int i = 0; i < 400 && server.stats.failNextPosts.load() > 0; ++i) {
        HANDLE h = CreateFileW(server.name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                               OPEN_EXISTING, 0, nullptr);
        if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
        Sleep(5);
    }
    std::snprintf(label, sizeof(label),
                  "the loop keeps retrying through total starvation instead of wedging (budget left=%ld)",
                  server.stats.failNextPosts.load());
    CheckEq(static_cast<unsigned long>(server.stats.failNextPosts.load()), 0, label);

    for (int i = 0; i < 400 && server.stats.listening.load() < static_cast<long>(DevToolsListenerPoolSize); ++i) {
        HANDLE h = CreateFileW(server.name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                               OPEN_EXISTING, 0, nullptr);
        if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
        Sleep(5);
    }
    CheckEq(static_cast<unsigned long>(server.stats.listening.load()), DevToolsListenerPoolSize,
            "the pool refills to full depth once posting succeeds again");

    BurstResult recovered;
    Burst(server.name, 4, 20, &recovered);
    CheckEq(recovered.notFound, 0, "clients are served normally again after starvation lifts");
}

// A slot's manual-reset event is created on first post and then reused for the life of the loop. That made
// CreateEventW failure the one failure in this file that could be PERMANENT rather than transient: events were
// created up front in a startup loop, and postListener simply returned when a slot had none, so a slot that
// lost its event was skipped for the rest of the process. With every slot affected, the pool stays empty
// forever and the pipe name never appears at all -- the tap is unreachable for the life of the target, and
// nothing in the loop ever retries.
//
// Deterministic by construction, and asserted on monotonic counters rather than on a live observation of the
// outage: recovery takes one backoff turn (~20ms), so probing for the missing name races it.
static void TestASlotWhoseEventFailsIsRetriedNotLostForever()
{
    // One dose per slot: every slot fails its first event, none has a second failure to recover from.
    Server server(L"evtfail", nullptr, static_cast<long>(DevToolsListenerPoolSize));

    // The recovery under test: later turns must try CreateEventW again rather than treating the slot as dead.
    for (int i = 0; i < 500 && server.stats.listening.load() < static_cast<long>(DevToolsListenerPoolSize); ++i) {
        Sleep(10);
    }
    char label[256];
    std::snprintf(label, sizeof(label),
                  "every slot recovers its event and the pool returns to full depth (listening=%ld)",
                  server.stats.listening.load());
    CheckEq(static_cast<unsigned long>(server.stats.listening.load()), DevToolsListenerPoolSize, label);

    // Non-vacuity. No post faults are injected here, so every one of these came from the event path -- one per
    // slot proves the outage covered the WHOLE pool and the recovery above was not just an untouched slot.
    std::snprintf(label, sizeof(label),
                  "every slot really did lose its event first (postFailures=%ld, budget left=%ld)",
                  server.stats.postFailures.load(), server.stats.failNextEvents.load());
    Check(server.stats.postFailures.load() >= static_cast<long>(DevToolsListenerPoolSize)
              && server.stats.failNextEvents.load() == 0, label);

    // Recovered in name only is not recovered: the slots have to serve.
    BurstResult served;
    Burst(server.name, 4, 20, &served);
    CheckEq(served.notFound, 0, "clients are served normally once the events are created");
    Check(served.ok > 0, "the recovered pool actually served clients");
}

// A repost that fails has to be made good BEFORE the turn's dispatch phase, not at the top of the next turn:
// dispatch is one CreateThread per accepted connection, so deferring the retry holds the pool short across the
// most expensive stretch of the turn. Driven down to a single surviving slot, that window was long enough for
// a client to see ERROR_FILE_NOT_FOUND.
//
// Deterministic by construction. The injection is armed while the pool is at FULL depth and the loop is parked
// in its wait, so the very next postListener call in the process is the one harvest makes inline -- the only
// call the pre-dispatch pass exists to cover. Arming it before a burst instead lets a top-of-turn pass absorb
// the failure and repair it, in which case the top-up legitimately never runs; that is a race, and asserting
// on it was flaky at some pool depths.
void TestAFailedRepostIsMadeGoodBeforeDispatch()
{
    Server server(L"topup");

    for (int i = 0; i < 500 && server.stats.listening.load() < static_cast<long>(DevToolsListenerPoolSize); ++i) {
        Sleep(10);
    }
    CheckEq(static_cast<unsigned long>(server.stats.listening.load()), DevToolsListenerPoolSize,
            "the pool reached full depth before the injection was armed");
    const long topUpsBefore = server.stats.topUps.load();

    server.stats.failNextPosts.store(1);

    // Exactly one client, so exactly one slot is harvested and exactly one inline repost is attempted.
    HANDLE client = CreateFileW(server.name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                OPEN_EXISTING, 0, nullptr);
    Check(client != INVALID_HANDLE_VALUE, "the single client connected, so exactly one harvest runs");
    if (client != INVALID_HANDLE_VALUE) CloseHandle(client);

    for (int i = 0; i < 500 && server.stats.topUps.load() == topUpsBefore; ++i) Sleep(10);

    char label[260];
    std::snprintf(label, sizeof(label),
                  "the harvest's failed repost is made good before dispatch "
                  "(topUps %ld -> %ld, postFail=%ld, budget left=%ld)",
                  topUpsBefore, server.stats.topUps.load(), server.stats.postFailures.load(),
                  server.stats.failNextPosts.load());
    Check(server.stats.topUps.load() > topUpsBefore, label);
    CheckEq(server.stats.failNextPosts.load(), 0L,
            "the injected post failure was actually consumed (non-vacuous)");
    CheckEq(server.stats.postFailures.load(), 1L,
            "exactly one post failed, so the top-up repaired exactly that slot");

    for (int i = 0; i < 500 && server.stats.listening.load() < static_cast<long>(DevToolsListenerPoolSize); ++i) {
        Sleep(10);
    }
    CheckEq(static_cast<unsigned long>(server.stats.listening.load()), DevToolsListenerPoolSize,
            "the pool is back at full depth after the top-up");
}

// CreateThread failing is the one dispatch outcome with no safe default: leaking the instance would pin a
// kernel object and a pool slot's worth of capacity for the life of the process, and silently dropping it
// would leave the client blocked on a pipe nobody will ever read.
void TestDispatchFailureShedsTheConnectionWithoutLosingTheName()
{
    Server server(L"dispatchfail");
    const unsigned long servedBefore = g_served.load();

    server.stats.failNextDispatches.store(40);

    BurstResult r;
    Burst(server.name, 8, 30, &r);

    char label[240];
    std::snprintf(label, sizeof(label),
                  "shedding connections never takes the name away (ok=%lu busy=%lu shed=%ld zero=%ld)",
                  r.ok, r.busy, server.stats.dispatchFailures.load(), server.stats.zeroWindows.load());
    CheckEq(r.notFound, 0, label);

    // Burst returns when the CLIENTS are done, which is not when the server has finished harvesting them --
    // and a connect the server has not reached yet has not consumed an injected fault. Wait for the shed
    // path to actually fire rather than sampling a counter mid-flight.
    for (int i = 0; i < 300 && server.stats.dispatchFailures.load() == 0; ++i) {
        HANDLE h = CreateFileW(server.name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                               OPEN_EXISTING, 0, nullptr);
        if (h != INVALID_HANDLE_VALUE) {
            DWORD written = 0;
            WriteFile(h, "x", 1, &written, nullptr);
            Sleep(5);                 // stay connected long enough for the server to harvest and dispatch
            CloseHandle(h);
        }
        Sleep(5);
    }
    std::snprintf(label, sizeof(label),
                  "the injected dispatch failures actually fired (shed=%ld, ok=%lu, budget left=%ld)",
                  server.stats.dispatchFailures.load(), r.ok, server.stats.failNextDispatches.load());
    Check(server.stats.dispatchFailures.load() > 0, label);
    CheckEq(server.stats.zeroWindows.load(), 0, "the pool never drains to zero while connections are shed");

    // Shedding must not wedge the loop: once injection is exhausted, later clients are served normally.
    BurstResult after;
    Burst(server.name, 4, 20, &after);
    CheckEq(after.notFound, 0, "the loop keeps serving after shedding");

    // Handlers are detached, so give them a bounded moment to retire before asking whether any ran. Without
    // this the assertion races the very threads it is asking about.
    for (int i = 0; i < 300 && g_served.load() <= servedBefore; ++i) Sleep(10);
    std::snprintf(label, sizeof(label),
                  "connections are still handled after the shed window (served %lu -> %lu)",
                  servedBefore, g_served.load());
    Check(g_served.load() > servedBefore, label);

    // In-flight accounting must not drift, or shutdown would wait out its whole budget every time. Both the
    // shed path and the normal handler-completion path publish the decrement, so once every handler thread
    // has retired the counter must be exactly zero -- a shed connection that forgot to release its slot would
    // leave it positive, and a double release would leave it negative.
    // Wait on the counter itself, not on the handler-thread count: the thunk publishes its decrement after
    // the handler returns, so a zero handler count still races the release it is standing in for.
    for (int i = 0; i < 300 && server.stats.InFlight() != 0; ++i) Sleep(10);
    std::snprintf(label, sizeof(label),
                  "in-flight accounting returns to zero after connections are shed (inFlight=%ld, shed=%ld)",
                  server.stats.InFlight(), server.stats.dispatchFailures.load());
    Check(server.stats.InFlight() == 0, label);
    Check(server.stats.maxInFlight.load() > 0, "connections were genuinely counted in flight (non-vacuous)");
}

// Shutdown while every slot has an unconsumed pending connect -- the normal state of an idle tap -- must
// release all of them. Counting them is stronger than watching the name disappear: a single leaked listener
// keeps the name alive, but so would a slot the loop simply forgot, and only the counter tells them apart.
void TestShutdownWithPendingSlotsReleasesEveryListener()
{
    long peakListening = 0;
    long listeningAfterTeardown = -1;
    {
        Server server(L"pendingstop");
        for (int i = 0; i < 200 && server.stats.listening.load() < static_cast<long>(DevToolsListenerPoolSize); ++i) {
            Sleep(10);
        }
        peakListening = server.stats.listening.load();
        CheckEq(static_cast<unsigned long>(peakListening), DevToolsListenerPoolSize,
                "an idle loop holds the full pool pending");
        // ~Server signals stop and joins, so teardown is complete before the destructor returns and the
        // count can be read while the object it belongs to is still alive.
        server.Stop();
        listeningAfterTeardown = server.stats.listening.load();
    }

    CheckEq(static_cast<unsigned long>(listeningAfterTeardown), 0,
            "shutdown releases every pending listener it was holding");
    Check(peakListening > 0, "the teardown assertion is not vacuous (listeners existed to release)");
}

// The tap's pipe is owner-only by construction. Recycling an instance after a client hang-up must not be a
// way to end up with a weaker one, so assert what a client actually faces AFTER the recycle path has run.
void TestRecycledInstancesKeepTheirSecurityDescriptor()
{
    // Phase A -- a deny-all descriptor. Present, non-null and zero-length, so an accidental fallback to a
    // default or NULL (grant-everyone) descriptor changes the observable result rather than merely looking
    // different in a dump.
    {
        SECURITY_DESCRIPTOR sd{};
        Check(InitializeSecurityDescriptor(&sd, SECURITY_DESCRIPTOR_REVISION) != 0, "deny-all SD initialized");
        ACL acl{};
        Check(InitializeAcl(&acl, sizeof(acl), ACL_REVISION) != 0, "deny-all DACL initialized");
        Check(SetSecurityDescriptorDacl(&sd, TRUE, &acl, FALSE) != 0, "deny-all DACL attached");
        SECURITY_ATTRIBUTES sa{ sizeof(sa), &sd, FALSE };

        Server server(L"sd-deny", &sa);
        BurstResult r;
        Burst(server.name, 8, 20, &r);

        char label[200];
        std::snprintf(label, sizeof(label),
                      "the descriptor is applied to every instance (ok=%lu busy=%lu other=%lu)",
                      r.ok, r.busy, r.other);
        CheckEq(r.ok, 0, label);
        CheckEq(r.notFound, 0, "a locked-down pipe still keeps its name present");
        Check(r.other > 0, "clients were refused by the descriptor, not by the name being gone");
    }

    // Phase B -- a descriptor that PERMITS the client, so connections (and therefore hang-ups, and therefore
    // the ERROR_NO_DATA recycle path) actually happen. Then read the DACL back off a live instance. The ACE
    // names this process's own token SID: CREATOR OWNER is not substituted for a directly created object, so
    // using it would produce an ACE that matches nobody and a test that proves only that access was denied.
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        Check(false, "opened the process token for the descriptor test");
        return;
    }
    DWORD needed = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &needed);
    std::vector<unsigned char> buffer(needed);
    const BOOL gotUser = GetTokenInformation(token, TokenUser, buffer.data(), needed, &needed);
    CloseHandle(token);
    Check(gotUser != 0, "read this process's own SID for the descriptor test");
    if (!gotUser) return;

    LPWSTR sidText = nullptr;
    const BOOL gotSid = ConvertSidToStringSidW(
        reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &sidText);
    Check(gotSid != 0, "formatted this process's SID");
    if (!gotSid) return;

    std::wstring sddl = L"D:(A;;GRGWRC;;;";
    sddl += sidText;
    sddl += L")";
    LocalFree(sidText);

    PSECURITY_DESCRIPTOR sd = nullptr;
    const BOOL built = ConvertStringSecurityDescriptorToSecurityDescriptorW(
        sddl.c_str(), SDDL_REVISION_1, &sd, nullptr);
    Check(built != 0, "owner-only test descriptor built from SDDL");
    if (!built) return;

    SECURITY_ATTRIBUTES sa{ sizeof(sa), sd, FALSE };
    Server server(L"sd-recycle", &sa);

    // Force the recycle path rather than waiting for a client to hang up at the right instant: the point of
    // this test is that an instance which has been through DisconnectNamedPipe/ConnectNamedPipe still carries
    // the descriptor it was created with, and that is only proven if the recycle definitely happened.
    // Stay one BELOW the per-slot recycle budget. A dose that exhausts it makes postListener give the slot up
    // for the rest of the turn instead of recycling it, which is a different scenario and one a shallow pool
    // cannot absorb: at depth 4 it emptied the pool and this very assertion caught a real ERROR_FILE_NOT_FOUND.
    const long injected = DevToolsHangUpRecycleBudget - 1;
    server.stats.failNextConnects.store(injected);

    BurstResult r;
    Burst(server.name, 8, 40, &r);
    CheckEq(r.notFound, 0, "a permissive pipe keeps its name across the recycle path");
    CheckEq(server.stats.failNextConnects.load(), 0,
            "every injected recycle was consumed before the descriptor was read back");
    Check(server.stats.connectFailures.load() >= injected,
          "the recycle path was exercised before the descriptor was read back (non-vacuous)");

    // READ_CONTROL is in the granted mask above, so this reads the DACL the kernel is really enforcing on an
    // instance that the loop has been recycling, not the one the test thinks it asked for.
    bool inspected = false;
    for (int i = 0; i < 200 && !inspected; ++i) {
        HANDLE h = CreateFileW(server.name.c_str(), GENERIC_READ | GENERIC_WRITE | READ_CONTROL, 0, nullptr,
                               OPEN_EXISTING, 0, nullptr);
        if (h == INVALID_HANDLE_VALUE) { Sleep(5); continue; }

        PACL dacl = nullptr;
        PSECURITY_DESCRIPTOR live = nullptr;
        if (GetSecurityInfo(h, SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION,
                            nullptr, nullptr, &dacl, nullptr, &live) == ERROR_SUCCESS) {
            Check(dacl != nullptr, "the recycled instance has a DACL (not a null, grant-everyone one)");
            CheckEq(dacl ? dacl->AceCount : 0, 1, "the recycled instance carries exactly the one ACE it was created with");
            if (live) LocalFree(live);
            inspected = true;
        }
        CloseHandle(h);
    }
    Check(inspected, "the live descriptor was actually read back (non-vacuous)");
    LocalFree(sd);
}

// The in-process DevTools window and every external inspector attach and detach repeatedly against one
// target. Ten full server lifetimes must not ratchet the process's handle count -- that is the shape of the
// leak an inspector session would accumulate over an afternoon.
void TestRepeatedStartStopCyclesDoNotAccumulateHandles()
{
    auto processHandles = [] {
        DWORD count = 0;
        GetProcessHandleCount(GetCurrentProcess(), &count);
        return static_cast<long>(count);
    };

    // Warm up first: the first cycle pays one-off costs (thread pool, loader) that are not a leak.
    {
        Server warm(L"cycle-warm");
        BurstResult r;
        Burst(warm.name, 2, 5, &r);
    }
    for (int i = 0; i < 200 && g_handlerThreads.load() != 0; ++i) Sleep(10);

    const long before = processHandles();
    for (int cycle = 0; cycle < 10; ++cycle) {
        Server server((L"cycle-" + std::to_wstring(cycle)).c_str());
        BurstResult r;
        Burst(server.name, 4, 10, &r);
        CheckEq(r.notFound, 0, "attach/detach cycles never lose the name");
    }
    for (int i = 0; i < 300 && g_handlerThreads.load() != 0; ++i) Sleep(10);
    const long after = processHandles();

    // Tolerance, not an exact number: the CRT and thread pool legitimately move a few handles around. A real
    // leak here is 32 listeners + 32 events per cycle, so ten cycles would be ~640 -- orders away from this.
    char label[200];
    std::snprintf(label, sizeof(label),
                  "ten start/stop cycles do not accumulate handles (%ld -> %ld)", before, after);
    Check(after - before <= 32, label);
}

// Shutdown must not be starved by traffic. WaitForMultipleObjects with bWaitAll FALSE reports only the LOWEST
// signalled index, and the stop event is necessarily last -- the pool's slot indices have to stay stable. So
// a loop that learns about shutdown only when the wait hands it the stop index never exits while clients keep
// arriving: there is always a signalled listener ahead of it. Whoever joins that thread blocks forever.
//
// Saturation is the whole test, and it has to be produced without a thread storm -- every accepted connection
// normally costs a handler thread, and hammering hard enough to keep a slot permanently signalled would spawn
// them by the thousand and measure the machine instead. Injecting dispatch failures sheds each connection
// without a thread, so the clients can run flat out and the ONLY thing under test is the wait's index
// ordering. (Sleep(1) does not work here: the Windows timer granularity makes it ~15 ms, which produces a few
// hundred connects a second -- nowhere near enough to keep a slot signalled across every loop turn.)
//
// RED CONTROL: delete the WaitForSingleObject(stopEvent, 0) check at the top of the loop in
// DevToolsPipeAcceptLoop.h. This trips instead of hanging, which is the point of the watchdog.
void TestShutdownIsNotStarvedByContinuousTraffic()
{
    std::atomic<bool> running{ true };
    std::atomic<unsigned long> connected{ 0 };
    std::vector<std::thread> clients;

    Server* server = new Server(L"stopstarve");
    const std::wstring name = server->name;

    // Shed every dispatch: the connection is accepted and closed by the loop itself, so no handler thread is
    // created no matter how fast the clients go. Budget far exceeds what the window below can consume.
    server->stats.failNextDispatches.store(1000000);

    for (int t = 0; t < 4; ++t) {
        clients.emplace_back([&name, &running, &connected] {
            while (running.load(std::memory_order_acquire)) {
                HANDLE h = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                       OPEN_EXISTING, 0, nullptr);
                if (h != INVALID_HANDLE_VALUE) {
                    CloseHandle(h);
                    connected.fetch_add(1, std::memory_order_relaxed);
                }
            }
        });
    }

    // Wait for the loop to be demonstrably saturated before asking it to stop, so the shutdown request lands
    // in the starving condition rather than a quiet moment.
    for (int i = 0; i < 400 && server->stats.dispatchFailures.load() < 200; ++i) Sleep(5);
    const long sheddingAtStop = server->stats.dispatchFailures.load();

    HANDLE done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    std::thread([server, done] { delete server; SetEvent(done); }).detach();
    const DWORD shutdown = WaitForSingleObject(done, 20000);

    running.store(false, std::memory_order_release);
    for (auto& c : clients) c.join();

    char label[200];
    std::snprintf(label, sizeof(label),
                  "shutdown is not starved by saturating traffic (%lu connects, %ld shed when stop was asked)",
                  connected.load(), sheddingAtStop);
    Check(shutdown == WAIT_OBJECT_0, label);
    Check(sheddingAtStop >= 200, "the loop was genuinely saturated when stop was requested (non-vacuous)");
    if (shutdown == WAIT_OBJECT_0) CloseHandle(done);
}

// The ownership case the idle-pool test above cannot reach: shutdown racing live traffic, so the loop tears
// the pool down while clients are mid-connect and handlers are mid-request. Every accepted handle has exactly
// one terminal owner (see the ownership table in DevToolsPipeAcceptLoop.h) and this is where a second owner would
// show up -- as a leaked instance, or as a double close. A double close is not silently survivable: the CRT
// raises STATUS_INVALID_HANDLE under a debugger and the handle number is otherwise free to be reused by an
// unrelated open, so this also runs the closest thing to that check available without Application Verifier.
//
// It is also the gate for shutdown starvation. WaitForMultipleObjects reports the LOWEST signalled index and
// the stop event is last, so a loop that only notices shutdown when the wait hands it the stop index never
// exits while clients keep arriving. To watch that go red, delete the WaitForSingleObject(stopEvent, 0) check
// at the top of the loop in DevToolsPipeAcceptLoop.h: the teardown assertion below trips instead of hanging.
void TestShutdownDuringActiveTrafficLeaksNoHandles()
{
    auto processHandles = [] {
        DWORD count = 0;
        GetProcessHandleCount(GetCurrentProcess(), &count);
        return static_cast<long>(count);
    };

    {   // warm-up cycle, so one-off costs are not charged to the measurement
        Server warm(L"racestop-warm");
        BurstResult r;
        Burst(warm.name, 2, 5, &r);
    }
    for (int i = 0; i < 200 && g_handlerThreads.load() != 0; ++i) Sleep(10);

    const long before = processHandles();
    const unsigned long servedBefore = g_served.load();
    std::atomic<unsigned long> connected{ 0 };

    for (int cycle = 0; cycle < 6; ++cycle) {
        std::wstring name;
        std::atomic<bool> running{ true };
        std::vector<std::thread> clients;

        Server* server = new Server((L"racestop-" + std::to_wstring(cycle)).c_str());
        name = server->name;

        // Keep connecting until the server goes away underneath these threads. Failures are expected and
        // ignored -- the point is that connects are genuinely in flight at the instant shutdown begins.
        // Paced deliberately: an unthrottled spin here is a thread-creation storm (every accepted
        // connection costs a handler thread) that measures the machine rather than the ownership rule.
        for (int t = 0; t < 6; ++t) {
            clients.emplace_back([&name, &running, &connected] {
                while (running.load(std::memory_order_acquire)) {
                    HANDLE h = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                           OPEN_EXISTING, 0, nullptr);
                    if (h != INVALID_HANDLE_VALUE) {
                        DWORD written = 0;
                        WriteFile(h, "x", 1, &written, nullptr);
                        CloseHandle(h);
                        connected.fetch_add(1, std::memory_order_relaxed);
                    }
                    Sleep(1);
                }
            });
        }
        Sleep(120);   // let traffic reach steady state before the teardown pulls the rug

        // Tear down on a watchdog. ~Server joins the loop thread, so a loop that cannot observe its stop
        // event under load does not fail this test -- it HANGS it, and a gate that hangs reports nothing.
        // Timing the teardown converts that into an assertion. The traffic is still flowing while it runs,
        // which is the whole point: this is the condition that starves a last-placed stop event.
        HANDLE done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        std::thread([server, done] { delete server; SetEvent(done); }).detach();
        const DWORD shutdown = WaitForSingleObject(done, 30000);

        running.store(false, std::memory_order_release);
        for (auto& c : clients) c.join();

        char stopLabel[160];
        std::snprintf(stopLabel, sizeof(stopLabel),
                      "shutdown completes while traffic is still arriving (cycle %d)", cycle);
        Check(shutdown == WAIT_OBJECT_0, stopLabel);
        if (shutdown != WAIT_OBJECT_0) { CloseHandle(done); return; }
        CloseHandle(done);
    }

    for (int i = 0; i < 300 && g_handlerThreads.load() != 0; ++i) Sleep(10);
    const long after = processHandles();

    Check(connected.load() > 0, "clients were genuinely connecting across shutdown (non-vacuous)");
    Check(g_served.load() > servedBefore, "handlers actually ran during the race (non-vacuous)");
    CheckEq(g_handlerThreads.load(), 0, "every handler drained after shutdown raced live traffic");

    // A leaked accepted instance is one handle per racing connect, over six cycles of continuous traffic --
    // hundreds. The tolerance covers CRT/thread-pool churn only.
    char label[220];
    std::snprintf(label, sizeof(label),
                  "shutdown racing live traffic leaks no handles (%ld -> %ld, %lu connects)",
                  before, after, connected.load());
    Check(after - before <= 32, label);
}

void TestHandlerThreadsDoNotAccumulate()
{
    Server server(L"threads");
    BurstResult r;
    Burst(server.name, 8, 50, &r);

    // Connection threads are detached; they must drain rather than pile up. Give them a moment, then require
    // the in-flight count to have returned to zero.
    for (int i = 0; i < 300 && g_handlerThreads.load() != 0; ++i) Sleep(10);
    CheckEq(g_handlerThreads.load(), 0, "no connection handler threads left in flight after the burst");
    Check(g_served.load() > 0, "connection handler actually ran (non-vacuous)");
}

// Saturation: more concurrent clients than the pool has slots. The accept loop harvests every ready slot per
// wake, so one turn can take at most one connection per slot -- the array it stages them in is sized to
// exactly that. Taking the wait-reported slot separately from the sweep broke the bound, because harvest
// re-posts the slot and the replacement can connect immediately (a queued waiter released the instant the
// instance appears), re-signalling the event before the sweep reaches it; that slot then contributes twice and
// the turn overruns the array by one HANDLE of stack.
//
// Scales with WINAPP_DEVTOOLS_LISTENER_POOL_SIZE so the same gate is cheap to saturate at a reduced depth: the shipping
// depth of 32 needs 32 simultaneous connections before the bound can be exceeded at all.
//
// The clients HOLD their connections open until every one of them has arrived, and that is load-bearing rather
// than incidental. A client that connects and closes immediately -- what Burst does -- can be gone before the
// loop reaches its slot, and harvest then fails and contributes nothing to the turn. With a fast enough loop
// under saturation EVERY harvest can fail that way, which is a legitimate outcome that leaves both counters at
// zero even though the clients genuinely connected. Gating multi-accept on Burst therefore fails about 1 run in
// 30 at depth 32 with "peak=0... ok=95", which says nothing about the bound under test. Holding the
// connections open makes the harvest succeed, so the property is driven rather than hoped for.
void TestSaturationNeverHarvestsMoreThanThePoolHasSlots()
{
    Server server(L"saturate", nullptr, 0, SaturationConnection);

    const int threads = static_cast<int>(DevToolsListenerPoolSize) * 2;
    HANDLE release = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    Check(release != nullptr, "the saturation test created its release event");
    if (!release) return;

    std::atomic<long> connected{ 0 };
    std::vector<std::thread> clients;
    clients.reserve(static_cast<size_t>(threads));
    const std::wstring name = server.name;
    for (int i = 0; i < threads; ++i) {
        clients.emplace_back([&name, &connected, release] {
            // Bounded connect retry: at saturation most attempts legitimately meet ERROR_PIPE_BUSY, and this
            // client's job is to OCCUPY an instance rather than to measure instantaneous capacity.
            for (int attempt = 0; attempt < 400; ++attempt) {
                HANDLE h = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                       OPEN_EXISTING, 0, nullptr);
                if (h != INVALID_HANDLE_VALUE) {
                    connected.fetch_add(1, std::memory_order_relaxed);
                    WaitForSingleObject(release, 10000);
                    CloseHandle(h);
                    return;
                }
                if (GetLastError() == ERROR_PIPE_BUSY) WaitNamedPipeW(name.c_str(), 20); else Sleep(5);
            }
        });
    }

    // Wait for every client to be holding an instance at once. That is the saturated state the bound is measured
    // against, and unlike a turn-shaped or in-flight-shaped condition it is guaranteed to arrive: each client
    // retries until it is in, and none of them lets go until `release`.
    for (int i = 0; i < 1000 && connected.load() < threads; ++i) Sleep(10);

    const long peakTurn = server.stats.maxAcceptedPerTurn.load();
    const long inFlightMax = server.stats.maxInFlight.load();
    const long arrived = connected.load();

    SetEvent(release);
    for (auto& t : clients) t.join();
    CloseHandle(release);

    // Wait for every dispatch thread to have fully unwound, not merely for the handlers: the saturation counter
    // is incremented after TestConnection releases g_handlerThreads, so reading it any earlier can undercount.
    for (int i = 0; i < 500 && DevToolsPipeDispatchThunksInFlight().load() != 0; ++i) Sleep(10);
    const unsigned long servedDelta = g_saturationServed.load();

    char label[300];
    std::snprintf(label, sizeof(label),
                  "a single accept turn never harvests more than the pool's %lu slots "
                  "(peak=%ld, %d clients, connected=%ld, served=%lu, inFlightMax=%ld)",
                  static_cast<unsigned long>(DevToolsListenerPoolSize), peakTurn, threads, arrived, servedDelta,
                  inFlightMax);
    Check(peakTurn <= static_cast<long>(DevToolsListenerPoolSize) && peakTurn >= 1, label);

    // Assert simultaneous held client connections and exact handler counts. Same-turn accepts
    // and peak in-flight handlers depend on scheduling and are not saturation guarantees.
    std::snprintf(label, sizeof(label),
                  "every client reached the server and was still holding when the peak was read "
                  "(connected=%ld of %d, non-vacuous)", arrived, threads);
    CheckEq(arrived, static_cast<long>(threads), label);
    std::snprintf(label, sizeof(label),
                  "the loop harvested and dispatched exactly the %d saturating connections (served=%lu)",
                  threads, servedDelta);
    CheckEq(servedDelta, static_cast<unsigned long>(threads), label);

    // The name must survive saturation, measured the way the hole is actually observable: with no retry.
    BurstResult r;
    Burst(server.name, 4, 20, &r);
    CheckEq(r.notFound, 0, "saturating the pool never costs the pipe name");
}

// A handler that is still running when the bounded drain gives up. This is not hypothetical: the drain is
// deliberately bounded so a wedged handler cannot hold shutdown open forever, which means the loop is designed
// to RETURN while a detached handler thread is still live. Everything that thread touches on its way out must
// therefore outlive both the loop's stack frame and the caller's stats object, so the only thing it is allowed
// to touch is the shared in-flight counter it co-owns.
//
// Both owners are destroyed here before the handler is released -- the loop thread is joined and its stack
// unmapped, and the caller's heap-allocated stats is deleted -- so a borrowed address faults or corrupts
// rather than merely misbehaving.
void TestAStragglingHandlerOutlivesTheLoopsFrame()
{
    // The stats object lives on its own private page, not on the CRT heap. Releasing the page unmaps it, so a
    // handler that still holds a pointer into it faults deterministically instead of silently writing into a
    // recycled heap block. That is the whole point: this hazard is invisible on a normal heap.
    void* page = VirtualAlloc(nullptr, sizeof(DevToolsPipeAcceptStats), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    Check(page != nullptr, "the straggler test reserved its own page for the stats object");
    auto* stats = new (page) DevToolsPipeAcceptStats();
    auto counter = stats->inFlightCounter;   // an observer's co-ownership, independent of `stats`
    const std::wstring name = L"\\\\.\\pipe\\winapp-devtools-test-" + std::to_wstring(GetCurrentProcessId()) + L"-straggler";
    HANDLE stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);

    std::thread loop([&name, stop, stats] {
        DevToolsRunPipeAcceptLoop(name.c_str(), nullptr, TestConnection, stop, nullptr, stats);
    });
    for (int i = 0; i < 500; ++i) {
        if (WaitNamedPipeW(name.c_str(), 10) || GetLastError() == ERROR_SEM_TIMEOUT) break;
        Sleep(10);
    }

    // A client that connects and then says nothing. Its handler blocks in the read for as long as we hold the
    // handle, so it is guaranteed to still be running when the drain expires.
    HANDLE client = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    Check(client != INVALID_HANDLE_VALUE, "the straggler client connected");
    for (int i = 0; i < 500 && stats->maxInFlight.load() == 0; ++i) Sleep(10);
    Check(stats->maxInFlight.load() > 0, "the straggling handler is genuinely in flight (non-vacuous)");

    SetEvent(stop);
    loop.join();                            // the loop's frame is gone once this returns

    char label[200];
    std::snprintf(label, sizeof(label),
                  "the handler really did outlive the loop, so the drain bound was exercised (inFlight=%ld)",
                  counter->load());
    Check(counter->load() > 0, label);

    stats->~DevToolsPipeAcceptStats();
    const BOOL released = VirtualFree(page, 0, MEM_RELEASE);
    Check(released != FALSE, "the stats page was genuinely released (a stale write to it now faults)");

    CloseHandle(client);                    // now the handler finishes and releases its slot
    for (int i = 0; i < 500 && counter->load() != 0; ++i) Sleep(10);
    std::snprintf(label, sizeof(label),
                  "a straggling handler releases its slot safely after the loop and its stats are gone "
                  "(inFlight=%ld)",
                  counter->load());
    Check(counter->load() == 0, label);

    // The slot release is not the handler's last act, so returning here would let the process exit out from
    // under the retiring thread and hide anything it does afterwards. Give it a window to finish.
    Sleep(250);
    CloseHandle(stop);
}

// A handler that throws is a thread entry point that has run out of frames: with nothing catching it the C++
// runtime calls std::terminate, and the app the tap is injected into dies over one client's request. The tap's
// own handler does its framing allocations outside the per-request try/catch, so this is the shape a
// std::bad_alloc under memory pressure takes. The loop must survive it, keep serving afterwards, and still
// return its in-flight count to zero -- a decrement skipped by the unwind would stall every later shutdown
// behind the full drain bound with nothing actually running.
void TestAThrowingHandlerCannotKillTheHostOrStallShutdown()
{
    Server server(L"throwing", nullptr, 0, ThrowingConnection);
    const unsigned long before = g_thrownHandlers.load(std::memory_order_acquire);

    BurstResult r;
    Burst(server.name, 4, 5, &r);

    // Every connection that got in is dispatched to a handler that throws. Wait for them to unwind before
    // reading anything about them.
    for (int i = 0; i < 500 && (g_thrownHandlers.load(std::memory_order_acquire) - before) < r.ok; ++i) Sleep(10);
    const unsigned long threw = g_thrownHandlers.load(std::memory_order_acquire) - before;

    char label[192];
    std::snprintf(label, sizeof(label),
                  "handlers really did throw (%lu threw, %lu connects got in, non-vacuous)", threw, r.ok);
    // Not `threw == r.ok`: Burst closes each client immediately, so a connect can be recycled as an
    // ERROR_NO_DATA hang-up and never reach a handler at all. That difference is real behaviour, not a fault,
    // so it is reported rather than asserted. The property under test needs no exact count -- without the
    // backstop the first exception terminates the process and NOTHING below this line ever prints.
    Check(threw > 0, label);
    CheckEq(r.notFound, 0, "a throwing handler never costs the pipe name");

    BurstResult after;
    Burst(server.name, 1, 5, &after);
    std::snprintf(label, sizeof(label), "the loop still serves clients after %lu handler faults (ok=%lu of 5)",
                  threw, after.ok);
    Check(after.ok == 5, label);

    server.Stop();
    std::snprintf(label, sizeof(label), "in-flight returned to zero despite every handler throwing (inFlight=%ld)",
                  server.stats.InFlight());
    Check(server.stats.InFlight() == 0, label);
}

// F20: every background thread the tap starts is routed through DevToolsThreadEntry, because an exception that
// unwinds out of a thread proc is std::terminate -- and the tap runs inside the developer's app, so that
// terminate kills the app, not us. This exercises the wrapper directly on a real OS thread: without the
// catch, the process fastfails here (0xC0000409) and nothing after this point runs.
static DWORD WINAPI ThrowingThreadBody(LPVOID param)
{
    reinterpret_cast<std::atomic<int>*>(param)->fetch_add(1);
    throw std::runtime_error("bad_alloc stand-in from a thread proc");
}

void TestAThrowingThreadProcCannotKillTheProcess()
{
    std::atomic<int> entered{ 0 };
    HANDLE th = CreateThread(nullptr, 0, DevToolsThreadEntry<ThrowingThreadBody>, &entered, 0, nullptr);
    Check(th != nullptr, "the guarded thread started");
    if (!th) return;

    const DWORD waited = WaitForSingleObject(th, 10000);
    DWORD code = 0xFFFFFFFF;
    GetExitCodeThread(th, &code);
    CloseHandle(th);

    Check(waited == WAIT_OBJECT_0, "a thread proc that throws still retires (it did not hang)");
    // Non-vacuous: proves the body actually ran and actually threw, rather than the thread returning 0
    // because it never reached the throw.
    Check(entered.load() == 1, "the throwing body really ran");
    char label[160];
    std::snprintf(label, sizeof(label), "the guard swallowed the exception and returned cleanly (exit=%lu)",
                  static_cast<unsigned long>(code));
    Check(code == 0, label);
    // Reached at all only because the process survived the throw above.
    Check(true, "the process is still alive after a thread proc threw");
}

}   // namespace

int RunPipeAcceptTests()
{
    g_pipeAcceptFailures = 0;
    {
        Server local(L"local-only");
        HANDLE client = CreateFileW(local.name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        Check(client != INVALID_HANDLE_VALUE, "local client still connects to remote-rejecting listener");
        if (client != INVALID_HANDLE_VALUE) CloseHandle(client);
    }
    std::printf("DevToolsPipeAcceptLoop tests (listener pool depth %lu)\n",
                static_cast<unsigned long>(DevToolsListenerPoolSize));
    g_served = 0;

    TestServesASingleClient();
    TestConcurrentBurstNeverLosesThePipeName();
    TestHangUpRecycleIsRetried();
    TestNameSurvivesRepeatedDrainAndRefill();
    TestStoppingClosesTheListeners();
    TestPartialPostFailuresStillKeepTheNamePresent();
    TestASlotWhoseEventFailsIsRetriedNotLostForever();
    TestAFailedRepostIsMadeGoodBeforeDispatch();
    TestDispatchFailureShedsTheConnectionWithoutLosingTheName();
    TestShutdownWithPendingSlotsReleasesEveryListener();
    TestRecycledInstancesKeepTheirSecurityDescriptor();
    TestRepeatedStartStopCyclesDoNotAccumulateHandles();
    TestShutdownIsNotStarvedByContinuousTraffic();
    TestShutdownDuringActiveTrafficLeaksNoHandles();
    TestHandlerThreadsDoNotAccumulate();
    TestSaturationNeverHarvestsMoreThanThePoolHasSlots();
    TestAStragglingHandlerOutlivesTheLoopsFrame();
    TestAThrowingHandlerCannotKillTheHostOrStallShutdown();
    TestAThrowingThreadProcCannotKillTheProcess();

    // Connection handlers are DETACHED, so joining the accept-loop threads does not retire them. Drain them
    // here, at the end of the suite, because main removes the memory-fault watch and reads its count once
    // every suite has returned: a handler still running past that point could fault where nothing counts it,
    // and the process would exit 0 with a real access violation on the record. Asserted rather than merely
    // waited on, so a handler that never retires fails loudly instead of silently shrinking the watch window.
    for (int i = 0; i < 500 && g_handlerThreads.load() != 0; ++i) Sleep(10);
    CheckEq(g_handlerThreads.load(), 0,
            "every detached connection handler retired before the suite handed back to the fault watch");

    // The handler counter above is decremented by TestConnection as its LAST statement, which still leaves the
    // thunk's own post-handler region running: the in-flight decrement and the shared_ptr release that frees
    // the counter's control block when the last co-owner leaves. That is the exact region a previous lifetime
    // defect occupied, so the watch has to cover it too rather than stopping at the handler boundary.
    for (int i = 0; i < 500 && DevToolsPipeDispatchThunksInFlight().load() != 0; ++i) Sleep(10);
    CheckEq(DevToolsPipeDispatchThunksInFlight().load(), 0L,
            "every dispatch thunk left its post-handler region before the suite handed back to the fault watch");

    return g_pipeAcceptFailures;
}
