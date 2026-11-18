// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Included by native-runtime-tests.cpp: real RPC dispatch, connection queues and inspector routing.

#include "../native/WinApp.DevTools.Native/DevToolsText.h"

void OverlayFixtureInspect(InstanceHandle element, bool comments);
void OverlayFixturePick(InstanceHandle element, bool comments);

static IUnknown* TakeInspectorUiCallback()
{
    std::lock_guard<std::mutex> lock(g_heldMutex);
    IUnknown* handler = g_heldHandler;
    g_heldHandler = nullptr;
    return handler;
}

static bool InvokeInspectorUiCallback(IUnknown* handler)
{
    if (!handler) return false;
    IDispatcherQueueHandler* callback = nullptr;
    const HRESULT hr = handler->QueryInterface(IID_PPV_ARGS(&callback));
    if (SUCCEEDED(hr)) { callback->Invoke(); callback->Release(); }
    handler->Release();
    return SUCCEEDED(hr);
}

struct InspectorTestConnection
{
    HANDLE server = INVALID_HANDLE_VALUE;
    HANDLE client = INVALID_HANDLE_VALUE;
    DevToolsConn* conn = nullptr;
    std::wstring owner;

    bool Open(int index)
    {
        const std::wstring name = L"\\\\.\\pipe\\winapp-inspector-test-" +
            std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(index);
        server = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 4096, 4096, 0, nullptr);
        if (server == INVALID_HANDLE_VALUE) return false;
        client = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (client == INVALID_HANDLE_VALUE) return false;
        conn = DevToolsEvents_Register(server);
        if (!conn) return false;
        DevToolsJson negotiated;
        if (!DevToolsJsonParse(Call(L"DevTools.negotiate", L"{}"), negotiated)) return false;
        const auto* result = negotiated.Find(L"result");
        if (result) owner = result->GetString(L"ownerToken");
        return !owner.empty();
    }

    bool HasData() const
    {
        DWORD available = 0;
        return PeekNamedPipe(client, nullptr, 0, nullptr, &available, nullptr) && available != 0;
    }

    std::wstring ReadLine()
    {
        std::string line;
        const ULONGLONG deadline = GetTickCount64() + 3000;
        while (GetTickCount64() < deadline && line.size() < 65536) {
            if (!HasData()) { Sleep(1); continue; }
            char ch = 0;
            DWORD read = 0;
            if (!ReadFile(client, &ch, 1, &read, nullptr) || read != 1) return {};
            if (ch == '\n') return DevToolsFromUtf8(line);
            line += ch;
        }
        return {};
    }

    std::wstring Call(const wchar_t* method, const std::wstring& params)
    {
        const auto request = DevToolsRpcParse(L"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" +
            std::wstring(method) + L"\",\"params\":" + params + L"}");
        const std::wstring response = HandleRpc(conn, request);
        if (!response.empty()) return response;
        if (!InvokeInspectorUiCallback(TakeInspectorUiCallback())) return {};
        return ReadLine();
    }

    std::wstring OwnerParams() const { return L"{\"owner\":\"" + owner + L"\"}"; }
    std::wstring RegisterParams() const
    {
        return L"{\"owner\":\"" + owner + L"\",\"displayName\":\"Test editor\"}";
    }

    void Disconnect()
    {
        if (!conn) return;
        const auto id = DevToolsEvents_ConnId(conn);
        const auto disabled = DevToolsEvents_Unregister(conn);
        conn = nullptr;
        PipeFraming_AfterUnregister(id, disabled, nullptr);
    }

    ~InspectorTestConnection()
    {
        Disconnect();
        InvokeInspectorUiCallback(TakeInspectorUiCallback());
        if (client != INVALID_HANDLE_VALUE) CloseHandle(client);
        if (server != INVALID_HANDLE_VALUE) CloseHandle(server);
    }
};

