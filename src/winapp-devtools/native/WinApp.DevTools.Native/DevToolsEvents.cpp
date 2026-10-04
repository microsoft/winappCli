// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#define _CRT_RAND_S   // rand_s: the CRT's RtlGenRandom-backed source, used for the per-connection owner token

#include "DevToolsEvents.h"

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdlib>
#include <deque>
#include <mutex>
#include <new>
#include <thread>
#include <vector>
#include "DevToolsText.h"

namespace {

struct OutMsg {
    std::string  bytes;                       // the wire line, newline-terminated
    bool         isResponse = false;          // responses are never dropped/coalesced
    std::wstring coalesceKey;                  // keep-newest grouping key (empty for responses)
};

constexpr wchar_t kTreeInvalKey[] = L"\x01tree-inval";

std::string BuildTreeInvalidated(uint64_t droppedEvents) {
    return "{\"jsonrpc\":\"2.0\",\"method\":\"VisualTree.invalidated\",\"params\":{\"origin\":null,"
           "\"reason\":\"overflow\",\"droppedEvents\":" + std::to_string(droppedEvents) + "}}\n";
}

constexpr size_t kSoftCap = 256;
constexpr size_t kHardCap = 4096;

// Owner tokens are unguessable per-connection capabilities; UI ownership is not tied to short request pipes.
std::wstring MintOwnerToken() {
    std::wstring token;
    token.reserve(32);
    for (int i = 0; i < 4; ++i) {
        unsigned int value = 0;
        if (rand_s(&value) != 0) value = (unsigned int)GetTickCount() ^ (unsigned int)(uintptr_t)&token;
        wchar_t part[9]{};
        swprintf_s(part, L"%08x", value);
        token += part;
    }
    return token;
}

} // namespace

#ifdef WINAPP_DEVTOOLS_EVENTS_FAULT_INJECTION
DevToolsEventsTestHook& DevToolsEventsScheduleHook() { static DevToolsEventsTestHook hook = nullptr; return hook; }
static void DevToolsEventsObserve(DevToolsEventsTestStage stage, HANDLE pipe) {
    if (auto hook = DevToolsEventsScheduleHook()) hook(stage, pipe);
}
#else
#define DevToolsEventsObserve(stage, pipe) ((void)0)
#endif

struct DevToolsConn {
    std::atomic<uint32_t>   refs{1};             // read-loop owner + asynchronous UI response callbacks
    uint64_t                id = 0;                // stable per-connection id (F6): returned as connectionId in
    std::wstring            ownerToken;            // unguessable token this connection presents to own UI state
    std::atomic<bool>       negotiated{false};     // DevTools.negotiate completed: only then is ownerToken claimable
    HANDLE                  pipe = INVALID_HANDLE_VALUE;
    HANDLE                  writeEvent = nullptr;  // completion event for this connection's overlapped writes
    std::atomic<uint32_t>   domains{0};        // OR of DevToolsDomainBit values this client enabled
    std::mutex              m;
    std::condition_variable cv;
    std::deque<OutMsg>      queue;
    bool                    stop = false;      // terminal: no new writes or enqueues
    uint64_t                droppedTreeDeltas = 0; // precise tree deltas missed since last consistent (guarded by m);
    std::thread             writer;

    // Called under m, shared with overlapped write admission. Never closes the read loop's handle.
    void StopLocked(bool disconnect) {
        if (stop) return;
        stop = true;
        queue.clear();
        if (pipe != INVALID_HANDLE_VALUE) {
            CancelIoEx(pipe, nullptr);
            if (disconnect) DisconnectNamedPipe(pipe);
        }
        cv.notify_all();
    }

