// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for DevToolsBindingRelay -- the tap's client for its own process's managed agent.
//
// What is covered here is the part that can be wrong SILENTLY. The relay's frame has to match, byte for byte,
// what WinApp.DevTools.Managed's BinaryReader expects: a versioned header, a length-prefixed COM packet, then length-prefixed
// UTF-8 strings. Get any of that wrong by one byte and the agent does not fail loudly -- it blocks reading a
// length that never arrives, the relay times out, and the row renders "diagnosis unavailable", which looks
// exactly like an app that legitimately has no managed agent. A wire-format bug and a C++ app are
// indistinguishable at the UI, so the wire format is pinned here instead.
//
// The operation allowlist must refuse all commands outside managed binding inspection.
//
// Like the other suites here, each case carries a positive control: it first shows the wrong construction
// really does produce a different frame, so a suite that has stopped discriminating fails rather than passing
// vacuously.
//
// Build/run: scripts/test-native-units.ps1 (also invoked by src/winapp-devtools/build-devtools.ps1).

#include <windows.h>
#include "DevToolsBindingRelay.h"

#include <cstdio>
#include <string>
#include <vector>
#include <atomic>
#include <thread>
#include <sstream>
#include <wrl/client.h>
#include <stdexcept>

struct BindingFixture;
extern "C" HRESULT BindingFixtureCreate(BindingFixture**);
extern "C" HRESULT BindingFixtureDestroy(BindingFixture*);
extern "C" HRESULT BindingFixtureOwner(BindingFixture*, IAgileReference**);
extern "C" HRESULT BindingFixtureIdentity(BindingFixture*, IAgileReference*);
extern "C" void BindingFixtureCounts(BindingFixture*, ULONG*, ULONG*, ULONG*);
extern "C" void BindingFixtureFailPacketCopy(bool);

static int g_failures = 0;

static int ForeignPeer(DWORD target, const std::wstring& readyName)
{
    const auto name = L"\\\\.\\pipe\\winapp-devtools-binding-" + std::to_wstring(target);
    HANDLE pipe = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 4096, 4096, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) return 10;
    HANDLE ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, readyName.c_str());
    if (!ready) { CloseHandle(pipe); return 11; }
    HANDLE accept = OpenEventW(SYNCHRONIZE, FALSE, (readyName + L"-accept").c_str());
    if (!accept) { CloseHandle(ready); CloseHandle(pipe); return 15; }
    SetEvent(ready);
    CloseHandle(ready);
    const DWORD released = WaitForSingleObject(accept, 5000);
    CloseHandle(accept);
    if (released != WAIT_OBJECT_0) { CloseHandle(pipe); return 16; }
    if (!ConnectNamedPipe(pipe, nullptr)) {
        const DWORD error = GetLastError();
        if (error != ERROR_PIPE_CONNECTED && error != ERROR_NO_DATA) {
            CloseHandle(pipe);
            return 12;
        }
    }
    char frame[4096]{};
    DWORD received = 0;
    const BOOL read = ReadFile(pipe, frame, sizeof(frame), &received, nullptr);
    const DWORD error = read ? ERROR_SUCCESS : GetLastError();
    if (!read && error != ERROR_BROKEN_PIPE && error != ERROR_NO_DATA) {
        CloseHandle(pipe);
        return 17;
    }
    if (received) {
        const char forged[] = "{\"state\":\"ok\",\"peer\":\"foreign\"}\n";
        DWORD written = 0;
        WriteFile(pipe, forged, sizeof(forged) - 1, &written, nullptr);
        FlushFileBuffers(pipe);
    }
    DisconnectNamedPipe(pipe);
    CloseHandle(pipe);
    return received ? 1 : 0;
}

// The shared test executable is also the owned foreign peer; it must not run any suites in peer mode.
static const int g_peerMode = [] {
    const auto marker = wcsstr(GetCommandLineW(), L"--winapp-devtools-relay-peer ");
    if (marker) {
        std::wistringstream args(marker + wcslen(L"--winapp-devtools-relay-peer "));
        DWORD target = 0;
        std::wstring ready;
        if (!(args >> target >> ready)) ExitProcess(13);
        ExitProcess(static_cast<UINT>(ForeignPeer(target, ready)));
    }
    return 0;
}();

