// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

#pragma once

#include <windows.h>
#include <new>
#include "DevToolsText.h"
#include <string>

struct DevToolsFramingHost {
    void* user = nullptr;

    void* (*RegisterConn)(HANDLE pipe, void* user) = nullptr;
    // Identity of a registered connection, read BEFORE unregistering because unregistering may free it.
    unsigned long long (*ConnId)(void* conn, void* user) = nullptr;
    // Stops and JOINS the writer thread, returning the domains this connection was last subscribed to.
    unsigned (*UnregisterConn)(void* conn, void* user) = nullptr;
    // Client identity/integrity check. Runs once, after the first successful read.
    bool (*VerifyClient)(HANDLE pipe, void* user) = nullptr;
    std::wstring (*DispatchLine)(void* conn, const std::wstring& line, void* user) = nullptr;
    // Builds the response a dispatch fault becomes; `oom` distinguishes std::bad_alloc from anything else.
    std::wstring (*DispatchFault)(bool oom, void* user) = nullptr;
    // Hands a response to the writer thread's FIFO queue.
    void (*EnqueueResponse)(void* conn, const std::wstring& resp, void* user) = nullptr;
    void (*AfterUnregister)(unsigned long long connId, unsigned lastDisabled, void* user) = nullptr;
    void (*Log)(const wchar_t* message, void* user) = nullptr;
};

enum class DevToolsFramingStage { Append, Split, Convert, Dispatch, Enqueue, AfterUnregister };

#ifdef WINAPP_DEVTOOLS_PIPE_FAULT_INJECTION
inline DevToolsFramingStage& DevToolsFramingFaultStage()
{
    static DevToolsFramingStage stage = DevToolsFramingStage::Append;
    return stage;
}
inline long& DevToolsFramingFaultBudget()
{
    static long budget = 0;
    return budget;
}
inline long& DevToolsFramingFaultsTaken()
{
    static long taken = 0;
    return taken;
}
#endif

inline void DevToolsFramingMaybeThrow(DevToolsFramingStage stage)
{
#ifdef WINAPP_DEVTOOLS_PIPE_FAULT_INJECTION
    if (DevToolsFramingFaultBudget() > 0 && DevToolsFramingFaultStage() == stage) {
        --DevToolsFramingFaultBudget();
        ++DevToolsFramingFaultsTaken();
        throw std::bad_alloc();
    }
#else
    (void)stage;
#endif
}

inline constexpr size_t DevToolsFramingMaxLine = 64 * 1024;

inline void DevToolsRunPipeFraming(HANDLE h, const DevToolsFramingHost& host)
{
    void* conn = nullptr;
    try {
        // RegisterConn may throw before the framing boundary; h is still owned here and must be refused.
        conn = host.RegisterConn(h, host.user);
    } catch (...) {
        conn = nullptr;
    }
    if (!conn) {
        host.Log(L"pipe: could not register connection (writer thread); dropping client", host.user);
        DisconnectNamedPipe(h); CloseHandle(h);
        return;
    }

    // The pipe handle is overlapped so the reader thread and writer thread do not serialize each other.
    std::string acc; char buf[1024]; DWORD rd = 0;
    HANDLE readEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!readEvent) {
        host.UnregisterConn(conn, host.user);
        DisconnectNamedPipe(h); CloseHandle(h);
        return;
    }

    // Impersonation requires a first read; no framed request is dispatched until the client is verified.
    bool verifiedClient = false;

    try {
        // No-throw boundary: a framing fault drops this client and still runs teardown.
        for (;;) {
            OVERLAPPED ov{};
            ResetEvent(readEvent);
            ov.hEvent = readEvent;
            BOOL ok = ReadFile(h, buf, sizeof(buf), nullptr, &ov);
            if (!ok && GetLastError() != ERROR_IO_PENDING) break;         // real error (client closed / broken)
            if (!GetOverlappedResult(h, &ov, &rd, TRUE) || rd == 0) break; // wait for the read; 0 = clean EOF
            if (!verifiedClient) {
                if (!host.VerifyClient(h, host.user)) {
                    host.Log(L"pipe: refused a connection that failed the identity/integrity check", host.user);
                    break; // same disconnect/cleanup path as any other terminated connection
                }
                verifiedClient = true;
            }
            DevToolsFramingMaybeThrow(DevToolsFramingStage::Append);
            acc.append(buf, rd);
            size_t nl;
            while ((nl = acc.find('\n')) != std::string::npos) {
                DevToolsFramingMaybeThrow(DevToolsFramingStage::Split);
                std::string lineU = acc.substr(0, nl); acc.erase(0, nl + 1);
                if (!lineU.empty() && lineU.back() == '\r') lineU.pop_back();
                if (lineU.empty()) continue;                      // tolerate blank keep-alive lines
                DevToolsFramingMaybeThrow(DevToolsFramingStage::Convert);
                std::wstring wline = DevToolsFromUtf8(lineU);
                std::wstring resp;
                try {
                    DevToolsFramingMaybeThrow(DevToolsFramingStage::Dispatch);
                    resp = host.DispatchLine(conn, wline, host.user);
                }
                catch (const std::bad_alloc&) { resp = host.DispatchFault(true, host.user); }
                catch (...)                   { resp = host.DispatchFault(false, host.user); }
                if (resp.empty()) continue;                       // notification: no reply written
                DevToolsFramingMaybeThrow(DevToolsFramingStage::Enqueue);
                // Queue through the writer thread so responses cannot interleave with concurrent events.
                host.EnqueueResponse(conn, resp, host.user);
            }
            if (acc.size() > DevToolsFramingMaxLine) {
                host.Log(L"pipe: line exceeds 64KB without newline; dropping client", host.user);
                break;
            }
        }
    }
    catch (const std::bad_alloc&) { host.Log(L"pipe: out of memory framing a client's bytes; dropping client", host.user); }
    catch (...)                   { host.Log(L"pipe: unexpected fault framing a client's bytes; dropping client", host.user); }

    CloseHandle(readEvent);
    const unsigned long long releasingConn = host.ConnId(conn, host.user);
    // Unregister first: it joins the writer and prevents new state under a connection being released.
    const unsigned lastDisabled = host.UnregisterConn(conn, host.user);
    try {
        DevToolsFramingMaybeThrow(DevToolsFramingStage::AfterUnregister);
        host.AfterUnregister(releasingConn, lastDisabled, host.user);
    }
    catch (...) { host.Log(L"pipe: fault releasing connection state; continuing teardown", host.user); }
    // Do not flush or disconnect: flush can block forever, while disconnect can discard completed replies.
    CloseHandle(h);
}