    // One writer drains this connection's response/event queue in FIFO order.
    // The single writer serializes responses and events so JSON-RPC lines cannot interleave on the pipe.
    void WriterLoop() {
        DevToolsEventsObserve(DevToolsEventsTestStage::BeforeWriterLoop, pipe);
        for (;;) {
            OutMsg msg;
            {
                std::unique_lock<std::mutex> lk(m);
                cv.wait(lk, [this] { return stop || !queue.empty(); });
                if (stop) return;
                msg = std::move(queue.front());
                queue.pop_front();
                if (msg.coalesceKey == kTreeInvalKey) droppedTreeDeltas = 0;
            }
            DevToolsEventsObserve(DevToolsEventsTestStage::BeforeWrite, pipe);
            OVERLAPPED ov{};
            ResetEvent(writeEvent);
            ov.hEvent = writeEvent;
            DWORD wr = 0;
            BOOL ok;
            bool pending;
            {
                std::lock_guard<std::mutex> lk(m);
                if (stop) return;
                ok = WriteFile(pipe, msg.bytes.data(), (DWORD)msg.bytes.size(), nullptr, &ov);
                pending = !ok && GetLastError() == ERROR_IO_PENDING;
            }
            if (pending) {
                ok = GetOverlappedResult(pipe, &ov, &wr, TRUE);
            }
            if (!ok) {
                std::lock_guard<std::mutex> lk(m);
                StopLocked(true);
                return;
            }
        }
    }
};

static SRWLOCK           g_connLock = SRWLOCK_INIT;
static std::vector<DevToolsConn*> g_conns;

struct DevToolsConnLockExclusive {
    DevToolsConnLockExclusive() { AcquireSRWLockExclusive(&g_connLock); }
    ~DevToolsConnLockExclusive() { ReleaseSRWLockExclusive(&g_connLock); }
    DevToolsConnLockExclusive(const DevToolsConnLockExclusive&) = delete;
    DevToolsConnLockExclusive& operator=(const DevToolsConnLockExclusive&) = delete;
};
struct DevToolsConnLockShared {
    DevToolsConnLockShared() { AcquireSRWLockShared(&g_connLock); }
    ~DevToolsConnLockShared() { ReleaseSRWLockShared(&g_connLock); }
    DevToolsConnLockShared(const DevToolsConnLockShared&) = delete;
    DevToolsConnLockShared& operator=(const DevToolsConnLockShared&) = delete;
};
static uint32_t g_domainSubscribers[32]{};
static std::atomic<uint64_t> g_nextConnId{1};

#ifdef WINAPP_DEVTOOLS_EVENTS_FAULT_INJECTION
DevToolsEventsStage& DevToolsEventsFaultStage()  { static DevToolsEventsStage stage = DevToolsEventsStage::OwnerToken; return stage; }
long&           DevToolsEventsFaultBudget() { static long budget = 0; return budget; }
long&           DevToolsEventsFaultsTaken() { static long taken = 0; return taken; }
static void DevToolsEventsMaybeThrow(DevToolsEventsStage stage)
{
    if (stage != DevToolsEventsFaultStage() || DevToolsEventsFaultBudget() <= 0) return;
    --DevToolsEventsFaultBudget();
    ++DevToolsEventsFaultsTaken();
    throw std::bad_alloc();
}
#else
#define DevToolsEventsMaybeThrow(stage) ((void)0)
#endif

static unsigned int DomainIndex(uint32_t bit) {
    unsigned int index = 0;
    while (bit > 1) { bit >>= 1; ++index; }
    return index;
}

DevToolsConn* DevToolsEvents_Register(HANDLE pipe) {
    auto* c = new (std::nothrow) DevToolsConn();
    if (!c) return nullptr;
    c->id = g_nextConnId.fetch_add(1, std::memory_order_relaxed);
    try {
        DevToolsEventsMaybeThrow(DevToolsEventsStage::OwnerToken);
        c->ownerToken = MintOwnerToken();
    } catch (...) {
        delete c;
        return nullptr;
    }
    c->pipe = pipe;
    c->writeEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!c->writeEvent) { delete c; return nullptr; }
    try {
        // A writer fault disconnects only this client; exceptions must not escape into the injected host app.
        c->writer = std::thread([c] {
            try { c->WriterLoop(); }
            catch (...) {
                OutputDebugStringW(L"winapp: event writer fault; disconnecting client\n");
                std::lock_guard<std::mutex> lk(c->m);
                c->StopLocked(true);
            }
        });
    } catch (...) {
        CloseHandle(c->writeEvent);
        delete c;
        return nullptr;
    }
    try {
        DevToolsConnLockExclusive lk;
        DevToolsEventsMaybeThrow(DevToolsEventsStage::RegistryInsert);
        g_conns.push_back(c);
    } catch (...) {
        {
            std::lock_guard<std::mutex> stopLock(c->m);
            c->StopLocked(false);
        }
        c->cv.notify_all();
        if (c->writer.joinable()) c->writer.join();
        CloseHandle(c->writeEvent);
        delete c;
        return nullptr;
    }
    return c;
}