static void Check(bool cond, const char* what)
{
    if (!cond) { ++g_failures; std::printf("  FAIL  %s\n", what); }
    else       {              std::printf("  ok    %s\n", what); }
}

static void CheckEqI(long long got, long long want, const char* what)
{
    if (got != want) { ++g_failures; std::printf("  FAIL  %s (want %lld, got %lld)\n", what, want, got); }
    else             {              std::printf("  ok    %s (%lld)\n", what, got); }
}

// Decode the frame with the READER's rules rather than reusing the writer's, and BOUNDS-CHECK every read: a
// frame that is short by a byte is exactly the bug this suite exists to catch, and a decoder that walks off
// the end would report it as an access violation instead of as a named assertion.
static bool InBounds(const std::vector<char>& f, size_t at, size_t need, const char* what)
{
    if (at + need <= f.size()) return true;
    ++g_failures;
    std::printf("  FAIL  %s (frame is %zu bytes, needed %zu)\n", what, f.size(), at + need);
    return false;
}

static int ReadI32(const std::vector<char>& f, size_t at)
{
    if (!InBounds(f, at, 4, "reading a length prefix")) return 0;
    return (int)((unsigned char)f[at]) | ((int)((unsigned char)f[at + 1]) << 8) |
           ((int)((unsigned char)f[at + 2]) << 16) | ((int)((unsigned char)f[at + 3]) << 24);
}

static std::string ReadStr(const std::vector<char>& f, size_t at, int len)
{
    if (len < 0 || !InBounds(f, at, (size_t)len, "reading a string field")) return std::string();
    return std::string(f.begin() + (ptrdiff_t)at, f.begin() + (ptrdiff_t)at + len);
}

static void Test_FrameMatchesWhatTheAgentReads()
{
    std::printf("the BINDING frame is laid out exactly as the agent's reader consumes it\n");

    const std::vector<char> f =
        DevToolsBindingRelay_BuildBindingFrame({'C','O','M'}, L"diagnose", L"Text", L"{x:Bind Vm.Title, Mode=OneWay}");

    // Header. The agent's ReadLine stops at '\n', so the newline is part of the contract, not formatting.
    Check(f.size() > 9 && std::string(f.begin(), f.begin() + 9) == "BINDING2\n",
          "the frame opens with the versioned ASCII header");

    size_t at = 9;
    CheckEqI(ReadI32(f, at), 3, "the COM packet has a bounded length");
    at += 4;
    Check(ReadStr(f, at, 3) == "COM", "COM bytes follow the length, not an address");
    at += 3;

    int opLen = ReadI32(f, at); at += 4;
    CheckEqI(opLen, 8, "the op is length-prefixed");
    Check(ReadStr(f, at, opLen) == "diagnose", "the op is UTF-8 with no terminator");
    at += (size_t)opLen;

    int propLen = ReadI32(f, at); at += 4;
    Check(ReadStr(f, at, propLen) == "Text", "the property name follows the op");
    at += (size_t)propLen;

    int authLen = ReadI32(f, at); at += 4;
    Check(ReadStr(f, at, authLen) == "{x:Bind Vm.Title, Mode=OneWay}",
          "the authored markup follows the property name");
    at += (size_t)authLen;

    CheckEqI((long long)at, (long long)f.size(), "the frame ends exactly after the last field");
}

static void Test_EmptyAuthoredIsStillAField()
{
    std::printf("an empty authored expression is sent as a zero-length field, not omitted\n");
    // A live {Binding} needs no authored markup, so this is the COMMON case. Omitting the field entirely
    // would leave the agent's reader waiting on a length that never arrives -- a hang, not an error.
    const std::vector<char> f = DevToolsBindingRelay_BuildBindingFrame({'x'}, L"capture", L"Text", L"");
    size_t at = 9 + 4 + 1;
    int opLen = ReadI32(f, at); at += 4 + (size_t)opLen;
    int propLen = ReadI32(f, at); at += 4 + (size_t)propLen;
    CheckEqI(ReadI32(f, at), 0, "the authored field is present with length 0");
    CheckEqI((long long)(at + 4), (long long)f.size(), "and nothing follows it");
}

