// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include <windows.h>
#include <inspectable.h>
#include <wrl/client.h>
#include <stdexcept>

#include "DevToolsBindingRelay.h"
#include "DevToolsProtocol.h"
#include "DevToolsText.h"

#ifdef WINAPP_DEVTOOLS_BINDING_FAULT_INJECTION
extern bool DevToolsBindingTestFailPacketCopy();
#endif

namespace {

constexpr DWORD kConnectTimeoutMs = 1500;
constexpr DWORD kReplyTimeoutMs = 15000;

volatile LONG g_uiThreadId = 0;
constexpr size_t kMaxFieldBytes = 64 * 1024;

struct MarshalPacket {
    Microsoft::WRL::ComPtr<IStream> stream;
    bool published = false;
    HRESULT Revoke() {
        if (!published) return S_OK;
        LARGE_INTEGER zero{};
        HRESULT hr = stream->Seek(zero, STREAM_SEEK_SET, nullptr);
        if (SUCCEEDED(hr)) hr = CoReleaseMarshalData(stream.Get());
        if (SUCCEEDED(hr)) published = false;
        else OutputDebugStringW(L"DevToolsBindingRelay: could not revoke binding marshal data.\n");
        return hr;
    }
    ~MarshalPacket() { Revoke(); }
    HRESULT Create(IAgileReference* element, std::vector<char>& bytes) {
        Microsoft::WRL::ComPtr<IInspectable> target;
        HRESULT hr = element->Resolve(__uuidof(IInspectable), &target);
        if (FAILED(hr)) return hr;
        hr = CreateStreamOnHGlobal(nullptr, TRUE, &stream);
        if (FAILED(hr)) return hr;
        hr = CoMarshalInterface(stream.Get(), __uuidof(IInspectable), target.Get(),
            MSHCTX_LOCAL, nullptr, MSHLFLAGS_TABLESTRONG);
        if (FAILED(hr)) return hr;
        published = true;
#ifdef WINAPP_DEVTOOLS_BINDING_FAULT_INJECTION
        if (DevToolsBindingTestFailPacketCopy()) return E_OUTOFMEMORY;
#endif
        STATSTG stat{};
        hr = stream->Stat(&stat, STATFLAG_NONAME);
        if (FAILED(hr)) return hr;
        if (!stat.cbSize.QuadPart || stat.cbSize.QuadPart > kMaxFieldBytes)
            return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
        bytes.resize(static_cast<size_t>(stat.cbSize.QuadPart));
        LARGE_INTEGER zero{};
        hr = stream->Seek(zero, STREAM_SEEK_SET, nullptr);
        if (FAILED(hr)) return hr;
        ULONG read = 0;
        hr = stream->Read(bytes.data(), static_cast<ULONG>(bytes.size()), &read);
        return SUCCEEDED(hr) && read != bytes.size() ? E_FAIL : hr;
    }
};

void AppendI32(std::vector<char>& out, int v)
{
    out.push_back((char)(v & 0xFF));
    out.push_back((char)((v >> 8) & 0xFF));
    out.push_back((char)((v >> 16) & 0xFF));
    out.push_back((char)((v >> 24) & 0xFF));
}

void AppendString(std::vector<char>& out, const std::wstring& s)
{
    if (s.size() > kMaxFieldBytes) throw std::length_error("binding field too long");
    std::string u = DevToolsToUtf8(s);
    if (u.size() > kMaxFieldBytes) throw std::length_error("binding UTF-8 field too long");
    AppendI32(out, (int)u.size());
    out.insert(out.end(), u.begin(), u.end());
}

std::wstring PipeName()
{
    wchar_t buf[64];
    _snwprintf_s(buf, _countof(buf), _TRUNCATE, L"\\\\.\\pipe\\winapp-devtools-binding-%lu", GetCurrentProcessId());
    return buf;
}

DevToolsAppRuntime AppRuntime()
{
    if (GetModuleHandleW(L"coreclr.dll")) return DevToolsAppRuntime::Clr;
    // Every Native AOT executable exports its runtime's debugger header.
    return GetProcAddress(GetModuleHandleW(nullptr), "DotNetRuntimeDebugHeader")
        ? DevToolsAppRuntime::NativeAot : DevToolsAppRuntime::None;
}

bool TransferBefore(HANDLE pipe, void* buffer, DWORD size, DWORD& transferred,
                    bool writing, ULONGLONG deadline)
{
    OVERLAPPED operation{};
    operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!operation.hEvent) return false;
    BOOL complete = writing ? WriteFile(pipe, buffer, size, nullptr, &operation)
                            : ReadFile(pipe, buffer, size, nullptr, &operation);
    bool inTime = true;
    if (!complete && GetLastError() == ERROR_IO_PENDING) {
        const auto now = GetTickCount64();
        const DWORD remaining = now < deadline ? static_cast<DWORD>(deadline - now) : 0;
        if (WaitForSingleObject(operation.hEvent, remaining) != WAIT_OBJECT_0) {
            inTime = false;
            CancelIoEx(pipe, &operation);
            GetOverlappedResult(pipe, &operation, &transferred, TRUE);
        } else {
            complete = GetOverlappedResult(pipe, &operation, &transferred, FALSE);
        }
    } else if (complete) {
        complete = GetOverlappedResult(pipe, &operation, &transferred, FALSE);
    }
    CloseHandle(operation.hEvent);
    return inTime && complete && GetTickCount64() <= deadline;
}

} // namespace