template <typename Check>
static void CheckExternalInspector(Check check)
{
    InspectorTestConnection first, second;
    const bool opened = first.Open(1) && second.Open(2);
    check(opened, "external inspector tests create two real negotiated pipe connections");
    if (!opened) return;
    const auto firstId = DevToolsEvents_ConnId(first.conn);
    const auto secondId = DevToolsEvents_ConnId(second.conn);
    const auto savedPick = g_lastPickHandle.load();
    const auto savedGeneration = g_lastPickGen.load();
    g_lastPickHandle.store(0);
    DevToolsOverlay_SetInprocInspect(&InprocInspect);
    check(!RequestExternalInspector_ui({}), "without registration, presentation remains on the native path");
    check(first.Call(L"Inspector.registerExternal", L"{}").find(L"owner-connection-mismatch") != std::wstring::npos,
        "registration requires a negotiated owner");
    check(second.Call(L"Inspector.registerExternal", first.RegisterParams()).find(L"owner-connection-mismatch") != std::wstring::npos,
        "a different request connection cannot register another session's token");
    check(first.Call(L"Inspector.registerExternal", first.OwnerParams()).find(L"bad-args") != std::wstring::npos,
        "registration requires a display name");
    check(first.Call(L"Inspector.registerExternal", L"{\"owner\":\"invalid\",\"displayName\":\"Test\"}").find(L"unknown-owner") != std::wstring::npos,
        "a forged owner token is explicitly refused");
    g_ownedState.RecordSet(DevToolsStateAxis::Window, secondId, 1, 0);
    check(first.Call(L"Inspector.registerExternal", first.RegisterParams()).find(L"native-inspector-active") != std::wstring::npos,
        "registration refuses closed native window state that would restore to open");
    g_ownedState.Release(secondId);
    check(first.Call(L"Inspector.registerExternal", first.RegisterParams()).find(L"\"result\":null") != std::wstring::npos &&
        ExternalInspectorOwner_ui() == firstId, "registration assigns the connection's presentation ownership");
    check(first.Call(L"Inspector.registerExternal", first.RegisterParams()).find(L"\"result\":null") != std::wstring::npos,
        "the same connection can register idempotently");
    check(second.Call(L"Inspector.registerExternal", second.RegisterParams()).find(L"inspector-owned") != std::wstring::npos,
        "a second connection cannot take over presentation");
    check(second.Call(L"Inspector.unregisterExternal", second.OwnerParams()).find(L"inspector-owned") != std::wstring::npos &&
        ExternalInspectorOwner_ui() == firstId, "a second owner cannot unregister the inspector");
    check(second.Call(L"Window.open", L"{}").find(L"inspector-external") != std::wstring::npos,
        "explicit native opening is refused, not redirected or duplicated");

    OverlayFixtureInspect(0, false);
    const auto toolbar = first.ReadLine();
    check(toolbar.find(L"Inspector.openRequested") != std::wstring::npos &&
        toolbar.find(L"\"view\":\"inspect\"") != std::wstring::npos && toolbar.find(L"\"handle\"") == std::wstring::npos &&
        !DevToolsWindow_IsOpen(), "toolbar Inspect requests external presentation without opening native UI");
    MintSlot_nolock(707);
    const auto wire = PackWire_nolock(707);
    OverlayFixtureInspect(707, false);
    const auto element = first.ReadLine();
    check(element.find(L"\"handle\":\"" + std::to_wstring(wire) + L"\"") != std::wstring::npos,
        "element Inspect translates the raw element to an opaque wire handle");
    OverlayFixtureInspect(0, true);
    const auto comments = first.ReadLine();
    check(comments.find(L"\"view\":\"comments\"") != std::wstring::npos &&
        comments.find(L"\"handle\":\"" + std::to_wstring(wire) + L"\"") != std::wstring::npos,
        "comments-list request carries the selected element");
    DevToolsOverlay_SetPickedSink(&OnOverlayReleasedPick);
    OverlayFixturePick(707, false);
    const auto picked = first.ReadLine();
    check(picked.find(L"\"view\":\"inspect\"") != std::wstring::npos &&
        picked.find(L"\"handle\":\"" + std::to_wstring(wire) + L"\"") != std::wstring::npos && !DevToolsWindow_IsOpen(),
        "a committed in-app inspect pick requests the external inspector with its element");
    OverlayFixturePick(707, true);
    check(!first.HasData(), "a comment-mode pick does not redirect the engine's comment flyout");
    PublishOverlayPick(707, false);
    check(!first.HasData(), "a protocol coordinate pick updates selection without requesting inspector presentation");
    DevToolsOverlay_SetPickedSink(nullptr);
    check(!second.HasData(), "presentation notifications go only to the owner, without broadcast subscriptions");
    check(second.Call(L"Selection.poll", L"{}").find(std::to_wstring(wire)) != std::wstring::npos &&
        second.Call(L"Window.getState", L"{}").find(L"\"open\":false") != std::wstring::npos,
        "another client's selection reads and native window reads remain usable");

    check(first.Call(L"Inspector.unregisterExternal", first.OwnerParams()).find(L"\"result\":null") != std::wstring::npos &&
        !RequestExternalInspector_ui({}) && !DevToolsWindow_IsOpen(),
        "unregister restores native routing without opening a window");
    check(first.Call(L"Inspector.unregisterExternal", first.OwnerParams()).find(L"\"result\":null") != std::wstring::npos,
        "unregister is idempotent for the same live connection");
    first.Call(L"Inspector.registerExternal", first.RegisterParams());
    check(first.Call(L"DevTools.releaseOwnedState", first.OwnerParams()).find(L"\"result\"") != std::wstring::npos &&
        !ExternalInspectorOwner_ui() && !DevToolsWindow_IsOpen(),
        "releaseOwnedState releases presentation even when no UI axes were owned");

    first.Call(L"Inspector.registerExternal", first.RegisterParams());
    OverlayFixtureInspect(0, false);
    first.ReadLine();
    first.Disconnect();
    IUnknown* oldCleanup = TakeInspectorUiCallback();
    check(!RequestExternalInspector_ui({}), "disconnect immediately stops external routing before UI cleanup runs");
    check(second.Call(L"Inspector.registerExternal", second.RegisterParams()).find(L"\"result\":null") != std::wstring::npos,
        "a new live connection can register after the former owner disconnects");
    check(InvokeInspectorUiCallback(oldCleanup) && ExternalInspectorOwner_ui() == secondId,
        "delayed disconnect cleanup cannot unregister a newer owner");
    second.Disconnect();
    check(InvokeInspectorUiCallback(TakeInspectorUiCallback()) && g_externalInspectorOwner == 0 &&
        !DevToolsWindow_IsOpen(), "disconnect cleanup clears registration without opening native UI");
    FreeSlot_nolock(707);
    g_lastPickHandle.store(savedPick);
    g_lastPickGen.store(savedGeneration);
    DevToolsOverlay_SetInprocInspect(nullptr);
}