static void Test_NonAsciiSurvivesAsUtf8()
{
    std::printf("a non-ASCII path is encoded as UTF-8, not as narrowed bytes\n");
    // A property path can contain non-ASCII (a converter or a view-model member in a localized codebase).
    // Narrowing wide chars byte-wise would corrupt it and the diagnosis would name the wrong member.
    const std::vector<char> f = DevToolsBindingRelay_BuildBindingFrame({'x'}, L"diagnose", L"Text", L"{x:Bind Vm.T\u00eftle}");
    size_t at = 9 + 4 + 1;
    int opLen = ReadI32(f, at); at += 4 + (size_t)opLen;
    int propLen = ReadI32(f, at); at += 4 + (size_t)propLen;
    int authLen = ReadI32(f, at); at += 4;
    const std::string authored = ReadStr(f, at, authLen);
    // Positive control: a byte-narrowing encoder would emit ONE byte for U+00EF, so the length would be one
    // short. If this control ever stops holding, the assertion below has stopped discriminating.
    Check(authLen == (int)std::string("{x:Bind Vm.Title}").size() + 1,
          "control: the UTF-8 form is one byte longer than the byte-narrowed form would be");
    Check(authored.find("\xC3\xAF") != std::string::npos, "U+00EF is encoded as its two UTF-8 bytes");
}

static void Test_OnlyBindingOpsAreSendable()
{
    std::printf("the relay can only ever send binding ops -- this is the channel's scope boundary\n");
    Check(DevToolsBindingRelay_IsAllowedOp(L"diagnose"), "diagnose is allowed");
    Check(DevToolsBindingRelay_IsAllowedOp(L"capture"),  "capture is allowed");
    Check(DevToolsBindingRelay_IsAllowedOp(L"restore"),  "restore is allowed");
    Check(DevToolsBindingRelay_IsAllowedOp(L"restoreconfirmed"), "explicit owner-wide restore is allowed");
    Check(DevToolsBindingRelay_IsAllowedOp(L"clear"),    "clear is allowed");
    Check(DevToolsBindingRelay_IsAllowedOp(L"writesource"), "writesource is allowed");

    // Native DevToolsBindingInstall owns bind; the managed relay must not accept it.
    Check(!DevToolsBindingRelay_IsAllowedOp(L"bind"),
          "bind is not sent to the agent -- it is installed natively");

    // Removed watch commands cannot become binding operations.
    for (const wchar_t* forbidden : { L"APPLY", L"WIRE", L"RECONCILE", L"RELOAD", L"SETRES", L"JOURNALPROP",
                                      L"MOVE", L"SNAPSHOT", L"RESTORE", L"REBIND", L"PING" }) {
        Check(!DevToolsBindingRelay_IsAllowedOp(forbidden), "an enc verb is not a sendable binding op");
    }
    Check(!DevToolsBindingRelay_IsAllowedOp(L""), "an empty op is rejected");
    // Case matters: the allow-list is exact, so a near-miss cannot slip through on a case fold.
    Check(!DevToolsBindingRelay_IsAllowedOp(L"Diagnose"), "the allow-list is case-exact");
}

static void Test_UnavailableIsAlwaysWellFormedAndCarriesAReason()
{
    std::printf("the unavailable answer is a real diagnosis object, never an empty row\n");
    // This shape is load-bearing: a bound row that renders NOTHING reads as a clean bill of health, which is
    // the defect class this feature exists to stop. Every failure path must produce a renderable reason.
    const std::wstring j = DevToolsBindingRelay_UnavailableJson(L"this app has no managed DevTools agent");
    Check(j.rfind(L"{\"state\":\"unavailable\"", 0) == 0, "it declares the unavailable state first");
    Check(j.find(L"\"reason\":\"this app has no managed DevTools agent\"") != std::wstring::npos,
          "it carries the reason verbatim");
    Check(!j.empty() && j.back() == L'}', "it is a closed object");

    // A reason containing a quote or a backslash must not break out of the JSON string. Unescaped, this
    // would produce a document the panel cannot parse -- and an unparseable answer renders as no answer.
    const std::wstring tricky = DevToolsBindingRelay_UnavailableJson(L"path \"C:\\x\" is odd");
    Check(tricky.find(L"\\\"C:\\\\x\\\"") != std::wstring::npos, "quotes and backslashes in the reason are escaped");
    Check(tricky.back() == L'}', "the escaped form is still a closed object");
}