void DevToolsEvents_Retain(DevToolsConn* c) {
    if (c) c->refs.fetch_add(1, std::memory_order_relaxed);
}

void DevToolsEvents_Release(DevToolsConn* c) {
    if (c && c->refs.fetch_sub(1, std::memory_order_acq_rel) == 1) delete c;
}

// Remove from the registry before joining the writer so no new event can race the handle close.
uint32_t DevToolsEvents_Unregister(DevToolsConn* c) {
    if (!c) return 0;
    uint32_t lastDisabled = 0;
    {
        DevToolsConnLockExclusive lk;
        auto found = std::find(g_conns.begin(), g_conns.end(), c);
        if (found != g_conns.end()) {
            const uint32_t domains = c->domains.exchange(0, std::memory_order_relaxed);
            for (uint32_t bit = 1; bit != 0; bit <<= 1) {
                if ((domains & bit) == 0) continue;
                uint32_t& count = g_domainSubscribers[DomainIndex(bit)];
                if (count > 0 && --count == 0) lastDisabled |= bit;
            }
            g_conns.erase(found);
        }
    }
    {
        std::lock_guard<std::mutex> lk(c->m);
        c->StopLocked(false);
    }
    DevToolsEventsObserve(DevToolsEventsTestStage::AfterCancel, c->pipe);
    if (c->writer.joinable()) c->writer.join();
    if (c->writeEvent) CloseHandle(c->writeEvent);
    c->writeEvent = nullptr;
    DevToolsEvents_Release(c);
    return lastDisabled;
}

// Coalescible events may be dropped under pressure; responses are preserved until the hard cap disconnects.
static void Enqueue(DevToolsConn* c, std::string bytes, bool isResponse, DevToolsCoalesce policy, const std::wstring& key) {
    bool wake = false;
    {
        std::lock_guard<std::mutex> lk(c->m);
        if (c->stop) return;                           // client already disconnected

        if (!isResponse && policy == DevToolsCoalesce_KeepNewest && !key.empty()) {
            for (auto& q : c->queue) {
                if (!q.isResponse && q.coalesceKey == key) { q.bytes = std::move(bytes); wake = true; break; }
            }
            if (wake) { c->cv.notify_one(); return; }
        }

        if (!isResponse && c->queue.size() >= kSoftCap) return; // drop this event (keep-newest already tried)

        if (c->queue.size() >= kHardCap) {
            c->StopLocked(true);
            return;
        }

        c->queue.push_back(OutMsg{ std::move(bytes), isResponse, key });
        wake = true;
    }
    if (wake) c->cv.notify_one();
}

void DevToolsEvents_EnqueueResponse(DevToolsConn* c, const std::wstring& line) {
    if (!c || line.empty()) return;
    Enqueue(c, DevToolsToUtf8(line) + "\n", /*isResponse*/ true, DevToolsCoalesce_None, L"");
}

uint32_t DevToolsEvents_DomainBit(const std::wstring& domain) {
    if (domain == L"VisualTree") return DevToolsDomain_VisualTree;
    if (domain == L"Property")   return DevToolsDomain_Property;
    if (domain == L"Selection")  return DevToolsDomain_Selection;
    if (domain == L"Focus")      return DevToolsDomain_Focus;
    if (domain == L"Overlay")    return DevToolsDomain_Overlay;
    return 0;
}

DevToolsDomainChange DevToolsEvents_SetDomain(DevToolsConn* c, uint32_t bit, bool on) {
    if (!c || bit == 0 || (bit & (bit - 1)) != 0) return DevToolsDomain_ConnectionGone;
    DevToolsDomainChange change = DevToolsDomain_NoChange;
    DevToolsConnLockExclusive lk;
    if (std::find(g_conns.begin(), g_conns.end(), c) == g_conns.end()) {
        change = DevToolsDomain_ConnectionGone;
    } else {
        const uint32_t domains = c->domains.load(std::memory_order_relaxed);
        const bool wasOn = (domains & bit) != 0;
        if (wasOn != on) {
            uint32_t& count = g_domainSubscribers[DomainIndex(bit)];
            if (on) {
                c->domains.store(domains | bit, std::memory_order_relaxed);
                if (++count == 1) change = DevToolsDomain_FirstEnabled;
            } else {
                c->domains.store(domains & ~bit, std::memory_order_relaxed);
                if (count > 0 && --count == 0) change = DevToolsDomain_LastDisabled;
            }
        }
    }
    return change;
}

