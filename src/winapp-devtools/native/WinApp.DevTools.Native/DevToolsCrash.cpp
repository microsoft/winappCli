// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsCrash.h"
#include "DevToolsOverlay.h" // DevToolsOverlayLog - the opt-in tap log sink, shared across translation units

#include <windows.h>
#include <dbghelp.h>
#include <stdio.h>
#include <string>

static HMODULE g_selfMod = nullptr;

static void CrashLogLine(const wchar_t* fmt, ...)
{
    wchar_t buf[1024];
    va_list ap; va_start(ap, fmt); _vsnwprintf_s(buf, _countof(buf), _TRUNCATE, fmt, ap); va_end(ap);
    wchar_t path[MAX_PATH]; wchar_t tmp[MAX_PATH];
    DWORD n = GetTempPathW(MAX_PATH, tmp);
    if (n) { wcscpy_s(path, tmp); wcscat_s(path, L"winapp-devtools-crash.log"); } else { wcscpy_s(path, L"winapp-devtools-crash.log"); }
    static SRWLOCK lk = SRWLOCK_INIT; AcquireSRWLockExclusive(&lk);
    FILE* f = nullptr; if (_wfopen_s(&f, path, L"a, ccs=UTF-8") == 0 && f) { fwprintf(f, L"[%lu] %s\n", GetCurrentProcessId(), buf); fclose(f); }
    ReleaseSRWLockExclusive(&lk);
    DevToolsOverlayLog(L"%s", buf); // mirror into the opt-in tap log too
}

static std::wstring ModuleOffset(void* addr)
{
    HMODULE mod = nullptr;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(addr), &mod) && mod) {
        wchar_t name[MAX_PATH];
        if (GetModuleFileNameW(mod, name, MAX_PATH)) {
            const wchar_t* base = wcsrchr(name, L'\\'); base = base ? base + 1 : name;
            unsigned long long off = (unsigned long long)(reinterpret_cast<uintptr_t>(addr) - reinterpret_cast<uintptr_t>(mod));
            wchar_t out[MAX_PATH + 32];
            _snwprintf_s(out, _countof(out), _TRUNCATE, L"%s+0x%llx", base, off);
            return out;
        }
    }
    wchar_t out[32]; _snwprintf_s(out, _countof(out), _TRUNCATE, L"0x%p", addr); return out;
}

static void DevToolsCrashCapture(EXCEPTION_POINTERS* ep)
{
    EXCEPTION_RECORD* er = ep ? ep->ExceptionRecord : nullptr;
    DWORD code = er ? er->ExceptionCode : 0;
    void* at = er ? er->ExceptionAddress : nullptr;
    CrashLogLine(L"==== DevTools CRASH ==== code=0x%08X at=%s tid=%lu", code, ModuleOffset(at).c_str(), GetCurrentThreadId());
    if (er && code == EXCEPTION_ACCESS_VIOLATION && er->NumberParameters >= 2) {
        const wchar_t* op = er->ExceptionInformation[0] == 0 ? L"read" :
                            er->ExceptionInformation[0] == 1 ? L"write" : L"execute";
        CrashLogLine(L"  access violation: %s at 0x%llx", op, (unsigned long long)er->ExceptionInformation[1]);
    }
    void* frames[48]; USHORT nf = RtlCaptureStackBackTrace(0, 48, frames, nullptr);
    for (USHORT i = 0; i < nf; ++i) CrashLogLine(L"  #%02u %s", (unsigned)i, ModuleOffset(frames[i]).c_str());
    // Minidump for offline analysis (thread stacks + indirectly-referenced memory -> the faulting object).
    wchar_t tmp[MAX_PATH]; wchar_t dmp[MAX_PATH];
    DWORD n = GetTempPathW(MAX_PATH, tmp); if (!n) wcscpy_s(tmp, L".\\");
    _snwprintf_s(dmp, _countof(dmp), _TRUNCATE, L"%swinapp-devtools-crash-%lu-%llu.dmp",
                 tmp, GetCurrentProcessId(), (unsigned long long)GetTickCount64());
    HANDLE hf = CreateFileW(dmp, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hf != INVALID_HANDLE_VALUE) {
        MINIDUMP_EXCEPTION_INFORMATION mei{}; mei.ThreadId = GetCurrentThreadId(); mei.ExceptionPointers = ep; mei.ClientPointers = FALSE;
        MINIDUMP_TYPE mt = (MINIDUMP_TYPE)(MiniDumpWithDataSegs | MiniDumpWithThreadInfo |
                                           MiniDumpWithUnloadedModules | MiniDumpWithIndirectlyReferencedMemory);
        BOOL ok = MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), hf, mt, ep ? &mei : nullptr, nullptr, nullptr);
        CloseHandle(hf);
        CrashLogLine(L"  minidump %s: %s", ok ? L"written" : L"FAILED", dmp);
    } else {
        CrashLogLine(L"  minidump: could not create file %s", dmp);
    }
}

static LONG DevToolsDoCapture(EXCEPTION_POINTERS* ep)
{
    static LONG once = 0;
    if (InterlockedExchange(&once, 1) != 0) return EXCEPTION_CONTINUE_SEARCH;
    __try { DevToolsCrashCapture(ep); }
    __except (EXCEPTION_EXECUTE_HANDLER) { /* never let capture itself change how the process dies */ }
    return EXCEPTION_CONTINUE_SEARCH;
}

static LONG CALLBACK DevToolsVectoredHandler(EXCEPTION_POINTERS* ep)
{
    if (ep && ep->ExceptionRecord && ep->ExceptionRecord->ExceptionCode == EXCEPTION_ACCESS_VIOLATION) {
        HMODULE mod = nullptr;
        if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                               reinterpret_cast<LPCWSTR>(ep->ExceptionRecord->ExceptionAddress), &mod) &&
            mod && mod == g_selfMod) {
            DevToolsDoCapture(ep); // an AV whose faulting IP is in our DLL == our bug; capture even first-chance
        }
    }
    return EXCEPTION_CONTINUE_SEARCH; // never swallow - let normal dispatch continue unchanged
}

void DevToolsInstallCrashHandler()
{
    if (!GetEnvironmentVariableW(L"WINAPP_DEVTOOLS_LOG", nullptr, 0)) return;
    static LONG installed = 0;
    if (InterlockedExchange(&installed, 1) != 0) return;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&DevToolsInstallCrashHandler), &g_selfMod) ||
        !AddVectoredExceptionHandler(1, DevToolsVectoredHandler)) {
        DevToolsOverlayLog(L"could not install opt-in DevTools fault handler (error=%lu)", GetLastError());
        return;
    }
    DevToolsOverlayLog(L"opt-in DevTools fault handler installed (self=%p)", g_selfMod);
}
