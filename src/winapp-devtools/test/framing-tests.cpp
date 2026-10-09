// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Fault injection at the tap's per-connection framing boundary (F19).
//
// These tests drive the REAL framing runner from DevToolsPipeFraming.h -- the same inline function the shipping tap
// calls from PipeConnection -- against real named-pipe instances, with a fake eventing host standing in for the
// XAML world. That distinction is the whole point: a test that modelled the framing loop's SHAPE would keep
// passing after someone deleted the production try/catch, which is exactly the regression being gated.
//
// Two separate properties are at stake, and the difference matters because each has a different failure:
//
// * The exception must not escape the thread entry point. Without any catch, std::terminate kills the app the
// tap is injected into. The accept loop's dispatch thunk now backstops this too (see pipeaccept-tests.cpp),
// so the process survives even with the framing catch removed --
// * -- which is why teardown is asserted here rather than assumed. The unwind SKIPS the teardown: the read
// event, the writer registration and the pipe handle all leak, one per faulting connection, and the writer
// thread is never joined. The thunk's backstop cannot recover any of that. Removing the framing catch is
// therefore invisible to a survival-only assertion, and visible here.

#define WINAPP_DEVTOOLS_PIPE_FAULT_INJECTION

#include <windows.h>
#include <atomic>
#include <cstdio>
#include <string>
#include <vector>

#include "DevToolsPipeFraming.h"
#include "DevToolsEvents.h"

namespace {

int g_failures = 0;

void CheckF(bool cond, const char* label)
{
    std::printf(cond ? "  ok  %s\n" : "  FAIL %s\n", label);
    if (!cond) ++g_failures;
}

// Stands in for the tap's eventing world. Every callback records what production would have done, so teardown
// can be asserted as an exact sequence of counts rather than inferred from the connection ending.
struct FakeHost {
    std::atomic<long> registered{ 0 };
    std::atomic<long> unregistered{ 0 };   // the writer-thread stop/join
    std::atomic<long> verified{ 0 };
    std::atomic<long> dispatched{ 0 };
    std::atomic<long> faultResponses{ 0 }; // dispatch faults turned into a JSON-RPC error
    std::atomic<long> enqueued{ 0 };
    std::atomic<long> afterUnregister{ 0 };
    std::atomic<long> writerThreadsLive{ 0 };
    std::atomic<bool> failRegister{ false };
    std::atomic<bool> throwOnRegister{ false };
    // When set, Host_Enqueue writes the response onto the real pipe, so a client that never reads leaves
    // genuinely unread bytes pending in the pipe -- the only way to exercise a teardown-side flush.
    HANDLE responsePipe = INVALID_HANDLE_VALUE;
    std::atomic<long> responseBytesWritten{ 0 };
    std::vector<std::wstring> lines;
    CRITICAL_SECTION lock;

    FakeHost()  { InitializeCriticalSection(&lock); }
    ~FakeHost() { DeleteCriticalSection(&lock); }
};

// One per connection, so a leaked registration is observable as a live "writer thread".
struct FakeConn {
    FakeHost* host;
    unsigned long long id;
};

std::atomic<unsigned long long> g_nextConnId{ 1 };

void* Host_Register(HANDLE, void* user)
{
    FakeHost* host = static_cast<FakeHost*>(user);
    if (host->throwOnRegister.load()) throw std::bad_alloc();
    if (host->failRegister.load()) return nullptr;
    host->registered.fetch_add(1);
    host->writerThreadsLive.fetch_add(1);
    FakeConn* conn = new FakeConn{ host, g_nextConnId.fetch_add(1) };
    return conn;
}

unsigned long long Host_ConnId(void* conn, void*)
{
    return conn ? static_cast<FakeConn*>(conn)->id : 0;
}

unsigned Host_Unregister(void* conn, void* user)
{
    FakeHost* host = static_cast<FakeHost*>(user);
    host->unregistered.fetch_add(1);
    host->writerThreadsLive.fetch_sub(1);
    delete static_cast<FakeConn*>(conn);
    return 0;
}

bool Host_Verify(HANDLE, void* user)
{
    static_cast<FakeHost*>(user)->verified.fetch_add(1);
    return true;
}

std::wstring Host_Dispatch(void*, const std::wstring& line, void* user)
{
    FakeHost* host = static_cast<FakeHost*>(user);
    host->dispatched.fetch_add(1);
    EnterCriticalSection(&host->lock);
    host->lines.push_back(line);
    LeaveCriticalSection(&host->lock);
    return L"{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}";
}

std::wstring Host_DispatchFault(bool oom, void* user)
{
    static_cast<FakeHost*>(user)->faultResponses.fetch_add(1);
    return oom ? L"{\"error\":\"oom\"}" : L"{\"error\":\"internal\"}";
}

void Host_Enqueue(void*, const std::wstring&, void* user)
{
    FakeHost* host = static_cast<FakeHost*>(user);
    host->enqueued.fetch_add(1);
    if (host->responsePipe == INVALID_HANDLE_VALUE) return;
    // The handle is FILE_FLAG_OVERLAPPED, so the write has to be overlapped even though the payload fits the
    // pipe buffer and completes immediately.
    const char payload[] = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n";
    OVERLAPPED ov{};
    ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!ov.hEvent) return;
    DWORD written = 0;
    if (WriteFile(host->responsePipe, payload, sizeof(payload) - 1, nullptr, &ov) ||
        GetLastError() == ERROR_IO_PENDING) {
        if (GetOverlappedResult(host->responsePipe, &ov, &written, TRUE))
            host->responseBytesWritten.fetch_add(static_cast<long>(written));
    }
    CloseHandle(ov.hEvent);
}

