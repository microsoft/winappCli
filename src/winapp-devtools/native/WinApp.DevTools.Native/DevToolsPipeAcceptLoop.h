// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

#pragma once

#include <windows.h>
#include <atomic>
#include <memory>

struct DevToolsPipeAcceptStats {
    std::atomic<long> listening{ 0 };        // instances currently posted and waiting for a client
    std::atomic<long> minListening{ 1 << 30 };
    std::atomic<long> maxInFlight{ 0 };
    std::atomic<long> postFailures{ 0 };     // CreateNamedPipeW refusals
    std::atomic<long> connectFailures{ 0 };  // ConnectNamedPipe refusals, overwhelmingly ERROR_NO_DATA hang-ups
    std::atomic<long> lastConnectError{ 0 };
    std::atomic<long> zeroWindows{ 0 };      // times the posted count reached zero WHILE RUNNING
    std::atomic<long> dispatchFailures{ 0 }; // connections shed because no handler thread could be created
    std::atomic<long> maxAcceptedPerTurn{ 0 }; // most connections harvested in a single loop turn
    std::atomic<long> topUps{ 0 };           // slots restored by the pre-dispatch top-up pass

#ifdef WINAPP_DEVTOOLS_PIPE_FAULT_INJECTION
    std::atomic<long> failNextPosts{ 0 };
    std::atomic<long> failNextDispatches{ 0 };
    std::atomic<long> failNextConnects{ 0 };
    std::atomic<long> failNextEvents{ 0 };
    std::atomic<long> reuseDisconnects{ 0 };

    bool TakeInjectedFault(std::atomic<long>& budget)
    {
        long left = budget.load(std::memory_order_acquire);
        while (left > 0) {
            if (budget.compare_exchange_weak(left, left - 1)) return true;
        }
        return false;
    }
#endif

    // Detached dispatch threads may outlive the accept loop frame, so they share ownership of the counter.
    std::shared_ptr<std::atomic<long>> inFlightCounter = std::make_shared<std::atomic<long>>(0);

    long InFlight() const { return inFlightCounter->load(std::memory_order_acquire); }

    void Listening(long delta)
    {
        const long now = listening.fetch_add(delta, std::memory_order_acq_rel) + delta;
        if (now == 0) zeroWindows.fetch_add(1, std::memory_order_relaxed);
        long seen = minListening.load(std::memory_order_acquire);
        while (now < seen && !minListening.compare_exchange_weak(seen, now)) { }
    }
};

struct DevToolsPipeDispatch {
    HANDLE pipe;
    LPTHREAD_START_ROUTINE handler;
    std::shared_ptr<std::atomic<long>> inFlight;
};

inline bool DevToolsTakeInjectedPostFault(DevToolsPipeAcceptStats* stats)
{
#ifdef WINAPP_DEVTOOLS_PIPE_FAULT_INJECTION
    return stats && stats->TakeInjectedFault(stats->failNextPosts);
#else
    (void)stats;
    return false;
#endif
}

inline bool DevToolsTakeInjectedEventFault(DevToolsPipeAcceptStats* stats)
{
#ifdef WINAPP_DEVTOOLS_PIPE_FAULT_INJECTION
    return stats && stats->TakeInjectedFault(stats->failNextEvents);
#else
    (void)stats;
    return false;
#endif
}

inline bool DevToolsTakeInjectedDispatchFault(DevToolsPipeAcceptStats* stats)
{
#ifdef WINAPP_DEVTOOLS_PIPE_FAULT_INJECTION
    return stats && stats->TakeInjectedFault(stats->failNextDispatches);
#else
    (void)stats;
    return false;
#endif
}

#ifdef WINAPP_DEVTOOLS_PIPE_FAULT_INJECTION
inline std::atomic<long>& DevToolsPipeDispatchThunksInFlight()
{
    static std::atomic<long> count{ 0 };
    return count;
}
#endif

