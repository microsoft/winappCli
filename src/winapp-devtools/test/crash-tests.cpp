// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include <windows.h>
#include <dbghelp.h>
#include <cstdio>
#include <string>

static PVECTORED_EXCEPTION_HANDLER handler = nullptr;
static int dumps = 0, failures = 0;
static int selfAddress;
static HMODULE selfModule = reinterpret_cast<HMODULE>(1);
static BOOL WINAPI TestModule(DWORD, LPCWSTR address, HMODULE* module)
{
    *module = address == reinterpret_cast<LPCWSTR>(&selfAddress) ? selfModule : reinterpret_cast<HMODULE>(2);
    return TRUE;
}
static PVOID WINAPI TestInstall(ULONG, PVECTORED_EXCEPTION_HANDLER callback)
{
    handler = callback;
    return reinterpret_cast<PVOID>(1);
}
static HANDLE WINAPI TestCreateFile(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE)
{
    return reinterpret_cast<HANDLE>(42);
}
static BOOL WINAPI TestClose(HANDLE) { return TRUE; }
static BOOL WINAPI TestDump(HANDLE, DWORD, HANDLE, MINIDUMP_TYPE,
    PMINIDUMP_EXCEPTION_INFORMATION, PMINIDUMP_USER_STREAM_INFORMATION, PMINIDUMP_CALLBACK_INFORMATION)
{
    ++dumps;
    return TRUE;
}
static errno_t TestOpen(FILE** file, const wchar_t*, const wchar_t*) { *file = nullptr; return EACCES; }
void DevToolsOverlayLog(const wchar_t*, ...) {}

#define GetModuleHandleExW TestModule
#define AddVectoredExceptionHandler TestInstall
#define CreateFileW TestCreateFile
#define CloseHandle TestClose
#define MiniDumpWriteDump TestDump
#define _wfopen_s TestOpen
#include "../native/WinApp.DevTools.Native/DevToolsCrash.cpp"
#undef GetModuleHandleExW
#undef AddVectoredExceptionHandler
#undef CreateFileW
#undef CloseHandle
#undef MiniDumpWriteDump
#undef _wfopen_s

static LONG WINAPI AppFilter(EXCEPTION_POINTERS*) { return EXCEPTION_CONTINUE_SEARCH; }

int RunCrashTests()
{
    const auto check = [](bool ok, const char* message) {
        if (!ok) { ++failures; std::printf("FAIL crash policy: %s\n", message); }
    };
    wchar_t previous[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"WINAPP_DEVTOOLS_LOG", previous, _countof(previous));
    const auto priorFilter = SetUnhandledExceptionFilter(AppFilter);
    SetEnvironmentVariableW(L"WINAPP_DEVTOOLS_LOG", nullptr);
    DevToolsInstallCrashHandler();
    check(!handler, "default installs no exception handler");
    SetEnvironmentVariableW(L"WINAPP_DEVTOOLS_LOG", L"1");
    DevToolsInstallCrashHandler();
    check(handler != nullptr, "opt-in installs module-scoped observer");
    check(SetUnhandledExceptionFilter(priorFilter) == AppFilter, "app unhandled filter is untouched");
    // Module discovery is substituted, but the actual production handler and once guard run.
    g_selfMod = selfModule;
    EXCEPTION_RECORD record{};
    CONTEXT context{};
    EXCEPTION_POINTERS pointers{&record, &context};
    record.ExceptionCode = EXCEPTION_ACCESS_VIOLATION;
    record.ExceptionAddress = reinterpret_cast<void*>(3);
    check(handler && handler(&pointers) == EXCEPTION_CONTINUE_SEARCH && dumps == 0, "foreign fault is not captured");
    record.ExceptionAddress = &selfAddress;
    record.ExceptionCode = EXCEPTION_BREAKPOINT;
    check(handler && handler(&pointers) == EXCEPTION_CONTINUE_SEARCH && dumps == 0, "non-AV is not captured");
    record.ExceptionCode = EXCEPTION_ACCESS_VIOLATION;
    check(handler && handler(&pointers) == EXCEPTION_CONTINUE_SEARCH && dumps == 1, "own AV is observed without swallowing");
    if (handler) handler(&pointers);
    check(dumps == 1, "capture is bounded to one dump");
    SetEnvironmentVariableW(L"WINAPP_DEVTOOLS_LOG", length ? previous : nullptr);
    std::printf("Crash policy: 7 checks, %d failures\n", failures);
    return failures;
}