void Host_AfterUnregister(unsigned long long, unsigned, void* user)
{
    static_cast<FakeHost*>(user)->afterUnregister.fetch_add(1);
}

void Host_Log(const wchar_t*, void*) { }

DevToolsFramingHost MakeHost(FakeHost* fake)
{
    DevToolsFramingHost h;
    h.user = fake;
    h.RegisterConn = Host_Register;
    h.ConnId = Host_ConnId;
    h.UnregisterConn = Host_Unregister;
    h.VerifyClient = Host_Verify;
    h.DispatchLine = Host_Dispatch;
    h.DispatchFault = Host_DispatchFault;
    h.EnqueueResponse = Host_Enqueue;
    h.AfterUnregister = Host_AfterUnregister;
    h.Log = Host_Log;
    return h;
}

// A real pipe pair. The framing runner is driven against a genuine overlapped pipe instance rather than a
// memory buffer, so the read loop, the completion event and the handle teardown are the real ones.
struct PipePair {
    std::wstring name;
    HANDLE server = INVALID_HANDLE_VALUE;
    HANDLE client = INVALID_HANDLE_VALUE;

    bool Open(const wchar_t* tag, int index)
    {
        wchar_t buf[128];
        _snwprintf_s(buf, _countof(buf), _TRUNCATE, L"\\\\.\\pipe\\winapp-devtools-framing-test-%lu-%s-%d",
                     GetCurrentProcessId(), tag, index);
        name = buf;
        server = CreateNamedPipeW(name.c_str(),
                                  PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                                  PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                                  1, 4096, 4096, 0, nullptr);
        if (server == INVALID_HANDLE_VALUE) return false;
        client = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (client == INVALID_HANDLE_VALUE) { CloseHandle(server); server = INVALID_HANDLE_VALUE; return false; }
        return true;
    }

    void WriteLine(const char* text)
    {
        DWORD written = 0;
        WriteFile(client, text, static_cast<DWORD>(strlen(text)), &written, nullptr);
    }

    void CloseClient()
    {
        if (client != INVALID_HANDLE_VALUE) { CloseHandle(client); client = INVALID_HANDLE_VALUE; }
    }

    ~PipePair() { CloseClient(); }   // the server handle is owned and closed by the framing runner
};

// Handle count for THIS process. The teardown assertions are about handles the runner owns, and a leaked pipe
// handle or read event shows up here and nowhere else.
long ProcessHandles()
{
    DWORD count = 0;
    if (!GetProcessHandleCount(GetCurrentProcess(), &count)) return -1;
    return static_cast<long>(count);
}

void ResetInjection()
{
    DevToolsFramingFaultBudget() = 0;
    DevToolsFramingFaultsTaken() = 0;
    DevToolsFramingFaultStage() = DevToolsFramingStage::Append;
}