static void Test_NoAgentReasonDoesNotConflateTheTwoCases()
{
    std::printf("the no-agent reason distinguishes a missing startup host from no CLR\n");

    // A CLR process needs its managed binding host loaded before startup.
    const std::wstring managed = DevToolsBindingRelay_NoAgentReason(DevToolsAppRuntime::Clr);
    Check(managed.find(L"DOTNET_STARTUP_HOOKS") != std::wstring::npos, "the .NET case identifies the startup prerequisite");
    Check(managed.find(L"before app startup") != std::wstring::npos, "the prerequisite must precede startup");
    // Do not recommend a removed watch command.
    Check(managed.find(L"--watch") == std::wstring::npos, "no unavailable watch command is recommended");

    // The C++/WinRT case. There is no CLR to load the agent into, so no launch option would help. Naming one
    // here would be a lie, and a wrong instruction is worse than the vague message it replaces.
    const std::wstring native = DevToolsBindingRelay_NoAgentReason(DevToolsAppRuntime::None);
    Check(native.find(L"--watch") == std::wstring::npos, "the native case does NOT suggest a flag that cannot help");
    Check(native.find(L"winapp run") == std::wstring::npos, "and does not hand out a command either");
    Check(native.find(L"any launch option") != std::wstring::npos,
          "it says plainly that no launch would change this");

    // Native AOT has a .NET runtime; it just cannot host the managed agent.
    const std::wstring aot = DevToolsBindingRelay_NoAgentReason(DevToolsAppRuntime::NativeAot);
    Check(aot.find(L"Native AOT") != std::wstring::npos, "the AOT case names Native AOT");
    Check(aot.find(L"no .NET runtime") == std::wstring::npos, "and does not claim the app has no .NET runtime");
    Check(aot.find(L"Native binding path walking remains available") != std::wstring::npos, "and keeps the fallback");

    // Both are real sentences, not tokens: the row renders this as its subtitle and half an explanation
    // explains nothing.
    Check(managed != native && aot != native && aot != managed, "the three cases produce different text");
    Check(managed.size() > 40 && native.size() > 40 && aot.size() > 40, "all are sentences a developer can read");

    // And both survive the JSON wrapper they are actually delivered through -- an em dash and a quoted
    // command are exactly the characters that break a hand-rolled serializer.
    for (const std::wstring& r : { managed, native, aot }) {
        const std::wstring j = DevToolsBindingRelay_UnavailableJson(r);
        Check(j.rfind(L"{\"state\":\"unavailable\"", 0) == 0, "...wraps as an unavailable diagnosis");
        Check(j.back() == L'}', "...and is a closed object");
    }
}

