// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// The wire contract is DevToolsProtocolSchema.inc (published as winapp-devtools-schema.json); see
// docs/guides/devtools-advanced.md#use-the-devtools-protocol for usage. Mutation trust lives in DevToolsTrust.cpp.
// Threading contract: return from SetSite quickly; enumerate on an MTA worker thread.

#include <windows.h>
#include <unknwn.h>
#include <ocidl.h>
#include <inspectable.h>
#include <winstring.h>
#include <roapi.h>
#include <oleauto.h>
#include <xamlom.h>
#include "DevToolsProjected.h"
#include <sddl.h>
#include <atomic>
#include <mutex>
#include <new>
#include <string>
#include <map>
#include <set>
#include <vector>
#include <functional>
#include <algorithm>
#include <memory>
#include <cstdlib>
#include <cstdint>
#include <share.h>
#include "DevToolsOverlay.h"
#include "DevToolsInspectorAcceptance.h"
#include "DevToolsCrash.h"
#include "DevToolsSourcePath.h"
#include "DevToolsThreadGuard.h"
#include "DevToolsSink.h"
#include "DevToolsRead.h"
#include "DevToolsWindow.h"
#include "DevToolsProtocol.h"
#include "DevToolsProtocolSchema.h"
#include "DevToolsTrust.h"
#include "DevToolsEvents.h"
#include "DevToolsAppXaml.h"
#include "DevToolsBinding.h"
#include "DevToolsBindingInstall.h"
#include "DevToolsPathProbe.h"
#include "DevToolsAuthored.h"
#include "DevToolsCommentAnchor.h"
#include "DevToolsCommentText.h"
#include "DevToolsTreeWatch.h"
#include "DevToolsWriteGate.h"
#include "DevToolsBindingRelay.h"
#include "DevToolsBindingRow.h"
#include "DevToolsTreeLayout.h"
#include "DevToolsPerf.h"
#include "DevToolsUiDispatch.h"
#include "DevToolsOverlayStateTracker.h"
#include "DevToolsOwnedState.h"
#include "DevToolsPipeAcceptLoop.h"
#include "DevToolsPipeFraming.h"
#include "DevToolsBatch.h"

// DevToolsOverlay_ArmPick / DevToolsOverlay_DisarmPick (used by the selection.arm / selection.disarm verbs below) are
// declared in DevToolsOverlay.h (included above) and defined in DevToolsOverlay.cpp, which owns the pick catcher. Both
// route through the SAME OnPickClick the rail's Pick button uses, are idempotent, and MUST run on the app UI thread.

// The diagnostic log path, PID-QUALIFIED. WinApp.DevTools.Native.dll is injected into ANY app being inspected, so a
// single machine-wide file would mean two inspected apps appending to one handle with interleaved, ambiguous
// lines exactly when more than one thing is running. One file per injected process.
static const wchar_t* LogPath()
{
    static wchar_t path[MAX_PATH] = L"";
    if (!path[0]) {
        wchar_t tmp[MAX_PATH];
        DWORD n = GetTempPathW(MAX_PATH, tmp);
        if (!n) tmp[0] = L'\0';
        _snwprintf_s(path, _countof(path), _TRUNCATE, L"%swinapp-devtools-%lu.log", tmp, GetCurrentProcessId());
    }
    return path;
}

static std::atomic<const wchar_t*> g_guestInitializationError{nullptr};

static std::wstring GuestSiblingWriterPath()
{
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&g_guestInitializationError), &module)) return {};
    std::wstring path(32768, L'\0');
    const DWORD length = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) return {};
    path.resize(length);
    // The guest's verified immutable bundle uses canonical, drive-absolute paths, never a search path.
    if (path.size() < 3 || !iswalpha(path[0]) || path[1] != L':' || path[2] != L'\\') return {};
    std::wstring canonical(32768, L'\0');
    const DWORD fullLength = GetFullPathNameW(path.c_str(), static_cast<DWORD>(canonical.size()), canonical.data(), nullptr);
    if (!fullLength || fullLength >= canonical.size()) return {};
    canonical.resize(fullLength);
    if (_wcsicmp(path.c_str(), canonical.c_str()) != 0) return {};
    path.resize(path.find_last_of(L'\\') + 1);
    path += L"winapp.exe";
    const DWORD attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))) return {};
    return path;
}

static bool LoggingEnabled()
{
    static int enabled = -1;
    if (enabled < 0) { enabled = GetEnvironmentVariableW(L"WINAPP_DEVTOOLS_LOG", nullptr, 0) > 0 ? 1 : 0; }
    return enabled == 1;
}

// Held open under the same lock that serialises writes, and flushed per line so a crashing app still has its log -
// the tap runs inside somebody else's process, and losing the tail loses the interesting part. Never closed: the
// process owns it for its lifetime, and a close path would only add a teardown race.
static SRWLOCK g_logLock = SRWLOCK_INIT;

static void LogWriteLocked(const wchar_t* line)
{
    static FILE* f = nullptr;
    static bool tried = false;
    if (!tried) { tried = true; f = _wfsopen(LogPath(), L"a, ccs=UTF-8", _SH_DENYNO); }
    if (!f) return;
    fwprintf(f, L"[%lu] %s\n", GetCurrentProcessId(), line);
    fflush(f);
}

static void Log(const wchar_t* fmt, ...)
{
    if (!LoggingEnabled()) return;
    wchar_t buf[1024];
    va_list ap; va_start(ap, fmt); _vsnwprintf_s(buf, _countof(buf), _TRUNCATE, fmt, ap); va_end(ap);
    AcquireSRWLockExclusive(&g_logLock);
    LogWriteLocked(buf);
    ReleaseSRWLockExclusive(&g_logLock);
}

// Shared opt-in overlay diagnostics, serialized through Log's file lock.
void DevToolsOverlayLog(const wchar_t* fmt, ...)
{
    if (!LoggingEnabled()) return;
    wchar_t buf[1024];
    va_list ap; va_start(ap, fmt); _vsnwprintf_s(buf, _countof(buf), _TRUNCATE, fmt, ap); va_end(ap);
    Log(L"%s", buf);
}


static const CLSID CLSID_DevToolsTap = { 0x7C3D6A11, 0x0000, 0x4F00, { 0x9A,0x00,0x5A,0x4F,0x4B,0x45,0x00,0x01 } };

// The QUEUE is reached through the C++/WinRT projection (DevToolsDispatcherTryEnqueue) so its vtable slots come out
// of the winmd rather than a hand-declared struct, where one dropped declaration silently shifts every later slot.
struct __declspec(uuid("2E0872A9-4E29-5F14-B688-FB96D5F9D5F8")) IDispatcherQueueHandler : IUnknown
{ virtual HRESULT STDMETHODCALLTYPE Invoke() = 0; };

static IVisualTreeService3* g_vts3 = nullptr;
static std::atomic<int> g_elemCount{0};
static std::atomic<bool> g_adviseFailed{false};

#include "DevToolsTap.Census.inc"
#include "DevToolsTap.UiDispatch.inc"
#include "DevToolsTap.CensusQuery.inc"
#include "DevToolsTap.PropertyRead.inc"
#include "DevToolsTap.ElementCommands.inc"
#include "DevToolsTap.Route1Read.inc"
#include "DevToolsTap.OverlayState.inc"
#include "DevToolsTap.Commands.inc"
#include "DevToolsTap.Rpc.inc"
#include "DevToolsTap.EditBridges.inc"
#include "DevToolsTap.Lifecycle.inc"