const char* StageName(DevToolsFramingStage stage)
{
    switch (stage) {
        case DevToolsFramingStage::Append:   return "append";
        case DevToolsFramingStage::Split:    return "split";
        case DevToolsFramingStage::Convert:  return "convert";
        case DevToolsFramingStage::Dispatch: return "dispatch";
        case DevToolsFramingStage::Enqueue:  return "enqueue";
        case DevToolsFramingStage::AfterUnregister: return "after-unregister";
    }
    return "?";
}

// Runs one connection to completion on a real thread, the way the accept loop dispatches it. The try/catch
// here is NOT test scaffolding -- it stands in for DevToolsPipeDispatchThunk, which is what actually calls
// PipeConnection in the tap and which gained its own catch alongside the framing one. Modelling it is what
// makes the red control meaningful: with the framing catch deleted the exception escapes into this catch, the
// process survives exactly as it would in production, and the damage that remains is the skipped teardown.
struct RunnerArgs {
    HANDLE pipe;
    const DevToolsFramingHost* host;
    std::atomic<bool>* returned;
    std::atomic<bool>* escaped;
};

DWORD WINAPI RunnerThread(LPVOID param)
{
    RunnerArgs* args = static_cast<RunnerArgs*>(param);
    try {
        DevToolsRunPipeFraming(args->pipe, *args->host);
        args->returned->store(true);
    } catch (...) {
        args->escaped->store(true);
    }
    return 0;
}

// Drives one connection with `budget` faults injected at `stage`, and returns only once the runner thread has
// fully retired.
struct ConnectionOutcome {
    bool returned = false;
    bool escaped = false;
    bool threadRetired = false;
    long faultsTaken = 0;
};

ConnectionOutcome RunOneConnection(FakeHost& fake, DevToolsFramingStage stage, long budget, int index,
                                   const char* tag, const char* payload)
{
    ConnectionOutcome outcome;
    PipePair pipes;
    if (!pipes.Open(L"stage", index)) return outcome;

    ResetInjection();
    DevToolsFramingFaultStage() = stage;
    DevToolsFramingFaultBudget() = budget;

    const DevToolsFramingHost host = MakeHost(&fake);
    std::atomic<bool> returned{ false };
    std::atomic<bool> escaped{ false };
    RunnerArgs args{ pipes.server, &host, &returned, &escaped };
    HANDLE th = CreateThread(nullptr, 0, RunnerThread, &args, 0, nullptr);
    if (!th) { CloseHandle(pipes.server); return outcome; }

    pipes.WriteLine(payload);
    // The runner drops the connection on a framing fault; on the clean path it keeps reading until EOF, so the
    // client side has to close for the loop to end.
    Sleep(60);
    pipes.CloseClient();

    outcome.threadRetired = WaitForSingleObject(th, 10000) == WAIT_OBJECT_0;
    CloseHandle(th);
    outcome.returned = returned.load();
    outcome.escaped = escaped.load();
    outcome.faultsTaken = DevToolsFramingFaultsTaken();
    ResetInjection();
    (void)tag;
    return outcome;
}