static void Test_ConnectedReplyDeadline(bool partial, bool writeBlocked = false)
{
    const HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    Check(SUCCEEDED(apartment), "deadline caller initializes COM");
    if (FAILED(apartment)) return;
    BindingFixture* fixture = nullptr;
    Check(SUCCEEDED(BindingFixtureCreate(&fixture)), "deadline target is a real instrumented COM object");
    if (!fixture) { CoUninitialize(); return; }
    Microsoft::WRL::ComPtr<IAgileReference> owner;
    Check(SUCCEEDED(BindingFixtureOwner(fixture, &owner)), "deadline target owns an agile reference");
    wchar_t name[80];
    swprintf_s(name, L"\\\\.\\pipe\\winapp-devtools-binding-%lu", GetCurrentProcessId());
    HANDLE server = CreateNamedPipeW(name, PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 4096, 4096, 0, nullptr);
    Check(server != INVALID_HANDLE_VALUE, "the test exclusively owns its process-specific agent pipe");
    if (server == INVALID_HANDLE_VALUE) return;
    std::atomic<bool> requestRead{ false };
    std::atomic<bool> release{ false };
    std::thread peer([&] {
        if (ConnectNamedPipe(server, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED) {
            char request[1024];
            DWORD read = 0;
            if (writeBlocked || (ReadFile(server, request, sizeof(request), &read, nullptr) && read >= 8)) {
                requestRead = true;
                if (partial) {
                    DWORD written = 0;
                    WriteFile(server, "{", 1, &written, nullptr);
                }
                const auto cleanupAt = GetTickCount64() + 18000;
                while (!release && GetTickCount64() < cleanupAt) Sleep(10);
            }
        }
        DisconnectNamedPipe(server);
    });
    std::wstring reply;
    const auto start = GetTickCount64();
    const bool answered = DevToolsBindingRelay_Binding(L"diagnose", owner.Get(), L"Text",
        writeBlocked ? std::wstring(65536, L'x') : L"", &reply);
    const auto elapsed = GetTickCount64() - start;
    release = true;
    peer.join();
    CloseHandle(server);
    std::printf("  %s reply: elapsed=%llu ms; deadline=15000 ms; cleanup-release=18000 ms\n",
        writeBlocked ? "blocked-write" : partial ? "partial" : "silent", elapsed);
    Check(requestRead, "the peer accepted the real binding connection");
    if (writeBlocked) Check(reply.find(L"could not send") != std::wstring::npos,
        "non-reading peer reaches actual send failure after cancellation");
    Check(!answered && reply.find(L"unavailable") != std::wstring::npos, "missing reply is an explicit unavailable answer");
    Check(elapsed >= 14500 && elapsed < 16500, "the relay itself enforces its reply deadline before peer cleanup");
    owner.Reset();
    Check(SUCCEEDED(BindingFixtureDestroy(fixture)), "actual timed-out relay revoked table and released target");
    CoUninitialize();
}

static void Test_ForeignServerGetsNoFrame(bool delayedAccept = false, bool writeControl = false)
{
    const auto readyName = L"Local\\WinappRelayPeer-" + std::to_wstring(GetCurrentProcessId())
        + L"-" + std::to_wstring(GetTickCount64());
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, readyName.c_str());
    Check(ready != nullptr && GetLastError() != ERROR_ALREADY_EXISTS, "the peer readiness event is uniquely owned");
    if (!ready) return;
    HANDLE accept = CreateEventW(nullptr, TRUE, !delayedAccept, (readyName + L"-accept").c_str());
    Check(accept != nullptr, "the owned peer's accept barrier exists");
    if (!accept) { CloseHandle(ready); return; }
    wchar_t executable[32768]{};
    GetModuleFileNameW(nullptr, executable, _countof(executable));
    std::wstring command = L"\"" + std::wstring(executable) + L"\" --winapp-devtools-relay-peer "
        + std::to_wstring(GetCurrentProcessId()) + L" " + readyName;
    STARTUPINFOW startup{ sizeof(startup) };
    PROCESS_INFORMATION process{};
    const bool started = CreateProcessW(executable, command.data(), nullptr, nullptr, FALSE,
        CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process) != FALSE;
    Check(started, "the owned non-UI foreign peer starts");
    if (!started) { CloseHandle(accept); CloseHandle(ready); return; }
    const bool listening = WaitForSingleObject(ready, 5000) == WAIT_OBJECT_0;
    Check(listening, "the foreign process owns the actual process-specific binding pipe");
    std::wstring reply;
    if (listening) {
        if (writeControl) {
            const auto name = L"\\\\.\\pipe\\winapp-devtools-binding-" + std::to_wstring(GetCurrentProcessId());
            HANDLE client = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
            DWORD written = 0;
            const bool sent = client != INVALID_HANDLE_VALUE && WriteFile(client, "BINDING2\n", 9, &written, nullptr);
            Check(sent && written == 9, "negative control really transmits frame bytes before disconnect");
            if (client != INVALID_HANDLE_VALUE) CloseHandle(client);
        } else {
            Check(SUCCEEDED(CoInitializeEx(nullptr, COINIT_MULTITHREADED)), "foreign-peer caller COM initialized");
            BindingFixture* fixture = nullptr;
            Microsoft::WRL::ComPtr<IAgileReference> owner;
            const bool owned = SUCCEEDED(BindingFixtureCreate(&fixture)) && SUCCEEDED(BindingFixtureOwner(fixture, &owner));
            Check(owned, "foreign-peer probe has a real marshalable owner, not a null-input refusal");
            if (owned) {
                const bool accepted = DevToolsBindingRelay_Binding(L"diagnose", owner.Get(), L"Text", L"probe-only", &reply);
                Check(!accepted && reply.find(L"could not authenticate") != std::wstring::npos,
                    "a foreign server is refused by authentication before marshaling or transmission");
            }
            owner.Reset();
            if (fixture) Check(SUCCEEDED(BindingFixtureDestroy(fixture)), "foreign-peer probe releases its real owner");
            CoUninitialize();
        }
    }
    SetEvent(accept);
    const bool finished = WaitForSingleObject(process.hProcess, 5000) == WAIT_OBJECT_0;
    Check(finished, "peer disconnect completes without a frame or peer cleanup deadline");
    if (!finished) {
        TerminateProcess(process.hProcess, 14);
        WaitForSingleObject(process.hProcess, 5000);
    }
    DWORD code = 0;
    GetExitCodeProcess(process.hProcess, &code);
    std::printf("  foreign peer: delayed-accept=%d; negative-control=%d; exit=%lu\n", delayedAccept, writeControl, code);
    Check(code == (writeControl ? 1u : 0u), writeControl
        ? "the same foreign peer detects deliberately transmitted bytes"
        : "the foreign server receives zero bytes before refusal");
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    CloseHandle(ready);
    CloseHandle(accept);
}