void DevToolsBindingRelay_NoteUiThread()
{
    InterlockedExchange(&g_uiThreadId, (LONG)GetCurrentThreadId());
}

std::wstring DevToolsBindingRelay_UnavailableJson(const std::wstring& reason)
{
    return L"{\"state\":\"unavailable\",\"reason\":\"" + DevToolsJsonEscape(reason) + L"\"}";
}

const wchar_t* DevToolsBindingRelay_NoAgentReason(DevToolsAppRuntime runtime)
{
    if (runtime == DevToolsAppRuntime::Clr)
        return L"this .NET app has no loaded managed binding host. The host must be loaded through "
               L"DOTNET_STARTUP_HOOKS before app startup; attaching the native inspector cannot add it.";
    if (runtime == DevToolsAppRuntime::NativeAot)
        return L"Native AOT apps can't load the managed binding agent, so {Binding}/{x:Bind} can't be diagnosed "
               L"by it. Native binding path walking remains available.";
    return L"this app has no .NET runtime, so there is no managed agent to load \u2014 bindings cannot be "
           L"diagnosed by the managed host under any launch option. Native binding path walking remains available.";
}

bool DevToolsBindingRelay_IsAllowedOp(const std::wstring& op)
{
    // Only managed binding operations cross this pipe. Native DevToolsBindingInstall owns grafting.
    return op == L"diagnose" || op == L"capture" || op == L"restore" || op == L"restoreconfirmed" || op == L"clear"
        || op == L"writesource";
}

std::vector<char> DevToolsBindingRelay_BuildBindingFrame(const std::vector<char>& packet, const std::wstring& op,
                                                const std::wstring& prop, const std::wstring& authored)
{
    std::vector<char> f;
    if (packet.empty() || packet.size() > kMaxFieldBytes) throw std::length_error("invalid binding packet length");
    const char* header = "BINDING2\n";
    f.insert(f.end(), header, header + 9);
    AppendI32(f, static_cast<int>(packet.size()));
    f.insert(f.end(), packet.begin(), packet.end());
    AppendString(f, op);
    AppendString(f, prop);
    AppendString(f, authored);
    return f;
}

bool DevToolsBindingRelay_AgentPresent()
{    // WaitNamedPipe against a name that was never created fails immediately with FILE_NOT_FOUND, which is the
    std::wstring name = PipeName();
    if (WaitNamedPipeW(name.c_str(), 1)) return true;
    return GetLastError() != ERROR_FILE_NOT_FOUND;
}