// The core F19 gate. Every framing stage that allocates gets a fault, and each one must leave the connection
// torn down exactly once and the process alive.
void TestAFramingFaultAtEveryStageTearsDownExactlyOnce()
{
    const DevToolsFramingStage stages[] = {
        DevToolsFramingStage::Append,
        DevToolsFramingStage::Split,
        DevToolsFramingStage::Convert,
        DevToolsFramingStage::Dispatch,
        DevToolsFramingStage::Enqueue,
    };

    int index = 0;
    for (DevToolsFramingStage stage : stages) {
        FakeHost fake;
        const ConnectionOutcome outcome =
            RunOneConnection(fake, stage, 1, index++, StageName(stage), "{\"jsonrpc\":\"2.0\"}\n");

        char label[224];

        // Non-vacuous first: if the injection never fired, everything below would pass for the wrong reason.
        std::snprintf(label, sizeof(label), "[%s] the injected framing fault actually fired (taken=%ld)",
                      StageName(stage), outcome.faultsTaken);
        CheckF(outcome.faultsTaken == 1, label);

        std::snprintf(label, sizeof(label),
                      "[%s] the framing runner contained the fault instead of letting it escape (escaped=%d)",
                      StageName(stage), outcome.escaped ? 1 : 0);
        CheckF(outcome.returned && !outcome.escaped, label);

        std::snprintf(label, sizeof(label), "[%s] the connection thread retired", StageName(stage));
        CheckF(outcome.threadRetired, label);

        // The teardown the unwind would have skipped. `unregistered` is the writer-thread stop/join, and
        // writerThreadsLive returning to zero is that join having actually happened rather than the
        // registration simply being abandoned.
        std::snprintf(label, sizeof(label),
                      "[%s] the writer registration was released exactly once (registered=%ld unregistered=%ld)",
                      StageName(stage), fake.registered.load(), fake.unregistered.load());
        CheckF(fake.registered.load() == 1 && fake.unregistered.load() == 1, label);

        std::snprintf(label, sizeof(label), "[%s] no writer thread outlived the connection (live=%ld)",
                      StageName(stage), fake.writerThreadsLive.load());
        CheckF(fake.writerThreadsLive.load() == 0, label);

        std::snprintf(label, sizeof(label),
                      "[%s] the post-unregister release still ran on the faulting path (calls=%ld)",
                      StageName(stage), fake.afterUnregister.load());
        CheckF(fake.afterUnregister.load() == 1, label);

        // A dispatch-stage fault is contained by the INNER catch, which is a different contract: the client
        // gets a structured error and the connection survives to serve later lines. The outer stages drop it.
        if (stage == DevToolsFramingStage::Dispatch) {
            std::snprintf(label, sizeof(label),
                          "[dispatch] a dispatch fault became a structured error, not a dropped connection "
                          "(faultResponses=%ld enqueued=%ld)",
                          fake.faultResponses.load(), fake.enqueued.load());
            CheckF(fake.faultResponses.load() == 1 && fake.enqueued.load() == 1, label);
        }
    }

    CheckF(true, "the process survived a framing fault at every allocating stage");
}

// Teardown is only exact if it is exact under repetition: one leaked handle per faulting connection is the
// shape this defect actually took, and a single connection cannot tell a leak from a delay.
void TestRepeatedFramingFaultsLeakNoHandles()
{
    FakeHost warm;
    RunOneConnection(warm, DevToolsFramingStage::Split, 1, 900, "warm", "{\"a\":1}\n");

    const long before = ProcessHandles();

    FakeHost fake;
    const int kConnections = 40;
    long fired = 0;
    for (int i = 0; i < kConnections; ++i) {
        const ConnectionOutcome outcome =
            RunOneConnection(fake, DevToolsFramingStage::Split, 1, 1000 + i, "leak", "{\"a\":1}\n");
        fired += outcome.faultsTaken;
    }

    const long after = ProcessHandles();

    char label[224];
    std::snprintf(label, sizeof(label), "every one of %d connections took its injected fault (fired=%ld)",
                  kConnections, fired);
    CheckF(fired == kConnections, label);

    std::snprintf(label, sizeof(label),
                  "%d faulting connections released every registration (registered=%ld unregistered=%ld)",
                  kConnections, fake.registered.load(), fake.unregistered.load());
    CheckF(fake.registered.load() == kConnections && fake.unregistered.load() == kConnections, label);

    // Leaked read events or pipe handles grow with the number of completed connections.
    std::snprintf(label, sizeof(label),
                  "%d faulting connections leaked no handles (before=%ld after=%ld delta=%ld)",
                  kConnections, before, after, after - before);
    CheckF(before > 0 && after > 0 && (after - before) < 8, label);
}