static void Test_BoundsAndActualRevocation()
{
    for (const auto& packet : {std::vector<char>{}, std::vector<char>(65537, 'x')}) {
        bool refused = false;
        try { DevToolsBindingRelay_BuildBindingFrame(packet, L"diagnose", L"Text", L""); }
        catch (const std::length_error&) { refused = true; }
        Check(refused, "empty/oversized packet refused before transmission");
    }
    bool refused = false;
    try { DevToolsBindingRelay_BuildBindingFrame({'x'}, L"diagnose", L"Text", std::wstring(65537,L'x')); }
    catch (const std::length_error&) { refused = true; }
    Check(refused, "oversized authored input refused");
    Check(SUCCEEDED(CoInitializeEx(nullptr, COINIT_MULTITHREADED)), "revocation caller COM initialized");
    BindingFixture* fixture = nullptr;
    if (FAILED(BindingFixtureCreate(&fixture))) { Check(false, "fixture created"); CoUninitialize(); return; }
    Microsoft::WRL::ComPtr<IAgileReference> owner;
    BindingFixtureOwner(fixture, &owner);
    Check(SUCCEEDED(BindingFixtureIdentity(fixture, owner.Get())), "agile resolved IUnknown matches acquired identity");
    ULONG baseline=0, lateCalls=0, revocations=0;
    BindingFixtureCounts(fixture,&baseline,&lateCalls,&revocations);
    for (const char* response : {"BINDING2 {\"state\":\"none\"}\n", "BINDING2 broken\n", "{\"state\":\"none\"}\n", "", "frame-failure", "copy-failure"}) {
        const bool copyFailure = std::string(response) == "copy-failure";
        const bool frameFailure = std::string(response) == "frame-failure";
        std::vector<char> packet;
        const auto name = L"\\\\.\\pipe\\winapp-devtools-binding-" + std::to_wstring(GetCurrentProcessId());
        HANDLE pipe = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX|FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE|PIPE_WAIT,1,4096,4096,0,nullptr);
        Check(pipe != INVALID_HANDLE_VALUE, "revocation test owns its pipe");
        if (pipe == INVALID_HANDLE_VALUE) continue;
        std::thread peer([&] {
            if (ConnectNamedPipe(pipe,nullptr) || GetLastError()==ERROR_PIPE_CONNECTED) {
                auto exact = [&](void* value, DWORD bytes) {
                    auto target=static_cast<char*>(value);
                    while(bytes) { DWORD read=0; if(!ReadFile(pipe,target,bytes,&read,nullptr)||!read)return false; target+=read;bytes-=read; }
                    return true;
                };
                char header[9]; DWORD length=0;
                if (exact(header,9) && std::string(header,9)=="BINDING2\n" && exact(&length,4) && length<=65536) {
                    packet.resize(length);
                    if (exact(packet.data(),length)) {
                        for(int i=0;i<3;++i) { DWORD size=0; if(!exact(&size,4)||size>65536)break; std::vector<char> field(size); if(!exact(field.data(),size))break; }
                        DWORD sent=0; WriteFile(pipe,response,static_cast<DWORD>(strlen(response)),&sent,nullptr);
                        if (*response) FlushFileBuffers(pipe);
                    }
                }
            }
            DisconnectNamedPipe(pipe);
        });
        std::wstring answer;
        BindingFixtureFailPacketCopy(copyFailure);
        bool accepted=DevToolsBindingRelay_Binding(L"diagnose",owner.Get(),L"Text",
            frameFailure ? std::wstring(65537,L'x') : L"",&answer);
        BindingFixtureFailPacketCopy(false);
        peer.join(); CloseHandle(pipe);
        ULONG remaining=0;
        BindingFixtureCounts(fixture,&remaining,&lateCalls,&revocations);
        Check(remaining == baseline && lateCalls == 0, "each actual relay path releases its table reference before returning");
        Check(accepted == (std::string(response).find("BINDING2 {")==0), "actual relay accepts versioned JSON only");
        if (copyFailure || frameFailure) {
            Check(packet.empty(), "post-marshal copy/frame failure sends no packet");
            continue;
        }
        Check(!packet.empty(), "actual relay published a real COM packet");
        Microsoft::WRL::ComPtr<IStream> stream;
        CreateStreamOnHGlobal(nullptr,TRUE,&stream);
        ULONG written=0; stream->Write(packet.data(),static_cast<ULONG>(packet.size()),&written);
        LARGE_INTEGER zero{}; stream->Seek(zero,STREAM_SEEK_SET,nullptr);
        Microsoft::WRL::ComPtr<IInspectable> late;
        HRESULT hr=CoUnmarshalInterface(stream.Get(),__uuidof(IInspectable),&late);
        Check(FAILED(hr)&&!late, "actual relay revoked original packet after success/malformed/old/disconnect reply");
    }
    std::wstring answer;
    // No live connection is needed to check absence cleanup; table creation happens only after authentication.
    Check(!DevToolsBindingRelay_Binding(L"diagnose",owner.Get(),L"Text",L"",&answer), "absent peer does not retain a packet");
    owner.Reset();
    Check(SUCCEEDED(BindingFixtureDestroy(fixture)), "repeated relay paths return observer references to baseline");
    CoUninitialize();
}

int RunBindingRelayTests()
{
    std::printf("DevToolsBindingRelay tests\n");
    Test_FrameMatchesWhatTheAgentReads();
    Test_EmptyAuthoredIsStillAField();
    Test_NonAsciiSurvivesAsUtf8();
    Test_OnlyBindingOpsAreSendable();
    Test_UnavailableIsAlwaysWellFormedAndCarriesAReason();
    Test_NoAgentReasonDoesNotConflateTheTwoCases();
    Test_ForeignServerGetsNoFrame();
    Test_ForeignServerGetsNoFrame(true);
    Test_ForeignServerGetsNoFrame(true, true);
    Test_BoundsAndActualRevocation();
    Test_ConnectedReplyDeadline(false);
    Test_ConnectedReplyDeadline(true);
    Test_ConnectedReplyDeadline(false, true);
    return g_failures;
}