inline void DevToolsPipeDispatchEnter()
{
#ifdef WINAPP_DEVTOOLS_PIPE_FAULT_INJECTION
    DevToolsPipeDispatchThunksInFlight().fetch_add(1, std::memory_order_acq_rel);
#endif
}

inline void DevToolsPipeDispatchExit()
{
#ifdef WINAPP_DEVTOOLS_PIPE_FAULT_INJECTION
    DevToolsPipeDispatchThunksInFlight().fetch_sub(1, std::memory_order_acq_rel);
#endif
}

inline DWORD WINAPI DevToolsPipeDispatchThunk(LPVOID param)
{
    DWORD result = 0;
    {
        DevToolsPipeDispatch* dispatch = static_cast<DevToolsPipeDispatch*>(param);
        HANDLE pipe = dispatch->pipe;
        LPTHREAD_START_ROUTINE handler = dispatch->handler;
        std::shared_ptr<std::atomic<long>> inFlight = dispatch->inFlight;
        delete dispatch;

        // Thread-proc boundary: swallow C++ exceptions so a bad request cannot terminate the host app.
        try {
            result = handler(pipe);
        } catch (...) {
            result = 0;
        }
        inFlight->fetch_sub(1, std::memory_order_acq_rel);
    }
    DevToolsPipeDispatchExit();
    return result;
}


#ifndef WINAPP_DEVTOOLS_LISTENER_POOL_SIZE
#define WINAPP_DEVTOOLS_LISTENER_POOL_SIZE 32
#endif
inline constexpr DWORD DevToolsListenerPoolSize = WINAPP_DEVTOOLS_LISTENER_POOL_SIZE;

// The wait set is every listener plus stopEvent; exceeding MAXIMUM_WAIT_OBJECTS needs a different design.
static_assert(DevToolsListenerPoolSize + 1 <= MAXIMUM_WAIT_OBJECTS,
    "listener pool + stop event must fit one WaitForMultipleObjects call");

// Bound hang-up recycling so a client that repeatedly closes cannot pin the accept thread.
inline constexpr int DevToolsHangUpRecycleBudget = 16;

// Optional diagnostic sink. Receives already-formatted lines; never called on the hot path.
using DevToolsPipeAcceptLog = void (*)(const wchar_t* message);