// The fault must cost exactly one client. A connection opened afterwards has to be served normally, which is
// what makes this a survivable fault rather than a wedged pipe.
void TestTheConnectionAfterAFramingFaultIsServedNormally()
{
    FakeHost fake;
    const ConnectionOutcome faulted =
        RunOneConnection(fake, DevToolsFramingStage::Convert, 1, 2000, "before", "{\"first\":1}\n");

    char label[224];
    std::snprintf(label, sizeof(label), "the first connection really faulted (taken=%ld)", faulted.faultsTaken);
    CheckF(faulted.faultsTaken == 1, label);

    const long dispatchedBefore = fake.dispatched.load();

    // No injection this time.
    const ConnectionOutcome clean =
        RunOneConnection(fake, DevToolsFramingStage::Append, 0, 2001, "after", "{\"second\":2}\n");

    std::snprintf(label, sizeof(label), "the next connection retired cleanly (returned=%d escaped=%d retired=%d)",
                  clean.returned ? 1 : 0, clean.escaped ? 1 : 0, clean.threadRetired ? 1 : 0);
    CheckF(clean.returned && !clean.escaped && clean.threadRetired, label);

    const long served = fake.dispatched.load() - dispatchedBefore;
    std::snprintf(label, sizeof(label),
                  "the connection after a framing fault was served normally (lines dispatched=%ld)", served);
    CheckF(served == 1, label);

    std::snprintf(label, sizeof(label), "its response was queued to the writer (enqueued=%ld)",
                  fake.enqueued.load());
    CheckF(fake.enqueued.load() >= 1, label);
}

// The refusal paths own the handle too. A connection whose writer thread cannot start never enters the framing
// loop, so its teardown is the one place the runner closes a handle it never read from.
void TestARefusedRegistrationStillClosesTheHandle()
{
    FakeHost fake;
    fake.failRegister.store(true);

    const long before = ProcessHandles();
    const int kConnections = 20;
    for (int i = 0; i < kConnections; ++i) {
        PipePair pipes;
        if (!pipes.Open(L"refuse", 3000 + i)) { CheckF(false, "could not create the test pipe"); return; }
        const DevToolsFramingHost host = MakeHost(&fake);
        DevToolsRunPipeFraming(pipes.server, host);
    }
    const long after = ProcessHandles();

    char label[224];
    std::snprintf(label, sizeof(label), "a refused connection registers no writer (registered=%ld)",
                  fake.registered.load());
    CheckF(fake.registered.load() == 0, label);

    std::snprintf(label, sizeof(label),
                  "%d refused connections leaked no handles (before=%ld after=%ld delta=%ld)",
                  kConnections, before, after, after - before);
    CheckF(before > 0 && after > 0 && (after - before) < 8, label);
}

// A fault in the post-unregister release must not cost the pipe instance. AfterUnregister allocates (it posts
// to the UI thread) and runs after the framing boundary has already been left, so before the guard a throw
// there skipped the disconnect and close entirely -- one listener slot burned per faulting disconnect.
void TestAnAfterUnregisterFaultStillClosesTheHandle()
{
    FakeHost warm;
    RunOneConnection(warm, DevToolsFramingStage::Split, 1, 3900, "warm", "{\"a\":1}\n");

    const long before = ProcessHandles();

    FakeHost fake;
    const int kConnections = 30;
    long fired = 0;
    long escapes = 0;
    long retired = 0;
    for (int i = 0; i < kConnections; ++i) {
        const ConnectionOutcome outcome = RunOneConnection(fake, DevToolsFramingStage::AfterUnregister, 1,
                                                           4000 + i, "after-unregister", "{\"a\":1}\n");
        fired += outcome.faultsTaken;
        if (outcome.escaped) ++escapes;
        if (outcome.threadRetired) ++retired;
    }

    const long after = ProcessHandles();

    char label[224];
    std::snprintf(label, sizeof(label),
                  "every one of %d connections took its post-unregister fault (fired=%ld)", kConnections, fired);
    CheckF(fired == kConnections, label);

    std::snprintf(label, sizeof(label), "no post-unregister fault escaped the connection (escaped=%ld)", escapes);
    CheckF(escapes == 0, label);

    std::snprintf(label, sizeof(label), "every connection thread retired (retired=%ld of %d)",
                  retired, kConnections);
    CheckF(retired == kConnections, label);

    // Unregister runs BEFORE the faulting call, so the writer must still have been released every time.
    std::snprintf(label, sizeof(label),
                  "the writer registration was still released every time (registered=%ld unregistered=%ld live=%ld)",
                  fake.registered.load(), fake.unregistered.load(), fake.writerThreadsLive.load());
    CheckF(fake.registered.load() == kConnections && fake.unregistered.load() == kConnections &&
           fake.writerThreadsLive.load() == 0, label);

    // The assertion the guard exists for: the pipe handle is closed on the faulting path too.
    std::snprintf(label, sizeof(label),
                  "%d post-unregister faults leaked no handles (before=%ld after=%ld delta=%ld)",
                  kConnections, before, after, after - before);
    CheckF(before > 0 && after > 0 && (after - before) < 8, label);
}

