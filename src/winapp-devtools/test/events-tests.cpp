// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// DevToolsEvents connection-registry tests.
//
// Every operation performed under the registry lock ALLOCATES: push_back grows the connection vector, Enqueue
// copies bytes into a connection's queue, DevToolsToUtf8 builds a string. The tap's thread and dispatch boundaries
// catch, so a lock left held by an unwind never surfaces as a crash -- it surfaces as every later register,
// unregister and subscription change blocking forever inside the customer's application, which reads as "the
// inspector stopped responding" with no fault to find.
//
// The gate is therefore not "did it throw" but "does the NEXT lock acquisition still complete". Each follow-up
// is issued on its own thread with a timeout, so a leaked lock fails the run instead of hanging it.

#include <windows.h>

#include <atomic>
#include <cstdio>
#include <string>
#include <thread>

#include "../native/WinApp.DevTools.Native/DevToolsEvents.h"

namespace {

int g_failures = 0;

void CheckE(bool cond, const char* label)
{
    std::printf("  %s  %s\n", cond ? "ok " : "FAIL", label);
    if (!cond) ++g_failures;
}

// A real pipe pair: DevToolsEvents_Register starts a writer thread bound to this handle, so the registration and
// unwind paths under test are the real ones rather than a stub.
struct EventsPipePair {
    HANDLE server = INVALID_HANDLE_VALUE;
    HANDLE client = INVALID_HANDLE_VALUE;