// Prepost replacements before dispatch and harvest all ready slots so the pipe name never disappears.
inline void DevToolsRunPipeAcceptLoop(
    const wchar_t* pipeName,
    SECURITY_ATTRIBUTES* sa,
    LPTHREAD_START_ROUTINE handler,
    HANDLE stopEvent = nullptr,
    DevToolsPipeAcceptLog log = nullptr,
    DevToolsPipeAcceptStats* stats = nullptr)
{
    struct ListenerSlot {
        HANDLE pipe = INVALID_HANDLE_VALUE;
        HANDLE evt = nullptr;    // manual-reset; created on first post and reused for the life of the loop
        OVERLAPPED ov{};
        bool posted = false;
        bool immediate = false;  // client arrived before ConnectNamedPipe; no overlapped result to collect
    };

    ListenerSlot slots[DevToolsListenerPoolSize];

    auto postListener = [&](ListenerSlot& slot) {
        if (slot.posted) return;
        if (!slot.evt) {
            slot.evt = DevToolsTakeInjectedEventFault(stats)
                ? nullptr
                : CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!slot.evt) {
                if (log) log(L"pipe: CreateEventW failed; slot deferred to the next pass");
                if (stats) stats->postFailures.fetch_add(1, std::memory_order_relaxed);
                return;
            }
        }
        HANDLE h = DevToolsTakeInjectedPostFault(stats)
            ? INVALID_HANDLE_VALUE
            // Overlapped instances let accepts, reads, and writes proceed without serializing behind blocked I/O.
            : CreateNamedPipeW(pipeName, PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                PIPE_UNLIMITED_INSTANCES, 4096, 4096, 0, sa);
        if (h == INVALID_HANDLE_VALUE) {
            if (log) log(L"pipe: CreateNamedPipeW failed");
            if (stats) stats->postFailures.fetch_add(1, std::memory_order_relaxed);
            return;
        }

        auto disconnectForReuse = [stats](HANDLE pipe) {
            DisconnectNamedPipe(pipe);
#ifdef WINAPP_DEVTOOLS_PIPE_FAULT_INJECTION
            if (stats) stats->reuseDisconnects.fetch_add(1, std::memory_order_relaxed);
#else
            (void)stats;
#endif
        };

        for (int attempt = 0; attempt < DevToolsHangUpRecycleBudget; ++attempt) {
            ResetEvent(slot.evt);
            slot.ov = OVERLAPPED{};
            slot.ov.hEvent = slot.evt;
            slot.immediate = false;

#ifdef WINAPP_DEVTOOLS_PIPE_FAULT_INJECTION
            const bool injectHangUp = stats && stats->TakeInjectedFault(stats->failNextConnects);
#else
            const bool injectHangUp = false;
#endif

            BOOL connected = FALSE;
            DWORD ce = ERROR_NO_DATA;
            if (!injectHangUp) {
                connected = ConnectNamedPipe(h, &slot.ov);
                ce = connected ? ERROR_SUCCESS : GetLastError();
            }

            if (connected) {
                slot.immediate = true;          // synchronous success is legal on an overlapped handle
            } else {
                if (ce == ERROR_PIPE_CONNECTED) {
                    slot.immediate = true;
                } else if (ce == ERROR_NO_DATA) {
                    if (stats) {
                        stats->connectFailures.fetch_add(1, std::memory_order_relaxed);
                        stats->lastConnectError.store(static_cast<long>(ce), std::memory_order_relaxed);
                    }
                    disconnectForReuse(h);
                    continue;
                } else if (ce != ERROR_IO_PENDING) {
                    if (stats) {
                        stats->connectFailures.fetch_add(1, std::memory_order_relaxed);
                        stats->lastConnectError.store(static_cast<long>(ce), std::memory_order_relaxed);
                    }
                    CloseHandle(h);
                    return;
                }
            }

            slot.pipe = h;
            slot.posted = true;
            if (stats) stats->Listening(+1);
            if (slot.immediate) SetEvent(slot.evt);
            return;
        }

        CloseHandle(h);
    };

    auto harvest = [&](ListenerSlot& slot, HANDLE* accepted) -> bool {
        DWORD dummy = 0;
        const BOOL connected = slot.immediate ? TRUE : GetOverlappedResult(slot.pipe, &slot.ov, &dummy, FALSE);
        const DWORD ce = connected ? ERROR_SUCCESS : GetLastError();

        HANDLE h = slot.pipe;
        slot.pipe = INVALID_HANDLE_VALUE;
        slot.posted = false;
        slot.immediate = false;
        if (stats) stats->Listening(-1);
        ResetEvent(slot.evt);
        // Replacement happens before dispatch so a burst cannot drain the listener pool to zero.
        postListener(slot);                                  // replacement first, always

        if (!connected && ce != ERROR_PIPE_CONNECTED) { CloseHandle(h); return false; }
        *accepted = h;
        return true;
    };

    bool stopping = false;
    auto inFlight = stats ? stats->inFlightCounter : std::make_shared<std::atomic<long>>(0);
    while (!stopping) {
        if (stopEvent && WaitForSingleObject(stopEvent, 0) == WAIT_OBJECT_0) break;

        HANDLE waits[DevToolsListenerPoolSize + 1];
        DWORD  waitCount = 0;
        for (DWORD i = 0; i < DevToolsListenerPoolSize; ++i) {
            postListener(slots[i]);
            if (slots[i].posted) { waits[waitCount] = slots[i].evt; ++waitCount; }
        }

        const DWORD postedCount = waitCount;
        const DWORD stopIndex = waitCount;
        if (stopEvent) { waits[waitCount] = stopEvent; ++waitCount; }

        if (postedCount == 0) {
            if (log) log(L"pipe: no listener could be posted; backing off");
            const DWORD backoff = stopEvent ? WaitForSingleObject(stopEvent, 20) : WAIT_FAILED;
            if (backoff == WAIT_OBJECT_0) break;
            if (backoff != WAIT_TIMEOUT) Sleep(20);
            continue;
        }

        const DWORD w = WaitForMultipleObjects(waitCount, waits, FALSE, INFINITE);
        if (w < WAIT_OBJECT_0 || w >= WAIT_OBJECT_0 + waitCount) { Sleep(20); continue; }

        const DWORD signalled = w - WAIT_OBJECT_0;
        if (stopEvent && signalled == stopIndex) { stopping = true; break; }

        HANDLE accepted[DevToolsListenerPoolSize];
        DWORD  acceptedCount = 0;
        for (DWORD i = 0; i < DevToolsListenerPoolSize && acceptedCount < DevToolsListenerPoolSize; ++i) {
            if (slots[i].posted && WaitForSingleObject(slots[i].evt, 0) == WAIT_OBJECT_0) {
                if (harvest(slots[i], &accepted[acceptedCount])) ++acceptedCount;
            }
        }
        if (stats) {
            long peakTurn = stats->maxAcceptedPerTurn.load(std::memory_order_acquire);
            while (static_cast<long>(acceptedCount) > peakTurn &&
                   !stats->maxAcceptedPerTurn.compare_exchange_weak(peakTurn, static_cast<long>(acceptedCount))) { }
        }

        for (auto& slot : slots) {
            const bool wasShort = !slot.posted;
            postListener(slot);
            if (wasShort && slot.posted && stats) stats->topUps.fetch_add(1, std::memory_order_relaxed);
        }

        for (DWORD i = 0; i < acceptedCount; ++i) {
            const long nowInFlight = inFlight->fetch_add(1, std::memory_order_acq_rel) + 1;
            if (stats) {
                long peak = stats->maxInFlight.load(std::memory_order_acquire);
                while (nowInFlight > peak && !stats->maxInFlight.compare_exchange_weak(peak, nowInFlight)) { }
            }
            auto* dispatch = new (std::nothrow) DevToolsPipeDispatch{ accepted[i], handler, inFlight };
            const bool injected = DevToolsTakeInjectedDispatchFault(stats);
            DevToolsPipeDispatchEnter();
            HANDLE t = (dispatch && !injected)
                ? CreateThread(nullptr, 0, DevToolsPipeDispatchThunk, dispatch, 0, nullptr)
                : nullptr;
            if (!t) {
                if (log) log(L"pipe: could not dispatch connection; shedding it");
                if (stats) stats->dispatchFailures.fetch_add(1, std::memory_order_relaxed);
                DevToolsPipeDispatchExit();
                delete dispatch;
                inFlight->fetch_sub(1, std::memory_order_acq_rel);
                DisconnectNamedPipe(accepted[i]);
                CloseHandle(accepted[i]);
                continue;
            }
            // The dispatch record owns the pipe after CreateThread succeeds; the accept loop only drops its thread handle.
            CloseHandle(t);                                                 // detached; cleans itself up
        }
    }

    for (int i = 0; i < 500 && inFlight->load(std::memory_order_acquire) > 0; ++i) Sleep(10);

    for (auto& slot : slots) {
        if (slot.posted && slot.pipe != INVALID_HANDLE_VALUE) {
            CancelIoEx(slot.pipe, &slot.ov);
            DWORD cancelled = 0;
            GetOverlappedResult(slot.pipe, &slot.ov, &cancelled, TRUE);
            CloseHandle(slot.pipe);
            slot.pipe = INVALID_HANDLE_VALUE;
            slot.posted = false;
            if (stats) stats->listening.fetch_sub(1, std::memory_order_acq_rel);
        }
        if (slot.evt) { CloseHandle(slot.evt); slot.evt = nullptr; }
    }
}