// Teardown must not wait for a peer that refuses to read. Oversized unterminated input ends
// the connection without blocking indefinitely in FlushFileBuffers.
void TestANonReadingClientDoesNotPinTeardown()
{
    FakeHost fake;
    PipePair pipes;
    if (!pipes.Open(L"noread", 5000)) { CheckF(false, "could not create the test pipe"); return; }

    ResetInjection();
    fake.responsePipe = pipes.server;

    const DevToolsFramingHost host = MakeHost(&fake);
    std::atomic<bool> returned{ false };
    std::atomic<bool> escaped{ false };
    RunnerArgs args{ pipes.server, &host, &returned, &escaped };
    HANDLE th = CreateThread(nullptr, 0, RunnerThread, &args, 0, nullptr);
    if (!th) { CloseHandle(pipes.server); CheckF(false, "could not start the connection thread"); return; }

    // One real request, so the host writes a real response into the pipe that this client will never read.
    pipes.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1}\n");
    for (int i = 0; i < 200 && fake.responseBytesWritten.load() == 0; ++i) Sleep(10);

    // Confirm the response really is sitting unread on the client side BEFORE driving the loop out. Checking
    // after would race the teardown -- DisconnectNamedPipe discards the pipe's unread data, so a fast (correct)
    // teardown would report nothing pending and the timing assertion below would prove nothing.
    DWORD pending = 0;
    for (int i = 0; i < 200; ++i) {
        DWORD avail = 0;
        if (PeekNamedPipe(pipes.client, nullptr, 0, nullptr, &avail, nullptr) && avail > 0) { pending = avail; break; }
        Sleep(10);
    }

    // Drive the read loop out WITHOUT closing the client: an oversized line with no newline is the documented
    // drop path, and it leaves the peer connected with its response still unread.
    std::string oversized(DevToolsFramingMaxLine + 4096, 'x');
    DWORD written = 0;
    WriteFile(pipes.client, oversized.data(), static_cast<DWORD>(oversized.size()), &written, nullptr);

    const DWORD waited = WaitForSingleObject(th, 5000);
    const bool retiredPromptly = waited == WAIT_OBJECT_0;

    char label[256];
    std::snprintf(label, sizeof(label), "the host wrote a real response onto the pipe (bytes=%ld enqueued=%ld)",
                  fake.responseBytesWritten.load(), fake.enqueued.load());
    CheckF(fake.responseBytesWritten.load() > 0 && fake.enqueued.load() == 1, label);

    std::snprintf(label, sizeof(label), "the client left those bytes unread while still connected (pending=%lu)",
                  pending);
    CheckF(pending > 0, label);

    std::snprintf(label, sizeof(label),
                  "teardown completed without waiting on the non-reading client (wait=0x%lx retired=%d)",
                  waited, retiredPromptly ? 1 : 0);
    CheckF(retiredPromptly, label);

    if (retiredPromptly) {
        std::snprintf(label, sizeof(label),
                      "the connection was released exactly once (unregistered=%ld live=%ld afterUnregister=%ld)",
                      fake.unregistered.load(), fake.writerThreadsLive.load(), fake.afterUnregister.load());
        CheckF(fake.unregistered.load() == 1 && fake.writerThreadsLive.load() == 0 &&
               fake.afterUnregister.load() == 1, label);
        CloseHandle(th);

        // Not waiting on the peer must not mean throwing its reply away. A pipelining client dropped for an
        // exceptional reason still completed a response into the buffer, and it is entitled to read it after
        // the server side is gone -- which DisconnectNamedPipe would have discarded.
        char drained[128]{};
        DWORD got = 0;
        const BOOL readOk = ReadFile(pipes.client, drained, sizeof(drained) - 1, &got, nullptr);
        std::snprintf(label, sizeof(label),
                      "the completed response survived teardown and was still readable (ok=%d bytes=%lu)",
                      readOk ? 1 : 0, got);
        CheckF(readOk && got == static_cast<DWORD>(pending), label);
    } else {
        // The thread is parked in teardown and owns the server handle; closing the client is what would let a
        // flush return, so do that rather than leaving the run wedged.
        pipes.CloseClient();
        WaitForSingleObject(th, 5000);
        CloseHandle(th);
    }
    fake.responsePipe = INVALID_HANDLE_VALUE;
}