    bool Open(int index)
    {
        wchar_t name[128];
        _snwprintf_s(name, _countof(name), _TRUNCATE, L"\\\\.\\pipe\\winapp-devtools-events-test-%lu-%d",
                     GetCurrentProcessId(), index);
        server = CreateNamedPipeW(name, PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                                  PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 4096, 4096, 0, nullptr);
        if (server == INVALID_HANDLE_VALUE) return false;
        client = CreateFileW(name, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (client == INVALID_HANDLE_VALUE) { CloseHandle(server); server = INVALID_HANDLE_VALUE; return false; }
        return true;
    }

    ~EventsPipePair()
    {
        if (client != INVALID_HANDLE_VALUE) CloseHandle(client);
        if (server != INVALID_HANDLE_VALUE) CloseHandle(server);
    }
};

long ProcessHandleCount()
{
    DWORD count = 0;
    if (!GetProcessHandleCount(GetCurrentProcess(), &count)) return -1;
    return static_cast<long>(count);
}

void ResetEventsInjection()
{
    DevToolsEventsFaultBudget() = 0;
    DevToolsEventsFaultsTaken() = 0;
    DevToolsEventsFaultStage() = DevToolsEventsStage::RegistryInsert;
}

// The construction BEFORE the registry insert allocates too (the owner token is a string). It runs on the
// accept path, which owns the pipe handle and reads a null return as a refusal, so a throw escaping here would
// leak the handle as well as the connection.
void TestAnOwnerTokenFaultRefusesCleanly()
{
    const long before = ProcessHandleCount();

    const int kAttempts = 25;
    long fired = 0;
    long nonNull = 0;
    long escapes = 0;
    for (int i = 0; i < kAttempts; ++i) {
        EventsPipePair pipes;
        if (!pipes.Open(200 + i)) { CheckE(false, "could not create the test pipe"); return; }
        ResetEventsInjection();
        DevToolsEventsFaultStage() = DevToolsEventsStage::OwnerToken;
        DevToolsEventsFaultBudget() = 1;
        try {
            if (DevToolsEvents_Register(pipes.server) != nullptr) ++nonNull;
        } catch (...) {
            ++escapes;
        }
        fired += DevToolsEventsFaultsTaken();
    }
    ResetEventsInjection();

    const long after = ProcessHandleCount();

    char label[224];
    std::snprintf(label, sizeof(label), "every one of %d attempts took its owner-token fault (fired=%ld)",
                  kAttempts, fired);
    CheckE(fired == kAttempts, label);

    std::snprintf(label, sizeof(label), "no owner-token fault escaped DevToolsEvents_Register (escaped=%ld)", escapes);
    CheckE(escapes == 0, label);

    std::snprintf(label, sizeof(label), "each attempt reported refusal instead of a connection (nonNull=%ld)",
                  nonNull);
    CheckE(nonNull == 0, label);

    std::snprintf(label, sizeof(label),
                  "%d refused constructions leaked nothing (before=%ld after=%ld delta=%ld)",
                  kAttempts, before, after, after - before);
    CheckE(before > 0 && after > 0 && (after - before) < 8, label);
}

// Runs `work` on its own thread and reports whether it finished inside the timeout. A leaked registry lock
// blocks the work forever, so this is what turns a deadlock into a reported failure.
struct ProbeArgs {
    void (*work)(void*);
    void* state;
};

DWORD WINAPI ProbeThread(LPVOID param)
{
    ProbeArgs* args = static_cast<ProbeArgs*>(param);
    args->work(args->state);
    return 0;
}

bool CompletesWithin(void (*work)(void*), void* state, DWORD timeoutMs)
{
    ProbeArgs args{ work, state };
    HANDLE th = CreateThread(nullptr, 0, ProbeThread, &args, 0, nullptr);
    if (!th) return false;
    const bool finished = WaitForSingleObject(th, timeoutMs) == WAIT_OBJECT_0;
    if (!finished) {
        // The thread is wedged on the lock and cannot be safely killed; leaving it parked is the least-bad
        // option in a test process that is about to report a failure and exit.
        CloseHandle(th);
        return false;
    }
    CloseHandle(th);
    return true;
}

struct RegisterProbe {
    HANDLE pipe;
    DevToolsConn* conn;
};

void DoRegister(void* state)
{
    RegisterProbe* probe = static_cast<RegisterProbe*>(state);
    probe->conn = DevToolsEvents_Register(probe->pipe);
}

// A throw while inserting into the registry must release the exclusive lock, and must not strand the writer
// thread, its event or the connection it had already built.
void TestARegistryInsertFaultLeavesTheLockUsable()
{
    EventsPipePair pipes;
    if (!pipes.Open(1)) { CheckE(false, "could not create the test pipe"); return; }

    ResetEventsInjection();
    DevToolsEventsFaultStage() = DevToolsEventsStage::RegistryInsert;
    DevToolsEventsFaultBudget() = 1;

    DevToolsConn* faulted = DevToolsEvents_Register(pipes.server);

    char label[224];
    std::snprintf(label, sizeof(label), "the injected registry fault actually fired (taken=%ld)",
                  DevToolsEventsFaultsTaken());
    CheckE(DevToolsEventsFaultsTaken() == 1, label);

    CheckE(faulted == nullptr, "a failed registration reports failure instead of a half-registered connection");

    // The gate: this needs the SAME exclusive lock the faulting insert was holding.
    ResetEventsInjection();
    EventsPipePair second;
    if (!second.Open(2)) { CheckE(false, "could not create the second test pipe"); return; }
    RegisterProbe probe{ second.server, nullptr };
    const bool completed = CompletesWithin(DoRegister, &probe, 5000);

    CheckE(completed, "the next registration still acquired the registry lock after the fault (no deadlock)");
    if (!completed) return;

    CheckE(probe.conn != nullptr, "and it succeeded, so the registry itself is intact");
    // Unregister consumes the read loop's initial reference (DevToolsEvents.cpp), so nothing to release here.
    if (probe.conn) DevToolsEvents_Unregister(probe.conn);
}

// The unwind has to be complete, not merely lock-safe: the writer thread is already running by the time the
// insert throws, so a bare `return nullptr` would leak a thread, an event and the connection per attempt.
void TestRepeatedRegistryFaultsStrandNothing()
{
    ResetEventsInjection();
    {
        EventsPipePair warm;
        if (warm.Open(50)) {
            DevToolsConn* c = DevToolsEvents_Register(warm.server);
            if (c) DevToolsEvents_Unregister(c);
        }
    }

    const long before = ProcessHandleCount();

    const int kAttempts = 25;
    long fired = 0;
    long nonNull = 0;
    for (int i = 0; i < kAttempts; ++i) {
        EventsPipePair pipes;
        if (!pipes.Open(100 + i)) { CheckE(false, "could not create the test pipe"); return; }
        ResetEventsInjection();
        DevToolsEventsFaultStage() = DevToolsEventsStage::RegistryInsert;
        DevToolsEventsFaultBudget() = 1;
        if (DevToolsEvents_Register(pipes.server) != nullptr) ++nonNull;
        fired += DevToolsEventsFaultsTaken();
    }
    ResetEventsInjection();

    const long after = ProcessHandleCount();

    char label[224];
    std::snprintf(label, sizeof(label), "every one of %d registrations took its injected fault (fired=%ld)",
                  kAttempts, fired);
    CheckE(fired == kAttempts, label);

    std::snprintf(label, sizeof(label), "no faulting registration returned a connection (nonNull=%ld)", nonNull);
    CheckE(nonNull == 0, label);

    // The writer thread handle and its completion event are both process handles, so a per-attempt strand
    // shows up here as growth proportional to the attempt count.
    std::snprintf(label, sizeof(label),
                  "%d failed registrations stranded no thread or event handles (before=%ld after=%ld delta=%ld)",
                  kAttempts, before, after, after - before);
    CheckE(before > 0 && after > 0 && (after - before) < 8, label);
}

struct CountProbe {
    uint32_t count;
};

void DoSubscriberCount(void* state)
{
    static_cast<CountProbe*>(state)->count = DevToolsEvents_DomainSubscriberCount(DevToolsDomain_VisualTree);
}

struct UnregisterProbe {
    DevToolsConn* conn;
    bool done;
};

void DoUnregister(void* state)
{
    UnregisterProbe* probe = static_cast<UnregisterProbe*>(state);
    DevToolsEvents_Unregister(probe->conn);
    probe->done = true;
}

// A broadcast enqueues under the SHARED lock, and enqueueing allocates. This one runs on the application's UI
// thread, so a lock left held here wedges the registry for the whole process from the worst possible caller.
void TestABroadcastFaultLeavesTheLockUsable()
{
    EventsPipePair pipes;
    if (!pipes.Open(3)) { CheckE(false, "could not create the test pipe"); return; }

    ResetEventsInjection();
    DevToolsConn* conn = DevToolsEvents_Register(pipes.server);
    if (!conn) { CheckE(false, "could not register the subscriber connection"); return; }
    DevToolsEvents_SetDomain(conn, DevToolsDomain_VisualTree, true);

    char label[224];
    const uint32_t subscribed = DevToolsEvents_DomainSubscriberCount(DevToolsDomain_VisualTree);
    std::snprintf(label, sizeof(label), "the connection is subscribed, so the broadcast reaches the enqueue "
                  "(subscribers=%lu)", static_cast<unsigned long>(subscribed));
    CheckE(subscribed == 1, label);

    DevToolsEventsFaultStage() = DevToolsEventsStage::BroadcastEnqueue;
    DevToolsEventsFaultBudget() = 1;
    bool threw = false;
    try {
        DevToolsEvents_Broadcast(DevToolsDomain_VisualTree, L"{\"method\":\"VisualTree.changed\"}",
                            DevToolsCoalesce_None, std::wstring());
    } catch (...) {
        threw = true;
    }

    std::snprintf(label, sizeof(label), "the injected broadcast fault actually fired (taken=%ld threw=%d)",
                  DevToolsEventsFaultsTaken(), threw ? 1 : 0);
    CheckE(DevToolsEventsFaultsTaken() == 1 && threw, label);
    ResetEventsInjection();

    // A shared acquisition first: it succeeds even if the lock is still held SHARED, so it distinguishes a
    // leaked shared lock from a leaked exclusive one rather than just reporting "stuck".
    CountProbe counter{ 0 };
    const bool sharedOk = CompletesWithin(DoSubscriberCount, &counter, 5000);
    CheckE(sharedOk, "a shared reader still completed after the broadcast fault");

    // The real gate: an EXCLUSIVE acquisition, which blocks behind any still-held shared lock.
    UnregisterProbe probe{ conn, false };
    const bool exclusiveOk = CompletesWithin(DoUnregister, &probe, 5000);
    CheckE(exclusiveOk && probe.done,
           "an exclusive writer still completed after the broadcast fault (no leaked shared lock)");
    if (!exclusiveOk) return;

    const uint32_t remaining = DevToolsEvents_DomainSubscriberCount(DevToolsDomain_VisualTree);
    std::snprintf(label, sizeof(label), "the subscriber count was still released correctly (remaining=%lu)",
                  static_cast<unsigned long>(remaining));
    CheckE(remaining == 0, label);
}

struct WriterSchedule {
    HANDLE pipe;
    DevToolsEventsTestStage holdAt;
    HANDLE entered = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE release = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE cancelled = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    ~WriterSchedule() { CloseHandle(entered); CloseHandle(release); CloseHandle(cancelled); }
};
WriterSchedule* g_schedule = nullptr;

void ScheduleWriter(DevToolsEventsTestStage stage, HANDLE pipe)
{
    if (!g_schedule || pipe != g_schedule->pipe) return;
    if (stage == DevToolsEventsTestStage::AfterCancel) SetEvent(g_schedule->cancelled);
    if (stage == g_schedule->holdAt) {
        SetEvent(g_schedule->entered);
        if (WaitForSingleObject(g_schedule->release, 10000) != WAIT_OBJECT_0) ExitProcess(90);
    }
}

void TestUnregisterIsTerminal(DevToolsEventsTestStage stage, int index)
{
    EventsPipePair pipes;
    if (!pipes.Open(index)) { CheckE(false, "could not create shutdown test pipe"); return; }
    WriterSchedule schedule{ pipes.server, stage };
    g_schedule = &schedule;
    DevToolsEventsScheduleHook() = ScheduleWriter;
    auto conn = DevToolsEvents_Register(pipes.server);
    if (!conn) ExitProcess(91);
    DevToolsEvents_EnqueueResponse(conn, std::wstring(256 * 1024, L'x'));
    if (WaitForSingleObject(schedule.entered, 3000) != WAIT_OBJECT_0) ExitProcess(92);
    HANDLE finished = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    std::thread unregister([&] { DevToolsEvents_Unregister(conn); SetEvent(finished); });
    if (WaitForSingleObject(schedule.cancelled, 3000) != WAIT_OBJECT_0) ExitProcess(93);
    SetEvent(schedule.release);
    const bool bounded = WaitForSingleObject(finished, 500) == WAIT_OBJECT_0;
    CheckE(bounded, stage == DevToolsEventsTestStage::BeforeWriterLoop
        ? "a queued writer resumed after cancellation cannot pin unregister"
        : "a popped write resumed after cancellation cannot pin unregister");
    if (!bounded) {
        DisconnectNamedPipe(pipes.server);
        if (WaitForSingleObject(finished, 3000) != WAIT_OBJECT_0) ExitProcess(94);
    }
    unregister.join();
    CloseHandle(finished);
    DevToolsEventsScheduleHook() = nullptr;
    g_schedule = nullptr;
}

void TestOverflowEndsFutureReads(bool treeDelta, int index)
{
    EventsPipePair pipes;
    if (!pipes.Open(index)) { CheckE(false, "could not create overflow test pipe"); return; }
    WriterSchedule schedule{ pipes.server, DevToolsEventsTestStage::BeforeWriterLoop };
    g_schedule = &schedule;
    DevToolsEventsScheduleHook() = ScheduleWriter;
    auto conn = DevToolsEvents_Register(pipes.server);
    if (!conn || WaitForSingleObject(schedule.entered, 3000) != WAIT_OBJECT_0) ExitProcess(95);
    DevToolsEvents_SetDomain(conn, DevToolsDomain_VisualTree, true);
    for (int i = 0; i < 4096; ++i) DevToolsEvents_EnqueueResponse(conn, L"{}");
    if (treeDelta) DevToolsEvents_BroadcastTreeDelta(L"{\"method\":\"VisualTree.changed\"}");
    else DevToolsEvents_EnqueueResponse(conn, L"{}");
    OVERLAPPED read{};
    read.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    char byte = 0;
    DWORD count = 0;
    const BOOL started = ReadFile(pipes.server, &byte, 1, nullptr, &read);
    const DWORD error = started ? ERROR_SUCCESS : GetLastError();
    bool terminal = !started && error != ERROR_IO_PENDING;
    if (error == ERROR_IO_PENDING) {
        terminal = WaitForSingleObject(read.hEvent, 500) == WAIT_OBJECT_0;
        if (!terminal) CancelIoEx(pipes.server, &read);
        GetOverlappedResult(pipes.server, &read, &count, TRUE);
    }
    CheckE(terminal, treeDelta ? "tree-delta hard cap prevents a later blocked read"
                              : "response hard cap prevents a later blocked read");
    CloseHandle(read.hEvent);
    SetEvent(schedule.release);
    DevToolsEvents_Unregister(conn);
    DevToolsEventsScheduleHook() = nullptr;
    g_schedule = nullptr;
    CheckE(DevToolsEvents_DomainSubscriberCount(DevToolsDomain_VisualTree) == 0,
        "terminal overflow still releases its registry subscription");
}

}   // namespace

int g_eventsFailures = 0;

int RunEventsTests()
{
    std::printf("DevToolsEvents tests (registry-lock fault injection)\n");
    g_failures = 0;

    TestAnOwnerTokenFaultRefusesCleanly();
    TestARegistryInsertFaultLeavesTheLockUsable();
    TestRepeatedRegistryFaultsStrandNothing();
    TestABroadcastFaultLeavesTheLockUsable();
    TestUnregisterIsTerminal(DevToolsEventsTestStage::BeforeWriterLoop, 501);
    TestUnregisterIsTerminal(DevToolsEventsTestStage::BeforeWrite, 502);
    TestOverflowEndsFutureReads(false, 503);
    TestOverflowEndsFutureReads(true, 504);

    g_eventsFailures = g_failures;
    return g_failures;
}