bool DevToolsEvents_DomainEnabled(DevToolsConn* c, uint32_t bit) {
    return c && bit != 0 && (c->domains.load(std::memory_order_relaxed) & bit) != 0;
}

uint32_t DevToolsEvents_DomainSubscriberCount(uint32_t bit) {
    if (bit == 0 || (bit & (bit - 1)) != 0) return 0;
    DevToolsConnLockShared lk;
    const uint32_t count = g_domainSubscribers[DomainIndex(bit)];
    return count;
}

uint64_t DevToolsEvents_ConnId(DevToolsConn* c) {
    return c ? c->id : 0;
}

std::wstring DevToolsEvents_MarkNegotiated(DevToolsConn* c) {
    if (!c) return std::wstring();
    c->negotiated.store(true, std::memory_order_release);
    return c->ownerToken;   // copied: the connection can be released out from under the caller
}

uint64_t DevToolsEvents_ResolveOwnerToken(const std::wstring& token) {
    if (token.empty()) return 0;
    uint64_t id = 0;
    DevToolsConnLockShared lk;
    for (DevToolsConn* c : g_conns) {
        if (c->negotiated.load(std::memory_order_acquire) && c->ownerToken == token) { id = c->id; break; }
    }
    return id;
}

bool DevToolsEvents_IsOwnerLive(uint64_t connId) {
    if (connId == 0) return false;   // the "attribute to nobody" sentinel is not a session
    bool live = false;
    DevToolsConnLockShared lk;
    for (DevToolsConn* c : g_conns) {
        if (c->id == connId && c->negotiated.load(std::memory_order_acquire)) { live = true; break; }
    }
    return live;
}

void DevToolsEvents_Broadcast(uint32_t bit, const std::wstring& eventLine, DevToolsCoalesce policy, const std::wstring& key) {
    if (bit == 0 || eventLine.empty()) return;
    std::string bytes = DevToolsToUtf8(eventLine) + "\n";
    DevToolsConnLockShared lk;
    for (DevToolsConn* c : g_conns) {
        if ((c->domains.load(std::memory_order_relaxed) & bit) == 0) continue; // not subscribed
        DevToolsEventsMaybeThrow(DevToolsEventsStage::BroadcastEnqueue);
        Enqueue(c, bytes, /*isResponse*/ false, policy, key);
    }
}

static void EnqueueTreeDelta(DevToolsConn* c, const std::string& deltaBytes) {
    bool wake = false;
    {
        std::lock_guard<std::mutex> lk(c->m);
        if (c->stop) return;                            // client already disconnected
        if (c->droppedTreeDeltas == 0 && c->queue.size() < kSoftCap) {
            c->queue.push_back(OutMsg{ deltaBytes, /*isResponse*/ false, std::wstring() });
            wake = true;
        } else {
            c->droppedTreeDeltas++;
            std::string inval = BuildTreeInvalidated(c->droppedTreeDeltas);
            for (auto& q : c->queue) {
                if (!q.isResponse && q.coalesceKey == kTreeInvalKey) { q.bytes = std::move(inval); wake = true; break; }
            }
            if (!wake) {
                if (c->queue.size() >= kHardCap) {
                    c->StopLocked(true);
                    return;
                }
                c->queue.push_back(OutMsg{ std::move(inval), /*isResponse*/ false, kTreeInvalKey });
                wake = true;
            }
        }
    }
    if (wake) c->cv.notify_one();
}

void DevToolsEvents_BroadcastTreeDelta(const std::wstring& deltaLine) {
    if (deltaLine.empty()) return;
    std::string bytes = DevToolsToUtf8(deltaLine) + "\n";
    DevToolsConnLockShared lk;
    for (DevToolsConn* c : g_conns) {
        if ((c->domains.load(std::memory_order_relaxed) & DevToolsDomain_VisualTree) == 0) continue; // not subscribed
        EnqueueTreeDelta(c, bytes);
    }
}