// RegisterConn is a host callback that allocates, and it runs BEFORE the framing boundary while this function
// owns the accepted handle. A throw there -- not merely a null return -- has to end as a refusal, or the pipe
// instance leaks and the listener pool loses a slot permanently.
void TestARegistrationThatThrowsStillClosesTheHandle()
{
    FakeHost fake;
    fake.throwOnRegister.store(true);

    const long before = ProcessHandles();
    const int kConnections = 25;
    long escapes = 0;
    for (int i = 0; i < kConnections; ++i) {
        PipePair pipes;
        if (!pipes.Open(L"throwreg", 6000 + i)) { CheckF(false, "could not create the test pipe"); return; }
        const DevToolsFramingHost host = MakeHost(&fake);
        try {
            DevToolsRunPipeFraming(pipes.server, host);
        } catch (...) {
            ++escapes;
            CloseHandle(pipes.server);   // the runner did not take ownership; do not leak it in the test either
        }
    }
    const long after = ProcessHandles();

    char label[224];
    std::snprintf(label, sizeof(label),
                  "a throwing registration never escapes the runner, so the handle is never orphaned (escaped=%ld of %d)",
                  escapes, kConnections);
    CheckF(escapes == 0, label);

    std::snprintf(label, sizeof(label), "and it registers no writer (registered=%ld live=%ld)",
                  fake.registered.load(), fake.writerThreadsLive.load());
    CheckF(fake.registered.load() == 0 && fake.writerThreadsLive.load() == 0, label);

    std::snprintf(label, sizeof(label),
                  "%d throwing registrations leaked no pipe handles (before=%ld after=%ld delta=%ld)",
                  kConnections, before, after, after - before);
    CheckF(before > 0 && after > 0 && (after - before) < 8, label);
}

struct RealWriterHost {
    bool overflow;
    std::atomic<DevToolsConn*> connection{ nullptr };
    std::atomic<int> unregistered{ 0 };
};

DevToolsFramingHost MakeRealWriterHost(RealWriterHost* state)
{
    DevToolsFramingHost host;
    host.user = state;
    host.RegisterConn = [](HANDLE pipe, void* user) -> void* {
        auto conn = DevToolsEvents_Register(pipe);
        if (conn) {
            DevToolsEvents_Retain(conn);
            DevToolsEvents_SetDomain(conn, DevToolsDomain_Property, true);
        }
        static_cast<RealWriterHost*>(user)->connection = conn;
        return conn;
    };
    host.ConnId = [](void* conn, void*) { return static_cast<unsigned long long>(DevToolsEvents_ConnId(static_cast<DevToolsConn*>(conn))); };
    host.UnregisterConn = [](void* conn, void* user) {
        ++static_cast<RealWriterHost*>(user)->unregistered;
        return static_cast<unsigned>(DevToolsEvents_Unregister(static_cast<DevToolsConn*>(conn)));
    };
    host.VerifyClient = [](HANDLE, void*) { return true; };
    host.DispatchLine = [](void* conn, const std::wstring& line, void* user) {
        if (static_cast<RealWriterHost*>(user)->overflow) {
            DevToolsEvents_EnqueueResponse(static_cast<DevToolsConn*>(conn), std::wstring(256 * 1024, L'x'));
            for (int i = 0; i < 4097; ++i) DevToolsEvents_EnqueueResponse(static_cast<DevToolsConn*>(conn), L"{}");
            return std::wstring();
        }
        DevToolsEvents_Broadcast(DevToolsDomain_Property, L"event-" + line, DevToolsCoalesce_None, L"");
        return L"reply-" + line;
    };
    host.DispatchFault = [](bool, void*) { return std::wstring(L"fault"); };
    host.EnqueueResponse = [](void* conn, const std::wstring& response, void*) {
        DevToolsEvents_EnqueueResponse(static_cast<DevToolsConn*>(conn), response);
    };
    host.AfterUnregister = [](unsigned long long, unsigned, void*) {};
    host.Log = Host_Log;
    return host;
}