bool DevToolsBindingRelay_Binding(const std::wstring& op, IAgileReference* element,
                         const std::wstring& prop, const std::wstring& authored,
                         std::wstring* outJson)
{
    std::wstring sink;
    if (!outJson) outJson = &sink;

    if (!DevToolsBindingRelay_IsAllowedOp(op)) {
        *outJson = DevToolsBindingRelay_UnavailableJson(L"unsupported binding operation");
        return false;
    }

    LONG ui = InterlockedCompareExchange(&g_uiThreadId, 0, 0);
    // The managed agent marshals back to the UI thread, so a UI-thread caller would deadlock waiting.
    if (ui != 0 && (DWORD)ui == GetCurrentThreadId()) {
        *outJson = DevToolsBindingRelay_UnavailableJson(L"internal: the diagnosis must not be requested on the UI thread");
        return false;
    }

    std::wstring name = PipeName();
    if (!WaitNamedPipeW(name.c_str(), kConnectTimeoutMs)) {
        *outJson = DevToolsBindingRelay_UnavailableJson(
            GetLastError() == ERROR_FILE_NOT_FOUND
                ? DevToolsBindingRelay_NoAgentReason(AppRuntime())
                : L"the managed DevTools agent did not accept a connection");
        return false;
    }

    // Identification-level pipe access plus same-process PID verification keeps the managed relay local.
    HANDLE h = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        *outJson = DevToolsBindingRelay_UnavailableJson(L"could not open the managed DevTools agent's pipe");
        return false;
    }

    ULONG serverProcessId = 0;
    if (!GetNamedPipeServerProcessId(h, &serverProcessId) || serverProcessId != GetCurrentProcessId()) {
        *outJson = DevToolsBindingRelay_UnavailableJson(L"could not authenticate an in-process managed DevTools agent");
        CloseHandle(h);
        return false;
    }

    struct ClosePipe { HANDLE value; ~ClosePipe() { CloseHandle(value); } } closePipe{h};
    DevToolsBindingApartment apartment;
    if (FAILED(apartment.result) || !element) {
        *outJson = DevToolsBindingRelay_UnavailableJson(L"binding transfer requires an owned element on an MTA caller");
        return false;
    }
    // The original trusted stream is revoked before this call's COM apartment ends, even on exceptions.
    MarshalPacket packet;
    std::vector<char> frame;
    try {
        std::vector<char> bytes;
        HRESULT hr = packet.Create(element, bytes);
        if (FAILED(hr)) {
            *outJson = DevToolsBindingRelay_UnavailableJson(L"could not marshal the selected binding element");
            return false;
        }
        frame = DevToolsBindingRelay_BuildBindingFrame(bytes, op, prop, authored);
    } catch (const std::exception&) {
        *outJson = DevToolsBindingRelay_UnavailableJson(L"could not construct the bounded binding request");
        return false;
    }

    // Overlapped I/O enforces the deadline even if the peer goes silent.
    DWORD mode = PIPE_READMODE_BYTE | PIPE_WAIT;
    SetNamedPipeHandleState(h, &mode, nullptr, nullptr);

    bool ok = false;
    DWORD written = 0;
    const ULONGLONG deadline = GetTickCount64() + kReplyTimeoutMs;
    if (TransferBefore(h, frame.data(), (DWORD)frame.size(), written, true, deadline) && written == frame.size()) {
        std::string line;
        for (;;) {
            char c = 0;
            DWORD got = 0;
            if (!TransferBefore(h, &c, 1, got, false, deadline) || got != 1) break;
            if (c == '\n') { ok = true; break; }
            if (c != '\r') line.push_back(c);
            if (line.size() > 64 * 1024) break;              // a reply this large is a protocol fault, not an answer
            if (GetTickCount64() > deadline) break;
        }
        if (ok) {
            const std::string prefix = "BINDING2 ";
            DevToolsJson answer;
            *outJson = line.rfind(prefix, 0) == 0
                ? DevToolsFromUtf8(line.data() + prefix.size(), line.size() - prefix.size()) : L"";
            if (!DevToolsJsonParse(*outJson, answer) || answer.type != DevToolsJsonType::Object) {
                *outJson = DevToolsBindingRelay_UnavailableJson(L"the managed DevTools agent returned an incompatible or malformed answer");
                ok = false;
            }
        } else {
            *outJson = DevToolsBindingRelay_UnavailableJson(L"the managed DevTools agent did not answer");
        }
    } else {
        *outJson = DevToolsBindingRelay_UnavailableJson(L"could not send the request to the managed DevTools agent");
    }

    if (FAILED(packet.Revoke())) {
        *outJson = DevToolsBindingRelay_UnavailableJson(L"could not revoke the binding transfer");
        ok = false;
    }
    return ok;
}