bool WaitForBytes(HANDLE pipe, DWORD expected)
{
    for (int i = 0; i < 300; ++i) {
        DWORD available = 0;
        if (PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr) && available >= expected) return true;
        Sleep(10);
    }
    return false;
}

void TestFramingWithRealWriter(bool overflow)
{
    ResetInjection();
    RealWriterHost state{ overflow };
    PipePair pipes;
    if (!pipes.Open(L"real-writer", overflow ? 9101 : 9102)) {
        CheckF(false, "could not create real-writer framing pipe");
        return;
    }
    const auto host = MakeRealWriterHost(&state);
    std::atomic<bool> returned{ false }, escaped{ false };
    RunnerArgs args{ pipes.server, &host, &returned, &escaped };
    HANDLE thread = CreateThread(nullptr, 0, RunnerThread, &args, 0, nullptr);
    if (!thread) { CloseHandle(pipes.server); CheckF(false, "could not start real framing runner"); return; }
    const std::string buffered = "event-buffered\nreply-buffered\n";
    bool bytesBuffered = false;
    if (overflow) {
        pipes.WriteLine("overflow\n");
    } else {
        const std::string expected = "event-one\nreply-one\nevent-two\nreply-two\n";
        pipes.WriteLine("one\ntwo\n");
        const bool readable = WaitForBytes(pipes.client, static_cast<DWORD>(expected.size()));
        CheckF(readable, "real framing and writer produce responses and subscribed events");
        if (readable) {
            char data[128]{};
            DWORD count = 0;
            const BOOL ok = ReadFile(pipes.client, data, static_cast<DWORD>(expected.size()), &count, nullptr);
            CheckF(ok && std::string(data, count) == expected, "real response/event writer preserves healthy FIFO framing");
            pipes.WriteLine("buffered\n");
            bytesBuffered = WaitForBytes(pipes.client, static_cast<DWORD>(buffered.size()));
        }
        CheckF(bytesBuffered, "real writer completed unread bytes before exceptional teardown");
        if (bytesBuffered) {
            std::string oversized(DevToolsFramingMaxLine + 4096, 'x');
            DWORD written = 0;
            WriteFile(pipes.client, oversized.data(), static_cast<DWORD>(oversized.size()), &written, nullptr);
        } else {
            pipes.CloseClient();
        }
    }
    const bool terminal = WaitForSingleObject(thread, 1000) == WAIT_OBJECT_0;
    CheckF(terminal, overflow ? "actual framing exits after real writer hard-cap overflow without peer cleanup"
                             : "actual framing and writer stop without waiting on buffered client bytes");
    if (!terminal) {
        pipes.CloseClient();
        if (WaitForSingleObject(thread, 3000) != WAIT_OBJECT_0) ExitProcess(96);
    }
    CloseHandle(thread);
    CheckF(returned && !escaped && state.unregistered == 1, "real connection unregisters exactly once without an escaping fault");
    if (!overflow && terminal && bytesBuffered) {
        char data[128]{};
        DWORD count = 0;
        const BOOL ok = ReadFile(pipes.client, data, static_cast<DWORD>(buffered.size()), &count, nullptr);
        CheckF(ok && std::string(data, count) == buffered, "completed real-writer bytes survive ordinary close-only teardown");
    }
    if (auto conn = state.connection.load()) {
        DevToolsEvents_EnqueueResponse(conn, L"late retained callback");
        DevToolsEvents_Release(conn);
    }
    CheckF(DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Property) == 0, "real framing releases the writer's domain subscription");
}

}   // namespace

int g_framingFailures = 0;

int RunFramingTests()
{
    std::printf("DevToolsPipeFraming tests (framing-boundary fault injection)\n");
    g_failures = 0;

    TestAFramingFaultAtEveryStageTearsDownExactlyOnce();
    TestRepeatedFramingFaultsLeakNoHandles();
    TestTheConnectionAfterAFramingFaultIsServedNormally();
    TestARefusedRegistrationStillClosesTheHandle();
    TestAnAfterUnregisterFaultStillClosesTheHandle();
    TestANonReadingClientDoesNotPinTeardown();
    TestARegistrationThatThrowsStillClosesTheHandle();
    TestFramingWithRealWriter(false);
    TestFramingWithRealWriter(true);

    g_framingFailures = g_failures;
    return g_failures;
}
